using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TerminalNotes;
using TerminalNotes.Services;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes visuals " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            VerifyAdmissionOverlay(fixture);
            VerifyNativeBusyBackground(fixture);
            Console.WriteLine("PASS terminal admission overlay and native busy background.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL " + exception);
            return 1;
        }
        finally { Directory.Delete(fixture, recursive: true); }
    }

    private static void VerifyAdmissionOverlay(string fixture)
    {
        using var pane = new TerminalPane(fixture, useProjectContext: false);
        pane.RowDefinitions.Add(new RowDefinition());
        pane.RowDefinitions.Add(new RowDefinition());
        pane.ColumnDefinitions.Add(new ColumnDefinition());
        pane.ColumnDefinitions.Add(new ColumnDefinition());
        Call(pane, "ShowResourceAdmissionWait", "사용 가능한 메모리가 부족합니다.");
        var overlay = Field<Border>(pane, "_resourceAdmissionOverlay");
        pane.Measure(new Size(480, 320));
        pane.Arrange(new Rect(0, 0, 480, 320));
        pane.UpdateLayout();
        Require(overlay.Visibility == Visibility.Visible && overlay.ActualWidth == pane.ActualWidth && overlay.ActualHeight == pane.ActualHeight &&
            Grid.GetRowSpan(overlay) == 2 && Grid.GetColumnSpan(overlay) == 2,
            $"Resource wait did not cover the full terminal body: {overlay.ActualWidth}x{overlay.ActualHeight}, pane {pane.ActualWidth}x{pane.ActualHeight}, spans {Grid.GetRowSpan(overlay)}/{Grid.GetColumnSpan(overlay)}.");
        foreach (string reason in new[] { "사용 가능한 메모리가 부족합니다.", "CPU 사용률이 높습니다.", "다른 터미널이 시작 중입니다." })
        {
            Call(pane, "ShowResourceAdmissionWait", reason);
            Require(Field<TextBlock>(pane, "_resourceAdmissionReason").Text == reason &&
                System.Windows.Automation.AutomationProperties.GetName(overlay).Contains(reason, StringComparison.Ordinal),
                "Resource reason was not updated visually and accessibly.");
        }
        Call(pane, "ShowResourceAdmissionWait", "메모리 여유를 기다리는 중 · 메모리 92% · 여유 512 MiB · CPU 96%\n다른 터미널의 시작이 끝나면 차례대로 실행합니다.");
        pane.UpdateLayout();
        Capture(pane, fixture + "-normal.png");
        pane.Width = 160;
        pane.Height = 360;
        pane.Measure(new Size(160, 360));
        pane.Arrange(new Rect(0, 0, 160, 360));
        pane.UpdateLayout();
        Require(pane.ActualWidth == 160 && pane.ActualHeight == 360 &&
            overlay.ActualWidth == pane.ActualWidth && overlay.ActualHeight == pane.ActualHeight &&
            Field<TextBlock>(pane, "_resourceAdmissionReason").TextWrapping == TextWrapping.Wrap,
            $"Resource wait did not fill and wrap in a narrow split pane: pane {pane.ActualWidth}x{pane.ActualHeight}, overlay {overlay.ActualWidth}x{overlay.ActualHeight}.");
        var scroller = (ScrollViewer)overlay.Child;
        Require(scroller.ComputedHorizontalScrollBarVisibility == Visibility.Collapsed &&
            scroller.ExtentHeight <= scroller.ViewportHeight,
            "A normal long resource reason did not fit the narrow pane.");
        Capture(pane, fixture + "-narrow.png");
        pane.Height = 140;
        pane.Measure(new Size(160, 140));
        pane.Arrange(new Rect(0, 0, 160, 140));
        pane.UpdateLayout();
        Require(pane.ActualHeight == 140 && scroller.ComputedVerticalScrollBarVisibility == Visibility.Visible &&
            scroller.ScrollableHeight > 0, "A short pane clipped the wait details instead of allowing scrolling.");
        Call(pane, "ShowResourceAdmissionWait", (object?)null);
        Require(overlay.Visibility == Visibility.Collapsed, "Admission did not dismiss the wait overlay.");
        SetField(pane, "_ready", true);
        Call(pane, "ShowResourceAdmissionWait", "다른 터미널이 시작 중입니다.");
        Require(overlay.Visibility == Visibility.Collapsed, "Resource wait covered an already started pane.");
        SetField(pane, "_ready", false);
        pane.Dispose();
        Call(pane, "ShowResourceAdmissionWait", "CPU 사용률이 높습니다.");
        Require(overlay.Visibility == Visibility.Collapsed, "A disposed pane reopened its resource overlay.");
        Console.WriteLine("PASS full-body resource wait reasons, admission dismissal, and ready/disposal guards.");
    }

    private static void VerifyNativeBusyBackground(string fixture)
    {
        using var pane = new TerminalPane(fixture, useProjectContext: false);
        using var monitor = new OpenCodeStatusMonitor(new Uri("http://127.0.0.1:43127/"), fixture);
        using var host = new OpenCodeHeadlessHost(new Uri("http://127.0.0.1:43127/"));
        // Observe only this test executable's liveness. Detach it before any owning host cleanup.
        using var liveness = new TestProcessLiveness(host);
        SetField(pane, "_headlessOpenCodeHost", host);
        SetField(pane, "_usesNativeOpenCodeStatus", true);
        SetField(pane, "_openCodeStatus", monitor);
        SetField(pane, "_ready", true);
        void Snapshot(OpenCodeStatusKind kind)
        {
            SetField(pane, "_openCodeSnapshot", new OpenCodeStatusSnapshot(kind, false, "fixture-session"));
            Call(pane, "ApplyActivityVisuals");
        }
        SetField(monitor, "_lastActivityAt", (DateTimeOffset?)DateTimeOffset.UtcNow.AddMinutes(-1));
        Snapshot(OpenCodeStatusKind.Busy);
        Require(pane.IsReady && pane.IsRunning && !monitor.HasRecentActivity && pane.Activity == TerminalActivity.Active && pane.ActivityLabel == "진행 중" &&
            ((SolidColorBrush)pane.Background).Color == (Color)ColorConverter.ConvertFromString("#00853F"),
            "Confirmed native busy lost its green work state when recent activity expired.");
        foreach (OpenCodeStatusKind kind in new[] { OpenCodeStatusKind.Idle, OpenCodeStatusKind.Pending, OpenCodeStatusKind.Unknown })
        {
            Snapshot(kind);
            Require(pane.Activity != TerminalActivity.Active &&
                ((SolidColorBrush)pane.Background).Color == (Color)ColorConverter.ConvertFromString("#252D3A"),
                $"Native {kind} was incorrectly shown as active work.");
        }
        Snapshot(OpenCodeStatusKind.NeedsInput);
        Require(pane.Activity == TerminalActivity.NeedsInput &&
            ((SolidColorBrush)pane.Background).Color == (Color)ColorConverter.ConvertFromString("#FF8A00"),
            "Native approval did not remain orange.");
        Snapshot(OpenCodeStatusKind.Busy);
        SetField(pane, "_nativeAutomationSupervisionNotice", "응답 변화 대기");
        Call(pane, "ApplyActivityVisuals");
        Require(pane.Activity == TerminalActivity.Active && pane.ActivityLabel == "진행 중" &&
            ((SolidColorBrush)pane.Background).Color == (Color)ColorConverter.ConvertFromString("#00853F"),
            "An informational response-change wait overrode confirmed native busy.");
        SetField(pane, "_nativeAutomationSupervisionNotice", "정체 작업 중단 확인 중");
        Call(pane, "ApplyActivityVisuals");
        Require(pane.Activity == TerminalActivity.Active && pane.ActivityLabel == "진행 중" &&
            ((SolidColorBrush)pane.Background).Color == (Color)ColorConverter.ConvertFromString("#00853F"),
            "A supervision notice overrode exact native busy activity.");
        Snapshot(OpenCodeStatusKind.NeedsInput);
        Require(pane.Activity == TerminalActivity.NeedsInput &&
            ((SolidColorBrush)pane.Background).Color == (Color)ColorConverter.ConvertFromString("#FF8A00"),
            "Supervision overrode native approval priority.");
        Console.WriteLine("PASS native busy remains green without recent animation evidence; idle/pending/approval/recovery keep distinct states.");
    }

    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void SetField(object target, string name, object? value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(name + " available: " + string.Join(", ", target.GetType()
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Select(candidate => candidate.Name)));
        field.SetValue(target, value);
    }
    private static object? Call(object target, string name, params object?[] arguments) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Capture(FrameworkElement surface, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth),
            (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
        Console.WriteLine("PREVIEW " + path);
    }

    private sealed class TestProcessLiveness : IDisposable
    {
        private readonly OpenCodeHeadlessHost _host;
        private readonly Process _process = Process.GetCurrentProcess();
        internal TestProcessLiveness(OpenCodeHeadlessHost host)
        {
            _host = host;
            SetField(_host, "_process", _process);
        }
        public void Dispose()
        {
            SetField(_host, "_process", null);
            _process.Dispose();
        }
    }
}
