using Winference.Infrastructure.Setup;

namespace Winference.App.Services;

/// <summary>Lets UI tests reach the unsupported-PC screen on supported hardware. Absent from Release builds.</summary>
internal static class TestHardware
{
    public static Func<CancellationToken, Task<SetupHardware>>? Override()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("WINFERENCE_TEST_HARDWARE") == "unsupported")
            return _ => Task.FromResult(new SetupHardware("Test processor", "Test graphics", "No supported NPU detected",
                8L << 30, false, "NPU driver readiness is checked during setup.",
                "This NPU setup needs a supported AMD Ryzen AI NPU. GPU models can still be added from Models."));
#endif
        return null;
    }

    /// <summary>Opens on a given page, so screens can be reviewed without moving the user's mouse.</summary>
    public static Presentation.ShellPage? StartPage()
    {
#if DEBUG
        if (Enum.TryParse<Presentation.ShellPage>(Environment.GetEnvironmentVariable("WINFERENCE_TEST_PAGE"), out var page)) return page;
#endif
        return null;
    }

    /// <summary>A test run in its own data folder may sit beside the user's open copy.</summary>
    public static string InstanceSuffix()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("WINFERENCE_DATA_ROOT") is { Length: > 0 }) return ".Test";
#endif
        return string.Empty;
    }

    /// <summary>Shortens the idle timeout so unloading can be tested in seconds.</summary>
    public static TimeSpan? IdleTimeoutOverride()
    {
#if DEBUG
        if (int.TryParse(Environment.GetEnvironmentVariable("WINFERENCE_TEST_IDLE_SECONDS"), out var seconds)) return TimeSpan.FromSeconds(seconds);
#endif
        return null;
    }

    public static long? FreeBytesOverride()
    {
#if DEBUG
        if (long.TryParse(Environment.GetEnvironmentVariable("WINFERENCE_TEST_FREE_BYTES"), out var bytes)) return bytes;
#endif
        return null;
    }
}
