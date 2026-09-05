using System.Text.Json;
using Winference.Core.Chat;

namespace Winference.Infrastructure.Chat;

/// <summary>Local private chat data. Construction and a missing-file read do not create storage.</summary>
public sealed class JsonConversationStore
    : IConversationStore
{
    public const long MaximumFileBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    public JsonConversationStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _path = Path.Combine(Path.GetFullPath(dataRoot), "Chat", "workspace.json");
    }

    public async Task<ChatWorkspaceLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return new(ChatWorkspace.Empty, null);
            try
            {
                var saved = await ReadValidatedAsync(cancellationToken).ConfigureAwait(false);
                return Normalize(saved);
            }
            catch (Exception error) when (error is JsonException or InvalidDataException)
            {
                // Keep the original in place. Save makes a byte-for-byte backup
                // before replacing an unreadable or newer-schema workspace.
                return new(ChatWorkspace.Empty,
                    "Saved chats could not be read. The original file was preserved; new chats can still be saved.");
            }
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(ChatWorkspace workspace, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Snapshot(workspace);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 8192, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(output, snapshot, JsonOptions, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (output.Length > MaximumFileBytes)
                    throw new InvalidDataException("Saved chats exceed the supported local file size; previous chats were kept.");
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_path))
            {
                try { await ReadValidatedAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is JsonException or InvalidDataException)
                {
                    var backup = Path.Combine(directory, "workspace.preserved-" +
                        DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + Guid.NewGuid().ToString("N") + ".json");
                    File.Copy(_path, backup, overwrite: false);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
            finally { _gate.Release(); }
        }
    }

    private async Task<ChatWorkspace> ReadValidatedAsync(CancellationToken token)
    {
        if (new FileInfo(_path).Length > MaximumFileBytes)
            throw new InvalidDataException("Saved chat file exceeds the supported size.");
        await using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
            8192, FileOptions.Asynchronous);
        var saved = await JsonSerializer.DeserializeAsync<ChatWorkspace>(input, JsonOptions, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Saved chat data is empty.");
        return Snapshot(saved);
    }

    private static ChatWorkspace Snapshot(ChatWorkspace workspace)
    {
        if (workspace is null || workspace.SchemaVersion != ChatWorkspace.CurrentSchemaVersion ||
            workspace.Conversations is null || workspace.Conversations.Count > 2_000)
            throw new InvalidDataException("Saved chat schema or conversation count is unsupported.");
        var conversations = new List<SavedConversation>(workspace.Conversations.Count);
        var conversationIds = new HashSet<Guid>();
        foreach (var conversation in workspace.Conversations)
        {
            if (conversation is null || conversation.Id == Guid.Empty || !conversationIds.Add(conversation.Id) ||
                !Identity(conversation.ModelId) || !Identity(conversation.ProfileId) || !Identity(conversation.ArtifactId) ||
                conversation.Title is null || conversation.Title.Length > 256 || conversation.UpdatedUtc == default ||
                conversation.Draft is null || conversation.Draft.Length > 24_000 ||
                conversation.Messages is null || conversation.Messages.Count > 10_000)
                throw new InvalidDataException("Saved conversation identity or content is invalid.");
            try { conversation.ConversationCompatibility?.Validate(); }
            catch (ArgumentException error) { throw new InvalidDataException("Saved conversation compatibility is invalid.", error); }
            var messages = new List<SavedChatMessage>(conversation.Messages.Count);
            var messageIds = new HashSet<Guid>();
            foreach (var message in conversation.Messages)
            {
                if (message is null || message.Id == Guid.Empty || !messageIds.Add(message.Id) ||
                    message.Role is not ("user" or "assistant") || message.Text is null ||
                    message.Text.Length > 2_000_000 || message.Status is null || message.Status.Length > 256 ||
                    !ValidOrigin(message.OriginModelId, message.OriginProfileId, message.OriginArtifactId,
                        message.OriginModelName, message.OriginMode, allowUnattributedLabel: message.Role == "user") ||
                    message.TurnId == Guid.Empty || message.AttemptId == Guid.Empty || !ValidOutcome(message.AttemptOutcome) ||
                    message.Role == "user" && (message.AttemptId is not null || message.AttemptOutcome is not null ||
                        message.PreviousAttempts is { Count: > 0 }) ||
                    message.PreviousAttempts is { Count: > 32 })
                    throw new InvalidDataException("Saved message identity, role or content is invalid.");
                if (message.PreviousAttempts is { } attempts)
                {
                    var attemptIds = new HashSet<Guid>();
                    foreach (var attempt in attempts)
                        if (attempt is null || attempt.AttemptId == Guid.Empty || !attemptIds.Add(attempt.AttemptId) ||
                            attempt.AttemptId == message.AttemptId || attempt.Text is null || attempt.Text.Length > 2_000_000 ||
                            attempt.Status is null || attempt.Status.Length > 256 || attempt.Outcome is null ||
                            !ValidOutcome(attempt.Outcome) || attempt.Outcome == "running" || !ValidOrigin(attempt.OriginModelId, attempt.OriginProfileId,
                                attempt.OriginArtifactId, attempt.OriginModelName, attempt.OriginMode))
                            throw new InvalidDataException("Saved reply attempt is invalid.");
                }
                messages.Add(message with { PreviousAttempts = message.PreviousAttempts?.ToArray() });
            }
            conversations.Add(conversation with { Messages = messages.ToArray() });
        }
        if (workspace.ActiveConversationId is { } active && !conversationIds.Contains(active))
            throw new InvalidDataException("The active conversation is missing.");
        return workspace with { Conversations = conversations.ToArray() };
    }

    private static bool Identity(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512;

    private static bool ValidOutcome(string? value) => value is null or "running" or "completed" or "stopped" or "failed" or "length" or "interrupted";

    private static bool ValidOrigin(string? model, string? profile, string? artifact, string? name,
        Winference.Core.Models.ExecutionMode? mode, bool allowUnattributedLabel = false) =>
        (model is null && profile is null && artifact is null && (allowUnattributedLabel || name is null && mode is null) ||
         Identity(model) && Identity(profile) && Identity(artifact)) &&
        (name is null || !string.IsNullOrWhiteSpace(name) && name.Length <= 200) &&
        (mode is null || Enum.IsDefined(mode.Value));

    private static ChatWorkspaceLoadResult Normalize(ChatWorkspace workspace)
    {
        var changed = false;
        var conversations = workspace.Conversations.Select(conversation =>
        {
            var messages = conversation.Messages.ToArray();
            for (var i = 0; i < messages.Length; i++)
            {
                var current = messages[i];
                if (current.Role == "user" && i + 1 < messages.Length && messages[i + 1].Role == "assistant")
                {
                    var assistant = messages[i + 1];
                    var completed = current.IsContextEligible && assistant.IsContextEligible &&
                        assistant.Status.Length == 0 && !string.IsNullOrWhiteSpace(current.Text) &&
                        !string.IsNullOrWhiteSpace(assistant.Text) &&
                        (assistant.AttemptOutcome is null or "completed") &&
                        (assistant.TurnId is null || assistant.TurnId == current.Id);
                    messages[i] = current with { IsContextEligible = completed };
                    messages[i + 1] = assistant with
                    {
                        IsContextEligible = completed,
                        Status = completed ? assistant.Status : RestoredStatus(assistant.Status),
                        AttemptOutcome = assistant.AttemptOutcome == "running" ? "interrupted" : assistant.AttemptOutcome
                    };
                    changed |= messages[i] != current || messages[i + 1] != assistant;
                    i++;
                }
                else
                {
                    messages[i] = current with
                    {
                        IsContextEligible = false,
                        Status = current.Role == "assistant" ? RestoredStatus(current.Status) : current.Status,
                        AttemptOutcome = current.AttemptOutcome == "running" ? "interrupted" : current.AttemptOutcome
                    };
                    changed |= messages[i] != current;
                }
            }
            return conversation with { Messages = messages };
        }).ToArray();
        return new(workspace with { Conversations = conversations },
            changed ? "An unfinished reply was restored as interrupted. Completed replies were kept." : null);
    }

    private static string RestoredStatus(string status) => status.Length == 0 ||
        status.StartsWith("Thinking", StringComparison.OrdinalIgnoreCase) ||
        status.StartsWith("Writing", StringComparison.OrdinalIgnoreCase) ||
        status.StartsWith("Generating", StringComparison.OrdinalIgnoreCase)
            ? "Interrupted" : status;
}
