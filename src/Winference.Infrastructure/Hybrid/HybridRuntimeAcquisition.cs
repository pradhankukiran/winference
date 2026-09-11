using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Winference.Core.Hybrid;
using Winference.Core.Runtime;
using Winference.Core.Setup;
using Winference.Infrastructure.Setup;

namespace Winference.Infrastructure.Hybrid;

/// <summary>
/// Explicit acquisition of the complete native Ryzen AI 1.7.0 runtime payload.
/// The immutable release pin is from lemonade-sdk/ryzenai-server v1.7.0.
/// Construction and inspection never fetch files or execute a runtime.
/// </summary>
public sealed class HybridRuntimeAcquisition
{
    public const string ReleaseTag = "v1.7.0";
    public const string AssetName = "ryzenai-server.zip";
    public const string CacheAssetName = "ryzenai-server-v1.7.0.zip";
    public const string ArchiveSha256 = "1b263dcd7c71bb51819dcda569a10647733da153c53891cfb12d2277466971cf";
    public const long ArchiveBytes = 569_142_342;
    public static readonly Uri DownloadUri = new($"https://github.com/lemonade-sdk/ryzenai-server/releases/download/{ReleaseTag}/{AssetName}");
    private const long MaximumExtractedBytes = 3_000_000_000;
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".sys", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".js", ".msi", ".scr", ".cpl", ".ocx" };
    private readonly string _archivePath;
    private readonly HttpClient _downloads;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HybridRuntimeAcquisition(string dataRoot, HttpClient? downloads = null)
    {
        var root = Path.GetFullPath(dataRoot);
        var directory = Path.Combine(root, "Runtimes", "ryzenai-server-1.7.0-win-x64");
        ExpectedInstallation = new(directory, Path.Combine(directory, "ryzenai-server.exe"), ReleaseTag,
            ArchiveSha256, Path.Combine(directory, "pin.json"));
        _archivePath = Path.Combine(root, "Downloads", CacheAssetName);
        _downloads = downloads ?? new HttpClient(new HttpClientHandler { MaxAutomaticRedirections = 5 })
            { Timeout = TimeSpan.FromSeconds(60) };
    }

    public HybridRuntimeInstallation ExpectedInstallation { get; }

    public async Task<HybridRuntimeStatus> InspectAsync(CancellationToken cancellationToken = default)
    {
        var verified = await VerifyAsync(ExpectedInstallation, cancellationToken).ConfigureAwait(false);
        return verified
            ? new(true, "GPU + NPU component installed. Device and model readiness are checked when you load a model.", 0, ExpectedInstallation)
            : new(false, "The verified GPU + NPU component is missing or changed. Review its download before loading a GPU + NPU model.", ArchiveBytes, null);
    }

    /// <summary>Call only after visible user confirmation of this component download.</summary>
    public async Task<HybridRuntimeInstallation> AcquireAsync(IProgress<SetupProgress> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await VerifyAsync(ExpectedInstallation, cancellationToken).ConfigureAwait(false)) return ExpectedInstallation;
            await PinnedRuntimeAcquisition.DownloadVerifiedAsync(_downloads, DownloadUri, _archivePath,
                ArchiveBytes, ArchiveSha256, progress, cancellationToken).ConfigureAwait(false);
            var target = ExpectedInstallation.RuntimeDirectory;
            var staging = target + ".staging-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staging);
            try
            {
                progress.Report(new("InstallingRuntime", "Preparing the verified GPU + NPU component…"));
                var files = new List<RuntimeFile>();
                using (var archive = ZipFile.OpenRead(_archivePath))
                {
                    var entries = PayloadEntries(archive);
                    foreach (var (entry, relative) in entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var destination = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
                        if (!Inside(staging, destination)) throw InvalidArchive();
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        await using var source = entry.Open();
                        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                            FileShare.None, 81920, FileOptions.Asynchronous);
                        await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    foreach (var (_, relative) in entries)
                    {
                        var path = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                        files.Add(new(relative, await HashFileAsync(path, cancellationToken).ConfigureAwait(false), new FileInfo(path).Length));
                    }
                }
                if (!files.Any(file => file.Path == "ryzenai-server.exe") || !HasRequiredPayload(files.Select(file => file.Path)))
                    throw new RuntimeException("hybrid_runtime_files", "The GPU + NPU archive does not contain its required Windows Ryzen AI files. It was not installed.");
                // Preserve every payload file, including AMD redistribution terms and third-party notices.
                await AtomicJson.WriteAsync(Path.Combine(staging, "pin.json"), new RuntimePin(1, ReleaseTag,
                    AssetName, ArchiveSha256, DownloadUri.ToString(), files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray()), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string? previous = null;
                if (Directory.Exists(target))
                {
                    var oldPin = Path.Combine(target, "pin.json");
                    RuntimePin? prior;
                    try { prior = File.Exists(oldPin) ? await AtomicJson.ReadAsync<RuntimePin>(oldPin, cancellationToken).ConfigureAwait(false) : null; }
                    catch (JsonException) { prior = null; }
                    if (prior is null || prior.SchemaVersion != 1 || prior.ReleaseTag != ReleaseTag || prior.ZipSha256 != ArchiveSha256)
                        throw new RuntimeException("hybrid_runtime_conflict", "The GPU + NPU component location contains unrecognized files. They were preserved. Choose a clean app-data location before repair.");
                    previous = target + ".previous-" + Guid.NewGuid().ToString("N");
                    Directory.Move(target, previous);
                }
                try { Directory.Move(staging, target); }
                catch
                {
                    if (previous is not null && !Directory.Exists(target)) Directory.Move(previous, target);
                    throw;
                }
                progress.Report(new("RuntimeInstalled", "GPU + NPU component files installed. No model has been loaded."));
                return ExpectedInstallation;
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Read-only trust check. Hashes the original pinned ZIP and compares every extracted
    /// file against its original ZIP entry, so editing pin.json cannot bless changed code.
    /// No runtime, helper or device probe is executed.
    /// </summary>
    public static async Task<bool> VerifyAsync(HybridRuntimeInstallation installation,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (installation.ReleaseTag != ReleaseTag || installation.ArchiveSha256 != ArchiveSha256 ||
                !Path.IsPathFullyQualified(installation.RuntimeDirectory) ||
                !SamePath(installation.ExecutablePath, Path.Combine(installation.RuntimeDirectory, "ryzenai-server.exe")) ||
                !SamePath(installation.ManifestPath, Path.Combine(installation.RuntimeDirectory, "pin.json")) ||
                !File.Exists(installation.ManifestPath) || new FileInfo(installation.ManifestPath).Length > 4_194_304)
                return false;
            var root = Directory.GetParent(installation.RuntimeDirectory)?.Parent?.FullName;
            if (root is null) return false;
            var archivePath = Path.Combine(root, "Downloads", CacheAssetName);
            if (!File.Exists(archivePath)) return false;
            await using var archiveFile = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous);
            if (archiveFile.Length != ArchiveBytes ||
                Convert.ToHexStringLower(await SHA256.HashDataAsync(archiveFile, cancellationToken).ConfigureAwait(false)) != ArchiveSha256)
                return false;
            archiveFile.Position = 0;
            var pin = await AtomicJson.ReadAsync<RuntimePin>(installation.ManifestPath, cancellationToken).ConfigureAwait(false);
            if (pin is null || pin.SchemaVersion != 1 || pin.ReleaseTag != ReleaseTag || pin.Asset != AssetName ||
                pin.ZipSha256 != ArchiveSha256 || pin.Files is null || pin.Files.Count == 0)
                return false;
            using var archive = new ZipArchive(archiveFile, ZipArchiveMode.Read, leaveOpen: true);
            var entries = PayloadEntries(archive);
            if (pin.Files.Count != entries.Count || !entries.Any(item => item.Relative == "ryzenai-server.exe") ||
                !HasRequiredPayload(entries.Select(item => item.Relative))) return false;
            var listed = new Dictionary<string, RuntimeFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in pin.Files)
                if (file is null || !SafeRelative(file.Path) || file.Sha256 is not { Length: 64 } ||
                    file.Sha256.Any(character => !Uri.IsHexDigit(character)) || !listed.TryAdd(file.Path, file)) return false;
            foreach (var (entry, relative) in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.GetFullPath(Path.Combine(installation.RuntimeDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!Inside(installation.RuntimeDirectory, path) || !listed.TryGetValue(relative, out var file) ||
                    !File.Exists(path) || new FileInfo(path).Length != entry.Length || file.Size != entry.Length ||
                    IsReparsePoint(path, installation.RuntimeDirectory)) return false;
                await using var original = entry.Open();
                var expected = Convert.ToHexStringLower(await SHA256.HashDataAsync(original, cancellationToken).ConfigureAwait(false));
                if (file.Sha256 != expected || await HashFileAsync(path, cancellationToken).ConfigureAwait(false) != expected) return false;
            }
            foreach (var file in EnumerateRuntimeFiles(installation.RuntimeDirectory))
            {
                if (SamePath(file, installation.ManifestPath)) continue;
                if (IsReparsePoint(file, installation.RuntimeDirectory)) return false;
                var relative = Path.GetRelativePath(installation.RuntimeDirectory, file).Replace('\\', '/');
                if (!listed.ContainsKey(relative) && ExecutableExtensions.Contains(Path.GetExtension(file))) return false;
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException or RuntimeException)
        { return false; }
    }
}
