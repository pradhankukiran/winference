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

    public async Task<GpuModelRegistration?> ReadRememberedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_selectionPath)) return null;
            if (new FileInfo(_selectionPath).Length > 16_384) throw State("The saved GPU selection is invalid. Choose a registered model again.");
            var selection = await AtomicJson.ReadAsync<Selection>(_selectionPath, cancellationToken).ConfigureAwait(false);
            if (selection is null || selection.SchemaVersion != 1 || selection.Id == Guid.Empty)
                throw State("The saved GPU selection is invalid. Choose a registered model again.");
            var registration = (await ReadRegistryAsync(cancellationToken).ConfigureAwait(false)).Models
                .SingleOrDefault(item => item.Id == selection.Id);
            if (registration is null || registration.Sha256 != selection.Sha256 || registration.Profile.ProfileId != selection.ProfileId)
                throw State("The saved GPU selection does not match its registered files. Choose the model again.");
            return registration;
        }
        catch (JsonException error) { throw State("The saved GPU selection could not be read. Existing state was preserved.", error); }
        finally { _gate.Release(); }
    }

    /// <summary>The app calls this only after the descriptor's broker has reached verified Ready.</summary>
    public async Task RememberAsync(GpuModelRegistration registration, CancellationToken cancellationToken = default)
    {
        Validate(registration);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var saved = (await ReadRegistryAsync(cancellationToken).ConfigureAwait(false)).Models
                .SingleOrDefault(item => item.Id == registration.Id);
            if (saved is null || saved.Sha256 != registration.Sha256 || saved.Profile != registration.Profile ||
                !SamePath(saved.ModelPath, registration.ModelPath))
                throw State("The registered GPU model changed. Choose it again before saving the selection.");
            await AtomicJson.WriteAsync(_selectionPath, new Selection(1, registration.Id,
                registration.Sha256, registration.Profile.ProfileId), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public static async Task<RuntimeAvailability> VerifyAsync(GpuModelRegistration registration,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Validate(registration);
            if (!File.Exists(registration.ModelPath))
                return new(false, "The registered GGUF file is missing. Choose its new location in Models.", "gpu_model_missing");
            await using var file = new FileStream(registration.ModelPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length != registration.LengthBytes ||
                Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false)) != registration.Sha256)
                return new(false, "The selected GGUF file changed after registration. Choose it again to verify the new file.", "gpu_model_changed");
            return new(true, "The registered GGUF file matches its verified identity.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(false, "The selected GGUF file could not be read. Check its location and access, then retry.", "gpu_model_access"); }
    }
}
