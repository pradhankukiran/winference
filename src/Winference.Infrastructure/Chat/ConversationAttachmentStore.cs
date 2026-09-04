using System.Text;
using System.Text.Json;
using Winference.Core.Rag;
using Winference.Core.Runtime;

namespace Winference.Infrastructure.Chat;

/// <summary>
/// Keeps the text of each chat's attached documents under the data folder, one folder per chat.
/// Only the extracted text is kept, never the original file.
/// </summary>
public sealed class ConversationAttachmentStore(string dataRoot)
{
    public const int MaxAttachmentsPerChat = 10;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _root = Path.Combine(Path.GetFullPath(dataRoot), "Attachments");
    private readonly object _sync = new();

    public IReadOnlyList<AttachmentInfo> List(Guid conversationId)
    {
        lock (_sync) return ReadManifest(conversationId);
    }

    /// <summary>Loads every attachment of a chat into a fresh index. Missing text files are skipped.</summary>
    public LocalVectorIndex LoadIndex(Guid conversationId)
    {
        var index = new LocalVectorIndex();
        lock (_sync)
        {
            foreach (var item in ReadManifest(conversationId))
            {
                var path = TextPath(conversationId, item.Id);
                try
                {
                    if (File.Exists(path)) index.AddDocument(item.FileName, File.ReadAllText(path, Encoding.UTF8), item.Id);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        return index;
    }

    public AttachmentInfo Add(Guid conversationId, string fileName, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        lock (_sync)
        {
            var manifest = ReadManifest(conversationId).ToList();
            if (manifest.Count >= MaxAttachmentsPerChat)
                throw new RuntimeException("attachment_limit", $"A chat can have up to {MaxAttachmentsPerChat} documents. Remove one first.");
            var item = new AttachmentInfo(Guid.NewGuid().ToString("N"), Path.GetFileName(fileName),
                Encoding.UTF8.GetByteCount(text), DateTimeOffset.UtcNow);
            Directory.CreateDirectory(Folder(conversationId));
            File.WriteAllText(TextPath(conversationId, item.Id), text, new UTF8Encoding(false));
            manifest.Add(item);
            WriteManifest(conversationId, manifest);
            return item;
        }
    }

    public bool Remove(Guid conversationId, string attachmentId)
    {
        lock (_sync)
        {
            var manifest = ReadManifest(conversationId).ToList();
            if (manifest.RemoveAll(item => item.Id == attachmentId) == 0) return false;
            WriteManifest(conversationId, manifest);
            TryDelete(TextPath(conversationId, attachmentId));
            var folder = Folder(conversationId);
            if (Directory.Exists(folder))
                foreach (var vectors in Directory.EnumerateFiles(folder, attachmentId + ".*.vec")) TryDelete(vectors);
            return true;
        }
    }

    /// <summary>Removes a deleted chat's documents. Failure leaves files behind but never throws.</summary>
    public void DeleteConversation(Guid conversationId)
    {
        lock (_sync)
        {
            try
            {
                var folder = Folder(conversationId);
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Saves one embedding per chunk for a model. Search falls back to keywords if this fails.</summary>
    public void SaveEmbeddings(Guid conversationId, string attachmentId, string model, float[][] vectors)
    {
        lock (_sync)
        {
            var path = VectorPath(conversationId, attachmentId, model);
            var temporary = path + ".tmp";
            using (var writer = new BinaryWriter(File.Create(temporary)))
            {
                writer.Write(vectors.Length);
                writer.Write(vectors.Length == 0 ? 0 : vectors[0].Length);
                foreach (var vector in vectors)
                {
                    if (vector.Length != vectors[0].Length) throw new ArgumentException("Embeddings must share one length.", nameof(vectors));
                    foreach (var value in vector) writer.Write(value);
                }
            }
            File.Move(temporary, path, overwrite: true);
        }
    }

    /// <summary>The saved embeddings for a model, or null if there are none or they're unreadable.</summary>
    public float[][]? LoadEmbeddings(Guid conversationId, string attachmentId, string model)
    {
        lock (_sync)
        {
            var path = VectorPath(conversationId, attachmentId, model);
            try
            {
                if (!File.Exists(path)) return null;
                using var reader = new BinaryReader(File.OpenRead(path));
                var count = reader.ReadInt32();
                var length = reader.ReadInt32();
                if (count < 0 || length <= 0 || (long)count * length * 4 + 8 != new FileInfo(path).Length) return null;
                var vectors = new float[count][];
                for (var i = 0; i < count; i++)
                {
                    vectors[i] = new float[length];
                    for (var j = 0; j < length; j++) vectors[i][j] = reader.ReadSingle();
                }
                return vectors;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or EndOfStreamException) { return null; }
        }
    }

    private string VectorPath(Guid conversationId, string attachmentId, string model)
    {
        var safeModel = new string(model.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        return Path.ChangeExtension(TextPath(conversationId, attachmentId), null) + "." + safeModel + ".vec";
    }

    private string Folder(Guid conversationId) => Path.Combine(_root, conversationId.ToString("N"));
    private string ManifestPath(Guid conversationId) => Path.Combine(Folder(conversationId), "attachments.json");

    private string TextPath(Guid conversationId, string attachmentId)
    {
        if (attachmentId.Length != 32 || !attachmentId.All(char.IsAsciiHexDigitLower))
            throw new ArgumentException("Invalid attachment id.", nameof(attachmentId));
        return Path.Combine(Folder(conversationId), attachmentId + ".txt");
    }

    private List<AttachmentInfo> ReadManifest(Guid conversationId)
    {
        try
        {
            var path = ManifestPath(conversationId);
            if (!File.Exists(path)) return [];
            return (JsonSerializer.Deserialize<List<AttachmentInfo>>(File.ReadAllText(path), Options) ?? [])
                .Where(item => item is { Id.Length: 32, FileName.Length: > 0 } && item.Id.All(char.IsAsciiHexDigitLower)).ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private void WriteManifest(Guid conversationId, List<AttachmentInfo> manifest)
    {
        var path = ManifestPath(conversationId);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, Options));
        File.Move(temporary, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
