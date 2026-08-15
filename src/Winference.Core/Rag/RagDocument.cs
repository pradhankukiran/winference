namespace Winference.Core.Rag;

/// <summary>A piece of an attached document that can be placed in a prompt.</summary>
public sealed record DocumentChunk(
    string DocumentId,
    string SourceFileName,
    int ChunkIndex,
    string Content);

/// <summary>A document attached to a conversation, split into chunks.</summary>
public sealed record LocalDocument(
    string Id,
    string FileName,
    long SizeBytes,
    DateTimeOffset AddedUtc,
    IReadOnlyList<DocumentChunk> Chunks);

/// <summary>Saved details of an attachment. The extracted text is stored beside it.</summary>
public sealed record AttachmentInfo(string Id, string FileName, long SizeBytes, DateTimeOffset AddedUtc);
