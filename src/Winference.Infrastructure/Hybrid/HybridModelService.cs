using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Winference.Core.Hybrid;
using Winference.Core.Models;
using Winference.Core.Runtime;
using Winference.Core.Setup;
using Winference.Infrastructure.Setup;

namespace Winference.Infrastructure.Hybrid;

/// <summary>
/// Registers an AMD Ryzen AI hybrid export in place. The folder describes the
/// artifact: every top-level file is hashed and <c>genai_config.json</c> is read
/// structurally. No runtime or model acquisition occurs here.
/// </summary>
public sealed class HybridModelService
{
    /// <summary>The largest conversation this build runs on the hybrid worker, reply included.</summary>
    public const int MaximumContextLength = 2048;
    public const int MaximumOutputTokens = 256;
    private const long MaximumRegistryBytes = 4L * 1024 * 1024;
    private const long MaximumExportBytes = 32L * 1024 * 1024 * 1024;
    private const int MaximumExportFiles = 96;
    private const int MaximumConfigurationBytes = 1024 * 1024;
    private const int MaximumIdentityBytes = 256 * 1024;
    private const int MaximumMetadataBytes = 16 * 1024;

    // AMD publishes below-chance MMLU scores for these exports and the weights
    // reproduce that in AMD's own server. They stay visible but unselectable.
    // Every other export is judged by its own folder and configuration.
    private static readonly string[] KnownDefectiveExports =
    [
        "qwen2.5-0.5b-instruct-onnx-ryzenai-1.7-hybrid",
        "qwen2.5-coder-0.5b-instruct-onnx-ryzenai-1.7-hybrid",
        "qwen2.5-0.5b-instruct_rai_1.7.1_hybrid"
    ];

    public static string ExecutionStatus(HybridModelRegistration model) => IsKnownDefective(model)
        ? "Warning: AMD reported low benchmark scores for this 0.5B export."
        : "Files verified; device readiness is checked when loaded.";
    public static bool CanExecute(HybridModelRegistration model) => true;

    private readonly string _registryPath;
    private readonly string _selectionPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HybridModelService(string dataRoot)
    {
        var directory = Path.Combine(Path.GetFullPath(dataRoot), "Hybrid");
        _registryPath = Path.Combine(directory, "models.json");
        _selectionPath = Path.Combine(directory, "selection.json");
    }

    public async Task<IReadOnlyList<HybridModelRegistration>> ReadRegisteredAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return (await ReadRegistryAsync(cancellationToken).ConfigureAwait(false)).Models.ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<HybridModelRegistration> RegisterAsync(string folder, IProgress<SetupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var directory = ValidateFolderPath(folder);
        progress?.Report(new("InspectingHybrid", "Checking the selected GPU + NPU model folder…"));
        cancellationToken.ThrowIfCancellationRequested();
        HybridModelRegistration candidate;
        // The whole folder is read under one set of read-only handles, so the
        // manifest, the configuration and the identity all describe the same bytes.
        using (var export = await OpenExportAsync(directory, null, progress, cancellationToken).ConfigureAwait(false))
        {
            var configuration = export.Configuration ?? throw InvalidConfiguration();
            var files = export.ModelFiles;
            var identity = ReadIdentity(export.ReadText("README.md", MaximumIdentityBytes), directory);
            var revision = ReadRevision(directory);
            var sha256 = ComputeManifestDigest(files);
            candidate = new HybridModelRegistration(Guid.NewGuid(), directory, sha256, identity.DisplayName,
                identity.RepositoryId, revision, files, CreateProfile(identity.RepositoryId, revision, sha256, configuration.ContextLength),
                DateTimeOffset.UtcNow, ModelType: configuration.ModelType);
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadRegistryAsync(cancellationToken).ConfigureAwait(false);
            var prior = current.Models.FirstOrDefault(model => model.Sha256 == candidate.Sha256);
            var registration = candidate with { Id = prior?.Id ?? candidate.Id };
            var models = current.Models.Where(model => model.Id != registration.Id).Append(registration).ToArray();
            if (models.Length > 128) throw State("This build supports up to 128 registered GPU + NPU models.");
            var snapshot = new Registry(1, models);
            if (JsonSerializer.SerializeToUtf8Bytes(snapshot, AtomicJson.Options).LongLength > MaximumRegistryBytes)
                throw State("The GPU + NPU model list exceeds this build's supported size. Existing registrations were kept.");
            await AtomicJson.WriteAsync(_registryPath, snapshot, cancellationToken).ConfigureAwait(false);
            progress?.Report(new("HybridRegistered", "GPU + NPU model files verified and registered. No inference has started."));
            return registration;
        }
        finally { _gate.Release(); }
    }

    public async Task<HybridModelRegistration?> ReadRememberedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_selectionPath)) return null;
            if (new FileInfo(_selectionPath).Length > 16_384) throw State("The saved hybrid selection is invalid. Choose the model again.");
            var selection = await AtomicJson.ReadAsync<Selection>(_selectionPath, cancellationToken).ConfigureAwait(false);
            if (selection is null || selection.SchemaVersion != 1 || selection.Id == Guid.Empty)
                throw State("The saved GPU + NPU selection is invalid. Choose the model again.");
            var registration = (await ReadRegistryAsync(cancellationToken).ConfigureAwait(false)).Models
                .SingleOrDefault(model => model.Id == selection.Id);
            if (registration is null || registration.Sha256 != selection.Sha256 || registration.Profile.ProfileId != selection.ProfileId)
                throw State("The saved GPU + NPU selection does not match its registered model. Choose the model again; existing files were kept.");
            return registration;
        }
        catch (JsonException error) { throw State("The saved GPU + NPU selection could not be read. Existing files were kept.", error); }
        finally { _gate.Release(); }
    }

    /// <summary>Call only after the descriptor's broker has reached proved Ready.</summary>
    public async Task RememberAsync(HybridModelRegistration registration, CancellationToken cancellationToken = default)
    {
        Validate(registration);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var saved = (await ReadRegistryAsync(cancellationToken).ConfigureAwait(false)).Models
                .SingleOrDefault(model => model.Id == registration.Id);
            if (saved is null || saved.Sha256 != registration.Sha256 || saved.Profile != registration.Profile ||
                !SamePath(saved.ModelDirectory, registration.ModelDirectory))
                throw State("The GPU + NPU model registration changed. Choose it again before saving the selection.");
            await AtomicJson.WriteAsync(_selectionPath, new Selection(1, registration.Id, registration.Sha256,
                registration.Profile.ProfileId), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public static async Task<RuntimeAvailability> VerifyAsync(HybridModelRegistration registration,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var lease = await OpenVerifiedReadLeaseAsync(registration, cancellationToken).ConfigureAwait(false);
            return new(true, "The GPU + NPU model matches its verified files and configuration.");
        }
        catch (RuntimeException error) { return new(false, error.Message, error.Code); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        { return new(false, "The GPU + NPU model folder could not be read. Check its location and access, then retry.", "hybrid_model_access"); }
    }
}
