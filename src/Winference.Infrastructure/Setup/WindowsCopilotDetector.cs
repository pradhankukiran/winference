using Microsoft.Win32;

namespace Winference.Infrastructure.Setup;

public sealed record WindowsCopilotStatus(
    bool IsWindows11OrNewer,
    bool IsCopilotPlusEligible,
    bool HasXdna2Npu,
    string StatusDescription,
    string? AvailableModel);

/// <summary>
/// Probes Windows 11 Copilot+ on-device AI capabilities and hardware readiness.
/// </summary>
public static class WindowsCopilotDetector
{
    public const int MinimumWindows11Build = 26100; // Windows 11 24H2

    public static WindowsCopilotStatus Inspect(SetupHardware hardware)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(
                IsWindows11OrNewer: false,
                IsCopilotPlusEligible: false,
                HasXdna2Npu: false,
                StatusDescription: "Windows is required for Windows Copilot Runtime features.",
                AvailableModel: null);
        }

        var is24H2 = Environment.OSVersion.Version.Build >= MinimumWindows11Build;
        var hasXdna2 = hardware.NpuDetected;
        var hasMemory = hardware.MemoryBytes >= 16L * 1024 * 1024 * 1024; // 16GB RAM minimum for Copilot+

        var eligible = is24H2 && hasXdna2 && hasMemory;

        var description = eligible
            ? "This device meets Windows Copilot+ requirements (AMD XDNA2 NPU + 16GB+ RAM on Windows 11 24H2)."
            : !is24H2
                ? "Windows 11 version 24H2 (Build 26100+) is required for on-device Copilot Runtime."
                : !hasXdna2
                    ? "An AMD XDNA2 (40+ TOPS) NPU was not detected."
                    : "16 GB of system RAM is required for Copilot+ on-device AI.";

        return new WindowsCopilotStatus(
            IsWindows11OrNewer: is24H2,
            IsCopilotPlusEligible: eligible,
            HasXdna2Npu: hasXdna2,
            StatusDescription: description,
            AvailableModel: eligible ? "Phi-Silica (Windows On-Device NPU)" : null);
    }
}
