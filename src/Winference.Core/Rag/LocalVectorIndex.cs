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

    public void Clear()
    {
        lock (_sync) { _documents.Clear(); _vectors.Clear(); }
    }

    /// <summary>Attaches one embedding per chunk. Ignored unless the count matches the document's chunks.</summary>
    public bool SetEmbeddings(string documentId, float[][] vectors)
    {
        lock (_sync)
        {
            var document = _documents.FirstOrDefault(item => item.Id == documentId);
            if (document is null || vectors.Length != document.Chunks.Count || vectors.Any(vector => vector.Length == 0)) return false;
            _vectors[documentId] = vectors.Select(Normalize).ToArray();
            return true;
        }
    }

    public bool HasEmbeddings(string documentId)
    {
        lock (_sync) return _vectors.ContainsKey(documentId);
    }

    public void ClearEmbeddings()
    {
        lock (_sync) _vectors.Clear();
    }

    /// <summary>The chunks that share the most words with the query, best first.</summary>
    public IReadOnlyList<DocumentChunk> Search(string query, int topK = 3)
    {
        if (string.IsNullOrWhiteSpace(query) || topK <= 0) return [];
        var terms = Terms(query);
        if (terms.Count == 0) return [];
        return AllChunks()
            .Select(chunk => (Chunk: chunk, Score: ScoreChunk(chunk.Content, terms)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .Take(topK)
            .Select(item => item.Chunk)
            .ToArray();
    }

    /// <summary>
    /// Builds a system-prompt section of at most <paramref name="maxChars"/> characters.
    /// Small documents are included whole. Larger ones contribute their best-matching
    /// excerpts, then their opening excerpts, so "summarize this" still sees the text.
    /// </summary>
    /// <param name="queryVector">
    /// The question's embedding. When given, chunks with embeddings are ranked by meaning,
    /// with a small keyword bonus so exact names and numbers still count.
    /// </param>
    public string BuildContextPrompt(string query, int maxChars, float[]? queryVector = null)
    {
        if (maxChars <= 0) return string.Empty;
        var chunks = AllChunks();
        if (chunks.Count == 0) return string.Empty;

        var ranked = new List<DocumentChunk>();
        var terms = Terms(query ?? string.Empty);
        var keyword = chunks.ToDictionary(chunk => chunk, chunk => terms.Count > 0 ? ScoreChunk(chunk.Content, terms) : 0);
        Dictionary<string, float[][]> vectors;
        lock (_sync) vectors = new(_vectors);
        if (queryVector is { Length: > 0 } && vectors.Count > 0)
        {
            var normalizedQuery = Normalize(queryVector);
            var maxKeyword = Math.Max(1e-9, keyword.Values.DefaultIfEmpty(0).Max());
            ranked.AddRange(chunks
                .Where(chunk => vectors.TryGetValue(chunk.DocumentId, out var document) && document[chunk.ChunkIndex].Length == normalizedQuery.Length)
                .Select(chunk => (Chunk: chunk, Score: Dot(normalizedQuery, vectors[chunk.DocumentId][chunk.ChunkIndex]) + 0.15 * keyword[chunk] / maxKeyword))
                .OrderByDescending(item => item.Score)
                .Take(64)
                .Select(item => item.Chunk));
        }
        var meaningRanked = ranked.ToHashSet();
        if (terms.Count > 0)
            ranked.AddRange(chunks
                .Where(chunk => !meaningRanked.Contains(chunk) && keyword[chunk] > 0)
                .OrderByDescending(chunk => keyword[chunk]));
        // Then each document's opening, interleaved, so every attachment is represented.
        var seen = ranked.ToHashSet();
        ranked.AddRange(chunks.OrderBy(chunk => chunk.ChunkIndex).Where(chunk => !seen.Contains(chunk)));
    }
}
