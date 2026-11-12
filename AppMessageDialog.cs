using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace TerminalNotes;

/// <summary>Application-owned confirmations and errors with fully scrollable text.</summary>
internal static class AppMessageDialog
{
    internal static bool Confirm(Window owner, string title, string message, string confirmText = "확인") =>
        Create(owner, title, message, confirmText, confirmation: true).ShowDialog() == true;

    internal static void ShowError(Window owner, string title, string message) =>
        Create(owner, title, message, "확인", confirmation: false).ShowDialog();

    /// <summary>A confirmation with one more choice left of the main one: true main, false the other, null cancel.</summary>
    internal static bool? ConfirmOr(Window owner, string title, string message, string confirmText, string otherText)
    {
        bool other = false;
        var window = Build(owner, title, message, confirmText, confirmation: true, otherText, () => other = true);
        return window.ShowDialog() == true ? true : other ? false : null;
    }

    private static Window Create(Window owner, string title, string message, string confirmText, bool confirmation) =>
        Build(owner, title, message, confirmText, confirmation, null, null);

    private static Window Build(Window owner, string title, string message, string confirmText, bool confirmation,
        string? otherText, Action? otherPicked)
    {
        var window = new Window
        {
            Owner = StatusNotificationPresenter.FindVisibleOwner(owner), Title = title,
            // The height follows the message (no clipped text for long confirmations such as 플랜 정리),
            // capped to most of the screen; longer messages scroll inside.
            Width = 560, SizeToContent = SizeToContent.Height, MinWidth = 240, MinHeight = 180,
            MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height * 0.85),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize, ShowInTaskbar = false,
            Background = Brush(17, 17, 17), Foreground = Brush(241, 233, 216),
            FontFamily = new FontFamily(new Uri("pack://application:,,,/TerminalNotes;component/"), "./Assets/Fonts/#Pretendard, Segoe UI, Malgun Gothic"), FontSize = 14
        };
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Ideal);
        ApplyOwnerScrollBarStyle(window, owner);
        AutomationProperties.SetAutomationId(window, "AppMessageDialog");
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold,
            Foreground = Brush(216, 188, 130), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        var content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
        AutomationProperties.SetAutomationId(content, "AppMessageText");
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 0, 0, 16) };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Button MakeButton(string text, string id)
        {
            var button = new Button { Content = text, MinWidth = 76, Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand, Style = GoldButtonStyle() };
            AutomationProperties.SetAutomationId(button, id);
            actions.Children.Add(button);
            return button;
        }
        if (otherText is not null)
        {
            var other = MakeButton(otherText, "AppMessageOther");
            other.Click += (_, _) => { otherPicked?.Invoke(); window.DialogResult = false; };
        }
        var accept = MakeButton(confirmText, "AppMessageConfirm");
        accept.Click += (_, _) => window.DialogResult = true;
        Button defaultButton = accept;
        if (confirmation)
        {
            var cancel = MakeButton("취소", "AppMessageCancel");
            cancel.IsCancel = true;
            cancel.IsDefault = true;
            cancel.Click += (_, _) => window.DialogResult = false;
            defaultButton = cancel;
        }
        else { accept.IsDefault = true; accept.IsCancel = true; }
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);
        window.Content = new Border { Child = root, BorderBrush = Brush(58, 58, 58), BorderThickness = new Thickness(1) };
        // Center once the content has sized the window; centering earlier would freeze a guessed height.
        window.SourceInitialized += (_, _) => ApplyFrame(window);
        window.Loaded += (_, _) => { CenterInWorkArea(window); defaultButton.Focus(); };
        window.DpiChanged += (_, _) => CenterInWorkArea(window);
        return window;
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    internal static void ApplyOwnerScrollBarStyle(Window window, Window owner)
    {
        for (Window? current = owner; current is not null; current = current.Owner)
        {
            if (current.Resources[typeof(ScrollBar)] is not Style style) continue;
            window.Resources[typeof(ScrollBar)] = style;
            return;
        }
        if (Application.Current?.TryFindResource(typeof(ScrollBar)) is Style applicationStyle)
            window.Resources[typeof(ScrollBar)] = applicationStyle;
    }

    internal static Style GoldButtonStyle()
    {
        Brush Gold(string[] colors)
        {
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            double[] offsets = [0, .38, .70, 1];
            for (int index = 0; index < colors.Length; index++)
                brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(colors[index]), offsets[index]));
            brush.Freeze();
            return brush;
        }
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Gold(["#252525", "#252525", "#252525", "#252525"])));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(246, 231, 201)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(201, 168, 106)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        border.AppendChild(content);
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Button)) { VisualTree = border }));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Gold(["#2D2D2D", "#2D2D2D", "#2D2D2D", "#2D2D2D"])));
        style.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Control.BackgroundProperty, Gold(["#1B1B1B", "#1B1B1B", "#1B1B1B", "#1B1B1B"])));
        style.Triggers.Add(pressed);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(Control.BackgroundProperty, Brush(36, 36, 36)));
        disabled.Setters.Add(new Setter(Control.ForegroundProperty, Brush(130, 130, 130)));
        disabled.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(58, 58, 58)));
        style.Triggers.Add(disabled);
        return style;
    }

    internal static void ApplyFrame(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        // A neutral edge: no bronze (AGENTS.md, 절대 금지 색).
        int enabled = 1, caption = 0x00111111, text = 0x00D8E9F1, border = 0x00333333;
        DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
        DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
    }

    private static void CenterInWorkArea(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        IntPtr anchor = new WindowInteropHelper(window.Owner).Handle;
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(anchor, 2), ref monitor) || !GetWindowRect(handle, out var bounds)) return;
        if (!GetWindowRect(anchor, out var ownerBounds)) ownerBounds = monitor.Work;
        var dpi = VisualTreeHelper.GetDpi(window);
        int width = Math.Min(monitor.Work.Right - monitor.Work.Left, bounds.Right - bounds.Left);
        int height = Math.Min(monitor.Work.Bottom - monitor.Work.Top, bounds.Bottom - bounds.Top);
        window.MinWidth = Math.Min(240, width / dpi.DpiScaleX);
        window.MinHeight = Math.Min(160, height / dpi.DpiScaleY);
        int left = Math.Clamp(ownerBounds.Left + (ownerBounds.Right - ownerBounds.Left - width) / 2,
            monitor.Work.Left, monitor.Work.Right - width);
        int top = Math.Clamp(ownerBounds.Top + (ownerBounds.Bottom - ownerBounds.Top - height) / 2,
            monitor.Work.Top, monitor.Work.Bottom - height);
        SetWindowPos(handle, IntPtr.Zero, left, top, width, height, 0x0014);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { internal int Size; internal NativeRect Monitor, Work; internal uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
        int left, int top, int width, int height, uint flags);
}
