using Winference.Core.Models;

namespace Winference.Core.Runtime;

public enum ModeSwitchStage { Idle, CheckingTarget, Unloading, LoadingTarget, RestoringPrevious, Ready, Failed }
public enum ModeSwitchRecovery { None, PreviousPreserved, PreviousRestored, Stopped, CleanupUnconfirmed }

public sealed record ModeSwitchSnapshot(Guid OperationId, ModeSwitchStage Stage,
    ModelProfile? RequestedProfile, ModelProfile? ActiveProfile, string Detail,
    ModeSwitchRecovery Recovery = ModeSwitchRecovery.None);

/// <summary>Each load creates a fresh broker; stop proves completion of owned cleanup.</summary>
public sealed record ModelActivationPlan(ModelProfile Profile,
    Func<CancellationToken, Task> CheckAsync,
    Func<CancellationToken, Task<RuntimeSession>> LoadAsync,
    Func<CancellationToken, Task> StopAsync);

/// <summary>Serializes preflight, unload, exact target load and verified recovery.</summary>
public sealed class ModeSwitchCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _cleanupBlocked;
    private ModeSwitchSnapshot _snapshot = new(Guid.Empty, ModeSwitchStage.Idle, null, null, "AI not connected");
    public ModeSwitchSnapshot Snapshot => _snapshot;
    public event EventHandler<ModeSwitchSnapshot>? Changed;

    public async Task<ModeSwitchSnapshot> SwitchAsync(ModelActivationPlan target,
        ModelActivationPlan? previous, bool restorePrevious, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Profile.Validate();
        if (!await _gate.WaitAsync(0, token))
            throw new RuntimeException("SwitchBusy", "Wait for the current model change to finish.");
        var id = Guid.NewGuid();
        var unloading = false;
        var unloaded = false;
        var loading = false;
        try
        {
            if (_cleanupBlocked)
                throw new RuntimeException("CleanupUnconfirmed", "Close the app before loading another model; previous cleanup is not confirmed.");
            Set(id, ModeSwitchStage.CheckingTarget, target.Profile, restorePrevious ? previous?.Profile : null, "Checking the selected model…");
            await target.CheckAsync(token);
            token.ThrowIfCancellationRequested();
            if (previous is not null)
            {
                unloading = true;
                Set(id, ModeSwitchStage.Unloading, target.Profile, previous.Profile, "Stopping the previous model…");
                // Once unloading starts, observe cleanup even if the request is cancelled.
                await previous.StopAsync(CancellationToken.None);
                unloaded = true;
            }
            token.ThrowIfCancellationRequested();
            loading = true;
            Set(id, ModeSwitchStage.LoadingTarget, target.Profile, null, "Loading the selected model…");
            var session = await target.LoadAsync(token);
            Verify(session, target.Profile);
            token.ThrowIfCancellationRequested();
            return Set(id, ModeSwitchStage.Ready, target.Profile, target.Profile, "Model ready");
        }
        catch (Exception error)
        {
            var detail = error is OperationCanceledException ? "Model change cancelled." :
                error is RuntimeException ? error.Message : "The selected model could not be loaded.";
            if (_cleanupBlocked)
                return Set(id, ModeSwitchStage.Failed, target.Profile, null, detail, ModeSwitchRecovery.CleanupUnconfirmed);
            if (!unloading && !loading)
                return Set(id, ModeSwitchStage.Failed, target.Profile, restorePrevious ? previous?.Profile : null,
                    detail + " Your previous selection and chats were kept.", ModeSwitchRecovery.PreviousPreserved);
            // Failed unload is not permission to start either model on top of it.
            if (unloading && !unloaded)
            {
                _cleanupBlocked = true;
                return Set(id, ModeSwitchStage.Failed, target.Profile, null,
                    "The previous model could not finish stopping. Close the app before loading another model.",
                    ModeSwitchRecovery.CleanupUnconfirmed);
            }
            if (loading)
            {
                try { await target.StopAsync(CancellationToken.None); }
                catch
                {
                    _cleanupBlocked = true;
                    return Set(id, ModeSwitchStage.Failed, target.Profile, null,
                        "The selected model could not finish stopping. Close the app before loading another model.",
                        ModeSwitchRecovery.CleanupUnconfirmed);
                }
            }
            if (restorePrevious && previous is not null)
            {
                Set(id, ModeSwitchStage.RestoringPrevious, target.Profile, null, "Restoring the previous model…");
                try
                {
                    using var recovery = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                    await previous.CheckAsync(recovery.Token);
                    var restored = await previous.LoadAsync(recovery.Token);
                    Verify(restored, previous.Profile);
                    return Set(id, ModeSwitchStage.Failed, target.Profile, previous.Profile,
                        detail + " The previous model is ready again.", ModeSwitchRecovery.PreviousRestored);
                }
                catch
                {
                    try { await previous.StopAsync(CancellationToken.None); }
                    catch
                    {
                        _cleanupBlocked = true;
                        return Set(id, ModeSwitchStage.Failed, target.Profile, null,
                            "Recovery could not finish stopping. Close the app before loading another model.",
                            ModeSwitchRecovery.CleanupUnconfirmed);
                    }
                }
            }
            return Set(id, ModeSwitchStage.Failed, target.Profile, null,
                detail + " AI is stopped. Your conversations and drafts were kept.", ModeSwitchRecovery.Stopped);
        }
        finally { _gate.Release(); }
    }

    private ModeSwitchSnapshot Set(Guid id, ModeSwitchStage stage, ModelProfile? requested,
        ModelProfile? active, string detail, ModeSwitchRecovery recovery = ModeSwitchRecovery.None)
    {
        var snapshot = _snapshot = new(id, stage, requested, active, detail, recovery);
        if (Changed is { } handlers)
            foreach (EventHandler<ModeSwitchSnapshot> handler in handlers.GetInvocationList())
                try { handler(this, snapshot); } catch { /* An observer cannot abandon owned cleanup. */ }
        return snapshot;
    }

    private static void Verify(RuntimeSession session, ModelProfile target)
    {
        if (session.SessionId == Guid.Empty || session.Profile != target ||
            session.EffectiveContextLength != target.ContextLength || string.IsNullOrWhiteSpace(session.Device) ||
            string.IsNullOrWhiteSpace(session.Engine) || string.IsNullOrWhiteSpace(session.OwnershipProof) ||
            !session.Endpoint.IsAbsoluteUri || !session.Endpoint.IsLoopback || session.Endpoint.Scheme is not ("http" or "https"))
            throw new RuntimeException("SwitchIdentity", "The selected model or device could not be verified.");
    }
}
