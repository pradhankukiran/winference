using System.Text;

namespace Winference.Core.Rag;

/// <summary>
/// In-memory keyword retrieval over the documents attached to one conversation.
/// Needs no server, embedding model or network access.
/// </summary>
public sealed class LocalVectorIndex
{
    public const int DefaultChunkWords = 180;
    public const int DefaultChunkOverlap = 30;

    private static readonly char[] Separators =
        [' ', '\r', '\n', '\t', ',', '.', '?', '!', ';', ':', '-', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\', '*', '#', '|', '<', '>', '='];

    private readonly List<LocalDocument> _documents = [];
    private readonly Dictionary<string, float[][]> _vectors = [];
    private readonly object _sync = new();

    public int ChunkCount { get { lock (_sync) return _documents.Sum(document => document.Chunks.Count); } }
    public IReadOnlyList<LocalDocument> Documents { get { lock (_sync) return [.. _documents]; } }

    public LocalDocument AddDocument(string fileName, string text, string? documentId = null,
        int chunkWords = DefaultChunkWords, int chunkOverlap = DefaultChunkOverlap)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (chunkWords <= 0 || chunkOverlap < 0 || chunkOverlap >= chunkWords) throw new ArgumentOutOfRangeException(nameof(chunkOverlap));

        var id = documentId ?? Guid.NewGuid().ToString("N");
        var chunks = ChunkText(text, chunkWords, chunkOverlap)
            .Select((content, index) => new DocumentChunk(id, fileName, index, content)).ToArray();
        var document = new LocalDocument(id, fileName, Encoding.UTF8.GetByteCount(text), DateTimeOffset.UtcNow, chunks);
        lock (_sync)
        {
            _documents.RemoveAll(existing => existing.Id == id);
            _documents.Add(document);
        }
        return document;
    }

    public bool RemoveDocument(string documentId)
    {
        lock (_sync)
        {
            _vectors.Remove(documentId);
            return _documents.RemoveAll(document => document.Id == documentId) > 0;
        }
    }
}
