using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Winference.Core.Models;
using Winference.Core.Runtime;
using Winference.Infrastructure.Gpu;
using Winference.Infrastructure.Setup;

namespace Winference.Infrastructure.Models;

/// <summary>
/// Removes only canonical app-owned catalog files. The caller stops the selected
/// broker before committing; locked files independently prevent their removal.
/// </summary>
public sealed class ModelRemovalService
{
    private const long MaximumStateBytes = 64L * 1024 * 1024;
    private const string StoreMarker = "Winference model store, schema 1";
    private readonly SetupPaths _paths;
    private readonly GpuModelService _gpuModels;
    private readonly GpuModelCatalog _gpuCatalog;
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, ModelRemovalPlan> _plans = new();

    public ModelRemovalService(SetupPaths paths, GpuModelService gpuModels, GpuModelCatalog gpuCatalog)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _gpuModels = gpuModels ?? throw new ArgumentNullException(nameof(gpuModels));
        _gpuCatalog = gpuCatalog ?? throw new ArgumentNullException(nameof(gpuCatalog));
        _root = Path.GetFullPath(paths.DataRoot);
    }

    /// <summary>Conservative, read-only availability check; it never hashes a model.</summary>
    public bool CanRemove(LibraryProfile profile)
    {
        try
        {
            var target = Resolve(profile);
            if (target is null) return false;
            ValidateTarget(target);
            return target.Names.Any(name => File.Exists(Path.Combine(target.Directory, name))) ||
                profile.ExecutionMode == ExecutionMode.GpuOnly && profile.InstalledPath is not null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException or RuntimeException)
        { return false; }
    }

    public async Task<ModelRemovalPlan> PlanAsync(LibraryProfile profile, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var plan = await SnapshotAsync(profile, token).ConfigureAwait(false);
            if (plan.Files.Count == 0 && plan.GpuRegistrationId is null)
                throw Failure("removal_empty", "No app-owned files remain for this model.");
            if (_plans.Count >= 128) _plans.Clear();
            _plans[plan.Id] = plan;
            return plan;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw Failure("removal_access", "The model files could not be checked. Close any program using them and retry.", error); }
        finally { _gate.Release(); }
    }

    public async Task<ModelRemovalResult> RemoveAsync(ModelRemovalPlan plan, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        var leases = new List<FileStream>();
        try
        {
            if (!_plans.TryGetValue(plan.Id, out var issued) || !ReferenceEquals(issued, plan))
                throw Failure("removal_stale", "Review this model again before removing it.");
            var current = await SnapshotAsync(plan.Profile, token, leases).ConfigureAwait(false);
            if (!SameSnapshot(plan, current))
                throw Failure("removal_changed", "The model changed after the removal review. Nothing was removed; review it again.");
            IReadOnlyList<StateChange> changes;
            try { changes = await MetadataChangesAsync(plan, token).ConfigureAwait(false); }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
            { throw State(error); }
            token.ThrowIfCancellationRequested();

            // Commit completes or rolls back as one serialized operation. A late
            // cancellation must not abandon a partially moved package.
            var stagingParent = Path.Combine(_root, "Models", "Removing");
            EnsureSafePath(stagingParent);
            Directory.CreateDirectory(stagingParent);
            EnsureSafePath(stagingParent);
            var staging = Path.Combine(stagingParent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var moved = new List<ModelRemovalFile>();
            var attempted = new List<StateChange>();
            try
            {
                foreach (var file in plan.Files)
                {
                    EnsureSafePath(plan.ModelDirectory);
                    EnsureSafePath(staging);
                    var source = Path.Combine(plan.ModelDirectory, file.Name);
                    EnsureSafePath(source);
                    var info = new FileInfo(source);
                    if (!info.Exists || info.Length != file.LengthBytes || info.LastWriteTimeUtc != file.LastWriteUtc)
                        throw Failure("removal_changed", "The model changed during removal. Its files were preserved; review it again.");
                    File.Move(source, Path.Combine(staging, file.Name));
                    moved.Add(file);
                }
                foreach (var change in changes)
                {
                    attempted.Add(change);
                    await ApplyAsync(change.Path, change.After).ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                var recovered = true;
                foreach (var change in attempted.AsEnumerable().Reverse())
                    try { await RestoreAsync(change).ConfigureAwait(false); } catch { recovered = false; }
                foreach (var file in moved.AsEnumerable().Reverse())
                    try
                    {
                        EnsureSafePath(plan.ModelDirectory);
                        EnsureSafePath(staging);
                        File.Move(Path.Combine(staging, file.Name), Path.Combine(plan.ModelDirectory, file.Name));
                    }
                    catch { recovered = false; }
                TryRemoveEmpty(staging);
                if (!recovered)
                    throw Failure("removal_recovery", $"Removal could not finish. Remaining files were preserved at {staging}. Close programs using them before retrying.", error);
                throw Failure("removal_access", "Removal could not finish. Files and the previous model choices were restored. Close programs using this model and retry.", error);
            }

            _plans.TryRemove(plan.Id, out _);
            foreach (var lease in leases) await lease.DisposeAsync().ConfigureAwait(false);
            leases.Clear();
            long removedBytes = 0;
            var removedFiles = 0;
            foreach (var file in moved)
            {
                try
                {
                    var path = Path.Combine(staging, file.Name);
                    EnsureSafePath(path);
                    File.Delete(path);
                    removedBytes += file.LengthBytes;
                    ++removedFiles;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or RuntimeException) { }
            }
            TryRemoveEmpty(staging);
            TryRemoveEmpty(plan.ModelDirectory);
            var pending = Directory.Exists(staging);
            return new(removedBytes, removedFiles, pending, pending ? staging : null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw Failure("removal_access", "The app-owned model could not be removed. Close programs using it and retry. Existing chats were kept.", error); }
        finally
        {
            foreach (var lease in leases) await lease.DisposeAsync().ConfigureAwait(false);
            _gate.Release();
        }
    }

    private async Task<ModelRemovalPlan> SnapshotAsync(LibraryProfile profile, CancellationToken token, List<FileStream>? leases = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var target = Resolve(profile) ?? throw Failure("removal_external", "These files are stored outside Winference's model folder and will be kept.");
        ValidateTarget(target);
        Guid? registrationId = null;
        if (profile.ExecutionMode == ExecutionMode.GpuOnly)
        {
            var registration = (await _gpuModels.ReadRegisteredAsync(token).ConfigureAwait(false))
                .SingleOrDefault(model => model.Sha256 == target.Digest);
            if (registration is not null)
            {
                if (!SamePath(registration.ModelPath, Path.Combine(target.Directory, target.Names[0])) ||
                    registration.Profile.ProfileId != profile.ProfileId)
                    throw Failure("removal_external", "The registered GPU file is outside this app-owned download. It was kept.");
                registrationId = registration.Id;
            }
        }
        var files = new List<ModelRemovalFile>();
        foreach (var name in target.Names.OrderBy(value => value, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(target.Directory, name);
            EnsureSafePath(path);
            if (!File.Exists(path)) continue;
            var before = new FileInfo(path);
            var length = before.Length;
            var modified = before.LastWriteTimeUtc;
            FileStream? input = new FileStream(path, FileMode.Open, FileAccess.Read,
                leases is null ? FileShare.Read : FileShare.Read | FileShare.Delete, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            try
            {
                var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
                var after = new FileInfo(path);
                if (input.Length != length || after.Length != length || after.LastWriteTimeUtc != modified)
                    throw Failure("removal_changed", "The model changed while it was checked. Review it again; nothing was removed.");
                files.Add(new(name, length, modified, digest));
                if (leases is not null) { leases.Add(input); input = null; }
            }
            finally { if (input is not null) await input.DisposeAsync().ConfigureAwait(false); }
        }
        return new(Guid.NewGuid(), profile.ProfileId, profile.Artifact.Id, profile.ExecutionMode,
            target.Name, target.ModelId, target.Directory, new ReadOnlyCollection<ModelRemovalFile>(files),
            files.Sum(file => file.LengthBytes), profile, registrationId);
    }

    private Target? Resolve(LibraryProfile profile)
    {
        if (profile.ExecutionMode == ExecutionMode.GpuOnly)
        {
            var entry = _gpuCatalog.Models.SingleOrDefault(model => model.ArtifactId == profile.Artifact.Id);
            if (entry is null || profile.ProfileId != entry.ProfileId) return null;
            var directory = Path.Combine(_root, "Models", "Gpu", entry.Id);
            var destination = Path.Combine(directory, entry.FileName);
            if (profile.InstalledPath is not null && !SamePath(profile.InstalledPath, destination)) return null;
            return new(directory, entry.DisplayName, "gguf:" + entry.Sha256, [entry.FileName, entry.FileName + ".part"], entry.Sha256, false);
        }
        if (profile.ExecutionMode == ExecutionMode.NpuOnly && profile.Artifact.Id.StartsWith("flm:", StringComparison.Ordinal))
        {
            var alias = profile.Artifact.Id[4..];
            var catalog = FlmModelCatalog.Load(_paths.CatalogPath, _paths.ModelInfoPath);
            var model = catalog.Models.SingleOrDefault(candidate => candidate.Id == alias);
            if (model is null || model.Files.Count == 0 || model.Files.Any(file => Path.GetFileName(file.Name) != file.Name)) return null;
            var root = Path.GetFullPath(_paths.DefaultModelStore);
            if (profile.InstalledPath is not null && !SamePath(profile.InstalledPath, root)) return null;
            var names = model.Files.SelectMany(file => new[] { file.Name, file.Name + ".part" }).Distinct(StringComparer.Ordinal).ToArray();
            return new(Path.Combine(root, "models", model.DirectoryName), model.DisplayName, alias, names, null, true);
        }
        return null;
    }

    private void ValidateTarget(Target target)
    {
        if (!Path.GetFullPath(target.Directory).StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw Failure("removal_external", "Only downloads inside this Winference model folder can be removed.");
        EnsureSafePath(target.Directory);
        foreach (var name in target.Names)
        {
            var path = Path.Combine(target.Directory, name);
            EnsureSafePath(path);
            if (Directory.Exists(path)) throw Failure("removal_location", "A model filename is occupied by a directory. Its contents were kept.");
        }
        if (target.Npu)
        {
            var marker = Path.Combine(_paths.DefaultModelStore, ".winference-owned-store");
            EnsureSafePath(marker);
            if (!File.Exists(marker) || new FileInfo(marker).Length > 1024 || File.ReadAllText(marker) != StoreMarker)
                throw Failure("removal_external", "This NPU folder has no valid Winference ownership marker. Its files were kept.");
        }
    }

    private async Task<IReadOnlyList<StateChange>> MetadataChangesAsync(ModelRemovalPlan plan, CancellationToken token)
    {
        var changes = new List<StateChange>();
        var clearNpuChoice = false;
        if (plan.ExecutionMode == ExecutionMode.GpuOnly)
        {
            var registry = await ReadStateAsync(Path.Combine(_root, "Gpu", "models.json"), token).ConfigureAwait(false);
            if (registry is not null)
            {
                var models = registry.Node["models"] as JsonArray ?? throw State();
                var removed = false;
                foreach (var item in models.ToArray())
                    if (plan.GpuRegistrationId is not null && item?["id"]?.GetValue<Guid>() == plan.GpuRegistrationId)
                    { models.Remove(item); removed = true; }
                if (removed)
                    changes.Add(registry with { After = Serialize(registry.Node) });
            }
            var choice = await ReadStateAsync(Path.Combine(_root, "Gpu", "selection.json"), token).ConfigureAwait(false);
            if (choice is not null && (plan.GpuRegistrationId is not null && choice.Node["id"]?.GetValue<Guid>() == plan.GpuRegistrationId ||
                choice.Node["sha256"]?.GetValue<string>() == plan.ArtifactId[7..] && choice.Node["profileId"]?.GetValue<string>() == plan.ProfileId))
                changes.Add(choice with { After = null });
        }
        else
        {
            var setup = await ReadStateAsync(_paths.SettingsPath, token).ConfigureAwait(false);
            if (setup is not null && setup.Node["modelId"]?.GetValue<string>() == plan.ModelId &&
                setup.Node["modelRoot"]?.GetValue<string>() is { } root && SamePath(root, _paths.DefaultModelStore))
            { changes.Add(setup with { After = null }); clearNpuChoice = true; }
        }
        var active = await ReadStateAsync(Path.Combine(_root, "active-model.json"), token).ConfigureAwait(false);
        if (active is not null)
        {
            var mode = active.Node["mode"]?.GetValue<int>();
            if (plan.ExecutionMode == ExecutionMode.GpuOnly && plan.GpuRegistrationId is not null && mode == (int)ExecutionMode.GpuOnly &&
                    active.Node["gpuRegistrationId"]?.GetValue<Guid>() == plan.GpuRegistrationId ||
                clearNpuChoice && mode == (int)ExecutionMode.NpuOnly)
                changes.Add(active with { After = null });
        }
        return changes;
    }

    private async Task<StateChange?> ReadStateAsync(string path, CancellationToken token)
    {
        EnsureSafePath(path);
        if (Directory.Exists(path)) throw State();
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > MaximumStateBytes) throw State();
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            var node = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) as JsonObject ?? throw State();
            if (node["schemaVersion"]?.GetValue<int>() != 1) throw State();
            return new(path, bytes, bytes, node);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        { throw State(error); }
    }

    private async Task ApplyAsync(string path, byte[]? bytes)
    {
        EnsureSafePath(path);
        if (bytes is null) { File.Delete(path); return; }
        var temporary = path + ".remove-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes).ConfigureAwait(false);
            EnsureSafePath(path);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task RestoreAsync(StateChange change)
    {
        EnsureSafePath(change.Path);
        if (File.Exists(change.Path) && File.ReadAllBytes(change.Path).AsSpan().SequenceEqual(change.Before)) return;
        await ApplyAsync(change.Path, change.Before).ConfigureAwait(false);
    }

    private static byte[] Serialize(JsonObject node) => Encoding.UTF8.GetBytes(node.ToJsonString(AtomicJson.Options));
    private static bool SameSnapshot(ModelRemovalPlan first, ModelRemovalPlan second) =>
        first.ProfileId == second.ProfileId && first.ArtifactId == second.ArtifactId && first.ExecutionMode == second.ExecutionMode &&
        first.ModelId == second.ModelId && first.GpuRegistrationId == second.GpuRegistrationId &&
        SamePath(first.ModelDirectory, second.ModelDirectory) && first.Files.SequenceEqual(second.Files);
    private static bool SamePath(string first, string second) => string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    private static void EnsureSafePath(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            var info = Directory.Exists(current) ? (FileSystemInfo)new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null || info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw Failure("removal_link", "The model or removal folder contains a linked entry. Its files were kept.");
        }
    }
    private static void TryRemoveEmpty(string directory)
    {
        try { EnsureSafePath(directory); if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or RuntimeException) { }
    }
    private static RuntimeException Failure(string code, string message, Exception? error = null) => new(code, message, error);
    private static RuntimeException State(Exception? error = null) => Failure("removal_state", "Saved model choices could not be read safely. Nothing was removed; repair the model list before retrying.", error);
    private sealed record Target(string Directory, string Name, string ModelId, string[] Names, string? Digest, bool Npu);
    private sealed record StateChange(string Path, byte[] Before, byte[]? After, JsonObject Node);
}
