using Microsoft.UI.Dispatching;
using Winference.App.Diagnostics;
using Winference.App.ViewModels;
using Winference.Core.Developer;
using Winference.Core.Runtime;
using Winference.Infrastructure.Developer;

namespace Winference.App.Services;

/// <summary>One line of the request log. It stays in memory and is gone when Winference closes.</summary>
public sealed record ApiLogEntry(DateTimeOffset Time, string App, string Request, string Result, bool IsProblem);

/// <summary>
/// Answers apps on this PC through the local server. Each app is asked for once, then
/// remembered. Requests use the model selected in Winference and take turns with the chat.
/// </summary>
internal sealed class LocalApiHost(
    ShellViewModel shell,
    DispatcherQueue dispatcher,
    DialogService dialogs,
    AppLifetime lifetime,
    AppPermissionStore permissions,
    ReplyMeter meter,
    Func<double> temperature,
    Action requestAttention)
{
    private static readonly TimeSpan BusyWait = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly Dictionary<string, Task<bool>> _asking = [];
    private LocalApiServer? _server;

    public event EventHandler<ApiLogEntry>? Logged;

    public int? Port => _server?.Port;
    public bool IsRunning => _server is not null;

    /// <summary>Starts or moves the server. Returns a message for the user if it couldn't start.</summary>
    public async Task<string?> StartAsync(int port)
    {
        await StopAsync();
        try
        {
            _server = await LocalApiServer.StartAsync(port, HandleAsync);
            AppDiagnostics.Write("server.started", new { port });
            return null;
        }
        catch (RuntimeException error)
        {
            AppDiagnostics.Write("server.start.failed", new { port, code = error.Code });
            return error.Message;
        }
    }

    public async Task StopAsync()
    {
        if (_server is not { } server) return;
        _server = null;
        await server.DisposeAsync();
        AppDiagnostics.Write("server.stopped");
    }

    private async Task HandleAsync(LocalApiRequest request, IApiResponse response, CancellationToken cancellationToken)
    {
        var caller = CallerLookup.Identify(request.Client, request.ServerPort);
        var path = request.Path.TrimEnd('/');
        var what = $"{request.Method} {request.Path}";
        try
        {
            if (!await IsAllowedAsync(caller))
            {
                Log(caller, what, "Blocked", problem: true);
                throw new ApiException(403, "app_not_allowed", caller.ProcessId is null
                    ? "Winference couldn't tell which app sent this request, so it was refused."
                    : $"{caller.Name} isn't allowed to use Winference. Change this on Winference's Developer page.");
            }
            permissions.MarkUsed(caller.Key, DateTimeOffset.Now);

            switch (path)
            {
                case "" or "/v1":
                    RequireMethod(request, "GET");
                    await response.SendJsonAsync(200, """{"name":"Winference","status":"ok"}""", cancellationToken);
                    return;
                case "/v1/models" or "/models":
                    RequireMethod(request, "GET");
                    var modelId = await OnUiAsync(() => Task.FromResult(shell.Chat is { CanLoadModel: true } chat ? chat.ModelId : null));
                    await response.SendJsonAsync(200, OpenAiApi.ModelList(modelId), cancellationToken);
                    Log(caller, "Listed models", "Answered", problem: false);
                    return;
                case "/v1/chat/completions" or "/chat/completions":
                    RequireMethod(request, "POST");
                    var chatRequest = OpenAiApi.ParseChatRequest(request.Body);
                    what = "Chat: " + OpenAiApi.Preview(chatRequest);
                    var outcome = await AnswerAsync(caller, chatRequest, response, cancellationToken);
                    Log(caller, what, Describe(outcome), outcome.Status != 200);
                    return;
                default:
                    throw new ApiException(404, "not_found", $"Winference doesn't offer {request.Path}. Try /v1/chat/completions or /v1/models.");
            }
        }
        catch (ApiException error) when (error.Code != "app_not_allowed")
        {
            Log(caller, what, error.Message, problem: true);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log(caller, what, lifetime.IsClosing ? "Stopped: Winference closed" : "Stopped: the app hung up", problem: true);
            throw;
        }
    }

    private async Task<ApiOutcome> AnswerAsync(CallerApp caller, ApiChatRequest request, IApiResponse response, CancellationToken cancellationToken)
    {
        // Apps take turns. The chat can still go first; the responder waits for it.
        await _turn.WaitAsync(cancellationToken);
        ChatViewModel? chat = null;
        try
        {
            ApiTarget? target = null;
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (target is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (current, readiness, reason) = await OnUiAsync(async () =>
                {
                    if (shell.Chat is not { } open) return ((ChatViewModel?)null, AppReadiness.Unavailable, (string?)"No model is selected in Winference. Choose one in the app first.");
                    var (state, why) = await open.PrepareForAppAsync();
                    if (state == AppReadiness.Ready) open.ExternalActivity = $"Answering {caller.Name}…";
                    return (open, state, why);
                });
                if (readiness == AppReadiness.Unavailable)
                    throw new ApiException(503, "model_unavailable", reason ?? "The model isn't available.");
                if (readiness == AppReadiness.Ready)
                {
                    chat = current!;
                    target = new(chat.Broker, chat.ModelId, chat.Profile);
                    break;
                }
                if (waited.Elapsed > BusyWait)
                    throw new ApiException(503, "model_busy", "Winference is busy with another reply. Try again shortly.");
                await Task.Delay(500, cancellationToken);
            }
            return await ChatCompletionResponder.RespondAsync(target, request, response, temperature(), meter, BusyWait, cancellationToken);
        }
        finally
        {
            if (chat is not null) _ = OnUiAsync(() => { chat.ExternalActivity = null; return Task.FromResult(true); });
            _turn.Release();
        }
    }

    /// <summary>Known apps get their saved answer. A new app waits for the user, once, however many requests it sends.</summary>
    private async Task<bool> IsAllowedAsync(CallerApp caller)
    {
        // Allowing "an unknown app" would let any program in, so unidentified callers are always refused.
        if (caller.ProcessId is null) return false;
        if (permissions.Find(caller.Key) is { } known) return known.Allowed;
        Task<bool> asking;
        lock (_asking)
        {
            if (!_asking.TryGetValue(caller.Key, out asking!))
            {
                asking = OnUiAsync(() => AskAsync(caller));
                _asking[caller.Key] = asking;
            }
        }
        try { return await asking; }
        finally { lock (_asking) _asking.Remove(caller.Key); }
    }

    private async Task<bool> AskAsync(CallerApp caller)
    {
        if (permissions.Find(caller.Key) is { } known) return known.Allowed;
        var lines = new List<string>
        {
            "It wants to send text to your model and read the replies. Everything stays on this PC.",
        };
        if (caller.Path is not null) lines.Add($"Program: {caller.Path}");
        else lines.Add("Winference couldn't tell which program this is.");
        lines.Add("You can change this later on the Developer page.");
        requestAttention();
        var allowed = await dialogs.ConfirmAsync($"{caller.Name} wants to use Winference", [.. lines], "Allow", close: "Don't allow");
        // A dialog closed because Winference is closing is not an answer.
        if (lifetime.IsClosing) return false;
        permissions.Set(new(caller.Key, caller.Name, caller.Path, allowed, DateTimeOffset.Now));
        AppDiagnostics.Write("server.app.decided", new { allowed });
        return allowed;
    }

    private static void RequireMethod(LocalApiRequest request, string method)
    {
        if (request.Method != method) throw new ApiException(405, "method_not_allowed", $"Use {method} for {request.Path}.");
    }

    private static string Describe(ApiOutcome outcome)
    {
        var parts = new List<string> { outcome.Summary };
        if (outcome.CompletionTokens is > 0 and var tokens) parts.Add($"{tokens:N0} tokens");
        if (outcome.TokensPerSecond is { } rate) parts.Add($"{rate:0.0} tokens/s");
        return string.Join(" · ", parts);
    }

    private void Log(CallerApp caller, string request, string result, bool problem) =>
        Logged?.Invoke(this, new(DateTimeOffset.Now, caller.Name, request, result, problem));

    private Task<T> OnUiAsync<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
        {
            try { done.TrySetResult(await work()); }
            catch (Exception error) { done.TrySetException(error); }
        }))
            done.TrySetException(new ApiException(503, "closing", "Winference is closing."));
        return done.Task;
    }
}
