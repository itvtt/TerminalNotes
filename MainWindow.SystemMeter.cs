using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using TerminalNotes.Services;

namespace TerminalNotes;

// This PC's load beside 공지 (사용자: "공지버튼 오른쪽에 CPU, 메모리, GPU 등의 사용 퍼센트를 표시해", "현재 PC의 상태를 보려고
// 하는거야"). Read here, never from the relay: every 2 seconds while the window shows, nothing while it is minimized or in the tray.
// A PC with an NPU shows CPU · GPU · NPU · 메모리 (2026-11-11 사용자: "NPU가 있는 경우에 CPU, GPU, NPU, 메모리 이렇게 상단에 표시").
public partial class MainWindow
{
    private static readonly TimeSpan SystemMeterInterval = TimeSpan.FromSeconds(2);
    private CancellationTokenSource? _systemMeterLifetime;
    private volatile bool _systemMeterShown = true;
    private bool _systemMeterRead;

    internal string SystemMeterText => string.Join(" · ", new[] { SystemMeterCpu, SystemMeterGpu, SystemMeterNpu, SystemMeterMemory }
        .Where(value => value.Visibility == Visibility.Visible).Select(value => value.Text));

    private void StartSystemMeter()
    {
        _systemMeterLifetime = new CancellationTokenSource();
        CancellationToken token = _systemMeterLifetime.Token;
        void Track() => _systemMeterShown = IsVisible && WindowState != WindowState.Minimized;
        IsVisibleChanged += (_, _) => Track();
        StateChanged += (_, _) => Track();
        Closed += (_, _) => _systemMeterLifetime?.Cancel();
        WindowTitleBar.SizeChanged += (_, _) => FitSystemMeter();
        Track();
        _ = Task.Run(() => RunSystemMeterAsync(token), token);
    }

    private async Task RunSystemMeterAsync(CancellationToken token)
    {
        var resources = new WindowsStartupResourceSampler();
        using var gpu = GpuLoadSampler.TryOpen();
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_systemMeterShown)
                {
                    // CPU and GPU are rates: the first reading only starts them, so the meter appears from the second.
                    var sample = await resources.SampleAsync(token);
                    var load = gpu?.Sample();
                    _ = Dispatcher.BeginInvoke(() => ShowSystemMeter(sample, load?.Gpu, gpu is not null, load?.Npu, gpu?.HasNpu == true));
                }
                await Task.Delay(SystemMeterInterval, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine("PC meter stopped: " + exception.GetType().Name);
        }
    }

    private void ShowSystemMeter(StartupResourceMeasurement sample, double? gpu, bool hasGpu, double? npu = null, bool hasNpu = false)
    {
        if (sample.CpuLoadPercent is not { } cpu || sample.MemoryLoadPercent is not { } memory) return;
        SetMeterValue(SystemMeterCpu, "CPU", cpu);
        SetMeterValue(SystemMeterMemory, "메모리", memory);
        SystemMeterGpu.Visibility = hasGpu && gpu is not null ? Visibility.Visible : Visibility.Collapsed;
        if (gpu is { } load) SetMeterValue(SystemMeterGpu, "GPU", load);
        SystemMeterNpu.Visibility = hasNpu && npu is not null ? Visibility.Visible : Visibility.Collapsed;
        if (npu is { } neural) SetMeterValue(SystemMeterNpu, "NPU", neural);
        string used = sample.TotalPhysicalBytes is { } total && sample.AvailablePhysicalBytes is { } free
            ? $" ({(total - free) / 1073741824d:0.0} / {total / 1073741824d:0.0} GB)" : "";
        SystemMeter.ToolTip = $"이 PC 사용률 · 2초마다\nCPU {cpu:0}%" +
            (hasGpu ? gpu is { } shown ? $"\nGPU {shown:0}% (가장 바쁜 엔진)" : "\nGPU 확인 중" : "\nGPU 사용률을 읽을 수 없는 PC입니다") +
            (hasNpu ? npu is { } neuralShown ? $"\nNPU {neuralShown:0}%" : "\nNPU 확인 중" : "") +
            $"\n메모리 {memory}%{used}";
        System.Windows.Automation.AutomationProperties.SetName(SystemMeter, "PC 사용률 · " + SystemMeterText);
        _systemMeterRead = true;
        FitSystemMeter();
    }

    // The meter gives way first when the title bar runs short, so 공지 stays whole beside the tools on the right.
    private void FitSystemMeter()
    {
        if (!_systemMeterRead || CommandBar.Parent is not FrameworkElement tools || SystemMeter.Parent is not FrameworkElement left) return;
        SystemMeter.Visibility = Visibility.Visible;
        left.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (left.DesiredSize.Width > WindowTitleBar.ActualWidth - tools.ActualWidth) SystemMeter.Visibility = Visibility.Collapsed;
    }

    // Quiet grey, gold from 85%, where the PC starts to feel slow.
    private void SetMeterValue(System.Windows.Controls.TextBlock target, string label, double percent)
    {
        int value = (int)Math.Round(Math.Clamp(percent, 0, 100));
        target.Text = $"{label} {value}%";
        target.Foreground = (Brush)FindResource(value >= 85 ? "Accent" : "TextSecondary");
    }
}

/// <summary>How busy the GPU and the NPU are, as Task Manager counts them: each engine's use summed over the processes using it, and
/// the busiest engine. Windows' "GPU Engine" counters cover every adapter, the NPU too (a compute-only adapter): engines on an NPU's
/// adapter (NpuAdapters) count as NPU, the rest as GPU. Null on a PC without the counters (old drivers, some remote sessions).</summary>
internal sealed class GpuLoadSampler : IDisposable
{
    private const uint FormatDouble = 0x00000200, NoCap100 = 0x00008000;
    private const uint MoreData = 0x800007D2;
    private IntPtr _query, _counter;
    private readonly IReadOnlyCollection<string> _npuAdapters = NpuAdapters.CounterIds();

    private GpuLoadSampler(IntPtr query, IntPtr counter) { _query = query; _counter = counter; }

    /// <summary>This PC has an NPU (a compute-only machine-learning adapter)</summary>
    internal bool HasNpu => _npuAdapters.Count > 0;

    internal static GpuLoadSampler? TryOpen()
    {
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out IntPtr query) != 0) return null;
            if (PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out IntPtr counter) != 0)
            {
                PdhCloseQuery(query);
                return null;
            }
            PdhCollectQueryData(query);
            return new GpuLoadSampler(query, counter);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    /// <summary>The busiest GPU and NPU engine's use since the last call, 0 to 100 (NPU null without one); null when Windows gave no
    /// reading this time.</summary>
    internal (double Gpu, double? Npu)? Sample()
    {
        if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return null;
        uint size = 0;
        if (PdhGetFormattedCounterArray(_counter, FormatDouble | NoCap100, ref size, out _, IntPtr.Zero) != MoreData || size == 0) return null;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(_counter, FormatDouble | NoCap100, ref size, out uint count, buffer) != 0) return null;
            // Instances are "pid_<n>_luid_<a>_<b>_phys_<n>_eng_<n>_engtype_<kind>": one engine is everything after the process.
            var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            int itemSize = Marshal.SizeOf<CounterItem>();
            for (int index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + index * itemSize);
                if (item.Status != 0 || Marshal.PtrToStringUni(item.Name) is not { } name) continue;
                int luid = name.IndexOf("luid_", StringComparison.Ordinal);
                string engine = luid >= 0 ? name[luid..] : name;
                engines[engine] = engines.GetValueOrDefault(engine) + item.Value;
            }
            static double Busiest(IEnumerable<double> values) => Math.Clamp(values.DefaultIfEmpty(0).Max(), 0, 100);
            bool OnNpu(string engine) => _npuAdapters.Any(adapter => engine.StartsWith(adapter, StringComparison.OrdinalIgnoreCase));
            return (Busiest(engines.Where(engine => !OnNpu(engine.Key)).Select(engine => engine.Value)),
                HasNpu ? Busiest(engines.Where(engine => OnNpu(engine.Key)).Select(engine => engine.Value)) : null);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = _counter = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        internal IntPtr Name;
        internal uint Status;
        internal double Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}

/// <summary>
/// NPUs on this PC through DXCore: adapters for machine learning (the NPU hardware type, or D3D12 generic ML) without graphics, which
/// is how Task Manager tells an NPU from a GPU. Their adapter ids in the "GPU Engine" counter form (luid_0x&lt;high&gt;_0x&lt;low&gt;);
/// empty without an NPU or on Windows without DXCore. DXCore's methods are called through their function table (dxcore_interface.h
/// slot order), so this works without the runtime's COM support.
/// </summary>
internal static class NpuAdapters
{
    private static readonly Guid FactoryInterface = new("78ee5945-c36e-4b13-a669-005dd11c0f06");
    private static readonly Guid ListInterface = new("526c7776-40e9-459b-b711-f32ad76dfc28");
    private static readonly Guid AdapterInterface = new("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e");
    private static readonly Guid GenericMl = new("b71b0d41-1088-422f-a27c-0250b7d3a988");
    private static readonly Guid NpuHardware = new("d46140c4-add7-451b-9e56-06fe8c3b58ed");
    private static readonly Guid Graphics = new("0c9ece4d-2f6e-4f01-8c96-e89e331b47b1");
    private const uint InstanceLuidProperty = 0;

    /// <summary>Every machine-learning adapter seen (GPUs too): shows the lookup itself works on a PC without an NPU</summary>
    internal static int MachineLearningAdapterCount { get; private set; }

    internal static IReadOnlyCollection<string> CounterIds()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IntPtr factory = IntPtr.Zero;
        try
        {
            Guid factoryId = FactoryInterface;
            if (DXCoreCreateAdapterFactory(ref factoryId, out factory) != 0 || factory == IntPtr.Zero) return found;
            int adapters = 0;
            foreach (Guid kind in new[] { NpuHardware, GenericMl })
            {
                Guid attribute = kind, listId = ListInterface;
                if (Slot<CreateAdapterList>(factory, 3)(factory, 1, ref attribute, ref listId, out IntPtr list) != 0 || list == IntPtr.Zero) continue;
                try
                {
                    uint count = Slot<GetAdapterCount>(list, 4)(list);
                    for (uint index = 0; index < count; index++)
                    {
                        Guid adapterId = AdapterInterface, graphics = Graphics;
                        if (Slot<GetAdapter>(list, 3)(list, index, ref adapterId, out IntPtr adapter) != 0 || adapter == IntPtr.Zero) continue;
                        try
                        {
                            adapters++;
                            if (!Slot<IsValid>(adapter, 3)(adapter) || Slot<IsAttributeSupported>(adapter, 4)(adapter, ref graphics)) continue;
                            if (Slot<GetLuidProperty>(adapter, 6)(adapter, InstanceLuidProperty, (nuint)Marshal.SizeOf<Luid>(), out Luid luid) == 0)
                                found.Add($"luid_0x{luid.HighPart:X8}_0x{luid.LowPart:X8}");
                        }
                        finally { Marshal.Release(adapter); }
                    }
                }
                finally { Marshal.Release(list); }
            }
            MachineLearningAdapterCount = adapters;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or SEHException or MarshalDirectiveException)
        {
            System.Diagnostics.Trace.WriteLine("NPU lookup: " + exception.GetType().Name);
        }
        finally { if (factory != IntPtr.Zero) Marshal.Release(factory); }
        return found;
    }

    private static T Slot<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        internal uint LowPart;
        internal int HighPart;
    }

    [DllImport("dxcore.dll")]
    private static extern int DXCoreCreateAdapterFactory(ref Guid riid, out IntPtr factory);

    // After IUnknown (slots 0-2): IDXCoreAdapterFactory 3 CreateAdapterList; IDXCoreAdapterList 3 GetAdapter, 4 GetAdapterCount;
    // IDXCoreAdapter 3 IsValid, 4 IsAttributeSupported, 5 IsPropertySupported, 6 GetProperty. C++ bool returns are one byte.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateAdapterList(IntPtr self, uint attributeCount, ref Guid filterAttributes, ref Guid riid, out IntPtr list);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAdapter(IntPtr self, uint index, ref Guid riid, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetAdapterCount(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool IsValid(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool IsAttributeSupported(IntPtr self, ref Guid attribute);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetLuidProperty(IntPtr self, uint property, nuint bufferSize, out Luid value);
}
