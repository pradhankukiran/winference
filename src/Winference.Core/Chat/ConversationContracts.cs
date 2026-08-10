using Winference.Core.Models;

namespace Winference.Core.Chat;

/// <summary>A visible turn. Only paired, completed turns may enter model context.</summary>
public sealed record SavedChatMessage(
    Guid Id, string Role, string Text, string Status, bool IsContextEligible,
    string? OriginModelId = null, string? OriginProfileId = null, string? OriginArtifactId = null,
    string? OriginModelName = null, ExecutionMode? OriginMode = null,
    Guid? TurnId = null, Guid? AttemptId = null, string? AttemptOutcome = null,
    IReadOnlyList<SavedChatAttempt>? PreviousAttempts = null);

/// <summary>Explicit retry retains the previous attempt without duplicating its user turn.</summary>
public sealed record SavedChatAttempt(
    Guid AttemptId, string Text, string Status, string Outcome,
    string? OriginModelId, string? OriginProfileId, string? OriginArtifactId,
    string? OriginModelName, ExecutionMode? OriginMode);

/// <summary>Conversation identity includes the exact model profile and artifact.</summary>
public sealed record SavedConversation(
    Guid Id, string ModelId, string ProfileId, string ArtifactId, string Title,
    DateTimeOffset UpdatedUtc, string Draft, IReadOnlyList<SavedChatMessage> Messages,
    ConversationCompatibility? ConversationCompatibility = null, bool HasCustomTitle = false);

public sealed record ChatWorkspace(int SchemaVersion, Guid? ActiveConversationId, IReadOnlyList<SavedConversation> Conversations)
{
    public const int CurrentSchemaVersion = 1;
    public static ChatWorkspace Empty => new(CurrentSchemaVersion, null, []);
}

public sealed record ChatWorkspaceLoadResult(ChatWorkspace Workspace, string? Warning);

public interface IConversationStore
{
    Task<ChatWorkspaceLoadResult> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ChatWorkspace workspace, CancellationToken cancellationToken = default);
}
