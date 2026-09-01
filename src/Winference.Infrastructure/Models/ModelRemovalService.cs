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
}
