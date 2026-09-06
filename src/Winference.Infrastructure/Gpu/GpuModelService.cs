using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Winference.Core.Gpu;
using Winference.Core.Models;
using Winference.Core.Runtime;
using Winference.Core.Setup;
using Winference.Infrastructure.Models;
using Winference.Infrastructure.Setup;

namespace Winference.Infrastructure.Gpu;

/// <summary>Registers user-selected external GGUF files. No model is acquired or loaded here.</summary>
public sealed class GpuModelService
{
    private const long MaximumRegistryBytes = 64L * 1024 * 1024;
    private readonly string _registryPath;
    private readonly string _selectionPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GpuModelService(string dataRoot)
    {
        var directory = Path.Combine(Path.GetFullPath(dataRoot), "Gpu");
        _registryPath = Path.Combine(directory, "models.json");
        _selectionPath = Path.Combine(directory, "selection.json");
    }

    public async Task<IReadOnlyList<GpuModelRegistration>> ReadRegisteredAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return (await ReadRegistryAsync(cancellationToken).ConfigureAwait(false)).Models.ToArray(); }
        finally { _gate.Release(); }
    }

    /// <summary>Cheap presence check for read-only availability labels. It does not hash the file.</summary>
    public static bool IsFilePresent(GpuModelRegistration model)
    {
        ArgumentNullException.ThrowIfNull(model);
        try { return File.Exists(model.ModelPath) && new FileInfo(model.ModelPath).Length == model.LengthBytes; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    public async Task<GpuModelRegistration> RegisterAsync(string path, IProgress<SetupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = ValidatePath(path);
        progress?.Report(new("InspectingGguf", "Reading the selected GGUF model information…"));
        await using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var before = new FileInfo(fullPath);
        var length = file.Length;
        var modified = before.LastWriteTimeUtc;
        var metadata = await Task.Run(() => GgufMetadataReader.Read(file,
            Path.GetFileNameWithoutExtension(fullPath), cancellationToken), cancellationToken).ConfigureAwait(false);
        file.Position = 0;
        progress?.Report(new("VerifyingGguf", "Verifying the selected model. Existing files are kept in place…", 0, length));
        var digest = await HashAsync(file, progress, cancellationToken).ConfigureAwait(false);
        var after = new FileInfo(fullPath);
        if (file.Length != length || after.Length != length || after.LastWriteTimeUtc != modified)
            throw new RuntimeException("gpu_model_changed", "The model changed while it was being verified. Finish the download and choose it again.");
        var context = Math.Min(GpuCatalogModel.MaximumProfileContextLength,
            metadata.MaximumContextLength ?? GpuCatalogModel.MaximumProfileContextLength) / 256 * 256;
        var modelId = "gguf:" + digest;
        var profile = Profile(modelId, digest, context, Path.GetFileName(fullPath));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadRegistryAsync(cancellationToken).ConfigureAwait(false);
            var prior = current.Models.FirstOrDefault(item => item.Sha256 == digest);
            var registration = new GpuModelRegistration(prior?.Id ?? Guid.NewGuid(), fullPath, length, digest,
                metadata, profile, DateTimeOffset.UtcNow);
            Validate(registration);
            var models = current.Models.Where(item => item.Id != registration.Id).Append(registration).ToArray();
            if (models.Length > 128) throw new RuntimeException("gpu_model_limit", "This build supports up to 128 registered GPU models.");
            var snapshot = new Registry(1, models);
            if (JsonSerializer.SerializeToUtf8Bytes(snapshot, AtomicJson.Options).LongLength > MaximumRegistryBytes)
                throw new RuntimeException("gpu_model_limit", "Registered model metadata exceeds this build's supported size. Existing registrations were kept.");
            await AtomicJson.WriteAsync(_registryPath, snapshot, cancellationToken).ConfigureAwait(false);
            progress?.Report(new("GgufRegistered", "Model files verified and registered. No inference has started."));
            return registration;
        }
        finally { _gate.Release(); }
    }
}
