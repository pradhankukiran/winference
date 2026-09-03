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
}
