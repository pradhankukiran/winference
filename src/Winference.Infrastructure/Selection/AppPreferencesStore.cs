using System.Text.Json;
using Winference.Core.Models;

namespace Winference.Infrastructure.Selection;

/// <summary>Small per-user choices. Losing this file only resets them to defaults.</summary>
/// <param name="IdleUnloadMinutes">0 means a loaded model is never unloaded for being idle.</param>
/// <param name="ToolsEnabled">Lets models ask to use local tools. Off unless the user turns it on.</param>
/// <param name="SpeechInputModel">Preferred installed Lemonade speech model, if several are installed.</param>
/// <param name="ImageModel">Preferred installed Lemonade image model.</param>
/// <param name="Voice">Kokoro voice for Read aloud.</param>
public sealed record AppPreferences(string Theme = "Default", ModelFilter ModelFilter = ModelFilter.All,
    int IdleUnloadMinutes = IdleUnloadPolicy.DefaultMinutes, bool LoadOnStart = false, bool ToolsEnabled = false,
    string? SpeechInputModel = null, string? ImageModel = null, string Voice = Winference.Core.Media.MediaAddons.DefaultVoice,
    string? EmbeddingModel = null, string CustomInstructions = "", string ResponseStyle = "balanced");

public sealed class AppPreferencesStore(string dataRoot)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
    private readonly string _path = Path.Combine(Path.GetFullPath(dataRoot), "preferences.json");

    public AppPreferences Read()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            return JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(_path), Options) ?? new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    /// <returns>False if the choice could not be saved. It still applies to this session.</returns>
    public bool Write(AppPreferences preferences)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(preferences, Options));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
