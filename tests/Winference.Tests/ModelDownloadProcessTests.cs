using Winference.Core.Models;
using Winference.Core.Setup;
using Winference.Infrastructure.Setup;
using Winference.Infrastructure.Windows;

namespace Winference.Tests;

public class ModelDownloadProcessTests
{
    private static IReadOnlyDictionary<string, string> Environmentless() =>
        Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(item => (string)item.Key, item => (string)item.Value!, StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("hostname.exe", new string[0], 0)]
    [InlineData("where.exe", new[] { "/q", "winference-no-such-file" }, 1)]
    public async Task The_exit_code_is_still_readable_after_the_process_has_gone(string tool, string[] arguments, int expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var session = OwnedProcessSession.Start(Path.Combine(Environment.SystemDirectory, tool), arguments,
            Environment.SystemDirectory, Environmentless());
        await session.WaitForExitAsync(CancellationToken.None);
        await Task.Delay(300);

        Assert.Equal(expected, session.ExitCode);
    }

    /// <summary>
    /// Runs the real flm tool. It only runs when WINFERENCE_REAL_FLM_ROOT names a throwaway data
    /// folder holding a model with one small file removed, so a normal test run needs no network.
    /// </summary>
    [Fact]
    public async Task A_completed_pull_is_reported_as_a_success()
    {
        if (Environment.GetEnvironmentVariable("WINFERENCE_REAL_FLM_ROOT") is not { Length: > 0 } root) return;
        var repository = Environment.GetEnvironmentVariable("WINFERENCE_REPOSITORY_ROOT")!;
        var paths = new SetupPaths(root, string.Empty,
            Path.Combine(repository, "config", "catalogs", "flm-1.0.10.json"),
            Path.Combine(repository, "config", "catalogs", "flm-1.0.10-model-info.json"),
            [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "flm", "flm.exe")], []);
        var service = new FirstRunSetupService(paths, _ => Task.FromResult(
            new SetupHardware("test", "test", "test", 24L << 30, true, "test", null)));

        await service.DownloadAsync(new("lfm2:1.2b", ExecutionMode.NpuOnly, paths.DefaultModelStore, 2048),
            new Progress<SetupProgress>(), CancellationToken.None);

        var model = (await service.InspectAsync()).Models.Single(item => item.Id == "lfm2:1.2b");
        Assert.True(model.IsInstalled);
    }
}
