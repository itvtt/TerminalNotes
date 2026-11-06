using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using TerminalNotes.Services;

namespace TerminalNotes;

// This PC's load beside 공지 (사용자: "공지버튼 오른쪽에 CPU, 메모리, GPU 등의 사용 퍼센트를 표시해", "현재 PC의 상태를 보려고
// 하는거야"). Read here, never from the relay: every 2 seconds while the window shows, nothing while it is minimized or in the tray.
public partial class MainWindow
{
    private static readonly TimeSpan SystemMeterInterval = TimeSpan.FromSeconds(2);
    private CancellationTokenSource? _systemMeterLifetime;
    private volatile bool _systemMeterShown = true;
    private bool _systemMeterRead;

    internal string SystemMeterText => string.Join(" · ", new[] { SystemMeterCpu, SystemMeterGpu, SystemMeterMemory }
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
                    double? gpuLoad = gpu?.Sample();
                    _ = Dispatcher.BeginInvoke(() => ShowSystemMeter(sample, gpuLoad, gpu is not null));
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

    private void ShowSystemMeter(StartupResourceMeasurement sample, double? gpu, bool hasGpu)
    {
        if (sample.CpuLoadPercent is not { } cpu || sample.MemoryLoadPercent is not { } memory) return;
        SetMeterValue(SystemMeterCpu, "CPU", cpu);
        SetMeterValue(SystemMeterMemory, "메모리", memory);
        SystemMeterGpu.Visibility = hasGpu && gpu is not null ? Visibility.Visible : Visibility.Collapsed;
        if (gpu is { } load) SetMeterValue(SystemMeterGpu, "GPU", load);
        string used = sample.TotalPhysicalBytes is { } total && sample.AvailablePhysicalBytes is { } free
            ? $" ({(total - free) / 1073741824d:0.0} / {total / 1073741824d:0.0} GB)" : "";
        SystemMeter.ToolTip = $"이 PC 사용률 · 2초마다\nCPU {cpu:0}%" +
            (hasGpu ? gpu is { } shown ? $"\nGPU {shown:0}% (가장 바쁜 엔진)" : "\nGPU 확인 중" : "\nGPU 사용률을 읽을 수 없는 PC입니다") +
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

/// <summary>How busy the GPU is, as Task Manager counts it: each engine's use summed over the processes using it, and the busiest
/// engine of any adapter. Windows' "GPU Engine" counters; null on a PC without them (old drivers, some remote sessions).</summary>
internal sealed class GpuLoadSampler : IDisposable
{
    private const uint FormatDouble = 0x00000200, NoCap100 = 0x00008000;
    private const uint MoreData = 0x800007D2;
    private IntPtr _query, _counter;

    private GpuLoadSampler(IntPtr query, IntPtr counter) { _query = query; _counter = counter; }

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

    /// <summary>The busiest engine's use since the last call, 0 to 100; null when Windows gave no reading this time.</summary>
    internal double? Sample()
    {
        if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return null;
        uint size = 0;
        if (PdhGetFormattedCounterArray(_counter, FormatDouble | NoCap100, ref size, out _, IntPtr.Zero) != MoreData || size == 0) return null;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(_counter, FormatDouble | NoCap100, ref size, out uint count, buffer) != 0) return null;
            // Instances are "pid_<n>_luid_<a>_<b>_phys_<n>_eng_<n>_engtype_<kind>": one engine is everything after the process.
            var engines = new Dictionary<string, double>(StringComparer.Ordinal);
            int itemSize = Marshal.SizeOf<CounterItem>();
            for (int index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + index * itemSize);
                if (item.Status != 0 || Marshal.PtrToStringUni(item.Name) is not { } name) continue;
                int luid = name.IndexOf("luid_", StringComparison.Ordinal);
                string engine = luid >= 0 ? name[luid..] : name;
                engines[engine] = engines.GetValueOrDefault(engine) + item.Value;
            }
            return engines.Count == 0 ? 0 : Math.Clamp(engines.Values.Max(), 0, 100);
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
