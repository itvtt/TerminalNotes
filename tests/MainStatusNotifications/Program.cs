using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using TerminalNotes;

internal static class Program
{
    private static int _assertions;
    private static readonly BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes-main-notifications-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var input = new TextBox { Text = "terminal typing / IME stays here" };
        var owner = new Window { Width = 580, Height = 220, Content = input, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 140, Top = 140,
            Title = "Isolated notification test owner" };
        using var presenter = new StatusNotificationPresenter(owner);
        try
        {
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XElement resources = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"))
                .Root!.Element(presentation + "Window.Resources")!;
            resources.Name = presentation + "ResourceDictionary";
            resources.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
            owner.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
            VerifyMainRouting();
            VerifyRoutineStatuses(presenter);
            presenter.Publish("시작 실패 · 연결을 확인하세요.");
            Require(Notification(presenter) is null, "Unloaded owner opened a premature notification.");
            owner.Show();
            Pump();
            var notification = Notification(presenter)!;
            Require(notification.IsVisible && notification.Owner == owner, "Queued result did not open an owned modeless window.");
            Require(!notification.ShowActivated && !notification.ShowInTaskbar &&
                ReferenceEquals(notification.Resources[typeof(ScrollBar)], owner.Resources[typeof(ScrollBar)]),
                "Notification could activate, create taskbar clutter or omit its owner's dark scrollbar.");
            owner.Activate();
            input.Focus();
            Pump();
            Require(ReferenceEquals(Keyboard.FocusedElement, input), "Isolated input did not acquire focus before notification checks.");
            var focused = Keyboard.FocusedElement;
            var foreground = GetForegroundWindow();
            presenter.Publish("태스크 새로고침 실패 · 폴더 권한을 확인하세요.");
            Pump();
            Require(ReferenceEquals(notification, Notification(presenter)), "Each result created another window.");
            Require(ReferenceEquals(focused, Keyboard.FocusedElement) && foreground == GetForegroundWindow(), "Publishing stole keyboard/foreground focus.");
            int entryCount = Entries(presenter).Count;
            presenter.Publish("태스크 새로고침 실패 · 폴더 권한을 확인하세요.");
            Require(Entries(presenter).Count == entryCount && Entries(presenter)[0].Count == 2, "Repeated status was not coalesced.");
            Require(SendMessage(new WindowInteropHelper(notification).Handle, 0x0021, IntPtr.Zero, IntPtr.Zero) == new IntPtr(3),
                "Mouse interaction could activate the notification and steal IME focus.");
            var dismissed = notification;
            notification.Close();
            presenter.Publish("태스크 새로고침 실패 · 폴더 권한을 확인하세요.");
            Require(ReferenceEquals(Notification(presenter), dismissed) && !dismissed.IsVisible,
                "Polling reopened a dismissed duplicate result or discarded its reusable window.");
            entryCount = Entries(presenter).Count;
            focused = Keyboard.FocusedElement;
            foreground = GetForegroundWindow();
            VerifyRoutineStatuses(presenter);
            presenter.Publish("태스크 새로고침 실패 · 폴더 권한을 확인하세요.", reopenDuplicate: true);
            Pump();
            Require(ReferenceEquals(Notification(presenter), dismissed) && dismissed.IsVisible,
                "Explicit retry failure did not reopen the same notification window.");
            Require(Entries(presenter).Count == entryCount && Entries(presenter)[0].Count == 4,
                "Repeated retry failure duplicated history instead of coalescing.");
            Require(ReferenceEquals(focused, Keyboard.FocusedElement) && foreground == GetForegroundWindow(),
                "Reopening the same explicit result stole input focus.");
            VerifyCentered(notification, owner);
            owner.Left += 110;
            owner.Top += 65;
            presenter.Publish("위치가 바뀐 창에서 오류", reopenDuplicate: true);
            Pump();
            VerifyCentered(notification, owner);
            var activeDialog = new Window { Owner = owner, Width = 600, Height = 420,
                Left = owner.Left + 20, Top = owner.Top + 20, ShowActivated = false,
                Title = "Isolated owned planner" };
            {
                activeDialog.Show();
                presenter.Publish("열린 플랜메이커 위에 표시할 오류", reopenDuplicate: true);
                Pump();
                Require(notification.Owner == activeDialog && notification.IsVisible && !notification.Topmost &&
                    ReferenceEquals(notification.Resources[typeof(ScrollBar)], owner.Resources[typeof(ScrollBar)]),
                    "Notification is hidden behind an owned dialog or became always-on-top.");
                VerifyCentered(notification, activeDialog);
            Require(ReferenceEquals(focused, Keyboard.FocusedElement) && foreground == GetForegroundWindow(),
                    "Raising a notification above an owned dialog stole focus.");
                presenter.Publish("메인 창 소유 오류", reopenDuplicate: true);
                activeDialog.Hide();
                presenter.Publish("플랜메이커를 닫은 뒤의 오류", reopenDuplicate: true);
                Require(notification.Owner == owner, "Notification retained a hidden dialog owner.");
                activeDialog.Close();
            }
            notification.Close();
            owner.WindowState = WindowState.Minimized;
            presenter.Publish("최소화 중 받은 새로운 오류", reopenDuplicate: true);
            VerifyRoutineStatuses(presenter);
            Pump();
            Require(!notification.IsVisible, "Minimized owner surfaced a background notification.");
            owner.WindowState = WindowState.Normal;
            Pump();
            Require(notification.IsVisible, "Restoring owner lost a pending notification.");
            VerifyCentered(notification, owner);
            notification.Close();
            owner.Hide();
            presenter.Publish("트레이에 숨긴 동안 받은 새로운 오류", reopenDuplicate: true);
            Pump();
            Require(!notification.IsVisible, "Hidden owner surfaced a background notification.");
            owner.Show();
            Pump();
            Require(notification.IsVisible, "Showing hidden owner lost its pending notification.");
            VerifyConfirmation(owner);
            dismissed.Close();
            presenter.Publish("선택한 터미널을 시작할 수 없습니다 · 연결 확인 필요");
            Pump();
            notification = Notification(presenter)!;
            Require(notification.IsVisible && ReferenceEquals(notification, dismissed), "New important result did not reuse/reopen the notification.");
            VerifyAttentionWarnings(presenter);
            for (int i = 0; i < 40; i++) presenter.Publish("새로고침 실패 " + i);
            Require(Entries(presenter).Count == 30, "Recent notification history is unbounded.");
            presenter.Publish("새로고침 실패 · 프로젝트 폴더를 읽을 수 없습니다.\n폴더 접근 권한을 확인한 뒤 다시 시도하세요.");
            Require(Entries(presenter)[0].Message.Contains('\n'), "Full multiline result was flattened in its separate window.");
            notification.Left = -100000;
            notification.Top = -100000;
            notification.Width = 20000;
            notification.Height = 20000;
            Pump();
            VerifyBounds(notification);
            notification.Width = 520;
            notification.Height = 360;
            Pump();
            VerifyRoutineStatuses(presenter);
            Pump();
            SaveScreenshot(notification, Path.Combine(fixture, "main-status-notifications.png"));
            presenter.Dispose();
            Require(!notification.IsVisible && Notification(presenter) is null, "Disposal left an orphan notification window.");
            presenter.PublishAttention("종료 후 조치 알림");
            Require(Notification(presenter) is null && Entries(presenter).Count == 0, "Disposed presenter accepted a late callback.");
            using var lifecyclePresenter = new StatusNotificationPresenter(owner);
            lifecyclePresenter.Publish("창 종료 오류 수명주기 검사");
            var lifecycleWindow = Notification(lifecyclePresenter)!;
            owner.Close();
            Pump();
            Require(!lifecycleWindow.IsVisible && Notification(lifecyclePresenter) is null, "Owner exit left a notification alive.");
            Console.WriteLine($"PASS {_assertions} main status notification assertions; isolated WPF owner, no app/model/process/settings.");
            Console.WriteLine("Evidence: " + fixture);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + error); return 1; }
        finally { presenter.Dispose(); owner.Close(); app.Shutdown(); }
    }

    private static StatusNotificationWindow? Notification(StatusNotificationPresenter presenter) =>
        (StatusNotificationWindow?)typeof(StatusNotificationPresenter).GetField("_window", Members)!.GetValue(presenter);
    private static List<StatusNotificationEntry> Entries(StatusNotificationPresenter presenter) =>
        (List<StatusNotificationEntry>)typeof(StatusNotificationPresenter).GetField("_entries", Members)!.GetValue(presenter)!;

    private static void VerifyRoutineStatuses(StatusNotificationPresenter presenter)
    {
        var window = Notification(presenter);
        bool visible = window?.IsVisible == true;
        var entries = Entries(presenter).Select(entry => (entry.Message, entry.Count)).ToArray();
        bool pending = (bool)typeof(StatusNotificationPresenter).GetField("_pending", Members)!.GetValue(presenter)!;
        foreach (string message in new[] { "시작 준비 중", "저장 완료 · C:\\오류\\실패\\plan.md",
            "태스크 목록을 새로고침했습니다.", "변경된 파일이 없습니다", "검색 결과가 없습니다",
            "선택 완료 · 오류 프로젝트", "새로고침 진행 3", "오류 없음", "오류가 없습니다",
            "오류 없이 완료", "실패가 없습니다", "실패는 없습니다", "오류 복구 중 · 원래 요청을 이어갑니다", "오류 복구 완료",
            "화면 응답이 중단된 뒤 복구되었습니다.", "플랜메이커를 중단했습니다 · 초안 유지" })
        {
            presenter.Publish(message);
            presenter.Publish(message, reopenDuplicate: true);
        }
        Require(ReferenceEquals(window, Notification(presenter)) && (window?.IsVisible == true) == visible,
            "A routine status created or reopened a notification.");
        Require(entries.SequenceEqual(Entries(presenter).Select(entry => (entry.Message, entry.Count))) &&
            pending == (bool)typeof(StatusNotificationPresenter).GetField("_pending", Members)!.GetValue(presenter)!,
            "Routine statuses changed actionable history or deferred notification state.");
    }

    private static void VerifyAttentionWarnings(StatusNotificationPresenter presenter)
    {
        foreach (string failure in new[] { "작업 폴더를 찾을 수 없습니다 · C:\\project",
            "오류 복구 실패 · 연결이 끊어졌습니다", "정체 복구 확인 필요 · 요청을 확인하세요",
            "미리보기 화면이 종료되었습니다. 편집 탭을 선택한 뒤 미리보기를 다시 열어주세요.",
            "프로젝트 이동을 마친 뒤 새로고침하세요.", "같은 이름의 파일 또는 프로젝트 폴더가 이미 있습니다" })
        {
            presenter.Publish(failure);
            Require(Notification(presenter)?.IsVisible == true && Entries(presenter)[0].Message == failure,
                "An actionable failure or blocked operation was filtered out: " + failure);
        }
        const string message = "붙여넣기는 200,000자 이하로 나눠주세요.";
        presenter.PublishAttention(message);
        var window = Notification(presenter)!;
        Require(window.IsVisible && Entries(presenter)[0].Message == message,
            "An explicit warning without error keywords stayed inline.");
        window.Close();
        presenter.PublishAttention(message);
        Require(!window.IsVisible && Entries(presenter)[0].Count == 2,
            "A repeated background warning bypassed dismissal/deduplication.");
        presenter.PublishAttention(message, reopenDuplicate: true);
        Require(window.IsVisible && Entries(presenter)[0].Count == 3,
            "An explicit warning retry failed to reuse/reopen its coalesced notice.");
    }

    private static void VerifyMainRouting()
    {
        string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.source.cs"));
        string status = source[source.IndexOf("    private void SetStatus(string message)", StringComparison.Ordinal)..];
        status = status[..status.IndexOf("    private Settings? ReadSettings()", StringComparison.Ordinal)];
        string route = status[..status.IndexOf("    private void ShowError", StringComparison.Ordinal)];
        Require(route.Contains("StatusLabel.Text =") && route.Contains("notifications.Publish(message, reopenDuplicate)"),
            "Main status lost its inline update or separate notification routing.");
        Require(route.Contains("SetStatus(string message) => SetStatusCore(message, reopenDuplicate: true)") &&
            route.Contains("SetProgressStatus(string message) => SetStatusCore(message, reopenDuplicate: false)"),
            "Explicit user results and repeated background progress are not distinguished.");
        Require(source.Contains("terminal.StatusChanged += SetProgressStatus;"), "Terminal polling uses the explicit-result reopen route.");
        Require(source.Contains("terminal.UserWarning += SetAttentionStatus;") &&
            route.Contains("notifications.PublishAttention(message, reopenDuplicate)"),
            "Typed terminal warnings lost their explicit attention route.");
        Require(route.Contains("Dispatcher.CheckAccess()") && route.Contains("Dispatcher.BeginInvoke"), "Background terminal status cannot safely reach the UI.");
        Require(!status[status.IndexOf("    private void ShowError", StringComparison.Ordinal)..].Contains("SetStatus("),
            "Existing modal error gets a duplicate popup.");
        Require(source.Contains("_statusNotifications?.Dispose();"), "Main resource disposal does not close notifications.");
        Require(source.Contains("AppVersionLabel.Text = \"v\" + ApplicationDiagnostics.DisplayVersion;"),
            "The title bar version must use the deployed application's actual version.");
        Require(source.Contains("SetStatus(\"태스크 목록을 새로고침했습니다.\")"), "Refresh result bypasses the notification route.");
        Require(source.Contains("SetInlineStatus(ProjectDisplayName(project) + \" · \" + planPath)") &&
            source.Contains("SetInlineStatus($\"{ProjectDisplayName(project)} · 기존 터미널 배치를 유지합니다.") &&
            route.Contains("if (inlineOnly) return;"),
            "Ordinary project selection can classify an arbitrary project name as an error.");
        Require(source.Contains("SetAttentionStatus(\"CMD 이어가기 · \" + ex.Message)") &&
            source.Contains("SetAttentionStatus((saved ? \"plan.md에 저장했습니다. 터미널 전송을 확인하세요") &&
            source.Contains("SetAttentionStatus(\"일반 터미널은 최대 8개까지 열 수 있습니다.\")") &&
            source.Contains("SetAttentionStatus(\"플랜이 없습니다 ·"),
            "A known failed dispatch, handoff or blocked operation lost its explicit warning route.");
    }

    private static void VerifyBounds(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        Require(GetWindowRect(handle, out var bounds) && GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor), "Monitor/window bounds unavailable.");
        Require(bounds.Left >= monitor.Work.Left && bounds.Top >= monitor.Work.Top &&
            bounds.Right <= monitor.Work.Right && bounds.Bottom <= monitor.Work.Bottom, "Notification escaped the current monitor work area.");
        Require(window.MinWidth <= (monitor.Work.Right - monitor.Work.Left) / VisualTreeHelper.GetDpi(window).DpiScaleX,
            "Minimum size ignores monitor DPI/work area.");
    }

    private static void VerifyCentered(Window window, Window owner)
    {
        bool hasWindow = GetWindowRect(new WindowInteropHelper(window).Handle, out var bounds);
        bool hasOwner = GetWindowRect(new WindowInteropHelper(owner).Handle, out var anchor);
        Require(hasWindow && hasOwner, "Window centers unavailable.");
        Require(Math.Abs((bounds.Left + bounds.Right) - (anchor.Left + anchor.Right)) <= 3 &&
            Math.Abs((bounds.Top + bounds.Bottom) - (anchor.Top + anchor.Bottom)) <= 3,
            "Notification was not centered over the current owner on showing/reopening.");
    }

    private static void VerifyConfirmation(Window owner)
    {
        Window Create(bool confirmation) => (Window)typeof(AppMessageDialog).GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [owner, "플랜을 정리할까요?", string.Join('\n', Enumerable.Repeat("기존 조건을 유지합니다.", 300)), "정리", confirmation])!;
        var dialog = Create(true);
        var root = (Grid)((Border)dialog.Content).Child;
        var scroll = root.Children.OfType<ScrollViewer>().Single();
        var buttons = root.Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
        Require(scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto &&
            ReferenceEquals(dialog.Resources[typeof(ScrollBar)], owner.Resources[typeof(ScrollBar)]) &&
            buttons.Single(button => button.IsCancel).IsDefault && !buttons[0].IsDefault,
            "Confirmation must scroll the full message and default to Cancel.");
        Require(((SolidColorBrush)dialog.Background).Color == Color.FromRgb(17, 17, 17) &&
            buttons.All(button => button.Background is LinearGradientBrush gold && gold.IsFrozen && gold.GradientStops.Count == 4 &&
                ((SolidColorBrush)button.Foreground).Color == Color.FromRgb(246, 231, 201)),
            "Application popup lost its black surface or readable static deep-bronze actions with gold text.");
        dialog.Close();
        int ticks = 0;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Normal,
            (_, _) =>
            {
                var visible = owner.OwnedWindows.Cast<Window>().FirstOrDefault(window =>
                    System.Windows.Automation.AutomationProperties.GetAutomationId(window) == "AppMessageDialog" && window.IsVisible);
                if (visible is null) { if (++ticks > 50) throw new InvalidOperationException("Confirmation failed to show."); return; }
                VerifyBounds(visible);
                visible.DialogResult = false;
            }, Dispatcher.CurrentDispatcher);
        timer.Start();
        try { Require(!AppMessageDialog.Confirm(owner, "플랜을 정리할까요?", "원본 조건을 유지합니다.", "정리"),
            "Cancelled application confirmation was accepted."); }
        finally { timer.Stop(); }
    }

    private static void SaveScreenshot(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Require(bool success, string message) { _assertions++; if (!success) throw new InvalidOperationException(message); }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { internal int Size; internal NativeRect Monitor, Work; internal uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
}
