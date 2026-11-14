using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Winference.Infrastructure.Windows;

public enum ChipKind { Gpu, Npu }

/// <summary>
/// One reading. Busy is a percentage, like Task Manager shows; power is in watts.
/// A null value means this PC doesn't report it.
/// </summary>
public sealed record ChipSample(double? NpuBusy, double? GpuBusy, double? NpuWatts, double? GpuWatts, double? ChipWatts);

/// <summary>
/// Reads how busy the NPU and GPU are and how much power they draw, from Windows' performance
/// counters. NPUs appear among the "GPU Engine" counters as compute-only adapters, which
/// Windows' graphics kernel marks as such. Power comes from the "Energy Meter" counters that
/// some PCs, including AMD Ryzen AI laptops, provide.
/// </summary>
public sealed partial class ChipMonitor : IDisposable
{
    private static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(3);
    private readonly object _sync = new();
    private readonly IReadOnlyDictionary<string, ChipKind> _adapters;
    private IntPtr _query;
    private readonly IntPtr _engines;
    private readonly IntPtr _power;
    private DateTime _lastCollect = DateTime.MinValue;

    private ChipMonitor(IntPtr query, IntPtr engines, IntPtr power, IReadOnlyDictionary<string, ChipKind> adapters)
    {
        _query = query;
        _engines = engines;
        _power = power;
        _adapters = adapters;
    }

    public bool HasNpu => _adapters.Values.Contains(ChipKind.Npu);

    /// <summary>Null when this PC has no usable counters.</summary>
    public static ChipMonitor? TryCreate()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out var query) != 0) return null;
            var engines = PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out var engineCounter) == 0
                ? engineCounter : IntPtr.Zero;
            var power = PdhAddEnglishCounter(query, @"\Energy Meter(*)\Power", IntPtr.Zero, out var powerCounter) == 0
                ? powerCounter : IntPtr.Zero;
            if (engines == IntPtr.Zero && power == IntPtr.Zero)
            {
                PdhCloseQuery(query);
                return null;
            }
            return new ChipMonitor(query, engines, power, ReadAdapters());
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Takes a reading. Rates need two collections, so the first call after a pause
    /// only starts measuring and returns null.
    /// </summary>
    public ChipSample? Sample()
    {
        lock (_sync)
        {
            if (_query == IntPtr.Zero) return null;
            var now = DateTime.UtcNow;
            var primed = now - _lastCollect <= MaxGap;
            _lastCollect = now;
            if (PdhCollectQueryData(_query) != 0 || !primed) return null;
            var (npu, gpu) = _engines == IntPtr.Zero ? (null, null) : Busy(Read(_engines), _adapters);
            var (npuWatts, gpuWatts, chipWatts) = _power == IntPtr.Zero ? (null, null, null) : Power(Read(_power));
            return new(npu, gpu, npuWatts, gpuWatts, chipWatts);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_query == IntPtr.Zero) return;
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Adds each engine's use across processes, then takes the busiest engine of each adapter,
    /// as Task Manager does. Unknown adapters are left out.
    /// </summary>
    public static (double? Npu, double? Gpu) Busy(IEnumerable<(string Instance, double Value)> samples,
        IReadOnlyDictionary<string, ChipKind> adapters)
    {
        var engines = new Dictionary<(string Luid, string Engine), double>();
        foreach (var (instance, value) in samples)
        {
            var match = EngineName().Match(instance);
            if (!match.Success || !double.IsFinite(value)) continue;
            var key = (match.Groups[1].Value.ToLowerInvariant(), match.Groups[2].Value + "_" + match.Groups[3].Value);
            engines[key] = engines.GetValueOrDefault(key) + Math.Max(0, value);
        }
        double? Busiest(ChipKind kind)
        {
            if (!adapters.Values.Contains(kind)) return null;
            var busiest = engines.Where(pair => adapters.TryGetValue(pair.Key.Luid, out var found) && found == kind)
                .Select(pair => pair.Value).DefaultIfEmpty(0).Max();
            return Math.Clamp(busiest, 0, 100);
        }
        return (Busiest(ChipKind.Npu), Busiest(ChipKind.Gpu));
    }

    /// <summary>Energy meter readings are in milliwatts. The whole-chip figure prefers the widest meter.</summary>
    public static (double? Npu, double? Gpu, double? Chip) Power(IEnumerable<(string Instance, double Value)> samples)
    {
        var meters = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, value) in samples)
            if (double.IsFinite(value) && value >= 0) meters[instance.Trim()] = value / 1000;
        double? Meter(params string[] names)
        {
            foreach (var name in names) if (meters.TryGetValue(name, out var watts)) return watts;
            return null;
        }
        return (Meter("NPU Power"), Meter("GPU Power"), Meter("APU Power", "Socket Power", "RAPL_Package0_PKG"));
    }

    /// <summary>LUIDs are written the way counter names show them: 0x{high}_0x{low}.</summary>
    public static string LuidKey(int high, uint low) => $"0x{high:x8}_0x{low:x8}";

    [GeneratedRegex(@"luid_(0x[0-9a-f]+_0x[0-9a-f]+)_phys_(\d+)_eng_(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EngineName();

    private static List<(string Instance, double Value)> Read(IntPtr counter)
    {
        var results = new List<(string, double)>();
        uint size = 0;
        var status = PdhGetFormattedCounterArray(counter, Format, ref size, out _, IntPtr.Zero);
        if (status != PdhMoreData || size == 0) return results;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, Format, ref size, out var count, buffer) != 0) return results;
            const int ItemSize = 24;
            for (var i = 0; i < count; i++)
            {
                var item = buffer + i * ItemSize;
                var state = (uint)Marshal.ReadInt32(item, 8);
                if (state is not (0 or 1)) continue;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                var value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                if (name is not null) results.Add((name, value));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return results;
    }

    /// <summary>Lists display, render and compute-only adapters. Software renderers are skipped.</summary>
    private static Dictionary<string, ChipKind> ReadAdapters()
    {
        var adapters = new Dictionary<string, ChipKind>();
        var request = new EnumAdapters3 { Filter = IncludeComputeOnly };
        if (D3DKMTEnumAdapters3(ref request) != 0 || request.Count == 0) return adapters;
        var itemSize = Marshal.SizeOf<AdapterInfo>();
        var buffer = Marshal.AllocHGlobal(itemSize * (int)request.Count);
        var type = Marshal.AllocHGlobal(4);
        try
        {
            request.Adapters = buffer;
            if (D3DKMTEnumAdapters3(ref request) != 0) return adapters;
            for (var i = 0; i < request.Count; i++)
            {
                var info = Marshal.PtrToStructure<AdapterInfo>(buffer + i * itemSize);
                Marshal.WriteInt32(type, 0);
                var query = new QueryAdapterInfo { Adapter = info.Adapter, Type = AdapterTypeQuery, Data = type, Size = 4 };
                var flags = D3DKMTQueryAdapterInfo(ref query) == 0 ? (uint)Marshal.ReadInt32(type) : 0;
                var close = new CloseAdapter { Adapter = info.Adapter };
                D3DKMTCloseAdapter(ref close);
                if ((flags & SoftwareDevice) != 0) continue;
                adapters[LuidKey(info.LuidHigh, info.LuidLow)] = (flags & ComputeOnly) != 0 ? ChipKind.Npu : ChipKind.Gpu;
            }
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException) { }
        finally
        {
            Marshal.FreeHGlobal(type);
            Marshal.FreeHGlobal(buffer);
        }
        return adapters;
    }

    private const uint Format = 0x00000200 | 0x00008000; // PDH_FMT_DOUBLE | PDH_FMT_NOCAP100
    private const uint PdhMoreData = 0x800007D2;
    private const ulong IncludeComputeOnly = 1;
    private const int AdapterTypeQuery = 15; // KMTQAITYPE_ADAPTERTYPE
    private const uint SoftwareDevice = 1u << 2;
    private const uint ComputeOnly = 1u << 11;

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterInfo
    {
        public uint Adapter;
        public uint LuidLow;
        public int LuidHigh;
        public uint Sources;
        public int PrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumAdapters3
    {
        public ulong Filter;
        public uint Count;
        public IntPtr Adapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryAdapterInfo
    {
        public uint Adapter;
        public int Type;
        public IntPtr Data;
        public uint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CloseAdapter
    {
        public uint Adapter;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    private static extern uint PdhOpenQuery(string? source, IntPtr user, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTEnumAdapters3(ref EnumAdapters3 request);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo request);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref CloseAdapter request);
}
