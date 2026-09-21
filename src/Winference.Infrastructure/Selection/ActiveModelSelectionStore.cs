using System.Text.Json;
using Winference.Core.Models;
using Winference.Core.Runtime;
using Winference.Infrastructure.Setup;

namespace Winference.Infrastructure.Selection;

public sealed record ActiveModelSelection(ExecutionMode Mode, Guid? GpuRegistrationId = null, int SchemaVersion = 1,
    Guid? HybridRegistrationId = null);

/// <summary>Chooses the remembered engine without overwriting its model-specific settings.</summary>
public sealed class ActiveModelSelectionStore(string dataRoot)
{
    private readonly string _path = Path.Combine(Path.GetFullPath(dataRoot), "active-model.json");

    public async Task<ActiveModelSelection?> ReadAsync(CancellationToken token = default)
    {
        if (!File.Exists(_path)) return null;
        try
        {
            if (new FileInfo(_path).Length > 16_384) throw new InvalidDataException("Invalid active model choice.");
            var saved = await AtomicJson.ReadAsync<ActiveModelSelection>(_path, token).ConfigureAwait(false);
            if (saved is null || !IsValid(saved)) throw new InvalidDataException("Invalid active model choice.");
            return saved;
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new RuntimeException("selection_invalid", "The saved model choice could not be read. Choose a model again; existing files and chats were kept.", error);
        }
    }

    public Task SaveAsync(ActiveModelSelection selection, CancellationToken token = default)
    {
        if (!IsValid(selection)) throw new ArgumentException("Invalid active model choice.", nameof(selection));
        return AtomicJson.WriteAsync(_path, selection, token);
    }

    private static bool IsValid(ActiveModelSelection value) => value.SchemaVersion == 1 &&
        (value.Mode == ExecutionMode.NpuOnly && value.GpuRegistrationId is null && value.HybridRegistrationId is null ||
         value.Mode == ExecutionMode.GpuOnly && value.GpuRegistrationId is { } id && id != Guid.Empty && value.HybridRegistrationId is null ||
         value.Mode == ExecutionMode.GpuNpu && value.HybridRegistrationId is { } hybridId && hybridId != Guid.Empty && value.GpuRegistrationId is null);
}
