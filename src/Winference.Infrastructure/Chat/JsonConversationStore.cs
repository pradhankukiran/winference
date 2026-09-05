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
}
