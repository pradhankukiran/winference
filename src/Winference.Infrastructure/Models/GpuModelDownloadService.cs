using System.Collections.Concurrent;
using Winference.Core.Downloads;
using Winference.Core.Gpu;
using Winference.Core.Runtime;
using Winference.Core.Setup;
using Winference.Infrastructure.Gpu;
using Winference.Infrastructure.Setup;

namespace Winference.Infrastructure.Models;

/// <summary>
/// Downloads one approved GGUF into an app-owned folder, verifies its pinned
/// byte length and SHA-256, and only then registers it for GPU execution.
/// Partial bytes stay in a single <c>.part</c> file so a retry can resume.
/// </summary>
public sealed class GpuModelDownloadService
{
    public static readonly DownloadCapabilities Capabilities = new(AppOwned: true, CanCancel: true, CanResume: true);
    private const long VerificationHeadroomBytes = 128L * 1024 * 1024;
    private readonly string _dataRoot;
    private readonly GpuModelService _registrations;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public GpuModelDownloadService(string dataRoot, GpuModelService registrations, HttpClient? http = null)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        _registrations = registrations ?? throw new ArgumentNullException(nameof(registrations));
        _http = http ?? new HttpClient(new HttpClientHandler { MaxAutomaticRedirections = 5 })
        { Timeout = TimeSpan.FromMinutes(30) };
    }

    /// <summary>Read-only destination calculation. It creates no folder and inspects no model.</summary>
    public string DestinationFor(GpuCatalogModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Path.Combine(_dataRoot, "Models", "Gpu", model.Id, model.FileName);
    }

    public async Task<GpuModelRegistration> DownloadAsync(GpuCatalogModel model, IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.Validate();
        var gate = _gates.GetOrAdd(model.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            progress?.Report(new(DownloadState.Queued, $"Preparing {model.DisplayName}…"));
            var destination = DestinationFor(model);
            EnsureOwnedDirectory();
            EnsureNoLinkedParents(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            EnsureNoLinkedParents(destination);
            if (Directory.Exists(destination) || new FileInfo(destination).LinkTarget is not null)
                throw new RuntimeException("download_destination", "The app-owned download location contains an unexpected entry. It was left alone; choose another Winference data folder.");

            if (File.Exists(destination))
            {
                if (new FileInfo(destination).Length != model.LengthBytes)
                    throw new RuntimeException("download_conflict", "An app-owned file already exists at the download location with a different size. It was left alone. Remove or move that file before retrying.");
                progress?.Report(new(DownloadState.Verifying, $"Verifying the existing {model.DisplayName} download…", 0, model.LengthBytes));
                if (await PinnedRuntimeAcquisition.HashAsync(destination, cancellationToken).ConfigureAwait(false) != model.Sha256)
                    throw new RuntimeException("download_integrity", "An existing app-owned file does not match this catalog's verified checksum. It was left alone. Remove or move that file before retrying.");
                return await RegisterAsync(model, destination, progress, cancellationToken).ConfigureAwait(false);
            }

            var partial = destination + ".part";
            var existingPartial = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (existingPartial > model.LengthBytes)
                throw new RuntimeException("download_conflict", "The app-owned partial download is larger than the approved file. It was left alone. Remove or move it before retrying.");
            CheckFreeSpace(destination, model.LengthBytes - existingPartial);
            progress?.Report(new(DownloadState.Preparing, existingPartial > 0
                ? $"Resuming {model.DisplayName}…"
                : $"Starting the {model.DisplayName} download…", existingPartial, model.LengthBytes));
            try
            {
                await PinnedRuntimeAcquisition.DownloadVerifiedAsync(_http, model.Source, destination,
                    model.LengthBytes, model.Sha256, new SetupProgressSink(progress), cancellationToken,
                    "model file").ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                progress?.Report(new(DownloadState.Cancelled, "Download cancelled. Verified bytes were kept for a later retry."));
                throw;
            }
            catch (RuntimeException error)
            {
                progress?.Report(new(DownloadState.Failed, error.Message));
                throw;
            }
            progress?.Report(new(DownloadState.Verifying, $"Verifying {model.DisplayName} before registration…", model.LengthBytes, model.LengthBytes));
            return await RegisterAsync(model, destination, progress, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
