using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Winference.Core.Gpu;
using Winference.Core.Runtime;
using Winference.Core.Setup;
using Winference.Infrastructure.Setup;

namespace Winference.Infrastructure.Gpu;

/// <summary>
/// Implements explicit app-owned acquisition. Construction/inspection never download or execute a runtime.
/// The pin is copied from ODS ods/config/backends/amd.json at revision 72e59cdc665fc576d300061cabb5b980cbbaf1da.
/// Publication manifest uses the retained ODS standalone helper's pin.json schema 1.
/// </summary>
public sealed class GpuRuntimeAcquisition
{
    public const string ReleaseTag = "b9014";
    public const string AssetName = "llama-b9014-bin-win-vulkan-x64.zip";
    public const string ArchiveSha256 = "6cd4bc7a44256e674458b0c5ea2ae3461dca29ee87876c8d410ecc78652a3b0f";
    public const long ArchiveBytes = 33_541_404;
    public static readonly Uri DownloadUri = new($"https://github.com/ggml-org/llama.cpp/releases/download/{ReleaseTag}/{AssetName}");
    private const long MaximumExtractedBytes = 1_000_000_000;
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".sys", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".js", ".msi", ".scr", ".cpl", ".ocx" };
    private readonly string _archivePath;
    private readonly HttpClient _downloads;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GpuRuntimeAcquisition(string dataRoot, HttpClient? downloads = null)
    {
        var root = Path.GetFullPath(dataRoot);
        var directory = Path.Combine(root, "Runtimes", ReleaseTag + "-win-vulkan-x64");
        ExpectedInstallation = new(directory, Path.Combine(directory, "llama-server.exe"), ReleaseTag,
            ArchiveSha256, Path.Combine(directory, "pin.json"));
        _archivePath = Path.Combine(root, "Downloads", AssetName);
        _downloads = downloads ?? new HttpClient(new HttpClientHandler { MaxAutomaticRedirections = 5 })
            { Timeout = TimeSpan.FromSeconds(60) };
    }

    public GpuRuntimeInstallation ExpectedInstallation { get; }

    public async Task<GpuRuntimeStatus> InspectAsync(CancellationToken cancellationToken = default)
    {
        var verified = await VerifyAsync(ExpectedInstallation, cancellationToken).ConfigureAwait(false);
        return verified
            ? new(true, "GPU component installed. Device and model readiness are checked when you load a model.", 0, ExpectedInstallation)
            : new(false, "The verified GPU component is missing or changed. Review its download before loading a GPU model.", ArchiveBytes, null);
    }

    /// <summary>Call only after visible user confirmation of this component download.</summary>
    public async Task<GpuRuntimeInstallation> AcquireAsync(IProgress<SetupProgress> progress,
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
                progress.Report(new("InstallingRuntime", "Preparing the verified GPU component…"));
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
                if (!files.Any(file => file.Path == "llama-server.exe") || !files.Any(file => file.Path == "ggml-vulkan.dll"))
                    throw new RuntimeException("gpu_runtime_files", "The GPU archive does not contain its required Windows Vulkan files. It was not installed.");
                // Keep every payload file, including original licenses and notices.
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
                        throw new RuntimeException("gpu_runtime_conflict", "The GPU component location contains unrecognized files. They were preserved. Choose a clean app-data location before repair.");
                    previous = target + ".previous-" + Guid.NewGuid().ToString("N");
                    Directory.Move(target, previous);
                }
                try { Directory.Move(staging, target); }
                catch
                {
                    if (previous is not null && !Directory.Exists(target)) Directory.Move(previous, target);
                    throw;
                }
                progress.Report(new("RuntimeInstalled", "GPU component files installed. No model has been loaded."));
                return ExpectedInstallation;
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
        }
        finally { _gate.Release(); }
    }
}
