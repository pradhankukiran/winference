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

        const string header = "The user attached the documents below. Use them when they are relevant to the question. " +
            "If the answer is not in them, say so instead of guessing. Mention the file name when you rely on one.\n\n";
        var budget = maxChars - header.Length;
        if (budget <= 200) return string.Empty;

        var chosen = new List<DocumentChunk>();
        foreach (var chunk in ranked)
        {
            var cost = chunk.Content.Length + chunk.SourceFileName.Length + 24;
            if (cost <= budget)
            {
                chosen.Add(chunk);
                budget -= cost;
            }
            else if (chosen.Count == 0)
            {
                // A single very long excerpt is shortened rather than dropped.
                chosen.Add(chunk with { Content = chunk.Content[..Math.Max(0, budget - chunk.SourceFileName.Length - 40)] + " …" });
                break;
            }
            if (budget < 200) break;
        }

        var builder = new StringBuilder(header);
        string? currentDocument = null;
        var lastIndex = -2;
        foreach (var chunk in chosen.OrderBy(chunk => DocumentOrder(chunk.DocumentId)).ThenBy(chunk => chunk.ChunkIndex))
        {
            if (chunk.DocumentId != currentDocument)
            {
                if (currentDocument is not null) builder.AppendLine();
                builder.Append("--- ").Append(chunk.SourceFileName).AppendLine(" ---");
                currentDocument = chunk.DocumentId;
                lastIndex = -2;
            }
            else if (chunk.ChunkIndex != lastIndex + 1) builder.AppendLine("[…]");
            builder.AppendLine(chunk.Content.Trim());
            lastIndex = chunk.ChunkIndex;
        }
        return builder.ToString();
    }

    private static float[] Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var value in vector) sum += value * value;
        var length = (float)Math.Sqrt(sum);
        return length > 0 ? vector.Select(value => value / length).ToArray() : vector;
    }

    private static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    private List<DocumentChunk> AllChunks()
    {
        lock (_sync) return _documents.SelectMany(document => document.Chunks).ToList();
    }

    private int DocumentOrder(string id)
    {
        lock (_sync) return _documents.FindIndex(document => document.Id == id);
    }

    /// <summary>Word windows with overlap. Overlapping words are dropped again when adjacent chunks are joined.</summary>
    private static List<string> ChunkText(string text, int chunkWords, int chunkOverlap)
    {
        var result = new List<string>();
        var words = text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var i = 0;
        while (i < words.Length)
        {
            var take = Math.Min(chunkWords, words.Length - i);
            result.Add(string.Join(' ', words, i, take));
            if (i + take >= words.Length) break;
            i += take - chunkOverlap;
        }
        return result;
    }

    private static HashSet<string> Terms(string query) =>
        query.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 3 && !StopWords.Contains(word))
            .Select(word => word.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

    private static double ScoreChunk(string content, HashSet<string> terms)
    {
        var words = content.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return 0;
        var matched = new HashSet<string>(StringComparer.Ordinal);
        var hits = 0;
        foreach (var word in words)
        {
            var lower = word.ToLowerInvariant();
            if (!terms.Contains(lower)) continue;
            hits++;
            matched.Add(lower);
        }
        // Covering more distinct query words matters more than repeating one.
        return (matched.Count * 2 + hits) / Math.Sqrt(words.Length);
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "are", "but", "not", "you", "all", "any", "can", "had", "her", "was", "one", "our", "out",
        "has", "him", "his", "how", "its", "may", "who", "did", "does", "this", "that", "with", "have", "from", "they",
        "what", "when", "where", "which", "will", "would", "there", "their", "about", "into", "your", "than", "then",
        "them", "these", "those", "could", "should", "please", "tell", "explain", "document", "file", "attached"
    };
}
