using System.Text.Json;
using Winference.Infrastructure.Setup;

namespace Winference.Infrastructure.Selection;

/// <summary>A model's reply speed as measured on this PC, averaged over recent replies.</summary>
public sealed record MeasuredSpeed(double TokensPerSecond, int Replies, DateTimeOffset UpdatedUtc);

/// <summary>
/// Remembers how fast each model actually writes on this PC, so Discover can show real numbers
/// instead of estimates. Only short summaries are kept: no prompts or replies.
/// </summary>
public sealed class ModelSpeedStore(string dataRoot)
{
    /// <summary>The average follows the last few replies, so a driver or engine update shows up quickly.</summary>
    private const int Window = 10;
    private readonly string _path = Path.Combine(Path.GetFullPath(dataRoot), "model-speeds.json");
    private readonly object _sync = new();
    private Dictionary<string, MeasuredSpeed>? _speeds;

    public MeasuredSpeed? Read(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return null;
        lock (_sync) return Load().GetValueOrDefault(modelId);
    }

    public void Record(string modelId, double tokensPerSecond)
    {
        if (string.IsNullOrEmpty(modelId) || !double.IsFinite(tokensPerSecond) || tokensPerSecond is <= 0 or > 10_000) return;
        lock (_sync)
        {
            var speeds = Load();
            var previous = speeds.GetValueOrDefault(modelId);
            var samples = Math.Min(previous?.Replies ?? 0, Window - 1);
            var average = previous is null ? tokensPerSecond : (previous.TokensPerSecond * samples + tokensPerSecond) / (samples + 1);
            speeds[modelId] = new(Math.Round(average, 1), samples + 1, DateTimeOffset.UtcNow);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var staging = _path + ".new";
                File.WriteAllBytes(staging, JsonSerializer.SerializeToUtf8Bytes(speeds, AtomicJson.Options));
                File.Move(staging, _path, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private Dictionary<string, MeasuredSpeed> Load()
    {
        if (_speeds is not null) return _speeds;
        try
        {
            _speeds = File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, MeasuredSpeed>>(File.ReadAllBytes(_path), AtomicJson.Options) ?? []
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { _speeds = []; }
        _speeds = new(_speeds, StringComparer.Ordinal);
        return _speeds;
    }
}
