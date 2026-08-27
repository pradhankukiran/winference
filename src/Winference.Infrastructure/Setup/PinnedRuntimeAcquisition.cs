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
}
