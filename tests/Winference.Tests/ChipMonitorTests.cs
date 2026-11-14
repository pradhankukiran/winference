using Winference.Infrastructure.Windows;
using Xunit.Abstractions;

namespace Winference.Tests;

public sealed class ChipMonitorTests(ITestOutputHelper output)
{
    private static readonly Dictionary<string, ChipKind> Adapters = new()
    {
        ["0x00000000_0x0000c4dd"] = ChipKind.Gpu,
        ["0x00000000_0x0000e085"] = ChipKind.Npu,
    };

    [Fact]
    public void Engine_use_is_added_across_processes_and_the_busiest_engine_wins()
    {
        var (npu, gpu) = ChipMonitor.Busy(
        [
            ("pid_10_luid_0x00000000_0x0000E085_phys_0_eng_0_engtype_Compute", 30),
            ("pid_11_luid_0x00000000_0x0000E085_phys_0_eng_0_engtype_Compute", 25),
            ("pid_10_luid_0x00000000_0x0000C4DD_phys_0_eng_0_engtype_3D", 12),
            ("pid_12_luid_0x00000000_0x0000C4DD_phys_0_eng_1_engtype_Copy", 40),
            ("pid_12_luid_0x00000000_0x0000E019_phys_0_eng_0_engtype_3D", 99),
        ], Adapters);
        Assert.Equal(55, npu);
        Assert.Equal(40, gpu);
    }

    [Fact]
    public void Missing_chips_are_null_and_idle_chips_are_zero()
    {
        var (npu, gpu) = ChipMonitor.Busy([], new Dictionary<string, ChipKind> { ["0x00000000_0x00000001"] = ChipKind.Gpu });
        Assert.Null(npu);
        Assert.Equal(0, gpu);
    }

    [Fact]
    public void Busy_never_exceeds_one_hundred_percent()
    {
        var (_, gpu) = ChipMonitor.Busy(
        [
            ("pid_1_luid_0x00000000_0x0000c4dd_phys_0_eng_0_engtype_3D", 80),
            ("pid_2_luid_0x00000000_0x0000c4dd_phys_0_eng_0_engtype_3D", 70),
        ], Adapters);
        Assert.Equal(100, gpu);
    }

    [Fact]
    public void Energy_meters_are_read_in_watts()
    {
        var (npu, gpu, chip) = ChipMonitor.Power(
        [
            ("NPU Power", 1500), ("GPU Power", 629.5), ("APU Power", 8057.4), ("RAPL_Package0_PKG", 6663.8),
        ]);
        Assert.Equal(1.5, npu);
        Assert.Equal(0.6295, gpu);
        Assert.Equal(8.0574, chip);
        Assert.Equal((null, null, 6.6638), ChipMonitor.Power([("RAPL_Package0_PKG", 6663.8)]));
    }

    [Fact]
    public void Luid_keys_match_counter_names()
    {
        Assert.Equal("0x00000000_0x0000c4dd", ChipMonitor.LuidKey(0, 0xC4DD));
    }

    [Fact]
    public async Task Readings_start_after_one_second_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var monitor = ChipMonitor.TryCreate();
        if (monitor is null) return;
        Assert.Null(monitor.Sample());
        await Task.Delay(1000);
        var sample = monitor.Sample();
        Assert.NotNull(sample);
        output.WriteLine($"NPU found: {monitor.HasNpu}. Reading: {sample}");
        if (sample!.GpuBusy is { } busy) Assert.InRange(busy, 0, 100);
    }
}
