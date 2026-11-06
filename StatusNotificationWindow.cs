using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TerminalNotes;

/// <summary>One modeless surface for actionable results; showing it never activates it.</summary>
internal sealed class StatusNotificationPresenter : IDisposable
{
    private const int HistoryLimit = 30;
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromSeconds(5);
    private static readonly string[] AttentionMarkers = ["오류", "불가", "수 없", "지 못", "거부", "과부하",
        "자원 부족", "메모리 부족", "가득 찼", "응답하지 않", "올바르지 않", "이미 있습니다",
        "이름이어야 합니다", "다른 프로젝트 이름을 입력", "플랜이 없습니다", "먼저 입력하세요",
        "마친 뒤", "끝난 뒤", "먼저 완료하거나 중단", "하나를 닫아주세요", "기존 태스크를 비운 뒤",
        "미리보기 화면이 종료되었습니다", "종료하지 않았습니다", "retry when it finishes"];
    private readonly Window _owner;
    private readonly List<StatusNotificationEntry> _entries = [];
    private StatusNotificationWindow? _window;
    private bool _disposed;
    private bool _pending;

    internal StatusNotificationPresenter(Window owner)
    {
        _owner = owner;
        owner.Loaded += OwnerLoaded;
        owner.Closed += OwnerClosed;
        owner.IsVisibleChanged += OwnerVisibilityChanged;
        owner.StateChanged += OwnerStateChanged;
    }

    internal void Publish(string message, bool reopenDuplicate = false)
    {
        if (RequiresAttention(message)) PublishCore(message, reopenDuplicate);
    }

    // Typed warning sources already know that user attention is required, even
    // when their text contains no error keyword (for example clipboard failures).
    internal void PublishAttention(string message, bool reopenDuplicate = false)
        => PublishCore(message, reopenDuplicate);

    private static bool RequiresAttention(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        // Details can contain arbitrary project names, paths and exception text.
        // Classify only the status heading, keeping routine saves/selections quiet.
        int end = message.IndexOf(" · ", StringComparison.Ordinal);
        int lineEnd = message.IndexOfAny(['\r', '\n']);
        if (lineEnd >= 0 && (end < 0 || lineEnd < end)) end = lineEnd;
        string heading = (end < 0 ? message : message[..end]).Trim();
        if (heading.Contains("실패", StringComparison.Ordinal) &&
            !heading.Contains("실패 없", StringComparison.Ordinal) &&
            !heading.Contains("실패가 없", StringComparison.Ordinal) &&
            !heading.Contains("실패는 없", StringComparison.Ordinal)) return true;
        if (heading.Contains("확인 필요", StringComparison.Ordinal) ||
            heading.Contains("조치 필요", StringComparison.Ordinal)) return true;
        if (heading.Contains("오류 없", StringComparison.Ordinal) ||
            heading.Contains("오류가 없", StringComparison.Ordinal) ||
            heading.Contains("경고 없", StringComparison.Ordinal) ||
            heading.Contains("오류 복구 중", StringComparison.Ordinal) ||
            heading.Contains("복구 완료", StringComparison.Ordinal) ||
            heading.Contains("복구되었습니다", StringComparison.Ordinal) ||
            heading.Contains("회복되었습니다", StringComparison.Ordinal)) return false;
        return AttentionMarkers.Any(marker => heading.Contains(marker, StringComparison.Ordinal));
    }

    private void PublishCore(string message, bool reopenDuplicate)
    {
        if (_disposed || string.IsNullOrWhiteSpace(message)) return;
        DateTimeOffset now = DateTimeOffset.Now;
        message = message.Trim();
        var repeated = _entries.FirstOrDefault(entry => entry.Message == message && now - entry.Time <= RepeatInterval);
        if (repeated is not null)
        {
            repeated.Time = now;
            repeated.Count++;
            _entries.Remove(repeated);
            _entries.Insert(0, repeated);
            // Explicit user operations can produce the same result after dismissal.
            // Background polling only updates the existing history.
            if (reopenDuplicate)
            {
                _pending = true;
                if (_owner.IsLoaded) ShowPending();
            }
            else if (_window?.IsVisible == true) _window.Render(_entries);
            return;
        }
        _entries.Insert(0, new StatusNotificationEntry(message, now));
        if (_entries.Count > HistoryLimit) _entries.RemoveAt(_entries.Count - 1);
        _pending = true;
        if (_owner.IsLoaded) ShowPending();
    }

    private void OwnerLoaded(object sender, RoutedEventArgs e) => ShowPending();
    private void OwnerClosed(object? sender, EventArgs e) => Dispose();
    private void OwnerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => RefreshOwnerVisibility();
    private void OwnerStateChanged(object? sender, EventArgs e) => RefreshOwnerVisibility();

    private void RefreshOwnerVisibility()
    {
        if (_disposed) return;
        if (!_owner.IsVisible || _owner.WindowState == WindowState.Minimized)
        {
            if (_window?.IsVisible == true) { _pending = true; _window.Hide(); }
            return;
        }
        ShowPending();
    }

    internal static Window FindVisibleOwner(Window owner)
    {
        // Owned modal/modeless dialogs can cover the main window. The notification
        // must belong to the visible dialog to appear above it without activation.
        Window[] children = owner.OwnedWindows.Cast<Window>()
            .Where(window => window is not StatusNotificationWindow && window.IsVisible &&
                window.WindowState != WindowState.Minimized).ToArray();
        Window? child = children.FirstOrDefault(window => window.IsActive) ?? children.LastOrDefault();
        return child is null ? owner : FindVisibleOwner(child);
    }

    private void ShowPending()
    {
        if (_disposed || !_pending || !_owner.IsVisible || _owner.WindowState == WindowState.Minimized) return;
        Window anchor = FindVisibleOwner(_owner);
        if (_window is null)
        {
            var window = new StatusNotificationWindow { Owner = anchor };
            _window = window;
            window.Closed += (_, _) => { if (ReferenceEquals(_window, window)) _window = null; };
        }
        _window.Render(_entries);
        _window.Present(anchor);
        _pending = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.Loaded -= OwnerLoaded;
        _owner.Closed -= OwnerClosed;
        _owner.IsVisibleChanged -= OwnerVisibilityChanged;
        _owner.StateChanged -= OwnerStateChanged;
        _window?.CloseForOwner();
        _window = null;
        _entries.Clear();
    }
}

internal sealed class StatusNotificationEntry(string message, DateTimeOffset time)
{
    internal string Message { get; } = message;
    internal DateTimeOffset Time { get; set; } = time;
    internal int Count { get; set; } = 1;
}

internal sealed class StatusNotificationWindow : Window
{
    private readonly StackPanel _messages = new();
    private readonly TextBlock _count = new();
    private HwndSource? _source;
    private bool _updatingBounds;
    private bool _renderQueued;
    private bool _closed;
    private bool _allowClose;
    private IReadOnlyList<StatusNotificationEntry>? _pendingEntries;

    internal StatusNotificationWindow()
    {
        Title = "Terminal Notes · 알림";
        Width = 460;
        Height = 300;
        MinWidth = 240;
        MinHeight = 180;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowActivated = false;
        ShowInTaskbar = false;
        Background = Brush(17, 17, 17);
        Foreground = Brush(241, 233, 216);
        FontFamily = new FontFamily(new Uri("pack://application:,,,/TerminalNotes;component/"), "./Assets/Fonts/#Pretendard, Segoe UI, Malgun Gothic");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        FontSize = 14;

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new TextBlock { Text = "알림", FontSize = 16, FontWeight = FontWeights.SemiBold,
            Foreground = Brush(216, 188, 130),
            Margin = new Thickness(0, 0, 0, 12) };
        root.Children.Add(header);
        var scroll = new ScrollViewer { Content = _messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var close = new Button { Content = "닫기", Padding = new Thickness(14, 5, 14, 5), Focusable = false,
            Style = AppMessageDialog.GoldButtonStyle(),
            Cursor = System.Windows.Input.Cursors.Hand };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        _count.Foreground = Brush(170, 160, 142);
        _count.FontSize = 12;
        _count.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_count);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = new Border { Child = root, BorderBrush = Brush(102, 82, 57), BorderThickness = new Thickness(1) };
        SourceInitialized += (_, _) =>
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source?.AddHook(WindowHook);
            AppMessageDialog.ApplyFrame(this);
            KeepInWorkArea(positionAtOwner: true);
        };
        Loaded += (_, _) => KeepInWorkArea(positionAtOwner: true);
        LocationChanged += (_, _) => KeepInWorkArea(positionAtOwner: false);
        SizeChanged += (_, _) => KeepInWorkArea(positionAtOwner: false);
        DpiChanged += (_, _) => QueueWorkAreaUpdate();
        Closed += (_, _) => { _closed = true; _source?.RemoveHook(WindowHook); _source = null; };
    }

    internal void Render(IReadOnlyList<StatusNotificationEntry> entries)
    {
        _pendingEntries = entries;
        if (_renderQueued || _closed) return;
        _renderQueued = true;
        // A burst of progress updates becomes one layout pass, after pending input.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _renderQueued = false;
            if (!_closed && _pendingEntries is { } latest) RenderMessages(latest);
        }));
    }

    internal void Present(Window owner)
    {
        if (_closed) return;
        if (!ReferenceEquals(Owner, owner)) Owner = owner;
        AppMessageDialog.ApplyOwnerScrollBarStyle(this, owner);
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        KeepInWorkArea(positionAtOwner: true);
        // Raise this owned window above the active app dialog, preserving keyboard
        // and foreground focus. HWND_TOP is deliberately not HWND_TOPMOST.
        SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, 0, 0, 0, 0,
            0x0053); // NOMOVE | NOSIZE | NOACTIVATE | SHOWWINDOW
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    internal void CloseForOwner()
    {
        _allowClose = true;
        Close();
    }

    private void RenderMessages(IReadOnlyList<StatusNotificationEntry> entries)
    {
        _messages.Children.Clear();
        foreach (var entry in entries)
        {
            var row = new StackPanel();
            row.Children.Add(new TextBlock { Text = entry.Time.ToString("MM/dd HH:mm:ss") +
                (entry.Count > 1 ? $" · {entry.Count}회" : ""), FontSize = 11,
                Foreground = Brush(170, 160, 142), Margin = new Thickness(0, 0, 0, 3) });
            row.Children.Add(new TextBlock { Text = entry.Message, TextWrapping = TextWrapping.Wrap,
                FontSize = 14, LineHeight = 21 });
            _messages.Children.Add(new Border
            {
                BorderBrush = Brush(102, 82, 57), BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 0, 8, 9), Margin = new Thickness(0, 0, 0, 9), Child = row
            });
        }
        _count.Text = $"최근 알림 {entries.Count}개";
    }

    private static SolidColorBrush Brush(byte red, byte green, byte blue) => new(Color.FromRgb(red, green, blue));

    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021) // WM_MOUSEACTIVATE: clicks/scrolling must not steal terminal or IME focus.
        {
            handled = true;
            return new IntPtr(3); // MA_NOACTIVATE, still deliver the click (including Close).
        }
        if (message is 0x007E or 0x02E0 or 0x001A) QueueWorkAreaUpdate();
        return IntPtr.Zero;
    }

    private void QueueWorkAreaUpdate() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
        new Action(() => KeepInWorkArea(positionAtOwner: false)));

    private void KeepInWorkArea(bool positionAtOwner)
    {
        if (_updatingBounds || _source is null) return;
        IntPtr handle = _source.Handle;
        IntPtr anchor = positionAtOwner && Owner is not null ? new WindowInteropHelper(Owner).Handle : handle;
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(anchor, 2), ref monitor) || !GetWindowRect(handle, out var bounds)) return;
        _updatingBounds = true;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var work = monitor.Work;
            int availableWidth = Math.Max(1, work.Right - work.Left);
            int availableHeight = Math.Max(1, work.Bottom - work.Top);
            int width = Math.Min(availableWidth, Math.Max(1, bounds.Right - bounds.Left));
            int height = Math.Min(availableHeight, Math.Max(1, bounds.Bottom - bounds.Top));
            MinWidth = Math.Min(240, availableWidth / dpi.DpiScaleX);
            MinHeight = Math.Min(180, availableHeight / dpi.DpiScaleY);
            NativeRect ownerBounds = work;
            if (positionAtOwner && Owner is not null && GetWindowRect(anchor, out var actualOwnerBounds)) ownerBounds = actualOwnerBounds;
            int left = positionAtOwner ? ownerBounds.Left + (ownerBounds.Right - ownerBounds.Left - width) / 2 : bounds.Left;
            int top = positionAtOwner ? ownerBounds.Top + (ownerBounds.Bottom - ownerBounds.Top - height) / 2 : bounds.Top;
            left = Math.Clamp(left, work.Left, work.Right - width);
            top = Math.Clamp(top, work.Top, work.Bottom - height);
            if (left != bounds.Left || top != bounds.Top || width != bounds.Right - bounds.Left || height != bounds.Bottom - bounds.Top)
                SetWindowPos(handle, IntPtr.Zero, left, top, width, height, 0x0014); // NOZORDER | NOACTIVATE
        }
        finally { _updatingBounds = false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { internal int Size; internal NativeRect Monitor, Work; internal uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
        int left, int top, int width, int height, uint flags);
}
