using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Winference.Core.Runtime;
using Winference.Core.Setup;

namespace Winference.Infrastructure.Setup;

public static class PinnedRuntimeAcquisition
{
    public const long ArchiveBytes = 30_484_687;
    public const string ArchiveSha256 = "a7063890d7b91037a07f381a396a0d7787ebdb69e1b03280971faf0a80f35b73";
    public const string InstalledExeSha256 = "de83c3a34cc759e65e59831923e90205e799296bb277d38907b397c960573190";
    public const string PortableExeSha256 = "f1edf82facc62978dd9a0ff10596303db3a444811aa34ea878e46bae817c300a";
    public const string LemonadeExeSha256 = "2b2d227265cd3e3822f6af469403f421512d6c1d37df556d100ef4ca46945ea2";
    public static readonly Uri DownloadUri = new("https://github.com/ROCm/FastFlowLM/releases/download/v1.0.10/fastflowlm_1.0.10_windows_amd64.zip");

    public static async Task<string?> FindTrustedRuntimeAsync(SetupPaths paths, CancellationToken token)
    {
        var candidates = paths.RuntimeCandidates.Concat(Directory.Exists(paths.RuntimeDirectory)
            ? Directory.EnumerateFiles(paths.RuntimeDirectory, "flm.exe", SearchOption.AllDirectories) : []);
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(path)) continue;
            var hash = await HashAsync(path, token).ConfigureAwait(false);
            // A portable extraction is trusted only after its original archive
            // digest and its own manifest have been checked below.
            if (hash == InstalledExeSha256 || hash == PortableExeSha256 && await IsVerifiedPortableAsync(paths, path, hash, token).ConfigureAwait(false)) return Path.GetFullPath(path);
        }
        return null;
    }

    public static async Task<string> AcquireAsync(SetupPaths paths, IProgress<SetupProgress> progress, HttpClient client, CancellationToken token)
    {
        var existing = await FindTrustedRuntimeAsync(paths, token).ConfigureAwait(false);
        if (existing is not null) return existing;
        var downloadDirectory = Path.Combine(paths.DataRoot, "Downloads");
        Directory.CreateDirectory(downloadDirectory);
        var archive = Path.Combine(downloadDirectory, "fastflowlm-1.0.10.zip");
        await DownloadVerifiedAsync(client, DownloadUri, archive, ArchiveBytes, ArchiveSha256, progress, token).ConfigureAwait(false);
        var staging = paths.RuntimeDirectory + ".staging-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            progress.Report(new("InstallingRuntime", "Installing the local AI component…"));
            using (var zip = ZipFile.OpenRead(archive))
            {
                if (zip.Entries.Sum(entry => entry.Length) > 1_000_000_000) throw new InvalidDataException("Runtime archive is unexpectedly large.");
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    var destination = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                    if (!destination.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid runtime archive path.");
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(destination); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using var source = entry.Open();
                    await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                    await source.CopyToAsync(target, token).ConfigureAwait(false);
                }
            }
            var executable = Directory.EnumerateFiles(staging, "flm.exe", SearchOption.AllDirectories).SingleOrDefault()
                ?? throw new InvalidDataException("Runtime archive is missing its executable.");
            if (await HashAsync(executable, token).ConfigureAwait(false) != PortableExeSha256)
                throw new RuntimeException("runtime_identity", "The downloaded AI component's executable differs from the qualified release.");
            var hashes = new Dictionary<string, string>();
            foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                hashes[Path.GetRelativePath(staging, file)] = await HashAsync(file, token).ConfigureAwait(false);
            await AtomicJson.WriteAsync(Path.Combine(staging, "winference-runtime.json"), new VerifiedRuntime(ArchiveSha256, hashes), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(paths.RuntimeDirectory)!);
            string? previous = null;
            if (Directory.Exists(paths.RuntimeDirectory))
            {
                var ownership = Path.Combine(paths.RuntimeDirectory, "winference-runtime.json");
                var prior = File.Exists(ownership) ? await AtomicJson.ReadAsync<VerifiedRuntime>(ownership, token).ConfigureAwait(false) : null;
                if (prior?.ArchiveSha256 != ArchiveSha256)
                    throw new RuntimeException("runtime_conflict", "The AI component location contains unrecognized files. They were preserved; choose a clean Winference data location.");
                // A user-triggered repair preserves the previous app-owned
                // installation instead of deleting or modifying its files.
                previous = paths.RuntimeDirectory + ".previous-" + Guid.NewGuid().ToString("N");
                Directory.Move(paths.RuntimeDirectory, previous);
            }
            try { Directory.Move(staging, paths.RuntimeDirectory); }
            catch
            {
                if (previous is not null && !Directory.Exists(paths.RuntimeDirectory)) Directory.Move(previous, paths.RuntimeDirectory);
                throw;
            }
            return Path.Combine(paths.RuntimeDirectory, Path.GetRelativePath(staging, executable));
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    public static async Task DownloadVerifiedAsync(HttpClient client, Uri source, string destination,
        long expectedLength, string expectedSha256, IProgress<SetupProgress> progress, CancellationToken token,
        string subject = "local AI component")
    {
        if (File.Exists(destination) && new FileInfo(destination).Length == expectedLength &&
            await HashAsync(destination, token).ConfigureAwait(false) == expectedSha256) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + ".part";
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset >= expectedLength && offset > 0)
        {
            if (offset == expectedLength && await HashAsync(partial, token).ConfigureAwait(false) == expectedSha256)
            { token.ThrowIfCancellationRequested(); File.Move(partial, destination, true); return; }
            File.Delete(partial); offset = 0;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.PartialContent)
            throw new RuntimeException("download_failed", $"The {subject} could not be downloaded. Check the connection and retry.");
        if (response.StatusCode == HttpStatusCode.OK) offset = 0;
        else if (response.Content.Headers.ContentRange?.From != offset || response.Content.Headers.ContentRange?.Length != expectedLength)
            throw new RuntimeException("download_range", "The download server returned an unexpected resume range. Retry the download.");
        await using (var target = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.Read, 81920, FileOptions.Asynchronous))
        await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
        {
            var buffer = new byte[81920];
            var transferred = offset;
            progress.Report(new("DownloadingRuntime", $"Downloading the {subject}…", transferred, expectedLength));
            int read;
            while (true)
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
                stall.CancelAfter(TimeSpan.FromSeconds(60));
                try { read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { throw new RuntimeException("download_stalled", $"The {subject} download stopped responding. Check the connection and retry."); }
                if (read == 0) break;
                transferred += read;
                if (transferred > expectedLength) throw new RuntimeException("download_size", $"The download was larger than the verified {subject}. It was not kept.");
                await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                progress.Report(new("DownloadingRuntime", $"Downloading the {subject}…", transferred, expectedLength));
            }
        }
        progress.Report(new("VerifyingRuntime", $"Verifying the {subject} download…"));
        if (new FileInfo(partial).Length != expectedLength || await HashAsync(partial, token).ConfigureAwait(false) != expectedSha256)
            throw new RuntimeException("download_integrity", $"The {subject} download did not match its published checksum. It was not kept.");
        token.ThrowIfCancellationRequested();
        File.Move(partial, destination, true);
    }

    public static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
    }

    private static async Task<bool> IsVerifiedPortableAsync(SetupPaths paths, string executable, string hash, CancellationToken token)
    {
        var manifest = Path.Combine(paths.RuntimeDirectory, "winference-runtime.json");
        if (!File.Exists(manifest) || !Path.GetFullPath(executable).StartsWith(paths.RuntimeDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var state = await AtomicJson.ReadAsync<VerifiedRuntime>(manifest, token).ConfigureAwait(false);
            if (state?.ArchiveSha256 != ArchiveSha256 || !state.Files.TryGetValue(Path.GetRelativePath(paths.RuntimeDirectory, executable), out var expected) || hash != expected) return false;
            foreach (var file in state.Files)
            {
                var path = Path.GetFullPath(Path.Combine(paths.RuntimeDirectory, file.Key));
                if (!path.StartsWith(paths.RuntimeDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(path) || await HashAsync(path, token).ConfigureAwait(false) != file.Value) return false;
            }
            return true;
        }
        catch (System.Text.Json.JsonException) { return false; }
    }
    private sealed record VerifiedRuntime(string ArchiveSha256, Dictionary<string, string> Files);
}
