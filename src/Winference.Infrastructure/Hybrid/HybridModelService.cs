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

    /// <summary>Keep this lease through the runtime session to prevent model writes or replacement on Windows.</summary>
    public static Task<IDisposable> OpenVerifiedReadLeaseAsync(HybridModelRegistration registration,
        CancellationToken cancellationToken = default) => OpenVerifiedReadLeaseCoreAsync(registration, null, cancellationToken);

    private static async Task<IDisposable> OpenVerifiedReadLeaseCoreAsync(HybridModelRegistration registration,
        IProgress<SetupProgress>? progress, CancellationToken token)
    {
        Validate(registration);
        token.ThrowIfCancellationRequested();
        var root = ValidateFolderPath(registration.ModelDirectory);
        var export = await OpenExportAsync(root, registration.ModelFiles.Select(file => file.Name).ToArray(), progress, token)
            .ConfigureAwait(false);
        var kept = false;
        try
        {
            if (export.ModelFiles.Count != registration.ModelFiles.Count ||
                !export.ModelFiles.OrderBy(file => file.Name, StringComparer.Ordinal)
                    .SequenceEqual(registration.ModelFiles.OrderBy(file => file.Name, StringComparer.Ordinal)))
                throw Changed();
            var configuration = export.Configuration ?? throw InvalidConfiguration();
            if (configuration.ContextLength != registration.ContextLength) throw InvalidConfiguration();
            if (registration.ModelType.Length != 0 && configuration.ModelType != registration.ModelType)
                throw InvalidConfiguration();
            kept = true;
            return export;
        }
        finally { if (!kept) export.Dispose(); }
    }

    /// <summary>Open, hash and structurally validate one hybrid export folder.</summary>
    private static async Task<ReadLease> OpenExportAsync(string root, IReadOnlyList<string>? expected,
        IProgress<SetupProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Directory.Exists(root))
            throw new RuntimeException("hybrid_model_missing", "The GPU + NPU model folder was not found. Choose its current location in Models.");
        var present = EnumerateTopLevel(root);
        var names = expected ?? present;
        if (names.Count == 0) throw Missing();
        if (names.Count > MaximumExportFiles)
            throw State("This GPU + NPU folder contains too many files for this build. Choose the export folder itself.");
        if (expected is not null && expected.Any(name => !present.Contains(name, StringComparer.OrdinalIgnoreCase))) throw Missing();
        if (!names.Contains("genai_config.json", StringComparer.OrdinalIgnoreCase))
            throw new RuntimeException("hybrid_model_configuration", "This folder is not a GPU + NPU export: genai_config.json is missing. Choose the export folder itself.");
        var lease = new ReadLease();
        try
        {
            foreach (var name in names)
            {
                token.ThrowIfCancellationRequested();
                if (!IsSafeFileName(name)) throw State("The GPU + NPU registration contains an unsafe file name. Choose its complete folder again.");
                var path = Path.Combine(root, name);
                var before = new FileInfo(path);
                if (!before.Exists) throw Missing();
                if ((before.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw State("The GPU + NPU folder contains a linked entry. Choose the original complete export.");
                // Retain every handle before hashing. FileAccess.Read prevents local mutations
                // while FileShare.ReadWrite allows native runtimes to map data without sharing conflicts.
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                lease.Handles.Add(new(name, stream, before.LastWriteTimeUtc));
            }
            var total = lease.Handles.Sum(file => file.Stream.Length);
            if (total > MaximumExportBytes)
                throw State("This GPU + NPU folder is larger than this build supports. Choose a smaller export.");
            long completed = 0;
            progress?.Report(new("VerifyingHybrid", "Verifying the GPU + NPU model. Its original files stay in place…", completed, total));
            var buffer = new byte[1024 * 1024];
            var files = new List<HybridModelFile>(lease.Handles.Count);
            foreach (var opened in lease.Handles)
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long reported = completed;
                int read;
                while ((read = await opened.Stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    hash.AppendData(buffer, 0, read);
                    completed += read;
                    if (completed - reported >= 16 * 1024 * 1024)
                    {
                        progress?.Report(new("VerifyingHybrid", "Verifying the GPU + NPU model. Its original files stay in place…", completed, total));
                        reported = completed;
                    }
                }
                files.Add(new(opened.Name, opened.Stream.Length, Convert.ToHexStringLower(hash.GetHashAndReset())));
                progress?.Report(new("VerifyingHybrid", "Verifying the GPU + NPU model. Its original files stay in place…", completed, total));
            }
            files.Sort((first, second) => string.CompareOrdinal(first.Name, second.Name));
            lease.ModelFiles = files;
            var configuration = lease.Handles.Single(file => string.Equals(file.Name, "genai_config.json", StringComparison.OrdinalIgnoreCase));
            if (configuration.Stream.Length is <= 0 or > MaximumConfigurationBytes) throw InvalidConfiguration();
            configuration.Stream.Position = 0;
            try
            {
                using var document = await JsonDocument.ParseAsync(configuration.Stream,
                    new JsonDocumentOptions { MaxDepth = 32 }, token).ConfigureAwait(false);
                lease.Configuration = ReadConfiguration(document.RootElement, files.Select(file => file.Name).ToArray());
            }
            catch (JsonException error) { throw InvalidConfiguration(error); }
            foreach (var opened in lease.Handles)
            {
                var after = new FileInfo(Path.Combine(root, opened.Name));
                if (!after.Exists || after.Length != opened.Stream.Length || after.LastWriteTimeUtc != opened.ModifiedUtc) throw Changed();
            }
            if (expected is null)
            {
                var remaining = EnumerateTopLevel(root);
                if (!remaining.OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(names.OrderBy(name => name, StringComparer.Ordinal)))
                    throw Changed();
            }
            token.ThrowIfCancellationRequested();
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    /// <summary>The export must be one flat folder of regular files plus Hugging Face's cache metadata.</summary>
    private static List<string> EnumerateTopLevel(string root)
    {
        for (DirectoryInfo? directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw State("Choose the original GPU + NPU folder rather than a linked folder.");
        var files = new List<string>();
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            var name = Path.GetFileName(path);
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw State("The hybrid folder contains a linked entry. Choose the original complete export.");
            if ((attributes & FileAttributes.Directory) == 0) { files.Add(name); continue; }
            // hf download creates this non-model metadata. Nested model folders
            // are refused because the worker copies only top-level files.
            if (name != ".cache") throw State("The GPU + NPU folder contains nested folders. Choose the folder that holds genai_config.json directly.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                var cacheAttributes = File.GetAttributes(entry);
                if (Path.GetFileName(entry) != "huggingface" || (cacheAttributes & FileAttributes.Directory) == 0 ||
                    (cacheAttributes & FileAttributes.ReparsePoint) != 0)
                    throw State("The GPU + NPU folder contains unrecognized cache entries. Existing files were kept.");
            }
        }
        return files;
    }

    private async Task<Registry> ReadRegistryAsync(CancellationToken token)
    {
        if (!File.Exists(_registryPath)) return new(1, []);
        try
        {
            if (new FileInfo(_registryPath).Length > MaximumRegistryBytes) throw State("The hybrid model list exceeds this build's supported size. Its original file was kept.");
            var registry = await AtomicJson.ReadAsync<Registry>(_registryPath, token).ConfigureAwait(false);
            if (registry is null || registry.SchemaVersion != 1 || registry.Models is null || registry.Models.Count > 128 ||
                registry.Models.Any(model => model is null) || registry.Models.Select(model => model.Id).Distinct().Count() != registry.Models.Count ||
                registry.Models.Select(model => model.Sha256).Distinct(StringComparer.Ordinal).Count() != registry.Models.Count)
                throw State("The hybrid model list is invalid. Its original file was kept.");
            foreach (var model in registry.Models) Validate(model);
            return registry;
        }
        catch (JsonException error) { throw State("The hybrid model list could not be read. Its original file was kept.", error); }
    }

    private static void Validate(HybridModelRegistration model)
    {
        if (model is null) throw State("The hybrid registration is missing. Existing files were kept.");
        if (model.SchemaVersion != 1 || model.Id == Guid.Empty || model.RegisteredUtc == default ||
            !IsFriendlyName(model.DisplayName) || model.ModelFiles is null || model.ModelFiles.Count is 0 or > MaximumExportFiles ||
            model.ModelFiles.Any(file => file is null || !IsSafeFileName(file.Name) || file.LengthBytes < 0 || !IsDigest(file.Sha256)) ||
            model.ModelFiles.Select(file => file.Name).Distinct(StringComparer.Ordinal).Count() != model.ModelFiles.Count ||
            !IsDigest(model.Sha256) || ComputeManifestDigest(model.ModelFiles) != model.Sha256 ||
            model.ContextLength <= MaximumOutputTokens || model.ContextLength > MaximumContextLength ||
            model.Profile != CreateProfile(model.RepositoryId ?? "", model.Revision ?? "", model.Sha256, model.ContextLength) ||
            (model.ModelType is { Length: > 0 } type && !IsModelType(type)))
            throw State("The hybrid registration does not match its own files. Choose its complete folder again; existing files were kept.");
        _ = ValidateFolderPath(model.ModelDirectory);
    }

    private static ModelProfile CreateProfile(string repositoryId, string revision, string sha256, int contextLength)
    {
        var family = repositoryId.Length == 0 ? "local/hybrid" : repositoryId;
        var version = revision.Length == 0 ? sha256 : revision;
        return new(family + "@" + version, $"hybrid-{sha256}-ryzenai1.7-c{contextLength}", "sha256:" + sha256,
            "user.Winference-Hybrid-" + sha256[..16], ExecutionMode.GpuNpu, contextLength, MaximumOutputTokens);
    }

    private static ExportConfiguration ReadConfiguration(JsonElement root, IReadOnlyList<string> names)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Object ||
            !model.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || !IsModelType(type.GetString()) ||
            !model.TryGetProperty("context_length", out var context) || !context.TryGetInt32(out var contextLength) || contextLength <= 0 ||
            !model.TryGetProperty("decoder", out var decoder) || decoder.ValueKind != JsonValueKind.Object ||
            !decoder.TryGetProperty("filename", out var filename) || !RefersToExportFile(filename, names) ||
            !decoder.TryGetProperty("session_options", out var session) || session.ValueKind != JsonValueKind.Object)
            throw InvalidConfiguration();
        var ryzen = RyzenOptions(session) ?? throw InvalidConfiguration();
        int? sequenceLimit = null;
        if (ryzen.TryGetProperty("hybrid_opt_max_seq_length", out var limit))
        {
            if (limit.ValueKind != JsonValueKind.String || !int.TryParse(limit.GetString(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var value) || value <= 0) throw InvalidConfiguration();
            sequenceLimit = value;
        }
        if (ryzen.TryGetProperty("external_data_file", out var external) && !RefersToExportFile(external, names))
            throw InvalidConfiguration();
        var effective = Math.Min(contextLength, sequenceLimit ?? contextLength);
        if (effective <= MaximumOutputTokens)
            throw new RuntimeException("hybrid_model_configuration",
                "This hybrid export declares a conversation limit too small for a reply. Choose a different export.");
        return new(type.GetString()!, Math.Min(effective, MaximumContextLength));
    }

    private static JsonElement? RyzenOptions(JsonElement session)
    {
        if (session.TryGetProperty("provider_options", out var providers) && providers.ValueKind == JsonValueKind.Array)
            foreach (var provider in providers.EnumerateArray())
                if (provider.ValueKind == JsonValueKind.Object && provider.TryGetProperty("RyzenAI", out var ryzen) &&
                    ryzen.ValueKind == JsonValueKind.Object) return ryzen;
        // Older Ryzen AI exports put the same options in config_entries.
        if (session.TryGetProperty("config_entries", out var entries) && entries.ValueKind == JsonValueKind.Object) return entries;
        return null;
    }

    private static bool RefersToExportFile(JsonElement value, IReadOnlyList<string> names) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { } name &&
        IsSafeFileName(name) && names.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Read the export's own identity from its README, falling back to the folder name.</summary>
    private static (string DisplayName, string RepositoryId) ReadIdentity(string? readme, string directory)
    {
        var fallback = FriendlyName(Path.GetFileName(directory));
        if (string.IsNullOrWhiteSpace(readme)) return (fallback, "");
        var lines = readme.Replace("\r\n", "\n").Split('\n');
        string? baseModel = null;
        var frontMatter = false;
        var awaitingBaseModel = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line == "---") { frontMatter = !frontMatter; if (!frontMatter) break; continue; }
            if (!frontMatter) continue;
            if (awaitingBaseModel)
            {
                if (line.StartsWith("- ", StringComparison.Ordinal)) { baseModel = line[2..].Trim().Trim('"', '\''); break; }
                awaitingBaseModel = false;
            }
            if (!line.StartsWith("base_model:", StringComparison.Ordinal)) continue;
            var inline = line["base_model:".Length..].Trim().Trim('"', '\'');
            if (inline.Length != 0) { baseModel = inline; break; }
            awaitingBaseModel = true;
        }
        var heading = lines.Select(line => line.Trim())
            .Where(line => line.StartsWith("# ", StringComparison.Ordinal))
            .Select(line => line[2..].Trim())
            .FirstOrDefault(name => name.Contains('/', StringComparison.Ordinal) && !name.Any(char.IsWhiteSpace));
        var repositoryId = heading ?? "";
        var display = baseModel is null ? "" : FriendlyName(baseModel[(baseModel.LastIndexOf('/') + 1)..]);
        return (display.Length == 0 ? fallback : display, repositoryId);
    }

    private static string ReadRevision(string directory)
    {
        try
        {
            var path = Path.Combine(directory, ".cache", "huggingface", "download", "genai_config.json.metadata");
            if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > MaximumMetadataBytes) return "";
            var first = File.ReadLines(path).FirstOrDefault()?.Trim();
            return first is { Length: 40 } && first.All(Uri.IsHexDigit) ? first.ToLowerInvariant() : "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return ""; }
    }

    private static string FriendlyName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            if (char.IsControl(character)) continue;
            builder.Append(character is '-' or '_' ? ' ' : character);
        }
        var name = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return name.Length == 0 ? "Hybrid model" : name[..Math.Min(name.Length, 128)];
    }

    private static bool IsKnownDefective(HybridModelRegistration model) =>
        IsKnownDefectiveName(model.RepositoryId ?? "") || IsKnownDefectiveName(model.ModelDirectory ?? "");

    private static bool IsKnownDefectiveName(string value)
    {
        var name = value.Replace('\\', '/').TrimEnd('/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        return KnownDefectiveExports.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsFriendlyName(string? value) => value is { Length: > 0 and <= 128 } && !value.Any(char.IsControl);

    private static bool IsModelType(string? value) => value is { Length: > 0 and <= 64 } && char.IsAsciiLetter(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');

    private static bool IsDigest(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSafeFileName(string? name) => name is { Length: > 0 and <= 255 } && name is not ("." or "..") &&
        name == Path.GetFileName(name) && !name.Any(char.IsControl) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static string ComputeManifestDigest(IEnumerable<HybridModelFile> files)
    {
        var canonical = string.Concat(files.OrderBy(file => file.Name, StringComparer.Ordinal)
            .Select(file => file.Name + "\t" + file.LengthBytes.ToString(CultureInfo.InvariantCulture) + "\t" + file.Sha256 + "\n"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string ValidateFolderPath(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.Length > 32767 || !Path.IsPathFullyQualified(folder) || folder.Any(char.IsControl))
            throw State("Choose a hybrid model folder using its complete local path.");
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (OperatingSystem.IsWindows())
        {
            var driveRoot = Path.GetPathRoot(fullPath);
            if (driveRoot is null || driveRoot.Length != 3 || driveRoot[1] != ':')
                throw State("Choose a hybrid folder on a local Windows drive.");
        }
        return fullPath;
    }

    private static bool SamePath(string first, string second) =>
        string.Equals(ValidateFolderPath(first), ValidateFolderPath(second), StringComparison.OrdinalIgnoreCase);
    private static RuntimeException State(string message, Exception? inner = null) => new("hybrid_model_state", message, inner);
    private static RuntimeException Missing() => new("hybrid_model_missing", "The hybrid folder is incomplete. Finish its download and choose the folder again.");
    private static RuntimeException Changed() => new("hybrid_model_changed", "The hybrid model files differ from the verified download. Finish the correct download and choose its folder again. Existing files were kept.");
    private static RuntimeException InvalidConfiguration(Exception? inner = null) => new("hybrid_model_configuration",
        "This folder does not declare a supported AMD GPU + NPU model. Choose the AMD hybrid export folder itself.", inner);
    private sealed record ExportConfiguration(string ModelType, int ContextLength);
    private sealed record Registry(int SchemaVersion, IReadOnlyList<HybridModelRegistration> Models);
    private sealed record Selection(int SchemaVersion, Guid Id, string Sha256, string ProfileId);
    private sealed record OpenedFile(string Name, FileStream Stream, DateTime ModifiedUtc);
    private sealed class ReadLease : IDisposable
    {
        public List<OpenedFile> Handles { get; } = [];
        public IReadOnlyList<HybridModelFile> ModelFiles { get; set; } = [];
        public ExportConfiguration? Configuration { get; set; }
        public string? ReadText(string name, int maximumBytes)
        {
            var opened = Handles.SingleOrDefault(file => string.Equals(file.Name, name, StringComparison.OrdinalIgnoreCase));
            if (opened is null || opened.Stream.Length <= 0 || opened.Stream.Length > maximumBytes) return null;
            opened.Stream.Position = 0;
            using var reader = new StreamReader(opened.Stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            return reader.ReadToEnd();
        }
        public void Dispose() { foreach (var file in Handles) file.Stream.Dispose(); Handles.Clear(); }
    }
}
