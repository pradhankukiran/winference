using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Winference.Infrastructure.Setup;

internal static class WindowsSetupHardware
{
    public static Task<SetupHardware> InspectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(new SetupHardware("Unknown processor", "Unknown graphics", "No Windows NPU detected", 0, false,
                "Windows is required.", "This build requires Windows 11 and a supported AMD NPU."));
        var processor = "Processor unavailable";
        using (var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            processor = cpu?.GetValue("ProcessorNameString") as string ?? processor;
        var graphics = new List<string>();
        using (var video = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video"))
        {
            foreach (var id in video?.GetSubKeyNames() ?? [])
            {
                using var adapter = video?.OpenSubKey(id + @"\0000");
                var name = adapter?.GetValue("DriverDesc") as string;
                if (!string.IsNullOrWhiteSpace(name)) graphics.Add(name);
            }
        }
        var neural = new List<string>();
        var versions = new List<string>();
        var supportedNpu = false;
        using (var pci = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI"))
        {
            foreach (var deviceId in pci?.GetSubKeyNames() ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var device = pci?.OpenSubKey(deviceId);
                foreach (var instanceId in device?.GetSubKeyNames() ?? [])
                {
                    using var instance = device?.OpenSubKey(instanceId);
                    var description = instance?.GetValue("FriendlyName") as string ?? instance?.GetValue("DeviceDesc") as string ?? "";
                    var shortName = description.Split(';')[^1];
                    // The pinned FLM Windows probe recognizes this AMD XDNA2
                    // PCI identity (v1.0.10 src/src/main.cpp:195-215).
                    var isSupported = deviceId.Contains("VEN_1022&DEV_17F0", StringComparison.OrdinalIgnoreCase);
                    if (!isSupported && !shortName.Contains("NPU", StringComparison.OrdinalIgnoreCase) &&
                        !shortName.Contains("XDNA", StringComparison.OrdinalIgnoreCase) &&
                        !shortName.Contains("Neural", StringComparison.OrdinalIgnoreCase)) continue;
                    supportedNpu |= isSupported;
                    neural.Add(string.IsNullOrWhiteSpace(shortName) ? "AMD XDNA2 NPU" : shortName);
                    if (instance?.GetValue("Driver") is string driverPath)
                    {
                        using var driver = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driverPath);
                        if (driver?.GetValue("DriverVersion") is string version) versions.Add(version);
                    }
                }
            }
        }
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var physical = GlobalMemoryStatusEx(ref memory) ? checked((long)memory.TotalPhysical) : 0;
        var npu = supportedNpu;
        return Task.FromResult(new SetupHardware(processor.Trim(),
            graphics.Count > 0 ? string.Join(", ", graphics.Distinct()) : "Graphics information unavailable",
            neural.Count > 0 ? string.Join(", ", neural.Distinct()) : "No supported NPU detected", physical, npu,
            versions.Count > 0 ? $"Driver {string.Join(", ", versions.Distinct())}; readiness is checked during setup." : "NPU driver readiness is checked during setup.",
            npu ? null : "This NPU setup needs a supported AMD Ryzen AI NPU. GPU models can still be added from Models."));
    }

    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
