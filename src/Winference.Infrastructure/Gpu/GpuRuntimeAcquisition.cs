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
}
