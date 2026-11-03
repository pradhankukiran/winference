using Winference.Core.Media;
using Winference.Infrastructure.Lemonade;

namespace Winference.Tests;

/// <summary>
/// Runs the real bundled Lemonade server. Skipped unless WINFERENCE_MEDIA_IT points at lemond.exe;
/// WINFERENCE_MEDIA_ROOT chooses the data folder. It can download models.
/// </summary>
public sealed class MediaIntegrationTests
{
    [Fact]
    public async Task Voice_speech_and_images_work_end_to_end()
    {
        var lemond = Environment.GetEnvironmentVariable("WINFERENCE_MEDIA_IT");
        var root = Environment.GetEnvironmentVariable("WINFERENCE_MEDIA_ROOT");
        if (string.IsNullOrEmpty(lemond) || string.IsNullOrEmpty(root)) return;

        await using var media = new LemonadeMediaService(lemond, root);
        var log = new List<string>();
        var progress = new Progress<MediaDownloadProgress>(_ => { });
        var stage = new Progress<string>(log.Add);
        foreach (var model in new[] { "kokoro-v1", "Whisper-Base", "SD-Turbo-GGUF" })
        {
            var started = DateTime.UtcNow;
            await media.InstallAsync(model, progress, stage, CancellationToken.None);
            Console.WriteLine($"installed {model} in {(DateTime.UtcNow - started).TotalSeconds:0.0}s");
            Assert.True(media.IsInstalled(model));
        }

        var wav = await media.SpeakAsync("kokoro-v1", "af_heart", "The quick brown fox jumps over the lazy dog.", CancellationToken.None);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));

        var text = await media.TranscribeAsync("Whisper-Base", wav, CancellationToken.None);
        Console.WriteLine("heard: " + text);
        Assert.Contains("fox", text, StringComparison.OrdinalIgnoreCase);

        var png = await media.GenerateImageAsync("SD-Turbo-GGUF", "a lighthouse at sunset", CancellationToken.None);
        Assert.Equal(0x89, png[0]);
        Assert.Equal((byte)'P', png[1]);
    }
}
