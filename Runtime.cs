// Consolidated TerminalNotes source. Each region preserves an original compilation unit.
// TN_SOURCE_* symbols let focused tests compile their original dependency set.

#if TN_WPF || TN_SOURCE_ASSEMBLYINFO_CS
// Source: AssemblyInfo.cs
using System.Windows;

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
#endif

#if TN_WPF || TN_SOURCE_APP_XAML_CS
#region App.xaml.cs
namespace TerminalNotes
{
using global::System.Configuration;
using global::System.Data;
using global::System.Windows;
using global::System.IO;
using global::System.Runtime.InteropServices;
using global::TerminalNotes.Services;


/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private AppInstanceCoordinator? _instance;
    private bool _diagnosticsStarted;
    private bool _fatalExceptionObserved;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => AppMessageDialog.ApplyFrame((Window)sender)));
        string settingsPath = TerminalNotes.MainWindow.DefaultSettingsPath;
        string? workspace = null;
        int workspaceIndex = Array.IndexOf(e.Args, "--workspace");
        if (workspaceIndex >= 0 && workspaceIndex + 1 < e.Args.Length)
            try { workspace = Path.GetFullPath(e.Args[workspaceIndex + 1]); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { }
        try
        {
            // Retry acquisition too: the previous process might be exiting.
            var waiting = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                _instance = AppInstanceCoordinator.TryAcquire(settingsPath);
                if (_instance is not null) break;
                if (AppInstanceCoordinator.ReadOwnerProcessId(settingsPath) is int processId)
                    AllowSetForegroundWindow(processId);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                if (await AppInstanceCoordinator.TryActivateAsync(settingsPath, workspace, timeout.Token))
                {
                    Shutdown();
                    return;
                }
                await Task.Delay(100);
            } while (waiting.Elapsed < TimeSpan.FromSeconds(10));
            if (_instance is null)
            {
                MessageBox.Show("이미 실행 중인 Terminal Notes 창이 응답을 기다리고 있습니다. 트레이의 기존 창을 확인하세요. 기존 작업은 그대로 유지됩니다.", "Terminal Notes");
                Shutdown();
                return;
            }
            ApplicationDiagnostics.BeginSession();
            _diagnosticsStarted = true;
            DispatcherUnhandledException += (_, args) =>
            {
                _fatalExceptionObserved = true;
                ApplicationDiagnostics.RecordException("dispatcher_unhandled_exception", args.Exception, fatal: true);
                ApplicationDiagnostics.Flush();
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                _fatalExceptionObserved |= args.IsTerminating;
                if (args.ExceptionObject is Exception error)
                    ApplicationDiagnostics.RecordException("domain_unhandled_exception", error, fatal: args.IsTerminating);
                else ApplicationDiagnostics.Record("domain_unhandled_exception", "application", "unknown_exception");
                ApplicationDiagnostics.Flush();
            };
            TaskScheduler.UnobservedTaskException += (_, args) =>
                ApplicationDiagnostics.RecordException("unobserved_task_exception", args.Exception);
            try { GlobalAgentInstructions.EnsureFile(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ApplicationDiagnostics.RecordException("global_instructions_unavailable", exception);
                // A busy instruction store can be retried when the editor or a terminal opens.
            }
            var window = new TerminalNotes.MainWindow();
            MainWindow = window;
            _instance.StartListening(async requestedProject => await Dispatcher.InvokeAsync(() => window.ActivateFromLauncher(requestedProject)));
            window.Show();
        }
        catch (Exception exception)
        {
            if (!_diagnosticsStarted)
            {
                ApplicationDiagnostics.BeginSession();
                _diagnosticsStarted = true;
            }
            ApplicationDiagnostics.RecordException("startup_failed", exception, fatal: true);
            MessageBox.Show("Terminal Notes 시작 실패 · " + exception.Message, "Terminal Notes");
            Shutdown(1);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is TerminalNotes.MainWindow window && !window.PrepareForSystemShutdown()) e.Cancel = true;
        base.OnSessionEnding(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            var window = MainWindow as TerminalNotes.MainWindow;
            window?.DisposeResources();
            _instance?.Dispose();
            base.OnExit(e);
            if (_diagnosticsStarted)
            {
                ApplicationDiagnostics.Record("application_exit", "application", "exit_" + e.ApplicationExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (window?.HasPendingTerminalCleanup == true)
                    ApplicationDiagnostics.Record("shutdown_cleanup_pending", "terminal");
                if (e.ApplicationExitCode == 0 && !_fatalExceptionObserved && (window is null || window.ResourcesCleanlyDisposed))
                    ApplicationDiagnostics.CompleteNormalExit();
            }
        }
        catch (Exception exception)
        {
            if (_diagnosticsStarted) ApplicationDiagnostics.RecordException("shutdown_cleanup_failed", exception);
            throw;
        }
        finally
        {
            if (_diagnosticsStarted) ApplicationDiagnostics.Flush();
        }
    }
}

}
#endregion
#endif

#if TN_WPF || TN_SOURCE_AUTOMATIONACTIVITYINDICATOR_CS
#region AutomationActivityIndicator.cs
namespace TerminalNotes
{
using global::System.Windows;
using global::System.Windows.Automation;
using global::System.Windows.Media;
using global::System.Windows.Media.Animation;


/// <summary>An activity signal, never an estimated completion percentage.</summary>
internal sealed class AutomationActivityIndicator : FrameworkElement
{
    private static readonly DependencyProperty PulseProperty = DependencyProperty.Register(
        nameof(Pulse), typeof(double), typeof(AutomationActivityIndicator),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private double Pulse { get => (double)GetValue(PulseProperty); set => SetValue(PulseProperty, value); }
    private bool _active, _needsInput, _completed;
    private Brush _accent = Brushes.Gray;
    public bool IsAnimating { get; private set; }

    public AutomationActivityIndicator()
    {
        Width = 48;
        Height = 48;
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }

    public void SetStatus(bool active, bool needsInput, bool completed, Brush accent, string description)
    {
        _active = active;
        _needsInput = needsInput;
        _completed = completed;
        _accent = accent;
        AutomationProperties.SetName(this, description);
        UpdateAnimation();
        InvalidateVisual();
    }

    private void UpdateAnimation()
    {
        bool animate = _active && IsVisible;
        if (animate == IsAnimating) return;
        IsAnimating = animate;
        BeginAnimation(PulseProperty, animate ? new DoubleAnimation(0, Math.PI * 2, TimeSpan.FromSeconds(1.2))
        {
            RepeatBehavior = RepeatBehavior.Forever
        } : null);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        double scale = Math.Min(ActualWidth, ActualHeight) / 48;
        if (scale <= 0) return;
        drawing.PushTransform(new TranslateTransform((ActualWidth - 48 * scale) / 2, (ActualHeight - 48 * scale) / 2));
        drawing.PushTransform(new ScaleTransform(scale, scale));
        drawing.PushOpacity(.12);
        drawing.DrawEllipse(_accent, null, new Point(24, 24), 22, 22);
        drawing.Pop();
        drawing.DrawEllipse(null, new Pen(_accent, 2), new Point(24, 24), 20, 20);
        if (_completed)
        {
            var pen = new Pen(_accent, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            drawing.DrawLine(pen, new Point(15, 24), new Point(21, 30));
            drawing.DrawLine(pen, new Point(21, 30), new Point(33, 18));
        }
        else if (_needsInput)
        {
            drawing.DrawRoundedRectangle(_accent, null, new Rect(17, 16, 5, 16), 1, 1);
            drawing.DrawRoundedRectangle(_accent, null, new Rect(26, 16, 5, 16), 1, 1);
        }
        else
        {
            for (int bar = 0; bar < 3; bar++)
            {
                double height = _active ? 8 + 12 * (.5 + .5 * Math.Sin(Pulse + bar * 1.7)) : 4;
                drawing.DrawRoundedRectangle(_accent, null, new Rect(15 + bar * 7, 24 - height / 2, 4, height), 2, 2);
            }
        }
        drawing.Pop();
        drawing.Pop();
    }
}
}
#endregion
#endif

#if TN_WPF || TN_SOURCE_CONTROLS_NATIVETERMINALSURFACE_CS
#region Controls/NativeTerminalSurface.cs
namespace TerminalNotes.Controls
{
using global::System.Globalization;
using global::System.Text;
using global::System.Collections.Concurrent;
using global::System.Windows;
using global::System.Windows.Input;
using global::System.Windows.Media;
using global::System.Windows.Controls;
using global::System.Windows.Threading;
using global::TerminalNotes.TerminalEngine;


/// <summary>A lightweight WPF renderer and input surface for the portable VT model.</summary>
public sealed class NativeTerminalSurface : FrameworkElement
{
    public const int MinimumColumns = 40;
    public const int MinimumRows = 24;
    private static readonly Typeface NormalTypeface = new(new FontFamily("Cascadia Mono"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface BoldTypeface = new(new FontFamily("Cascadia Mono"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface ItalicTypeface = new(new FontFamily("Cascadia Mono"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
    private readonly TerminalEmulator _terminal;
    private readonly ConcurrentQueue<string> _outputQueue = new();
    private int _outputDrainPosted;
    private long _queuedOutputCharacters;
    private readonly DispatcherTimer _outputDrainTimer;
    private string? _outputRemainder;
    private int _outputRemainderOffset;
    private int _surfaceDisposed;
    private readonly DispatcherTimer _cursorTimer;
    private TerminalSnapshot? _snapshot;
    private double _fontSize = 15;
    private double _cellWidth = 8.5;
    private double _cellHeight = 17;
    private double _measuredFontSize = double.NaN;
    private double _measuredPixelsPerDip = double.NaN;
    private CultureInfo? _measuredCulture;
    private int _scrollbackOffset;
    private bool _cursorOn = true;
    private bool _draggingSelection;
    private bool _hasSelection;
    private bool _selectAll;
    private bool _focused;
    private readonly TextBox _inputHost;
    private bool _imeComposing;
    private bool _completingForShortcut;
    // Keys the IME swallowed (ImeProcessed) without ever starting a composition or committing text.
    private int _imeKeysWithoutComposition;
    private const int ImeStallKeyLimit = 3;
    /// <summary>Raised when Hangul input stalled (IME took keys but composed nothing) and was reconnected.</summary>
    public event Action<string>? ImeRecovered;
    /// <summary>Stage codes (never typed text) that show how far Hangul input got: IME composition,
    /// commit, delivery to the terminal, and whether the program in the terminal echoed anything.</summary>
    public event Action<string>? ImeStage;
    private bool _imeEnvironmentReported, _imeCompositionReported, _hangulCommitReported;
    private bool _awaitingHangulEcho;
    private int _hangulNoEchoReports;
    private DispatcherTimer? _hangulEchoTimer;
    private string _imePreedit = string.Empty;
    private int _imeCommitTimestamp = int.MinValue;
    // Some IME setups commit a syllable into the input TextBox without a TextInput event. Those syllables piled
    // up unsent, so only the composing one was ever visible ("텍" vanished when typing "스"). The TextBox content
    // is the fallback: _hostDelivered of its characters have reached the terminal; text the fallback sent ahead
    // of a late TextInput is claimed by it instead of going out twice.
    private int _hostDelivered;
    private string _unclaimedHostText = string.Empty;
    private bool _hostCheckPending, _hostFallbackReported;
    // Enter pressed while the line's last syllable is still composing. Some IMEs route that Enter before they
    // commit the syllable, or consume it to commit; dropping it left Korean commands unsent. It is held and
    // sent right after the commit.
    private bool _enterAfterCommit, _enterFromIme, _enterHoldReported;
    private int _enterAfterCommitTimestamp = int.MinValue, _sentEnterTimestamp = int.MinValue, _enterCommitWaits;
    private bool _enterShift, _enterAlt, _enterControl;
    private DispatcherTimer? _enterCommitTimer;
    private static readonly TimeSpan EnterCommitWait = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan EnterPassOnWait = TimeSpan.FromMilliseconds(120);
    private bool _minimumViewportEnabled;
    private bool _forceStateBackground;
    private ScrollViewer? _hostViewport;
    private int _anchorColumn, _anchorRow, _anchorScrollbackOffset;
    private int _selectionColumn, _selectionRow;
    private Brush _background = new SolidColorBrush(Color.FromRgb(12, 16, 22));
    private Brush _foreground = new SolidColorBrush(Color.FromRgb(237, 243, 251));
    private Brush _selection = new SolidColorBrush(Color.FromArgb(150, 67, 83, 107));
    private short _lastColumns, _lastRows;
    private long _renderedVersion = -1;
    // Each visible row is drawn once into a frozen Drawing and reused while its cells, the colours and the cell size stay the
    // same. Redrawing every style run of every row on each output frame (a FormattedText and new brushes per run) took ~17 ms
    // and 2 MB per frame for one OpenCode screen, on the UI thread every terminal and the window share: three busy terminals
    // stuttered and froze.
    private readonly List<(TerminalLine? Line, Drawing? Drawing)> _rowCache = [];
    private (double Width, double Height, double PixelsPerDip, int Columns, int Theme) _rowCacheKey;
    private int _themeVersion;
    private readonly Dictionary<Color, SolidColorBrush> _brushes = [];
    private static readonly Brush ScrollRail = Frozen(Color.FromRgb(20, 27, 36));
    private static readonly Brush ScrollThumb = Frozen(Color.FromRgb(98, 119, 142));

    public event Func<string, bool, long?, Exception?>? Input;
    public event Action<Exception>? InputFailed;
    public event Action<long, Exception?>? TextSendCompleted;
    public event Action<short, short>? DimensionsChanged;
    public event Action? Activated;
    public event Action? PasteRequested;
    public event Action<bool>? ContextMenuRequested;
    public event Action<string, bool>? Shortcut;
    public event Action? SelectionChanged;
    public event Action? CopyRequested;

    public NativeTerminalSurface(TerminalEmulator terminal)
    {
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        Focusable = true;
        KeyboardNavigation.SetIsTabStop(this, true);
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        // The terminal grid keeps pixel-snapped Display text; the UI around it uses Ideal.
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        InputMethod.SetIsInputMethodEnabled(this, true);
        // A real TextBox supplies WPF's editable TSF text store. FrameworkElement's
        // default store can prematurely commit subsequent Korean syllables.
        _inputHost = new TextBox
        {
            Background = Brushes.Transparent, Foreground = Brushes.Transparent,
            CaretBrush = Brushes.Transparent, SelectionBrush = Brushes.Transparent, SelectionTextBrush = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), Margin = new Thickness(0),
            FontFamily = NormalTypeface.FontFamily, FontSize = _fontSize,
            AcceptsReturn = false, AcceptsTab = false, IsUndoEnabled = false,
            IsHitTestVisible = false, FocusVisualStyle = null,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden
        };
        InputMethod.SetIsInputMethodEnabled(_inputHost, true);
        AddVisualChild(_inputHost);
        AddLogicalChild(_inputHost);
        _inputHost.TextChanged += (_, _) => { InvalidateArrange(); ScheduleHostCommitCheck(); };
        AddHandler(TextCompositionManager.TextInputStartEvent, new TextCompositionEventHandler(OnCompositionChanged), true);
        AddHandler(TextCompositionManager.TextInputUpdateEvent, new TextCompositionEventHandler(OnCompositionChanged), true);
        _terminal.Changed += OnTerminalChanged;
        _cursorTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Render, (_, _) =>
        {
            _cursorOn = !_cursorOn;
            // Only a focused terminal draws its cursor; the others need no redraw for the blink.
            if (_focused) InvalidateVisual();
        }, Dispatcher);
        _cursorTimer.Start();
        _outputDrainTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(1)
        };
        _outputDrainTimer.Tick += (_, _) =>
        {
            _outputDrainTimer.Stop();
            DrainOutput();
        };
        _snapshot = _terminal.GetSnapshot();
        SizeChanged += (_, _) => UpdateDimensions();
        Loaded += (_, _) => UpdateDimensions();
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue) { _draggingSelection = false; ReleaseMouseCapture(); }
        };
        Unloaded += (_, _) => { _draggingSelection = false; ReleaseMouseCapture(); };
        GotKeyboardFocus += (_, e) => { if (_completingForShortcut) return; if (ReferenceEquals(e.NewFocus, this)) FocusSurface(); else RestoreImeContext(); _focused = true; _cursorOn = true; InvalidateVisual(); Activated?.Invoke(); };
        LostKeyboardFocus += (_, _) =>
        {
            _focused = IsKeyboardFocusWithin;
            // A composition abandoned by a focus change never commits; a stale composing
            // flag would otherwise keep swallowing Enter/Backspace/arrows after focus returns.
            // Syllables committed before it still go out; the abandoned one does not.
            if (!_focused && _imeComposing)
            {
                DeliverUnannouncedHostText();
                _hostDelivered = _inputHost.Text.Length;
                ResetStaleComposition();
                ClearInputHostWhenIdle();
            }
            _cursorOn = true;
            InvalidateVisual();
        };
        PreviewMouseDown += OnPreviewMouseDown;
        PreviewMouseMove += OnPreviewMouseMove;
        PreviewMouseUp += OnPreviewMouseUp;
        PreviewMouseWheel += OnPreviewMouseWheel;
        PreviewKeyDown += OnPreviewKeyDown;
        AddHandler(TextCompositionManager.TextInputEvent, new TextCompositionEventHandler(OnTextInput), true);
    }

    public int ScrollbackOffset => _scrollbackOffset;
    public bool HasSelection => _hasSelection;
    public string SelectedText => GetSelectedText();

    public double MinimumViewportWidth
    {
        get { MeasureCell(); return MinimumColumns * _cellWidth + 20; }
    }

    public double MinimumViewportHeight
    {
        get { MeasureCell(); return MinimumRows * _cellHeight + 4; }
    }

    /// <summary>One character cell, before the viewport's scale.</summary>
    internal Size CellSize
    {
        get { MeasureCell(); return new Size(_cellWidth, _cellHeight); }
    }

    /// <summary>Fits the complete TUI into its pane while keeping its VT grid at least 40 by 24.</summary>
    public static ScrollViewer CreateScrollableViewport(NativeTerminalSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        surface._minimumViewportEnabled = true;
        var scale = new ScaleTransform(1, 1);
        surface.LayoutTransform = scale;
        var viewport = new ScrollViewer
        {
            Content = surface,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Focusable = false,
            Background = surface._background
        };
        surface._hostViewport = viewport;
        bool fitting = false;

        void FitSurfaceToViewport()
        {
            double availableWidth = viewport.ViewportWidth;
            double availableHeight = viewport.ViewportHeight;
            if (fitting || !double.IsFinite(availableWidth) || !double.IsFinite(availableHeight)
                || availableWidth <= 0 || availableHeight <= 0) return;
            fitting = true;
            try
            {
                // Keep OpenCode's safe logical grid, but show every row in small split panes.
                // LayoutTransform also maps pointer and text input coordinates back to the surface.
                double factor = Math.Min(1, Math.Min(availableWidth / surface.MinimumViewportWidth,
                    availableHeight / surface.MinimumViewportHeight));
                double width = availableWidth / factor;
                double height = availableHeight / factor;
                if (Math.Abs(scale.ScaleX - factor) > 0.000001) scale.ScaleX = factor;
                if (Math.Abs(scale.ScaleY - factor) > 0.000001) scale.ScaleY = factor;
                if (double.IsNaN(surface.Width) || Math.Abs(surface.Width - width) > 0.5) surface.Width = width;
                if (double.IsNaN(surface.Height) || Math.Abs(surface.Height - height) > 0.5) surface.Height = height;
            }
            finally { fitting = false; }
        }

        viewport.SizeChanged += (_, _) => FitSurfaceToViewport();
        viewport.Loaded += (_, _) => FitSurfaceToViewport();
        viewport.ScrollChanged += (_, e) =>
        {
            if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0) FitSurfaceToViewport();
        };
        surface.SizeChanged += (_, _) => FitSurfaceToViewport();
        return viewport;
    }

    public void SetTheme(string background, string foreground, string selectionBackground, bool forceStateBackground = false)
    {
        Color back = ParseColor(background, Colors.Black), fore = ParseColor(foreground, Colors.White);
        Color selection = ParseColor(selectionBackground, Color.FromRgb(67, 83, 107));
        if (forceStateBackground) selection = Color.FromArgb(145, selection.R, selection.G, selection.B);
        // The same colours again (the activity refresh repeats them) keep the drawn rows.
        if (_background is SolidColorBrush { Color: var oldBack } && oldBack == back && _foreground is SolidColorBrush { Color: var oldFore } &&
            oldFore == fore && _selection is SolidColorBrush { Color: var oldSelection } && oldSelection == selection &&
            _forceStateBackground == forceStateBackground) return;
        _background = new SolidColorBrush(back);
        _foreground = new SolidColorBrush(fore);
        _selection = new SolidColorBrush(selection);
        _forceStateBackground = forceStateBackground;
        _themeVersion++;
        if (_hostViewport is not null) _hostViewport.Background = _background;
        InvalidateVisual();
    }

    public void Feed(string text)
    {
        if (string.IsNullOrEmpty(text) || Volatile.Read(ref _surfaceDisposed) != 0) return;
        long queued = Interlocked.Add(ref _queuedOutputCharacters, text.Length);
        if (queued > 8_000_000)
        {
            while (_outputQueue.TryDequeue(out var discarded)) Interlocked.Add(ref _queuedOutputCharacters, -discarded.Length);
            const string warning = "\x1b[0m\r\n[화면 출력이 너무 많아 일부 출력을 생략했습니다]\r\n";
            _outputQueue.Enqueue(warning);
            Interlocked.Add(ref _queuedOutputCharacters, warning.Length);
        }
        _outputQueue.Enqueue(text);
        ScheduleOutputDrain();
    }

    public void FocusSurface()
    {
        if (!_inputHost.IsKeyboardFocused) Keyboard.Focus(_inputHost);
        RestoreImeContext();
    }

    [global::System.Runtime.InteropServices.DllImport("imm32.dll")]
    private static extern bool ImmAssociateContextEx(IntPtr window, IntPtr context, uint flags);

    // After focus passes through a hosted native window (WebView2 and similar), the top-level
    // window can be left without its default IME context: English still types, but the Korean
    // IME can no longer compose. Re-attaching the default context restores Hangul input and
    // leaves the user's 한/영 choice unchanged.
    private void RestoreImeContext()
    {
        const uint IACE_DEFAULT = 0x10;
        if (PresentationSource.FromVisual(this) is not global::System.Windows.Interop.HwndSource { Handle: var window } ||
            window == IntPtr.Zero) return;
        try { ImmAssociateContextEx(window, IntPtr.Zero, IACE_DEFAULT); }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException) { }
    }

    public void SelectAllText()
    {
        _snapshot ??= _terminal.GetSnapshot();
        _scrollbackOffset = 0;
        _selectAll = true;
        _hasSelection = true;
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    public void ClearSelectionText() => SetSelection(false);
    public void ClearTerminal()
    {
        _terminal.Clear();
        _scrollbackOffset = 0;
        SetSelection(false);
    }

    public void ScrollToBottom()
    {
        _scrollbackOffset = 0;
        InvalidateVisual();
    }

    public void SendText(string text, bool submit, long sendId)
    {
        try
        {
            var snapshot = _snapshot ?? _terminal.GetSnapshot();
            Exception? error = Emit(Encoding.UTF8.GetString(TerminalInputEncoder.EncodePaste(text, snapshot.BracketedPaste)), true, sendId);
            if (error is not null) throw error;
            if (submit)
            {
                error = Emit(Encoding.UTF8.GetString(TerminalInputEncoder.EncodeKey(TerminalKey.Enter)), true, sendId);
                if (error is not null) throw error;
            }
            TextSendCompleted?.Invoke(sendId, null);
        }
        catch (Exception exception) { TextSendCompleted?.Invoke(sendId, exception); }
    }

    /// <summary>A lone Enter keystroke, as if typed (after a paste has settled).</summary>
    public void SendEnter() => Send(Encoding.UTF8.GetString(TerminalInputEncoder.EncodeKey(TerminalKey.Enter)));

    public void PasteText(string text)
    {
        var snapshot = _snapshot ?? _terminal.GetSnapshot();
        Send(Encoding.UTF8.GetString(TerminalInputEncoder.EncodePaste(text, snapshot.BracketedPaste)));
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => index == 0
        ? _inputHost : throw new ArgumentOutOfRangeException(nameof(index));

    protected override global::System.Collections.IEnumerator LogicalChildren => new[] { _inputHost }.GetEnumerator();

    protected override Size ArrangeOverride(Size finalSize)
    {
        var snapshot = _snapshot ?? _terminal.GetSnapshot();
        double x = Math.Clamp(snapshot.CursorColumn * _cellWidth, 0, Math.Max(0, finalSize.Width - _cellWidth));
        double y = Math.Clamp(snapshot.CursorRow * _cellHeight, 0, Math.Max(0, finalSize.Height - _cellHeight));
        // Keep the actual TSF caret at the terminal cursor even while committed
        // prefixes remain in the text store until its composition finishes.
        Rect caret = _inputHost.GetRectFromCharacterIndex(_inputHost.SelectionStart);
        double caretOffset = caret.IsEmpty ? 0 : caret.X;
        _inputHost.Arrange(new Rect(x - caretOffset, y, Math.Max(_cellWidth * 4, finalSize.Width), _cellHeight + 4));
        return finalSize;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        MeasureCell();
        _inputHost.FontSize = _fontSize;
        _inputHost.Measure(new Size(double.PositiveInfinity, _cellHeight + 4));
        double width = double.IsInfinity(availableSize.Width) ? 80 * _cellWidth : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 24 * _cellHeight : availableSize.Height;
        return new Size(Math.Max(_cellWidth * 2, width), Math.Max(_cellHeight * 2, height));
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        MeasureCell();
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(_background, null, bounds);
        var snapshot = _snapshot ??= _terminal.GetSnapshot(_scrollbackOffset);
        if (snapshot.Version != _renderedVersion || snapshot.ScrollbackCount < _scrollbackOffset)
        {
            _scrollbackOffset = Math.Clamp(_scrollbackOffset, 0, snapshot.ScrollbackCount);
            snapshot = _snapshot = _terminal.GetSnapshot(_scrollbackOffset);
            _renderedVersion = snapshot.Version;
        }

        int visibleRows = Math.Min(snapshot.Rows, Math.Max(1, (int)Math.Ceiling(ActualHeight / _cellHeight)));
        int firstLine = Math.Max(0, snapshot.Lines.Count - visibleRows);
        var key = (_cellWidth, _cellHeight, _measuredPixelsPerDip, snapshot.Columns, _themeVersion);
        if (key != _rowCacheKey)
        {
            _rowCache.Clear();
            _rowCacheKey = key;
        }
        for (int row = 0; row < visibleRows; row++)
        {
            int lineIndex = firstLine + row;
            if (lineIndex >= snapshot.Lines.Count) break;
            dc.DrawDrawing(RowDrawing(snapshot.Columns, snapshot.Lines[lineIndex], row));
        }
        DrawSelection(dc, visibleRows, snapshot.Columns);
        if (_focused && _cursorOn && snapshot.CursorVisible && _scrollbackOffset == 0)
            DrawCursor(dc, snapshot);

        if (_focused && _imeComposing && !string.IsNullOrEmpty(_imePreedit))
        {
            double x = snapshot.CursorColumn * _cellWidth, y = snapshot.CursorRow * _cellHeight;
            var preedit = new FormattedText(_imePreedit, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                NormalTypeface, _fontSize, _foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawRectangle(_background, null, new Rect(x, y, Math.Max(_cellWidth, preedit.WidthIncludingTrailingWhitespace), _cellHeight));
            dc.DrawText(preedit, new Point(x, y));
            dc.DrawLine(new Pen(_foreground, 1), new Point(x, y + _cellHeight - 1),
                new Point(x + Math.Max(_cellWidth, preedit.WidthIncludingTrailingWhitespace), y + _cellHeight - 1));
        }

        if (snapshot.ScrollbackCount > 0)
        {
            double railWidth = 10;
            double railX = Math.Max(0, ActualWidth - railWidth - 3);
            dc.DrawRectangle(ScrollRail, null, new Rect(railX, 8, railWidth, Math.Max(1, ActualHeight - 16)));
            double ratio = snapshot.Rows / (double)(snapshot.Rows + snapshot.ScrollbackCount);
            double thumbHeight = Math.Max(18, (ActualHeight - 16) * ratio);
            double maxTop = Math.Max(1, ActualHeight - 16 - thumbHeight);
            double top = maxTop * (1 - _scrollbackOffset / (double)snapshot.ScrollbackCount);
            dc.DrawRoundedRectangle(ScrollThumb, null, new Rect(railX + 1, 8 + top, railWidth - 2, thumbHeight), 4, 4);
        }
    }

    // The row's cached drawing while its cells are the same; otherwise it is drawn again.
    private Drawing RowDrawing(int columns, TerminalLine line, int row)
    {
        while (_rowCache.Count <= row) _rowCache.Add((null, null));
        var (cachedLine, cachedDrawing) = _rowCache[row];
        if (cachedDrawing is not null && cachedLine is not null && SameCells(cachedLine, line))
        {
            _rowCache[row] = (line, cachedDrawing);
            return cachedDrawing;
        }
        var drawing = new DrawingGroup();
        using (var context = drawing.Open()) DrawLine(context, columns, line, row);
        if (drawing.CanFreeze) drawing.Freeze();
        _rowCache[row] = (line, drawing);
        return drawing;
    }

    private static bool SameCells(TerminalLine before, TerminalLine after)
    {
        if (ReferenceEquals(before, after)) return true;
        if (before.Cells.Count != after.Cells.Count) return false;
        for (int index = 0; index < before.Cells.Count; index++)
        {
            TerminalCell a = before.Cells[index], b = after.Cells[index];
            if (!ReferenceEquals(a, b) && (a.Width != b.Width || a.Style != b.Style || !string.Equals(a.Text, b.Text, StringComparison.Ordinal))) return false;
        }
        return true;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // One frozen brush per colour (a true-colour screen can use many: the set is dropped when it grows large).
    private SolidColorBrush BrushFor(Color color)
    {
        if (_brushes.TryGetValue(color, out var brush)) return brush;
        if (_brushes.Count > 1024) _brushes.Clear();
        return _brushes[color] = Frozen(color);
    }

    private void DrawLine(DrawingContext dc, int columns, TerminalLine line, int row)
    {
        int column = 0;
        int start = 0;
        while (start < line.Cells.Count && column < columns)
        {
            TerminalStyle style = line.Cells[start].Style;
            int runStart = column;
            var run = new StringBuilder();
            int runCells = 0;
            while (start < line.Cells.Count && line.Cells[start].Style == style && column < columns)
            {
                TerminalCell cell = line.Cells[start++];
                if (cell.Width == 0)
                {
                    if (run.Length > 0) run.Append(cell.Text);
                    continue;
                }
                run.Append(string.IsNullOrEmpty(cell.Text) ? " " : cell.Text);
                int width = Math.Clamp(cell.Width, 1, 2);
                column += width;
                runCells += width;
            }
            if (run.Length > 0) DrawRun(dc, run.ToString(), style, runStart, row, runCells);
        }
    }

    private void DrawRun(DrawingContext dc, string text, TerminalStyle style, int column, int row, int cells)
    {
        Color themeForeground = ((SolidColorBrush)_foreground).Color;
        Color stateBackground = ((SolidColorBrush)_background).Color;
        Color sourceForeground = Resolve(style.Foreground, themeForeground);
        Color sourceBackground = Resolve(style.Background, stateBackground);
        Color foreground = style.Inverse ? sourceBackground : sourceForeground;
        Color background = style.Inverse ? sourceForeground : sourceBackground;
        if (_forceStateBackground)
        {
            // Keep the activity color as the complete terminal surface, including
            // cells which carry explicit ANSI backgrounds or inverse video.
            background = stateBackground;
            if (style.Inverse && style.Background.IsDefault)
                foreground = themeForeground;
            foreground = EnsureReadableForeground(foreground, stateBackground);
        }
        if (background != stateBackground)
            dc.DrawRectangle(BrushFor(background), null, new Rect(column * _cellWidth, row * _cellHeight, cells * _cellWidth, _cellHeight));
        // A run of spaces only paints its background.
        if (!style.Underline && !style.Strike && !(_forceStateBackground && style.Inverse) && string.IsNullOrWhiteSpace(text)) return;
        Typeface typeface = style.Bold ? BoldTypeface : style.Italic ? ItalicTypeface : NormalTypeface;
        SolidColorBrush ink = BrushFor(foreground);
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface,
            _fontSize, ink, _measuredPixelsPerDip)
        { MaxTextWidth = Math.Max(_cellWidth, cells * _cellWidth + _cellWidth), MaxTextHeight = _cellHeight, Trimming = TextTrimming.None };
        dc.DrawText(formatted, new Point(column * _cellWidth, row * _cellHeight));
        if (style.Underline || (_forceStateBackground && style.Inverse))
            dc.DrawLine(new Pen(ink, 1), new Point(column * _cellWidth, (row + 1) * _cellHeight - 1), new Point((column + cells) * _cellWidth, (row + 1) * _cellHeight - 1));
        if (style.Strike)
            dc.DrawLine(new Pen(ink, 1), new Point(column * _cellWidth, row * _cellHeight + _cellHeight / 2), new Point((column + cells) * _cellWidth, row * _cellHeight + _cellHeight / 2));
    }

    private void DrawSelection(DrawingContext dc, int visibleRows, int columns)
    {
        if (!_hasSelection) return;
        if (_selectAll)
        {
            dc.DrawRectangle(_selection, null, new Rect(0, 0, columns * _cellWidth, visibleRows * _cellHeight));
            return;
        }
        if (_anchorScrollbackOffset != _scrollbackOffset) return;
        var (startColumn, startRow, endColumn, endRow) = OrderedSelection();
        int top = Math.Max(0, startRow);
        int bottom = Math.Min(visibleRows - 1, endRow);
        for (int row = top; row <= bottom; row++)
        {
            int left = row == startRow ? startColumn : 0;
            int right = row == endRow ? endColumn + 1 : columns;
            if (right <= left) continue;
            dc.DrawRectangle(_selection, null, new Rect(left * _cellWidth, row * _cellHeight, (right - left) * _cellWidth, _cellHeight));
        }
    }

    private void DrawCursor(DrawingContext dc, TerminalSnapshot snapshot)
    {
        if (snapshot.CursorColumn < 0 || snapshot.CursorColumn >= snapshot.Columns || snapshot.CursorRow < 0 || snapshot.CursorRow >= snapshot.Rows) return;
        double x = snapshot.CursorColumn * _cellWidth, y = snapshot.CursorRow * _cellHeight;
        var brush = _foreground;
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(180, ((SolidColorBrush)brush).Color.R, ((SolidColorBrush)brush).Color.G, ((SolidColorBrush)brush).Color.B)), null,
            new Rect(x, y, Math.Max(1, _cellWidth), _cellHeight));
        if (snapshot.CursorRow < snapshot.Lines.Count && snapshot.CursorColumn < snapshot.Lines[snapshot.CursorRow].Cells.Count)
        {
            var cell = snapshot.Lines[snapshot.CursorRow].Cells[snapshot.CursorColumn];
            if (!string.IsNullOrEmpty(cell.Text))
            {
                var text = new FormattedText(cell.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, NormalTypeface,
                    _fontSize, _background, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(text, new Point(x, y));
            }
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.ImeProcessed)
        {
            // The IME consumed Enter to commit the composing syllable. Unless it passes that Enter on
            // itself, the Enter is sent once the syllable arrives.
            if (_imeComposing && e.ImeProcessedKey == Key.Enter)
            {
                QueueEnterAfterCommit(e.Timestamp, e.KeyboardDevice.Modifiers, fromIme: true);
                return;
            }
            if (!_imeEnvironmentReported)
            {
                _imeEnvironmentReported = true;
                int layout = InputLanguageManager.Current?.CurrentInputLanguage?.LCID ?? 0;
                ReportImeStage($"ime_key_seen_lcid_{layout:x4}" + (SystemParameters.IsRemoteSession ? "_remote" : ""));
            }
            // A healthy Korean IME starts or updates a composition for every key it takes. When the
            // IME link to this window broke (focus passed through another window, a redraw, a busy
            // response), keys vanish with no composition at all; reconnect instead of staying mute.
            if (++_imeKeysWithoutComposition >= ImeStallKeyLimit) RecoverStalledIme();
            return;
        }
        _imeKeysWithoutComposition = 0;
        // Committed text that is still waiting for the fallback goes out before this key's own input.
        if (!_imeComposing) DeliverUnannouncedHostText();
        if (e.Key == Key.System && e.SystemKey == Key.None) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Enter)
        {
            // This key press's Enter already went out right after its syllable's commit.
            if (e.Timestamp == _sentEnterTimestamp)
            {
                e.Handled = true;
                return;
            }
            // Enter routed before the IME committed the line's last syllable: hold it until that commit arrives.
            if (_imeComposing)
            {
                e.Handled = true;
                QueueEnterAfterCommit(e.Timestamp, e.KeyboardDevice.Modifiers, fromIme: false);
                return;
            }
            // The IME passed on the Enter it consumed to commit: this one is sent instead of the held copy.
            CancelPendingEnter();
        }
        // Backspace/Escape that arrive with the IME's commit were consumed by the IME to edit or
        // cancel the syllable. Enter is different: the Korean IME commits the last syllable AND
        // passes Enter on, so it must reach the terminal (dropping it left Korean commands unsent).
        if (e.Timestamp == _imeCommitTimestamp && key is Key.Back or Key.Escape)
        {
            e.Handled = true;
            return;
        }
        ModifierKeys mods = e.KeyboardDevice.Modifiers;
        bool shift = (mods & ModifierKeys.Shift) != 0, alt = (mods & ModifierKeys.Alt) != 0, control = (mods & ModifierKeys.Control) != 0;
        bool altGr = control && alt && e.KeyboardDevice.IsKeyDown(Key.RightAlt);
        if (_imeComposing)
        {
            bool terminalShortcut = ((key == Key.Apps || key == Key.F10 && shift) && !control)
                || !altGr && control && (key == Key.V || shift && key is Key.C or Key.T or Key.M
                    || TryEncodeControlCharacter(key, out _));
            if (!terminalShortcut) return;
            // Temporarily leave the TextBox's text store so WPF commits it before
            // terminal paste/control/shortcut handling. Keep focus inside this pane.
            _completingForShortcut = true;
            try { Keyboard.Focus(this); FocusSurface(); }
            finally { _completingForShortcut = false; }
        }
        if ((key == Key.Apps || key == Key.F10 && shift) && !control)
        {
            e.Handled = true;
            ContextMenuRequested?.Invoke(_hasSelection);
            return;
        }
        if (!altGr && control && shift && key is Key.C or Key.T or Key.M)
        {
            e.Handled = true;
            if (key == Key.C) CopyRequested?.Invoke();
            else Shortcut?.Invoke(key == Key.T ? "t" : "m", true);
            return;
        }
        if (!altGr && control && key == Key.V)
        {
            e.Handled = true;
            PasteRequested?.Invoke();
            return;
        }
        if (shift && !control && _snapshot is { ScrollbackCount: > 0 } && key is Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            var state = _snapshot;
            _scrollbackOffset = key switch
            {
                Key.PageUp => Math.Min(state.ScrollbackCount, _scrollbackOffset + state.Rows),
                Key.PageDown => Math.Max(0, _scrollbackOffset - state.Rows),
                Key.Home => state.ScrollbackCount,
                _ => 0
            };
            _snapshot = _terminal.GetSnapshot(_scrollbackOffset);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.CapsLock or Key.NumLock or Key.Scroll) return;
        if (!altGr && control && TryEncodeControlCharacter(key, out byte controlByte))
        {
            e.Handled = true;
            Send(Encoding.UTF8.GetString([controlByte]));
            return;
        }
        // TextBox handles Space as an editing command without TextInput.
        if (key == Key.Space && !control)
        {
            e.Handled = true;
            Send(Encoding.UTF8.GetString(TerminalInputEncoder.EncodeText(" ", alt)));
            return;
        }
        if (TryMapKey(key, out TerminalKey terminalKey))
        {
            e.Handled = true;
            var snapshot = _snapshot ?? _terminal.GetSnapshot();
            Send(Encoding.UTF8.GetString(TerminalInputEncoder.EncodeKey(terminalKey, shift, alt, control, snapshot.ApplicationCursorKeys)));
            return;
        }
        if (key is Key.Tab or Key.Enter or Key.Back or Key.Escape)
        {
            e.Handled = true;
            var mapped = key switch { Key.Tab => TerminalKey.Tab, Key.Enter => TerminalKey.Enter, Key.Back => TerminalKey.Backspace, _ => TerminalKey.Escape };
            Send(Encoding.UTF8.GetString(TerminalInputEncoder.EncodeKey(mapped, shift, alt, control)));
        }
    }

    private void ResetStaleComposition()
    {
        _imeComposing = false;
        _imePreedit = string.Empty;
        InvalidateArrange();
        InvalidateVisual();
    }

    private void RecoverStalledIme()
    {
        _imeKeysWithoutComposition = 0;
        bool hostFocused = _inputHost.IsKeyboardFocused;
        RestoreImeContext();
        // Re-enter the TextBox text store so TSF attaches a fresh context to it.
        _completingForShortcut = true;
        try { Keyboard.Focus(this); }
        finally { _completingForShortcut = false; }
        FocusSurface();
        ResetStaleComposition();
        ImeRecovered?.Invoke(hostFocused ? "ime_stall_reconnected" : "ime_stall_refocused");
    }

    private void OnCompositionChanged(object sender, TextCompositionEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, _inputHost)) return;
        _imeKeysWithoutComposition = 0;
        _imeComposing = !string.IsNullOrEmpty(e.TextComposition.CompositionText);
        if (_imeComposing && !_imeCompositionReported) { _imeCompositionReported = true; ReportImeStage("ime_composition_seen"); }
        _imePreedit = e.TextComposition.CompositionText;
        InvalidateArrange();
        InvalidateVisual();
        // A new or updated syllable means the previous one was committed, announced or not.
        ScheduleHostCommitCheck();
    }

    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        OnTextInputCore(e);
        if (!_enterAfterCommit || _imeComposing) return;
        // Enter that picked a Hanja candidate commits an ideograph; it chose the candidate, not the line.
        if (_enterFromIme && e.Text.Any(IsIdeograph)) CancelPendingEnter();
        // A consumed Enter waits briefly in case the IME passes it on itself.
        else if (_enterFromIme) StartEnterTimer(EnterPassOnWait);
        // The held Enter follows the committed syllable, so the line is submitted complete.
        else FlushPendingEnter("ime_enter_after_commit");
    }

    private static bool IsIdeograph(char c) => c is >= '\u3400' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF';

    private void QueueEnterAfterCommit(int timestamp, ModifierKeys modifiers, bool fromIme)
    {
        _enterAfterCommit = true;
        _enterFromIme = fromIme;
        _enterAfterCommitTimestamp = timestamp;
        _enterShift = (modifiers & ModifierKeys.Shift) != 0;
        _enterAlt = (modifiers & ModifierKeys.Alt) != 0;
        _enterControl = (modifiers & ModifierKeys.Control) != 0;
        _enterCommitWaits = 0;
        StartEnterTimer(EnterCommitWait);
    }

    private void StartEnterTimer(TimeSpan wait)
    {
        if (_enterCommitTimer is null)
        {
            _enterCommitTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher);
            _enterCommitTimer.Tick += (_, _) => OnEnterCommitTimeout();
        }
        _enterCommitTimer.Stop();
        _enterCommitTimer.Interval = wait;
        _enterCommitTimer.Start();
    }

    // No commit within the wait: leave and re-enter the TextBox's text store (which makes TSF commit the
    // syllable) and wait once more. After that the Enter is sent regardless, so a line never stays stuck.
    private void OnEnterCommitTimeout()
    {
        _enterCommitTimer?.Stop();
        if (!_enterAfterCommit || Volatile.Read(ref _surfaceDisposed) != 0) return;
        if (_imeComposing && ++_enterCommitWaits == 1)
        {
            ReportImeStage("ime_enter_forced_commit");
            _completingForShortcut = true;
            try { Keyboard.Focus(this); FocusSurface(); }
            finally { _completingForShortcut = false; }
            if (_enterAfterCommit && _imeComposing) StartEnterTimer(EnterCommitWait);
            return;
        }
        if (_imeComposing)
        {
            // The line goes out as typed: unannounced commits and the stuck last syllable come before Enter.
            ResetStaleComposition();
            DeliverUnannouncedHostText();
            ClearInputHostWhenIdle();
            FlushPendingEnter("ime_enter_without_commit");
        }
        else
        {
            DeliverUnannouncedHostText();
            FlushPendingEnter("ime_enter_after_commit");
        }
    }

    private void CancelPendingEnter()
    {
        _enterAfterCommit = false;
        _enterCommitTimer?.Stop();
    }

    private void FlushPendingEnter(string stage)
    {
        if (!_enterAfterCommit) return;
        CancelPendingEnter();
        _sentEnterTimestamp = _enterAfterCommitTimestamp;
        // The normal path happens on every Enter with some IMEs: record it once, the fallback every time.
        if (stage != "ime_enter_after_commit" || !_enterHoldReported)
        {
            _enterHoldReported |= stage == "ime_enter_after_commit";
            ReportImeStage(stage);
        }
        Send(Encoding.UTF8.GetString(TerminalInputEncoder.EncodeKey(TerminalKey.Enter, _enterShift, _enterAlt, _enterControl)));
    }

    // Never mutate the TextBox synchronously inside its TSF composition
    // callback, or between syllables while the next composition is active.
    private void ClearInputHostWhenIdle() => Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
    {
        if (!_imeComposing && Volatile.Read(ref _surfaceDisposed) == 0)
        {
            DeliverUnannouncedHostText();
            _inputHost.Clear();
            _hostDelivered = 0;
            _unclaimedHostText = string.Empty;
            InvalidateArrange();
        }
    }));

    // The committed part of the input TextBox: all of it, or all but the composing syllable at its end
    // (-1 while the composition is not visibly at the end, so nothing is guessed).
    private int CommittedHostLength()
    {
        string text = _inputHost.Text;
        if (!_imeComposing) return text.Length;
        return _imePreedit.Length > 0 && text.EndsWith(_imePreedit, StringComparison.Ordinal) ? text.Length - _imePreedit.Length : -1;
    }

    // Runs after the IME's events for a key: by then a commit's own TextInput has arrived if there is one.
    private void ScheduleHostCommitCheck()
    {
        if (_hostCheckPending || Volatile.Read(ref _surfaceDisposed) != 0) return;
        _hostCheckPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _hostCheckPending = false;
            DeliverUnannouncedHostText();
        }));
    }

    // Sends committed TextBox text that never came with a TextInput event.
    private void DeliverUnannouncedHostText()
    {
        if (Volatile.Read(ref _surfaceDisposed) != 0) return;
        string text = _inputHost.Text;
        int committed = CommittedHostLength();
        if (committed < 0) return;
        if (committed < _hostDelivered)
        {
            // The TextBox was emptied or the IME reconverted delivered text.
            _hostDelivered = committed;
            _unclaimedHostText = string.Empty;
            return;
        }
        if (committed > _hostDelivered)
        {
            string missing = text[_hostDelivered..committed];
            _hostDelivered = committed;
            _unclaimedHostText += missing;
            if (!_hostFallbackReported) { _hostFallbackReported = true; ReportImeStage("ime_commit_without_text_input"); }
            SendTypedText(missing, alt: false, imeCommit: true);
        }
        if (!_imeComposing && text.Length > 0) ClearInputHostWhenIdle();
    }

    // A commit the TextBox holds: unannounced commits before it go out first, then it counts as delivered.
    private void TrackHostCommit(string text)
    {
        string host = _inputHost.Text;
        int at = host.IndexOf(text, Math.Min(_hostDelivered, host.Length), StringComparison.Ordinal);
        if (at < 0) return;
        if (at > _hostDelivered)
        {
            if (!_hostFallbackReported) { _hostFallbackReported = true; ReportImeStage("ime_commit_without_text_input"); }
            SendTypedText(host[_hostDelivered..at], alt: false, imeCommit: true);
        }
        _hostDelivered = at + text.Length;
        _unclaimedHostText = string.Empty;
    }

    private void OnTextInputCore(TextCompositionEventArgs e)
    {
        _imeKeysWithoutComposition = 0;
        bool fromHost = ReferenceEquals(e.OriginalSource, _inputHost);
        bool imeCommit = fromHost && _imeComposing;
        // A commit announced after the next syllable started (the TextBox holds "한ㄱ" while "ㄱ" composes)
        // belongs to the previous syllable: the new composition stays.
        string host = _inputHost.Text;
        bool previousSyllable = imeCommit && _imePreedit.Length > 0 && e.Text.Length > 0 && e.Text != _imePreedit &&
            host.EndsWith(_imePreedit, StringComparison.Ordinal) && host[..^_imePreedit.Length].EndsWith(e.Text, StringComparison.Ordinal);
        if (fromHost && !previousSyllable)
        {
            if (_imeComposing) _imeCommitTimestamp = e.Timestamp;
            _imeComposing = false;
            _imePreedit = string.Empty;
            InvalidateVisual();
            ClearInputHostWhenIdle();
        }
        // WPF moves Alt-generated result text into SystemText. IME preedit
        // lives in CompositionText and must never be sent before commitment.
        string text = string.IsNullOrEmpty(e.Text) ? e.SystemText : e.Text;
        if (string.IsNullOrEmpty(text)) return;
        var keyboard = e.Device as KeyboardDevice ?? Keyboard.PrimaryDevice;
        ModifierKeys mods = keyboard.Modifiers;
        bool altGr = (mods & (ModifierKeys.Control | ModifierKeys.Alt)) == (ModifierKeys.Control | ModifierKeys.Alt)
            && keyboard.IsKeyDown(Key.RightAlt);
        if (!imeCommit && (mods & ModifierKeys.Control) != 0 && !altGr) { e.Handled = true; return; }
        e.Handled = true;
        bool alt = !imeCommit && !altGr && (!string.IsNullOrEmpty(e.SystemText) || (mods & ModifierKeys.Alt) != 0);
        if (fromHost && !alt)
        {
            // The TextBox fallback already sent this commit ahead of its late TextInput.
            if (_unclaimedHostText.StartsWith(text, StringComparison.Ordinal))
            {
                _unclaimedHostText = _unclaimedHostText[text.Length..];
                return;
            }
            TrackHostCommit(text);
        }
        SendTypedText(text, alt, imeCommit);
    }

    private void SendTypedText(string text, bool alt, bool imeCommit)
    {
        string encoded = Encoding.UTF8.GetString(TerminalInputEncoder.EncodeText(text, alt));
        if (!text.Any(character => character > 0x7F)) { Send(encoded); return; }
        Exception? error = Emit(encoded, false, null);
        if (error is not null) { ReportImeStage("ime_hangul_send_failed"); InputFailed?.Invoke(error); return; }
        if (!_hangulCommitReported) { _hangulCommitReported = true; ReportImeStage(imeCommit ? "ime_hangul_committed" : "ime_hangul_text_without_composition"); }
        WatchHangulEcho();
    }

    // English working while Hangul does not is usually decided after delivery: the program in the
    // terminal (or a server behind it) drops non-ASCII input. No screen change after delivery shows that.
    private void WatchHangulEcho()
    {
        if (_hangulNoEchoReports >= 3) return;
        _awaitingHangulEcho = true;
        _hangulEchoTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
        {
            _hangulEchoTimer!.Stop();
            if (!_awaitingHangulEcho || Volatile.Read(ref _surfaceDisposed) != 0) return;
            _awaitingHangulEcho = false;
            _hangulNoEchoReports++;
            ReportImeStage("ime_hangul_no_echo");
        }, Dispatcher);
        _hangulEchoTimer.Stop();
        _hangulEchoTimer.Start();
    }

    private void ReportImeStage(string code)
    {
        try { ImeStage?.Invoke(code); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("IME stage observer failed: " + ex.GetType().Name); }
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right)
        {
            FocusSurface();
            var snapshot = _snapshot ?? _terminal.GetSnapshot();
            if (snapshot.MouseMode != 0 && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && CanReportMouse(snapshot, e.GetPosition(this)))
                SendMouse(2, e.GetPosition(this), snapshot, release: false);
            else ContextMenuRequested?.Invoke(_hasSelection);
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        FocusSurface();
        Activated?.Invoke();
        var state = _snapshot ?? _terminal.GetSnapshot();
        Point point = e.GetPosition(this);
        if (state.MouseMode != 0 && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && CanReportMouse(state, point))
        {
            SendMouse(0, point, state, release: false);
            _draggingSelection = false;
            e.Handled = true;
            return;
        }
        (_anchorColumn, _anchorRow) = Position(point, state);
        _anchorScrollbackOffset = _scrollbackOffset;
        (_selectionColumn, _selectionRow) = (_anchorColumn, _anchorRow);
        _draggingSelection = true;
        CaptureMouse();
        SetSelection(false);
        e.Handled = true;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        var state = _snapshot ?? _terminal.GetSnapshot();
        if (_draggingSelection && e.LeftButton == MouseButtonState.Pressed)
        {
            (_selectionColumn, _selectionRow) = Position(e.GetPosition(this), state);
            SetSelection(true);
            e.Handled = true;
        }
        else if (state.MouseMode == 1003 && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ||
                 state.MouseMode == 1002 && e.LeftButton == MouseButtonState.Pressed && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (CanReportMouse(state, e.GetPosition(this)))
            {
                SendMouse(0, e.GetPosition(this), state, motion: true);
                e.Handled = true;
            }
        }
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingSelection && e.ChangedButton == MouseButton.Left)
        {
            _draggingSelection = false;
            ReleaseMouseCapture();
            (_selectionColumn, _selectionRow) = Position(e.GetPosition(this), _snapshot ?? _terminal.GetSnapshot());
            SetSelection(true);
            e.Handled = true;
            return;
        }
        var state = _snapshot ?? _terminal.GetSnapshot();
        if (state.MouseMode >= 1000 && e.ChangedButton is MouseButton.Left or MouseButton.Right && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && CanReportMouse(state, e.GetPosition(this)))
        {
            SendMouse(e.ChangedButton == MouseButton.Left ? 0 : 2, e.GetPosition(this), state, release: true);
            e.Handled = true;
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var state = _snapshot ?? _terminal.GetSnapshot();
        int steps = Math.Max(1, Math.Abs(e.Delta) / 120);
        if (state.MouseMode >= 1000 && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && CanReportMouse(state, e.GetPosition(this)))
        {
            for (int i = 0; i < steps; i++) SendMouse(e.Delta > 0 ? 64 : 65, e.GetPosition(this), state);
        }
        else
        {
            _scrollbackOffset = Math.Clamp(_scrollbackOffset + (e.Delta > 0 ? steps * 3 : -steps * 3), 0, state.ScrollbackCount);
            _snapshot = _terminal.GetSnapshot(_scrollbackOffset);
            InvalidateVisual();
        }
        e.Handled = true;
    }

    private bool CanReportMouse(TerminalSnapshot snapshot, Point point)
    {
        if (snapshot.SgrMouse) return true;
        (int column, int row) = Position(point, snapshot);
        return column <= 94 && row <= 94;
    }

    private void SendMouse(int button, Point point, TerminalSnapshot snapshot, bool release = false, bool motion = false)
    {
        (int column, int row) = Position(point, snapshot);
        ModifierKeys mods = Keyboard.Modifiers;
        byte[] bytes = snapshot.SgrMouse
            ? TerminalInputEncoder.EncodeMouse(button, column + 1, row + 1, release,
                mods.HasFlag(ModifierKeys.Shift), mods.HasFlag(ModifierKeys.Alt), mods.HasFlag(ModifierKeys.Control), motion)
            : EncodeLegacyMouse(button, column, row, release, motion,
                mods.HasFlag(ModifierKeys.Shift), mods.HasFlag(ModifierKeys.Alt), mods.HasFlag(ModifierKeys.Control));
        Send(Encoding.UTF8.GetString(bytes));
    }

    private static byte[] EncodeLegacyMouse(int button, int column, int row, bool release, bool motion,
        bool shift, bool alt, bool control)
    {
        int code = (release ? 3 : Math.Clamp(button, 0, 2)) + (shift ? 4 : 0) + (alt ? 8 : 0) + (control ? 16 : 0);
        if (motion) code += 32;
        return Encoding.ASCII.GetBytes($"\x1b[M{(char)(32 + code)}{(char)(33 + column)}{(char)(33 + row)}");
    }

    private string GetSelectedText()
    {
        if (!_hasSelection) return string.Empty;
        if (_selectAll) return GetEntireBufferText();
        var (startColumn, startRow, endColumn, endRow) = OrderedSelection();
        return _terminal.GetSelectionText(startColumn, startRow, endColumn, endRow, _anchorScrollbackOffset);
    }

    private string GetEntireBufferText() => _terminal.GetAllText();

    private (int StartColumn, int StartRow, int EndColumn, int EndRow) OrderedSelection()
    {
        bool forward = _anchorRow < _selectionRow || (_anchorRow == _selectionRow && _anchorColumn <= _selectionColumn);
        return forward ? (_anchorColumn, _anchorRow, _selectionColumn, _selectionRow) : (_selectionColumn, _selectionRow, _anchorColumn, _anchorRow);
    }

    private (int Column, int Row) Position(Point point, TerminalSnapshot snapshot)
    {
        int column = Math.Clamp((int)(point.X / _cellWidth), 0, Math.Max(0, snapshot.Columns - 1));
        int row = Math.Clamp((int)(point.Y / _cellHeight), 0, Math.Max(0, snapshot.Rows - 1));
        return (column, row);
    }

    private void Send(string text)
    {
        if (text.Length == 0) return;
        Exception? error = Emit(text, false, null);
        if (error is not null) InputFailed?.Invoke(error);
    }

    private Exception? Emit(string text, bool programmatic, long? sendId) => Input?.Invoke(text, programmatic, sendId);

    private void SetSelection(bool value)
    {
        _selectAll = false;
        if (_hasSelection == value && !value) return;
        _hasSelection = value;
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    private void OnTerminalChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(OnTerminalChanged, DispatcherPriority.Render);
            return;
        }
        int previousScrollback = _snapshot?.ScrollbackCount ?? 0;
        _awaitingHangulEcho = false;
        _snapshot = _terminal.GetSnapshot(_scrollbackOffset);
        if (_scrollbackOffset == 0) ScrollToBottom();
        else if (_snapshot.ScrollbackCount < _scrollbackOffset) _scrollbackOffset = _snapshot.ScrollbackCount;
        else if (_snapshot.ScrollbackCount > previousScrollback) _scrollbackOffset += _snapshot.ScrollbackCount - previousScrollback;
        _snapshot = _terminal.GetSnapshot(_scrollbackOffset);
        InvalidateArrange();
        InvalidateVisual();
    }

    private void ScheduleOutputDrain()
    {
        if (Volatile.Read(ref _surfaceDisposed) != 0) return;
        if (Interlocked.Exchange(ref _outputDrainPosted, 1) != 0) return;
        if (Dispatcher.HasShutdownStarted) { Interlocked.Exchange(ref _outputDrainPosted, 0); return; }
        // Several panes can keep Render-priority continuations continuously
        // ready. Schedule parsing below Input so output cannot block app clicks.
        Dispatcher.BeginInvoke(DrainOutput, DispatcherPriority.Background);
    }

    private void DrainOutput()
    {
        try
        {
            if (Volatile.Read(ref _surfaceDisposed) != 0 || Dispatcher.HasShutdownStarted) return;
            const int maximumBatchCharacters = 65536;
            var batch = new StringBuilder(maximumBatchCharacters);
            while (batch.Length < maximumBatchCharacters)
            {
                if (_outputRemainder is null)
                {
                    if (!_outputQueue.TryDequeue(out _outputRemainder)) break;
                    _outputRemainderOffset = 0;
                }
                int count = Math.Min(maximumBatchCharacters - batch.Length, _outputRemainder.Length - _outputRemainderOffset);
                // Never split a UTF-16 surrogate pair: string Feed consumes complete runes.
                int end = _outputRemainderOffset + count;
                if (end < _outputRemainder.Length && count > 0 &&
                    char.IsHighSurrogate(_outputRemainder[end - 1]) && char.IsLowSurrogate(_outputRemainder[end]))
                {
                    if (count == 1) break;
                    count--;
                }
                batch.Append(_outputRemainder, _outputRemainderOffset, count);
                Interlocked.Add(ref _queuedOutputCharacters, -count);
                _outputRemainderOffset += count;
                if (_outputRemainderOffset == _outputRemainder.Length) _outputRemainder = null;
            }
            if (batch.Length > 0) _terminal.Feed(batch.ToString());
        }
        finally
        {
            if (Volatile.Read(ref _surfaceDisposed) == 0 && !Dispatcher.HasShutdownStarted &&
                (_outputRemainder is not null || !_outputQueue.IsEmpty))
            {
                // Keep the scheduling gate while yielding: incoming output cannot
                // bypass the gap with another output operation.
                _outputDrainTimer.Start();
            }
            else
            {
                Interlocked.Exchange(ref _outputDrainPosted, 0);
                if (!_outputQueue.IsEmpty) ScheduleOutputDrain();
            }
        }
    }

    private void UpdateDimensions()
    {
        if (!IsLoaded || ActualWidth <= 0 || ActualHeight <= 0) return;
        MeasureCell();
        int minimumColumns = _minimumViewportEnabled ? MinimumColumns : 2;
        int minimumRows = _minimumViewportEnabled ? MinimumRows : 2;
        short columns = (short)Math.Clamp((int)(Math.Max(1, ActualWidth - 16) / _cellWidth), minimumColumns, 500);
        short rows = (short)Math.Clamp((int)(ActualHeight / _cellHeight), minimumRows, 500);
        if (columns == _lastColumns && rows == _lastRows) return;
        _lastColumns = columns;
        _lastRows = rows;
        DimensionsChanged?.Invoke(columns, rows);
    }

    private void MeasureCell()
    {
        var culture = CultureInfo.CurrentCulture;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        // NormalTypeface is fixed; colors do not affect glyph metrics. Reuse the
        // measurement only while all variable text-layout inputs stay the same.
        if (_measuredFontSize == _fontSize && _measuredPixelsPerDip == pixelsPerDip &&
            ReferenceEquals(_measuredCulture, culture)) return;
        var formatted = new FormattedText("M", culture, FlowDirection.LeftToRight, NormalTypeface,
            _fontSize, _foreground, pixelsPerDip);
        _cellWidth = Math.Max(1, formatted.WidthIncludingTrailingWhitespace);
        _cellHeight = Math.Max(1, formatted.Height * 1.15);
        _measuredFontSize = _fontSize;
        _measuredPixelsPerDip = pixelsPerDip;
        _measuredCulture = culture;
    }

    private static bool TryMapKey(Key key, out TerminalKey mapped)
    {
        mapped = key switch
        {
            Key.Up => TerminalKey.Up, Key.Down => TerminalKey.Down, Key.Left => TerminalKey.Left, Key.Right => TerminalKey.Right,
            Key.Home => TerminalKey.Home, Key.End => TerminalKey.End, Key.Insert => TerminalKey.Insert, Key.Delete => TerminalKey.Delete,
            Key.PageUp => TerminalKey.PageUp, Key.PageDown => TerminalKey.PageDown,
            Key.F1 => TerminalKey.F1, Key.F2 => TerminalKey.F2, Key.F3 => TerminalKey.F3, Key.F4 => TerminalKey.F4,
            Key.F5 => TerminalKey.F5, Key.F6 => TerminalKey.F6, Key.F7 => TerminalKey.F7, Key.F8 => TerminalKey.F8,
            Key.F9 => TerminalKey.F9, Key.F10 => TerminalKey.F10, Key.F11 => TerminalKey.F11, Key.F12 => TerminalKey.F12,
            _ => (TerminalKey)(-1)
        };
        return (int)mapped >= 0;
    }

    private static bool TryEncodeControlCharacter(Key key, out byte value)
    {
        int letter = key switch
        {
            >= Key.A and <= Key.Z => (int)key - (int)Key.A + 1,
            Key.Space => 0,
            Key.OemOpenBrackets => 0x1b,
            Key.Oem5 => 0x1c,
            Key.OemCloseBrackets => 0x1d,
            Key.D6 => 0x1e,
            Key.OemMinus => 0x1f,
            _ => -1
        };
        value = (byte)Math.Max(0, letter);
        return letter >= 0;
    }

    private static Color Resolve(TerminalColor color, Color fallback) => color.IsDefault ? fallback : Color.FromRgb(color.R, color.G, color.B);
    private static Color EnsureReadableForeground(Color foreground, Color background)
    {
        if (ContrastRatio(foreground, background) >= 4.5) return foreground;
        return ContrastRatio(Colors.Black, background) >= ContrastRatio(Colors.White, background)
            ? Colors.Black
            : Colors.White;
    }

    private static double ContrastRatio(Color foreground, Color background)
    {
        double first = RelativeLuminance(foreground);
        double second = RelativeLuminance(background);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linear(byte component)
        {
            double value = component / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static Color ParseColor(string value, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(value); }
        catch { return fallback; }
    }

    public void DisposeSurface()
    {
        Interlocked.Exchange(ref _surfaceDisposed, 1);
        _enterCommitTimer?.Stop();
        _outputDrainTimer.Stop();
        _cursorTimer.Stop();
        _terminal.Changed -= OnTerminalChanged;
        _draggingSelection = false;
        ReleaseMouseCapture();
    }
}
}
#endregion
#endif

#if TN_WPF || TN_SOURCE_DIAGNOSTICLOGANALYSISDIALOG_XAML_CS
#region DiagnosticLogAnalysisDialog.xaml.cs
namespace TerminalNotes
{
using global::System.Windows;
using global::Microsoft.Win32;
using global::TerminalNotes.Services;


public partial class DiagnosticLogAnalysisDialog : Window
{
    private readonly string _settingsPath;
    private CancellationTokenSource? _loadCancellation;
    private string? _safeSummary;
    private bool _closed;

    public DiagnosticLogAnalysisDialog(string settingsPath)
    {
        InitializeComponent();
        _settingsPath = settingsPath;
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => { _closed = true; _loadCancellation?.Cancel(); };
    }

    // The analysis only feeds the report; nothing about it is shown or configured here.
    private async Task LoadAsync()
    {
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        try
        {
            var analysis = await DiagnosticLogAnalyzer.AnalyzeDefaultAsync(_settingsPath, cancellation.Token);
            if (!_closed) _safeSummary = analysis.Summary;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ApplicationDiagnostics.RecordException("report_analysis_failed", exception); }
        finally
        {
            if (!_closed)
            {
                SendButton.IsEnabled = true;
                LoadStatus.Text = "보낼 준비가 됐습니다.";
            }
            _loadCancellation = null;
            cancellation.Dispose();
        }
    }

    // Sends the report and a log ZIP to the relay; on failure the ZIP fallback appears.
    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        SendButton.IsEnabled = false;
        LoadStatus.Text = "보내는 중…";
        string zipPath = global::System.IO.Path.Combine(global::System.IO.Path.GetTempPath(), "TerminalNotes-report-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            string report = BuildReport();
            await WriteReportArchiveAsync(zipPath, report);
            string id = await ReportRelayClient.SendAsync(report, zipPath);
            if (_closed) return;
            LoadStatus.Text = "보냈습니다. 접수 번호 " + id;
            SendButton.Content = "보냄";
            ExportLogsButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            ApplicationDiagnostics.RecordException("report_send_failed", exception);
            if (_closed) return;
            LoadStatus.Text = "보내지 못했습니다 · " + ReportRelayClient.Describe(exception) + " ‘ZIP으로 저장…’으로 파일을 만들어 직접 보내 주세요.";
            ExportLogsButton.Visibility = Visibility.Visible;
            SendButton.IsEnabled = true;
        }
        finally
        {
            try { global::System.IO.File.Delete(zipPath); } catch (Exception) { }
        }
    }

    private static async Task WriteReportArchiveAsync(string path, string report)
    {
        await ApplicationDiagnostics.ExportArchiveAsync(path);
        await Task.Run(() =>
        {
            using var archive = global::System.IO.Compression.ZipFile.Open(path, global::System.IO.Compression.ZipArchiveMode.Update);
            archive.GetEntry("report.txt")?.Delete();
            using var writer = new global::System.IO.StreamWriter(archive.CreateEntry("report.txt").Open(), new global::System.Text.UTF8Encoding(true));
            writer.Write(report);
        });
    }

    // One self-contained text a user can paste or attach: environment, their own words, then the analysis.
    internal string BuildReport()
    {
        var report = new global::System.Text.StringBuilder();
        report.AppendLine("[Terminal Notes 문제 보고서]");
        report.AppendLine("앱 버전: v" + ApplicationDiagnostics.DisplayVersion);
        report.AppendLine("Windows: " + global::System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim() +
            " (" + global::System.Runtime.InteropServices.RuntimeInformation.OSArchitecture + ")");
        report.AppendLine("작업 모드: " + ReadWorkModeLabel());
        DateTimeOffset now = DateTimeOffset.Now;
        report.AppendLine($"작성 시각: {now:yyyy-MM-dd HH:mm} (UTC{now:zzz})");
        report.AppendLine();
        report.AppendLine("[어떤 문제가 있었나요]");
        string description = ProblemDescription.Text.Trim();
        report.AppendLine(description.Length > 0 ? description : "(작성하지 않음)");
        report.AppendLine();
        report.AppendLine("[자동 로그 분석]");
        report.AppendLine(_safeSummary ?? "(분석 결과 없음)");
        return report.ToString();
    }

    private string ReadWorkModeLabel()
    {
        try
        {
            if (!global::System.IO.File.Exists(_settingsPath)) return "확인 불가";
            using var document = global::System.Text.Json.JsonDocument.Parse(global::System.IO.File.ReadAllText(_settingsPath));
            return document.RootElement.TryGetProperty("WorkMode", out var mode) ? mode.GetString() switch
            {
                "Basic" => "일반", "Plan" => "플랜", "Advanced" => "고급", _ => "확인 불가"
            } : "확인 불가";
        }
        catch (Exception) { return "확인 불가"; }
    }
    private async void ExportLogs_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog
        {
            Title = "보고서 ZIP 저장", Filter = "ZIP 파일 (*.zip)|*.zip", DefaultExt = ".zip",
            FileName = "TerminalNotes-report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip"
        };
        if (picker.ShowDialog(this) != true) return;
        ExportLogsButton.IsEnabled = false;
        LoadStatus.Text = "ZIP 저장 중…";
        try
        {
            await WriteReportArchiveAsync(picker.FileName, BuildReport());
            if (!_closed) LoadStatus.Text = "ZIP을 저장했습니다. 이 파일을 보내 주세요.";
        }
        catch (Exception exception)
        {
            ApplicationDiagnostics.RecordException("log_export_failed", exception);
            if (!_closed) LoadStatus.Text = "ZIP을 저장하지 못했습니다. 저장 위치와 디스크 여유를 확인하세요.";
        }
        finally
        {
            if (!_closed) ExportLogsButton.IsEnabled = true;
        }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>Posts problem reports to the developer's relay (relay\relay.py).</summary>
internal static class ReportRelayClient
{
    // The relay is a PC on the company network: reached directly, never through the system proxy (which may not reach it).
    private static readonly global::System.Net.Http.HttpClient Http = new(new global::System.Net.Http.HttpClientHandler { UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(90) };

    // report-relay.json next to the app holds the relay address and ingest key; tests override both.
    internal static (string? Url, string? Key) Endpoint()
    {
        if (AppContext.GetData("TerminalNotes.ReportRelayUrl") is string testUrl)
            return (testUrl, AppContext.GetData("TerminalNotes.ReportRelayKey") as string);
        string path = global::System.IO.Path.Combine(AppContext.BaseDirectory, "report-relay.json");
        if (!global::System.IO.File.Exists(path)) return (null, null);
        using var document = global::System.Text.Json.JsonDocument.Parse(global::System.IO.File.ReadAllText(path));
        string? Read(string name) => document.RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;
        return (Read("Url"), Read("Key"));
    }

    public static async Task<string> SendAsync(string report, string zipPath)
    {
        var (url, key) = Endpoint();
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("보낼 주소가 설정되지 않았습니다.");
        using var content = new global::System.Net.Http.MultipartFormDataContent();
        content.Add(new global::System.Net.Http.StringContent(report, global::System.Text.Encoding.UTF8), "report");
        await using var zip = global::System.IO.File.OpenRead(zipPath);
        var file = new global::System.Net.Http.StreamContent(zip);
        file.Headers.ContentType = new global::System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        content.Add(file, "logs", "logs.zip");
        using var request = new global::System.Net.Http.HttpRequestMessage(global::System.Net.Http.HttpMethod.Post, url.TrimEnd('/') + "/api/reports") { Content = content };
        request.Headers.Add("X-TerminalNotes-Key", key ?? "");
        // The relay's status page lists each PC with its app version.
        if (typeof(ReportRelayClient).Assembly.GetName().Version is { } version)
            request.Headers.TryAddWithoutValidation("User-Agent", $"TerminalNotes/{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}");
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode) throw new global::System.Net.Http.HttpRequestException("응답 " + (int)response.StatusCode, null, response.StatusCode);
        using var body = global::System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
    }

    public static string Describe(Exception exception) => exception switch
    {
        global::System.Net.Http.HttpRequestException { StatusCode: global::System.Net.HttpStatusCode.TooManyRequests } => "너무 자주 보냈습니다. 잠시 후 다시 시도하세요.",
        global::System.Net.Http.HttpRequestException { StatusCode: global::System.Net.HttpStatusCode.Unauthorized } => "보내는 키가 맞지 않습니다.",
        global::System.Net.Http.HttpRequestException { StatusCode: not null } http => "서버 응답 오류(" + (int)http.StatusCode! + ").",
        global::System.Net.Http.HttpRequestException or TaskCanceledException => "받는 서버에 연결하지 못했습니다.",
        InvalidOperationException => exception.Message,
        _ => "보고서를 만들거나 보내는 중 문제가 생겼습니다."
    };
}
}
#endregion
#endif

#if TN_WPF || TN_SOURCE_DISPATCHERHEARTBEATWATCHDOG_CS
#region DispatcherHeartbeatWatchdog.cs
namespace TerminalNotes
{
using global::System.Diagnostics;
using global::System.IO;
using global::System.Text.Json;
using global::System.Windows.Threading;


/// <summary>
/// Monitors WPF dispatcher responsiveness from a thread-pool timer so a blocked
/// dispatcher can still be recorded to disk.
/// </summary>
internal sealed class DispatcherHeartbeatWatchdog : IDisposable
{
    private static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultStallThreshold = TimeSpan.FromSeconds(15);
    private readonly Dispatcher _dispatcher;
    private readonly string _logPath;
    private readonly Action<TimeSpan>? _recovered;
    private readonly Action<string, string>? _appendDiagnostic;
    private readonly TimeSpan _checkInterval;
    private readonly TimeSpan _stallThreshold;
    private readonly Timer _timer;
    private readonly object _contextLock = new();
    private readonly object _writeLock = new();
    private long _lastHeartbeatTimestamp;
    private long _maxStallDurationTicks;
    private int _heartbeatPending;
    private int _checkInProgress;
    private int _stalled;
    private int _disposed;
    private int? _terminalSlot;
    private uint? _sessionProcessId;
    private string? _projectName;
    private string? _terminalStage;
    private string? _lastTerminalFailureKey;
    private DateTimeOffset _lastTerminalFailureAt;

    internal DispatcherHeartbeatWatchdog(Dispatcher dispatcher, string logPath, Action<TimeSpan>? recovered = null,
        TimeSpan? checkInterval = null, TimeSpan? stallThreshold = null, Action<string, string>? appendDiagnostic = null)
    {
        _dispatcher = dispatcher;
        _logPath = logPath;
        _recovered = recovered;
        _appendDiagnostic = appendDiagnostic;
        _checkInterval = checkInterval ?? DefaultCheckInterval;
        _stallThreshold = stallThreshold ?? DefaultStallThreshold;
        _lastHeartbeatTimestamp = Stopwatch.GetTimestamp();
        _timer = new Timer(CheckDispatcher, null, _checkInterval, _checkInterval);
        QueueHeartbeat();
    }

    public void UpdateTerminalContext(int slot, string? projectName, string? stage, uint? processId, bool newSession = false)
    {
        lock (_contextLock)
        {
            if (newSession || _terminalSlot != slot || !string.Equals(_projectName, projectName, StringComparison.Ordinal))
                _sessionProcessId = processId;
            else if (processId is not null)
                _sessionProcessId = processId;
            _terminalSlot = slot;
            _projectName = projectName;
            _terminalStage = stage;
        }
    }

    public void RecordTerminalFailure(int slot, string? projectName, uint? processId, string stage, string operation, string exceptionType)
    {
        if (!IsSafeCode(operation) || !IsSafeCode(exceptionType) || !IsSafeCode(stage) || Volatile.Read(ref _disposed) != 0) return;
        var context = new TerminalContext(slot, processId, SafeProjectName(projectName), stage);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_contextLock)
        {
            string key = operation + "\n" + exceptionType + "\n" + slot + "\n" + context.Project;
            if (string.Equals(_lastTerminalFailureKey, key, StringComparison.Ordinal) &&
                now - _lastTerminalFailureAt < TimeSpan.FromMinutes(5))
                return;
            _lastTerminalFailureKey = key;
            _lastTerminalFailureAt = now;
        }

        ThreadPool.QueueUserWorkItem(_ => WriteDiagnostic("terminal_lifecycle_fault", TimeSpan.Zero, operation, exceptionType, context));
    }

    private void CheckDispatcher(object? state)
    {
        if (Interlocked.CompareExchange(ref _checkInProgress, 1, 0) != 0) return;
        try { CheckDispatcherCore(); }
        catch (Exception ex)
        {
            Trace.WriteLine("UI heartbeat check failed: " + ex.GetType().Name);
        }
        finally { Volatile.Write(ref _checkInProgress, 0); }
    }

    private void CheckDispatcherCore()
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        long lastHeartbeat = Volatile.Read(ref _lastHeartbeatTimestamp);
        var delay = Stopwatch.GetElapsedTime(lastHeartbeat);
        if (delay >= _stallThreshold)
        {
            if (Interlocked.CompareExchange(ref _stalled, 1, 0) == 0)
            {
                WriteDiagnostic("dispatcher_unresponsive", delay);
            }
            Interlocked.Exchange(ref _maxStallDurationTicks, delay.Ticks);
        }
        else if (Interlocked.CompareExchange(ref _stalled, 0, 1) == 1)
        {
            long stalledTicks = Interlocked.Exchange(ref _maxStallDurationTicks, 0);
            var duration = TimeSpan.FromTicks(Math.Max(stalledTicks, delay.Ticks));
            WriteDiagnostic("dispatcher_recovered", duration);
            try
            {
                if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
                    _dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (Volatile.Read(ref _disposed) == 0) _recovered?.Invoke(duration);
                    }), DispatcherPriority.Background);
            }
            catch (InvalidOperationException) { }
        }

        QueueHeartbeat();
    }

    private void QueueHeartbeat()
    {
        if (Volatile.Read(ref _disposed) != 0 || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished ||
            Interlocked.CompareExchange(ref _heartbeatPending, 1, 0) != 0)
            return;

        try
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (Volatile.Read(ref _disposed) == 0)
                    Volatile.Write(ref _lastHeartbeatTimestamp, Stopwatch.GetTimestamp());
                Volatile.Write(ref _heartbeatPending, 0);
            }), DispatcherPriority.Normal);
        }
        catch (InvalidOperationException)
        {
            Volatile.Write(ref _heartbeatPending, 0);
        }
    }

    private void WriteDiagnostic(string eventName, TimeSpan duration)
    {
        TerminalContext context;
        lock (_contextLock) context = CaptureTerminalContextLocked();
        WriteDiagnostic(eventName, duration, null, null, context);
    }

    private TerminalContext CaptureTerminalContextLocked() =>
        new(_terminalSlot, _sessionProcessId, _projectName, _terminalStage);

    private void WriteDiagnostic(string eventName, TimeSpan duration, string? operation, string? exceptionType, TerminalContext context)
    {
#if TN_WPF
        if (eventName is "dispatcher_unresponsive" or "dispatcher_recovered")
            global::TerminalNotes.Services.ApplicationDiagnostics.Record(eventName, "ui", "seconds_" + Math.Round(duration.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
#endif
        var record = new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            eventName,
            durationSeconds = Math.Round(duration.TotalSeconds, 1),
            terminalSlot = context.Slot,
            processId = context.ProcessId,
            project = context.Project,
            terminalStage = context.Stage,
            operation,
            exceptionType
        };

        try
        {
            lock (_writeLock)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                string? directory = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                string jsonLine = JsonSerializer.Serialize(record) + Environment.NewLine;
                if (_appendDiagnostic is not null)
                    _appendDiagnostic(_logPath, jsonLine);
                else
                    File.AppendAllText(_logPath, jsonLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Trace.WriteLine("UI responsiveness diagnostic could not be written: " + ex.GetType().Name);
        }
    }

    private static bool IsSafeCode(string value) => value.Length is > 0 and <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or '+' or '`');

    private static string? SafeProjectName(string? value) => value is { Length: > 0 and <= 255 } &&
        value.All(character => !char.IsControl(character)) ? value : null;

    private sealed record TerminalContext(int? Slot, uint? ProcessId, string? Project, string? Stage);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer.Dispose();
    }
}
}
#endregion
#endif

#if TN_WPF || TN_SOURCE_GLOBALAGENTINSTRUCTIONSDIALOG_XAML_CS
#region GlobalAgentInstructionsDialog.xaml.cs
namespace TerminalNotes
{
using global::System.IO;
using global::System.Windows;
using global::TerminalNotes.Services;


public partial class GlobalAgentInstructionsDialog : Window
{
    private string _loadedContents;
    private readonly Func<string, string, GlobalAgentInstructions.SaveResult> _saveInstructions;
    private readonly string _applicationTiming;
    private readonly string _instructionLabel;

    public GlobalAgentInstructionsDialog() : this("전역 지침", "전역 지침", "모든 프로젝트에 적용합니다.",
        GlobalAgentInstructions.FilePath, GlobalAgentInstructions.ReadCurrent, GlobalAgentInstructions.TrySave,
        "새 OpenCode 요청부터 적용됩니다.", "전역 지침", maximumLength: 0)
    { }

    internal static GlobalAgentInstructionsDialog CreatePlanMaker() => new("플랜메이커 지침", "플랜메이커 지침",
        "질문 방식과 플랜 작성 기준을 설정합니다.",
        PlanMakerInstructions.FilePath, PlanMakerInstructions.ReadCurrent, PlanMakerInstructions.TrySave,
        "저장한 지침은 새로 시작하는 플랜메이커 대화부터 적용됩니다.", "플랜메이커 지침", PlanMakerInstructions.MaximumLength);

    internal static GlobalAgentInstructionsDialog CreateOpenCodeConfig() => new(OpenCodeConfigFile.Title, OpenCodeConfigFile.Title, OpenCodeConfigFile.Description,
        OpenCodeConfigFile.FilePath, OpenCodeConfigFile.ReadCurrent, OpenCodeConfigFile.TrySave,
        "새로 여는 OpenCode 터미널부터 적용됩니다.", OpenCodeConfigFile.Title, maximumLength: 0);

    internal GlobalAgentInstructionsDialog(string title, string heading, string description, string path,
        Func<string> readInstructions, Func<string, string, GlobalAgentInstructions.SaveResult> saveInstructions,
        string applicationTiming, string instructionLabel, int maximumLength)
    {
        InitializeComponent();
        _saveInstructions = saveInstructions;
        _applicationTiming = applicationTiming;
        _instructionLabel = instructionLabel;
        Title = title;
        CaptionTitle.Text = title;
        HeadingText.Text = heading;
        DescriptionText.Text = description;
        DescriptionText.Visibility = string.IsNullOrEmpty(description) ? Visibility.Collapsed : Visibility.Visible;
        PathText.Text = path;
        InstructionsInput.MaxLength = maximumLength;
        System.Windows.Automation.AutomationProperties.SetName(InstructionsInput, title + " 내용");
        _loadedContents = readInstructions();
        InstructionsInput.Text = _loadedContents;
        SetSaveStatus("저장됨", _applicationTiming, "#1A1A1A", "#F2F2F2");
        InstructionsInput.TextChanged += (_, _) =>
        {
            bool modified = !string.Equals(InstructionsInput.Text, _loadedContents, StringComparison.Ordinal);
            SetSaveStatus(modified ? "미저장" : "저장됨",
                modified ? "변경사항을 저장하세요." : _applicationTiming,
                modified ? "#3E3E3E" : "#1A1A1A", modified ? "#FFE0A3" : "#F2F2F2");
        };
        StateChanged += (_, _) =>
        {
            bool maximized = WindowState == WindowState.Maximized;
            string action = maximized ? "이전 크기로 복원" : "최대화";
            MaximizeRestoreButton.ToolTip = action;
            System.Windows.Automation.AutomationProperties.SetName(MaximizeRestoreButton, action);
            MaximizeRestoreIcon.Data = System.Windows.Media.Geometry.Parse(maximized
                ? "M 2,0 L 10,0 L 10,8 M 0,2 L 8,2 L 8,10 L 0,10 Z" : "M 0,0 L 10,0 L 10,10 L 0,10 Z");
        };
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void SetSaveStatus(string label, string message, string background, string foreground)
    {
        SaveStatusLabel.Text = label;
        SaveStatusBadge.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(background)!;
        SaveStatusLabel.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(foreground)!;
        SaveStatusText.Text = message;
        SaveStatusText.Foreground = SaveStatusLabel.Foreground;
        System.Windows.Automation.AutomationProperties.SetName(SaveStatusText, message);
    }

    private void SaveInstructionsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            switch (_saveInstructions(_loadedContents, InstructionsInput.Text))
            {
                case GlobalAgentInstructions.SaveResult.Saved:
                    _loadedContents = InstructionsInput.Text;
                    SetSaveStatus("저장 완료", _applicationTiming, "#24483E", "#B3EAD1");
                    break;
                case GlobalAgentInstructions.SaveResult.Conflict:
                    SetSaveStatus("저장 충돌", "편집 중 파일이 변경되어 덮어쓰지 않았습니다. 최신 파일을 확인한 뒤 다시 편집하세요.", "#3E3E3E", "#FFE0A3");
                    break;
                default:
                    SetSaveStatus("저장 대기", "파일을 저장할 수 없습니다. 다른 TerminalNotes 창의 저장이 끝난 뒤 다시 시도하세요.", "#3E3E3E", "#FFE0A3");
                    break;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            SetSaveStatus("저장 실패", _instructionLabel + " 파일을 저장하지 못했습니다. " + exception.Message, "#363636", "#FFC2C2");
        }
    }
}
}
#endregion
#endif

#if TN_WPF || TN_SOURCE_NEWPROJECTDIALOG_CS
#region NewProjectDialog.cs
namespace TerminalNotes
{
using global::System.IO;
using global::System.Windows;
using global::System.Windows.Automation;
using global::System.Windows.Controls;
using global::System.Windows.Media;


internal sealed class NewProjectDialog : Window
{
    private readonly TextBox _nameInput;
    private readonly TextBox _goalInput;
    private readonly TextBlock _validationMessage;

    public string ProjectName => _nameInput.Text.Trim();
    // 2026-11-06 사용자: "플랜에는 태스크에서 생성될때 기본 목표만 입력 받게 해". The new plan holds only this goal.
    public string ProjectGoal => _goalInput.Text.Trim();

    public NewProjectDialog()
    {
        Title = "새 프로젝트";
        Width = 430;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Brush("#1A1A1A");
        Foreground = Brush("#F2F2F2");
        FontFamily = new FontFamily(new Uri("pack://application:,,,/TerminalNotes;component/"), "./Assets/Fonts/#Pretendard, Segoe UI, Malgun Gothic");
        FontSize = 14;
        if (Application.Current?.MainWindow?.TryFindResource(typeof(Button)) is Style buttonStyle)
            Resources[typeof(Button)] = buttonStyle;

        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock
        {
            Text = "프로젝트 이름",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        content.Children.Add(new TextBlock
        {
            Text = "작업 폴더 바로 아래에 같은 이름의 폴더와 시작 플랜을 만듭니다.",
            Visibility = Visibility.Collapsed,
            Foreground = Brush("#C9C0AE"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 16)
        });

        _nameInput = new TextBox
        {
            MaxLength = 60,
            Padding = new Thickness(10),
            FontSize = 15,
            Background = Brush("#121212"),
            Foreground = Brush("#F2F2F2"),
            CaretBrush = Brush("#E5C07B"),
            BorderBrush = Brush("#5C5C5C")
        };
        AutomationProperties.SetName(_nameInput, "프로젝트 이름과 폴더 이름");
        AutomationProperties.SetHelpText(_nameInput, "최대 60자. 경로 구분자와 Windows 예약 이름은 사용할 수 없습니다.");
        content.Children.Add(_nameInput);

        _validationMessage = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            Foreground = Brush("#C9C0AE"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        AutomationProperties.SetLiveSetting(_validationMessage, AutomationLiveSetting.Polite);
        content.Children.Add(_validationMessage);

        content.Children.Add(new TextBlock
        {
            Text = "기본 목표",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 18, 0, 4)
        });
        content.Children.Add(new TextBlock
        {
            Text = "플랜에는 이 목표만 들어갑니다. 요구사항·조건은 나중에 플랜메이커나 플랜 입력으로 더합니다. 비워 두면 목표 제목만 만듭니다.",
            Foreground = Brush("#C9C0AE"),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        _goalInput = new TextBox
        {
            MaxLength = 2000,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 74,
            MaxHeight = 160,
            Padding = new Thickness(10),
            FontSize = 14,
            Background = Brush("#121212"),
            Foreground = Brush("#F2F2F2"),
            CaretBrush = Brush("#E5C07B"),
            BorderBrush = Brush("#5C5C5C")
        };
        AutomationProperties.SetName(_goalInput, "기본 목표");
        AutomationProperties.SetHelpText(_goalInput, "새 플랜의 목표 구역에 그대로 들어갑니다. Enter로 줄을 바꿉니다.");
        content.Children.Add(_goalInput);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancel = new Button
        {
            Content = "취소",
            IsCancel = true,
            MinWidth = 74,
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 8, 0),
            Background = Brush("#1A1A1A"), Foreground = Foreground, BorderBrush = Brush("#5C5C5C")
        };
        AutomationProperties.SetName(cancel, "프로젝트 만들기 취소");
        buttons.Children.Add(cancel);

        var create = new Button
        {
            Content = "만들기",
            IsDefault = true,
            MinWidth = 90,
            Padding = new Thickness(12, 8, 12, 8),
            Background = Brush("#232323"),
            Foreground = Brush("#FAEDCF"),
            BorderBrush = Brush("#D9B76E")
        };
        if (Application.Current?.MainWindow?.TryFindResource("PrimaryButton") is Style primaryStyle)
        {
            create.Style = primaryStyle;
            create.ClearValue(Control.BackgroundProperty);
        }
        AutomationProperties.SetName(create, "새 프로젝트 만들기 확인");
        create.Click += Confirm_Click;
        buttons.Children.Add(create);
        content.Children.Add(buttons);

        Content = new Border { Child = content, BorderBrush = Brush("#5C5C5C"), BorderThickness = new Thickness(1),
            Background = Brush("#1A1A1A") };
        Loaded += (_, _) =>
        {
            _nameInput.Focus();
        };
    }

    public static bool TryValidateFolderName(string? value, out string normalizedName, out string error)
    {
        normalizedName = value?.Trim() ?? string.Empty;
        error = string.Empty;

        if (normalizedName.Length == 0)
        {
            error = "프로젝트 이름을 입력하세요.";
            return false;
        }
        if (normalizedName.Length > 60)
        {
            error = "프로젝트 이름은 60자 이하여야 합니다.";
            return false;
        }
        if (normalizedName is "." or "..")
        {
            error = "점만으로 된 이름은 사용할 수 없습니다.";
            return false;
        }
        if (normalizedName.EndsWith('.') || normalizedName.EndsWith(' '))
        {
            error = "이름 끝에 점이나 공백을 둘 수 없습니다.";
            return false;
        }
        if (Path.IsPathRooted(normalizedName) || normalizedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            normalizedName.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0 ||
            normalizedName.Any(char.IsControl))
        {
            error = "경로 구분자나 사용할 수 없는 문자가 포함되어 있습니다.";
            return false;
        }

        string deviceName = normalizedName.Split('.', 2)[0].TrimEnd(' ', '.');
        if (IsReservedDeviceName(deviceName))
        {
            error = "Windows 예약 이름은 프로젝트 폴더에 사용할 수 없습니다.";
            return false;
        }

        return true;
    }

    private static bool IsReservedDeviceName(string name)
    {
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
            return true;

        if (name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                                 name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
            return name[3] is >= '1' and <= '9' or '\u00B9' or '\u00B2' or '\u00B3';

        return false;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!TryValidateFolderName(_nameInput.Text, out _, out string error))
        {
            _validationMessage.Text = error;
            _validationMessage.Visibility = Visibility.Visible;
            _validationMessage.Foreground = Brush("#FFB86B");
            _nameInput.Focus();
            _nameInput.SelectAll();
            return;
        }

        DialogResult = true;
    }

    private static Brush Brush(string value) =>
        (Brush)new BrushConverter().ConvertFromString(value)!;
}
}
#endregion
#endif

#if TN_WPF || TN_SOURCE_PLANMAKERDIALOG_CS
#region PlanMakerDialog.cs
namespace TerminalNotes
{
using global::System.ComponentModel;
using global::System.Windows;
using global::System.Windows.Controls;
using global::System.Windows.Automation;
using global::System.Windows.Media;
using global::System.Windows.Media.Animation;
using global::System.Windows.Shell;
using ShapePath = global::System.Windows.Shapes.Path;
using global::TerminalNotes.Services;


/// <summary>Owns an isolated planning terminal; it never registers a main workbench slot.</summary>
internal sealed class PlanMakerDialog : Window
{
    internal TerminalPane Pane { get; private set; }
    internal bool PlanningCompleted { get; private set; }
    internal string? Error { get; private set; }
    private readonly CancellationTokenSource _cancellation;
    private readonly TextBlock _status;
    private StatusNotificationPresenter? _statusNotifications;
    // Docked mode: the planner's content lives in the main window's PlanMaker view instead of its own window.
    private FrameworkElement? _dockedRoot;
    private Border? _titleBarBorder;
    private Grid? _layoutGrid;
    private TextBlock? _planTitleText;
    private Grid? _statusLayoutGrid;
    internal bool IsDocked => _dockedRoot is not null;
    internal string ProjectPath => _project;
    private Window DialogOwner => IsDocked ? _owner : this;
    private bool IsShownToUser => IsDocked ? _dockedRoot!.IsVisible : IsVisible && WindowState != WindowState.Minimized;
    private readonly Border _statusBadge;
    private readonly Border _statusBar;
    private readonly TextBlock _statusLabel;
    private readonly TextBlock _recommendationStatus;
    private readonly TextBlock _recommendationDetail;
    private readonly ShapePath _recommendationIndicator;
    private string _recommendationVisualState = "manual";
    private bool _recommendationAnimationActive;
    private readonly Button _retry;
    private readonly Button _finish;
    private readonly Button _continue;
    private readonly Button _recommendedAnswers;
    private readonly string _project;
    private readonly Window _owner;
    private readonly TextBox _draft;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _cleaned;
    private bool _cleanupStarted;
    private bool _retrying;
    private string _confirmedGeneratedDraft = "";
    private PlanMakerInterviewControl? _interview;
    private Func<bool>? _attemptStillOwned;
    private bool _attemptActive;
    private bool _attemptHasDraft;
    private bool _interviewReady;
    private bool _returnAfterFinish;
    internal bool CompletedWithoutRemainingDecisions { get; private set; }
    private Task? _operation;
    private Task? _cleanup;

    internal PlanMakerDialog(Window owner, string project, string initialDraft, CancellationToken cancellationToken)
    {
        // Modeless and unowned: the main window, other tasks and terminals stay
        // usable and are never covered while the interview is in progress.
        _owner = owner;
        _project = project;
        Title = "플랜메이커 · " + System.IO.Path.GetFileName(project);
        double availableWidth = Math.Max(320, SystemParameters.WorkArea.Width - 40);
        double availableHeight = Math.Max(300, SystemParameters.WorkArea.Height - 40);
        Width = Math.Min(1200, availableWidth); Height = Math.Min(720, availableHeight);
        MinWidth = Math.Min(800, availableWidth); MinHeight = Math.Min(440, availableHeight);
        Background = CreatePlanningGradient("#1A1A1A", "#121212");
        Foreground = (Brush)owner.FindResource("TextPrimary");
        FontFamily = owner.FontFamily;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        FontSize = owner.FontSize;
        Icon = owner.Icon;
        // WindowChrome paints our caption while retaining native monitor/work-area sizing.
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        UseLayoutRounding = true;
        // An unowned planner must remain reachable from the taskbar.
        ShowInTaskbar = true;
        Resources[typeof(Button)] = owner.FindResource(typeof(Button));
        Resources[typeof(ToolTip)] = owner.FindResource(typeof(ToolTip));
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 36,
            ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        });
        WindowStartupLocation = WindowStartupLocation.Manual;
        CenterOnOwner(owner);
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Pane = CreatePlanningPane();
        var layout = _layoutGrid = new Grid { Margin = new Thickness(12) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(35, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(65, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _status = new TextBlock { Text = "답변을 플랜에 반영합니다.", FontSize = 12,
            TextWrapping = TextWrapping.Wrap, Foreground = Foreground, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(_status, "PlanMakerStatusMessage");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _statusLabel = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center };
        _statusBadge = new Border { Child = _statusLabel, CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center };
        var statusLayout = _statusLayoutGrid = new Grid();
        statusLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusLayout.ColumnDefinitions.Add(new ColumnDefinition());
        // The Auto Recommand column exists only for the accounts in LoopAutoRecommandUsers.AllowedUsers.
        statusLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LoopAutoRecommandUsers.Allowed ? 210 : 0) });
        statusLayout.Children.Add(_statusBadge);
        Grid.SetColumn(_status, 1);
        statusLayout.Children.Add(new ScrollViewer { Content = _status, MaxHeight = 36, VerticalAlignment = VerticalAlignment.Center,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetColumn(statusLayout.Children[1], 1);
        _recommendationIndicator = new ShapePath { Data = Geometry.Parse("M 8,1 A 7,7 0 1 1 1,8"),
            Width = 16, Height = 16, StrokeThickness = 2, Stretch = Stretch.Uniform,
            RenderTransformOrigin = new Point(.5, .5), RenderTransform = new RotateTransform(),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), IsHitTestVisible = false };
        AutomationProperties.SetAutomationId(_recommendationIndicator, "PlanMakerRecommendationActivity");
        _recommendationStatus = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(_recommendationStatus, "PlanMakerRecommendationStatus");
        AutomationProperties.SetLiveSetting(_recommendationStatus, AutomationLiveSetting.Polite);
        _recommendationDetail = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)new BrushConverter().ConvertFromString("#C9C0AE")!, Margin = new Thickness(0, 3, 0, 0) };
        var recommendationText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        recommendationText.Children.Add(_recommendationStatus);
        recommendationText.Children.Add(_recommendationDetail);
        var recommendationLayout = new DockPanel();
        DockPanel.SetDock(_recommendationIndicator, Dock.Left);
        recommendationLayout.Children.Add(_recommendationIndicator);
        recommendationLayout.Children.Add(recommendationText);
        var recommendationPanel = new Border { Child = recommendationLayout, Padding = new Thickness(10, 0, 0, 0),
            Margin = new Thickness(10, 0, 0, 0), BorderThickness = new Thickness(1, 0, 0, 0),
            BorderBrush = (Brush)new BrushConverter().ConvertFromString("#2B2B2B")! };
        if (!LoopAutoRecommandUsers.Allowed) recommendationPanel.Visibility = Visibility.Collapsed;
        Grid.SetColumn(recommendationPanel, 2); statusLayout.Children.Add(recommendationPanel);
        var statusBar = _statusBar = new Border { Child = statusLayout, Background = CreatePlanningGradient("#1A1A1A", "#1A1A1A"),
            BorderBrush = (Brush)new BrushConverter().ConvertFromString("#2B2B2B")!, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 8, 8, 0),
            VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(statusBar, "PlanMakerStatusBar");
        SetPlanningStatus(_status.Text);
        var planTitle = new TextBlock { Text = "플랜 · 자동 저장", FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)new BrushConverter().ConvertFromString("#E5C07B")!,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 8) };
        _planTitleText = planTitle;
        Grid.SetRow(planTitle, 1); layout.Children.Add(planTitle);
        var questionsTitle = new TextBlock { Text = "질문과 답변", FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)new BrushConverter().ConvertFromString("#E5C07B")!,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            ToolTip = "답변을 플랜에 반영합니다.",
            Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(questionsTitle, 1); Grid.SetColumn(questionsTitle, 2); layout.Children.Add(questionsTitle);
        Grid.SetRow(Pane, 2); Grid.SetColumn(Pane, 2); layout.Children.Add(Pane);
        _draft = new TextBox { Text = initialDraft, IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Style = owner.TryFindResource("PlanInputEditor") as Style,
            Background = (Brush)new BrushConverter().ConvertFromString("#121212")!,
            Foreground = (Brush)new BrushConverter().ConvertFromString("#F2F2F2")!,
            BorderBrush = (Brush)new BrushConverter().ConvertFromString("#2B2B2B")!, BorderThickness = new Thickness(1),
            FontSize = 15, Padding = new Thickness(14, 10, 14, 10), ToolTip = "질문 답변을 반영한 플랜 · 확인될 때마다 plan.md에 바로 저장되어 중간에 끊겨도 남습니다" };
        AutomationProperties.SetName(_draft, "플랜메이커 플랜 초안");
        AutomationProperties.SetAutomationId(_draft, "PlanMakerDraftPreview");
        Grid.SetRow(_draft, 2); layout.Children.Add(_draft);
        var splitter = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch, Background = (Brush)new BrushConverter().ConvertFromString("#262626")!,
            ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ShowsPreview = true, ToolTip = "플랜과 질문 영역 너비 조절" };
        Grid.SetRow(splitter, 2); Grid.SetColumn(splitter, 1); layout.Children.Add(splitter);
        _retry = new Button { Content = "다시 시도", Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 8, 0), Style = (Style)owner.FindResource("PrimaryButton"),
            ToolTip = "OpenCode 설정을 다시 읽고 플랜메이커를 재시작 · 현재 초안 유지" };
        AutomationProperties.SetAutomationId(_retry, "PlanMakerRetry");
        AutomationProperties.SetName(_retry, "플랜메이커 다시 시도");
        _finish = new Button { Content = "플랜으로 돌아가기", IsEnabled = false,
            Margin = new Thickness(0, 8, 0, 0), Style = (Style)owner.FindResource("PrimaryButton"),
            ToolTip = "질문을 마치고 플랜으로 돌아갑니다. 확인된 내용은 이미 plan.md에 저장되어 있고, 돌아가면 이어서 정리해 저장합니다." };
        AutomationProperties.SetAutomationId(_finish, "PlanMakerFinishInterview");
        AutomationProperties.SetName(_finish, "플랜메이커 질문을 마치고 플랜으로 돌아가기");
        AutomationProperties.SetHelpText(_finish, (string)_finish.ToolTip);
        _finish.Click += (_, _) =>
        {
            if (CanReturnToCompletedDraft()) { Close(); return; }
            if (!CanControlInterview() || !_interviewReady || !_attemptHasDraft || !_interview!.CanRequestFinish) return;
            _interview!.RequestFinish();
            if (!_interview.IsFinishRequested) { UpdateInterviewActions(); return; }
            _returnAfterFinish = true;
            SetPlanningStatus("질문 마무리 중… · 답변이 반영되면 플랜으로 돌아갑니다(확인된 내용은 plan.md에 저장됨).");
        };
        _continue = new Button { Content = "질문 계속", Visibility = Visibility.Collapsed,
            Background = (Brush)owner.FindResource("ButtonSurface"), Foreground = Foreground,
            BorderBrush = (Brush)owner.FindResource("ButtonLine"), Margin = new Thickness(0, 8, 8, 0),
            Padding = new Thickness(12, 6, 12, 6), ToolTip = "같은 대화에서 다음 질문을 이어갑니다" };
        AutomationProperties.SetAutomationId(_continue, "PlanMakerContinueInterview");
        AutomationProperties.SetName(_continue, "플랜메이커 질문 계속");
        _continue.Click += (_, _) =>
        {
            if (!CanControlInterview() || _interview?.IsAwaitingContinuation != true) return;
            _interview.RequestContinuation();
            SetPlanningStatus("같은 대화에서 다음 질문을 준비 중…");
        };
        _recommendedAnswers = new Button { Content = "Auto Recommand", IsEnabled = false,
            Background = (Brush)owner.FindResource("ButtonSurface"), Foreground = Foreground,
            BorderBrush = (Brush)owner.FindResource("ButtonLine"), Margin = new Thickness(0, 8, 8, 0),
            Padding = new Thickness(12, 6, 12, 6),
            ToolTip = "Auto Recommand: 추천 답변 자동 선택 · 추천이 없으면 설정된 모델로 추천을 보완 · 다시 눌러 끄기" };
        AutomationProperties.SetAutomationId(_recommendedAnswers, "PlanMakerRecommendedAnswers");
        AutomationProperties.SetHelpText(_recommendedAnswers, (string)_recommendedAnswers.ToolTip);
        if (!LoopAutoRecommandUsers.Allowed) _recommendedAnswers.Visibility = Visibility.Collapsed;
        _recommendedAnswers.Click += (_, _) =>
        {
            if (!LoopAutoRecommandUsers.Allowed || !CanControlInterview() || !_interviewReady) return;
            bool enabled = !_interview!.RecommendedAnswersEnabled;
            _interview.SetRecommendedAnswersEnabled(enabled);
            SetPlanningStatus(enabled
                ? "Auto Recommand 진행 중 · 이번 대화에서 추천 답변을 따릅니다. 표시된 추천이 없으면 설정된 모델로 추천을 보완합니다."
                : "직접 답변 모드 · 질문에 직접 답변해주세요.");
        };
        // Bottom row: progress status on the left, then the actions. The question terminal above gets the height.
        var actions = new Grid();
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.Children.Add(statusBar);
        foreach (var button in new[] { _retry, _recommendedAnswers, _continue, _finish }) button.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_retry, 1); actions.Children.Add(_retry);
        Grid.SetColumn(_recommendedAnswers, 2); actions.Children.Add(_recommendedAnswers);
        Grid.SetColumn(_continue, 3); actions.Children.Add(_continue);
        Grid.SetColumn(_finish, 4); actions.Children.Add(_finish);
        UpdateInterviewActions();
        Grid.SetRow(actions, 3); Grid.SetColumnSpan(actions, 3); layout.Children.Add(actions);
        var frame = new Grid();
        frame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        frame.RowDefinitions.Add(new RowDefinition());
        frame.Children.Add(_titleBarBorder = CreateTitleBar(owner));
        Grid.SetRow(layout, 1); frame.Children.Add(layout);
        Content = new Border { Child = frame, Background = Background,
            BorderBrush = (Brush)new BrushConverter().ConvertFromString("#2B2B2B")!, BorderThickness = new Thickness(1) };
        IsVisibleChanged += (_, _) => UpdateInterviewActions();
        StateChanged += (_, _) => UpdateInterviewActions();
        Closing += OnClosing;
        Closed += (_, _) => _closed.TrySetResult();
        _statusNotifications = new StatusNotificationPresenter(this);
    }

    private void CenterOnOwner(Window owner)
    {
        var area = SystemParameters.WorkArea;
        bool ownerPlaced = owner.IsLoaded && owner.WindowState == WindowState.Normal && owner.ActualWidth > 0 &&
            !double.IsNaN(owner.Left) && !double.IsNaN(owner.Top);
        double left = ownerPlaced ? owner.Left + (owner.ActualWidth - Width) / 2 : area.Left + (area.Width - Width) / 2;
        double top = ownerPlaced ? owner.Top + (owner.ActualHeight - Height) / 2 : area.Top + (area.Height - Height) / 2;
        Left = Math.Max(area.Left, Math.Min(left, area.Right - Width));
        Top = Math.Max(area.Top, Math.Min(top, area.Bottom - Height));
    }

    private static LinearGradientBrush CreatePlanningGradient(string top, string bottom, string? highlight = null)
    {
        var gradient = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(top),
            (Color)ColorConverter.ConvertFromString(bottom), new Point(0, 0), new Point(0, 1));
        if (highlight is not null)
            gradient.GradientStops.Insert(1, new GradientStop((Color)ColorConverter.ConvertFromString(highlight), .12));
        gradient.Freeze();
        return gradient;
    }

    private Border CreateTitleBar(Window owner)
    {
        var caption = new Grid();
        caption.ColumnDefinitions.Add(new ColumnDefinition());
        caption.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        caption.Children.Add(new TextBlock { Text = "플랜메이커 · " + System.IO.Path.GetFileName(_project),
            Foreground = (Brush)new BrushConverter().ConvertFromString("#E5C07B")!,
            FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        Button CaptionButton(string id, string tooltip, string geometry)
        {
            var button = new Button { Style = (Style)owner.FindResource("CaptionButton"), ToolTip = tooltip,
                Content = new ShapePath { Data = Geometry.Parse(geometry), Stroke = (Brush)owner.FindResource("TextSecondary"),
                    StrokeThickness = 1, Width = 10, Height = 10, Stretch = Stretch.None } };
            AutomationProperties.SetAutomationId(button, id);
            AutomationProperties.SetName(button, tooltip);
            WindowChrome.SetIsHitTestVisibleInChrome(button, true);
            buttons.Children.Add(button);
            return button;
        }
        var minimize = CaptionButton("PlanMakerMinimize", "최소화", "M 0,5 L 10,5");
        minimize.Click += (_, _) => SystemCommands.MinimizeWindow(this);
        var maximize = CaptionButton("PlanMakerMaximizeRestore", "최대화", "M 0,0 L 10,0 L 10,10 L 0,10 Z");
        maximize.Click += (_, _) =>
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        };
        StateChanged += (_, _) =>
        {
            bool maximized = WindowState == WindowState.Maximized;
            string action = maximized ? "이전 크기로 복원" : "최대화";
            maximize.ToolTip = action;
            AutomationProperties.SetName(maximize, action);
            ((ShapePath)maximize.Content).Data = Geometry.Parse(maximized
                ? "M 2,0 L 10,0 L 10,8 M 0,2 L 8,2 L 8,10 L 0,10 Z" : "M 0,0 L 10,0 L 10,10 L 0,10 Z");
        };
        var close = CaptionButton("PlanMakerClose", "플랜메이커 닫기 · 초안 유지", "M 0,0 L 10,10 M 10,0 L 0,10");
        close.Click += (_, _) => RequestClose();
        Grid.SetColumn(buttons, 1); caption.Children.Add(buttons);
        return new Border { Child = caption, Background = CreatePlanningGradient("#1A1A1A", "#1A1A1A"),
            BorderBrush = (Brush)new BrushConverter().ConvertFromString("#2B2B2B")!, BorderThickness = new Thickness(0, 1, 0, 1) };
    }

    /// <summary>Closes the planner (window or docked panel); confirms while the interview is still running.</summary>
    internal void RequestClose()
    {
        if (_attemptActive && !_cleanupStarted && !_cancellation.IsCancellationRequested &&
            !AppMessageDialog.Confirm(DialogOwner, "플랜메이커 닫기",
                "질문 대화가 진행 중입니다. 닫으면 대화가 종료됩니다.\n\n지금까지 반영된 플랜 초안은 유지됩니다.", "닫기"))
            return;
        if (IsDocked) Close();
        else SystemCommands.CloseWindow(this);
    }

    /// <summary>
    /// Moves the planner's content out of its own window so the main window can host it in a split
    /// PlanMaker view. Call before <see cref="RunAsync"/>; the window itself is then never shown.
    /// </summary>
    internal FrameworkElement DockIntoHost()
    {
        if (_dockedRoot is not null) return _dockedRoot;
        if (_titleBarBorder is not null) _titleBarBorder.Visibility = Visibility.Collapsed;
        var root = (FrameworkElement)Content;
        // The host supplies its own header, so drop the window caption row as well.
        if (root is Border { Child: Grid frame } && frame.RowDefinitions.Count > 0) frame.RowDefinitions[0].Height = new GridLength(0);
        Content = null;
        _dockedRoot = root;
        root.IsVisibleChanged += (_, _) => UpdateInterviewActions();
        _statusNotifications?.Dispose();
        _statusNotifications = new StatusNotificationPresenter(_owner);
        // Narrow split cells drop the draft column: the merged draft is already in the main plan editor.
        if (_layoutGrid is { } layout)
            layout.SizeChanged += (_, _) =>
            {
                bool narrow = layout.ActualWidth < 760;
                layout.ColumnDefinitions[0].Width = narrow ? new GridLength(0) : new GridLength(35, GridUnitType.Star);
                layout.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 12);
                _draft.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
                if (_planTitleText is not null) _planTitleText.Visibility = _draft.Visibility;
                if (_statusLayoutGrid is not null) _statusLayoutGrid.ColumnDefinitions[2].Width = new GridLength(!LoopAutoRecommandUsers.Allowed ? 0 : narrow ? 150 : 210);
            };
        return root;
    }

    private void SetPlanningStatus(string text) => SetPlanningStatusCore(text, reopenDuplicate: false);

    private void SetPlanningResult(string text) => SetPlanningStatusCore(text, reopenDuplicate: true);

    private void SetPlanningWarning(string text) => SetPlanningStatusCore(text, reopenDuplicate: true, requiresAttention: true);

    private void SetPlanningStatusCore(string text, bool reopenDuplicate, bool requiresAttention = false)
    {
        _status.Text = text;
        AutomationProperties.SetName(_status, text);
        string label, background, foreground;
        if (text.Contains("거부", StringComparison.Ordinal))
            (label, background, foreground) = ("제공자 거부", "#242932", "#C2C7D0");
        else if (text.Contains("마무리 중", StringComparison.Ordinal) || text.Contains("질문을 마칩니다", StringComparison.Ordinal))
            (label, background, foreground) = ("마무리 중", "#242932", "#C2C7D0");
        else if (text.Contains("질문을 마쳤습니다", StringComparison.Ordinal))
            (label, background, foreground) = ("✓ 플랜 완료", "#1B3B2B", "#A7E8BD");
        else if (text.Contains("오류", StringComparison.Ordinal) || text.Contains("실패", StringComparison.Ordinal))
            (label, background, foreground) = ("오류", "#202020", "#FFA7A7");
        else if (_interview?.IsAwaitingContinuation == true || text.Contains("질문을 멈췄습니다", StringComparison.Ordinal))
            (label, background, foreground) = ("질문 일시정지", "#343434", "#FFCC80");
        else if (text.Contains("답해주세요", StringComparison.Ordinal))
            (label, background, foreground) = ("답변 대기", "#343434", "#FFCC80");
        else if (text.Contains("기다리는", StringComparison.Ordinal) || text.Contains("실행 대기", StringComparison.Ordinal))
            (label, background, foreground) = ("실행 대기", "#242932", "#C2C7D0");
        else if (text.Contains("연결 중", StringComparison.Ordinal) || text.Contains("준비 중", StringComparison.Ordinal))
            (label, background, foreground) = ("연결 중", "#202C40", "#84ADFF");
        else if (text.Contains("반영했습니다", StringComparison.Ordinal) || text.Contains("중단했습니다", StringComparison.Ordinal))
            (label, background, foreground) = (text.Contains("중단했습니다", StringComparison.Ordinal) ? "중단" : "초안 반영", "#242932", "#C2C7D0");
        else
            (label, background, foreground) = ("플랜 작성", "#2D2D2D", "#E5C07B");
        _statusLabel.Text = label;
        if (_statusBar is not null)
            _statusBar.BorderBrush = (Brush)new BrushConverter().ConvertFromString(PlanningCompleted && Error is null ? "#E5C07B" : "#2B2B2B")!;
        _statusLabel.Foreground = (Brush)new BrushConverter().ConvertFromString(foreground)!;
        _statusBadge.Background = (Brush)new BrushConverter().ConvertFromString(background)!;
        UpdateInterviewActions();
        if (requiresAttention) _statusNotifications?.PublishAttention(text, reopenDuplicate);
        else _statusNotifications?.Publish(text, reopenDuplicate);
    }

    private bool CanControlInterview() => _attemptActive && !_cleanupStarted &&
        !_cancellation.IsCancellationRequested && _interview is { IsFinishRequested: false } &&
        _attemptStillOwned?.Invoke() == true;

    private bool CanReturnToCompletedDraft() => PlanningCompleted && Error is null && !_attemptActive &&
        _attemptHasDraft && !_cleanupStarted && !_cancellation.IsCancellationRequested &&
        _attemptStillOwned?.Invoke() == true;

    private void UpdateInterviewActions()
    {
        // The initial status is painted before the footer is constructed.
        if (_finish is null || _continue is null || _recommendedAnswers is null) return;
        bool active = CanControlInterview();
        _finish.IsEnabled = (active && _interviewReady && _attemptHasDraft && _interview!.CanRequestFinish) ||
            (!_returnAfterFinish && CanReturnToCompletedDraft());
        _continue.Visibility = active && _interview!.IsAwaitingContinuation && !_interview.HasContinuationRequest
            ? Visibility.Visible : Visibility.Collapsed;
        _continue.IsEnabled = active;
        bool recommended = active && _interviewReady && Error is null && !PlanningCompleted &&
            _interview?.RecommendedAnswersEnabled == true;
        _recommendedAnswers.IsEnabled = active && _interviewReady && Error is null && !PlanningCompleted;
        _recommendedAnswers.Content = "Auto Recommand";
        _recommendedAnswers.FontWeight = recommended ? FontWeights.SemiBold : FontWeights.Normal;
        _recommendedAnswers.Background = recommended
            ? (Brush)new BrushConverter().ConvertFromString("#2E8B66")! : (Brush)_owner.FindResource("ButtonSurface");
        _recommendedAnswers.BorderBrush = recommended
            ? (Brush)new BrushConverter().ConvertFromString("#8FE3BF")! : (Brush)_owner.FindResource("ButtonLine");
        _recommendedAnswers.Foreground = recommended
            ? (Brush)new BrushConverter().ConvertFromString("#F4FFF9")! : Foreground;
        AutomationProperties.SetName(_recommendedAnswers, recommended
            ? "플랜메이커 Auto Recommand · 켜짐 · 직접 답변으로 전환"
            : "플랜메이커 Auto Recommand · 꺼짐");
        UpdateRecommendationStatus(active, recommended);
        CompleteWhenNoDecisionsRemain(active);
    }

    // With Auto Recommand on, an AI that stopped asking (repeated questionless
    // turns) has no decision left to continue. Finish the interview, turn the
    // automatic answers off and tell the user instead of idling in a pause.
    private void CompleteWhenNoDecisionsRemain(bool active)
    {
        if (!active || !_interviewReady || Error is not null || PlanningCompleted || _returnAfterFinish ||
            _interview is not { RecommendedAnswersEnabled: true } interview)
            return;
        // Auto Recommand also ends when it converged (answers stopped changing the plan) or hit its answer budget.
        string? autoStop = interview.AutoStopReason;
        if (autoStop is null && !(interview.IsAwaitingContinuation && !interview.HasContinuationRequest)) return;
        if (autoStop is not null) _autoStopReason = autoStop;
        if (!_attemptHasDraft || !interview.CanRequestFinish)
        {
            interview.SetRecommendedAnswersEnabled(false);
            SetPlanningWarning("Auto Recommand 해제 · 더 이어갈 결정이 없습니다. 반영된 초안이 없으니 질문 계속 또는 닫기를 선택하세요.");
            return;
        }
        interview.RequestFinish();
        if (!interview.IsFinishRequested) return;
        CompletedWithoutRemainingDecisions = true;
        SetPlanningStatus("질문 마무리 중… · " + (_autoStopReason ?? "더 이어갈 결정이 없어") + " Auto Recommand를 해제했습니다. 답변이 반영되면 완료로 표시합니다.");
    }

    private string? _autoStopReason;

    private void UpdateRecommendationStatus(bool active, bool recommended)
    {
        string text = _status.Text;
        string state, title, detail, color;
        if (_cleanupStarted || _cancellation.IsCancellationRequested)
            (state, title, detail, color) = ("stopped", "Auto Recommand 중단", "확인된 플랜 초안은 유지합니다.", "#C9D2CE");
        else if (Error is not null || text.Contains("오류", StringComparison.Ordinal) ||
            text.Contains("실패", StringComparison.Ordinal) || text.Contains("거부", StringComparison.Ordinal))
            (state, title, detail, color) = ("error", "Auto Recommand 중단 · 오류", "오류를 확인한 뒤 다시 시도하세요.", "#FFB6B6");
        else if (text.Contains("중단했습니다", StringComparison.Ordinal))
            (state, title, detail, color) = ("stopped", "Auto Recommand 중단", "확인된 플랜 초안은 유지합니다.", "#C9D2CE");
        else if (PlanningCompleted || text.Contains("질문을 마쳤습니다", StringComparison.Ordinal))
            (state, title, detail, color) = ("completed", "✓ 플랜 완료", CompletedWithoutRemainingDecisions
                ? "더 이어갈 결정이 없어 완료했습니다. 확인 후 플랜으로 돌아가세요."
                : "플랜에 저장했습니다. 플랜으로 돌아가면 이어서 정리합니다.", "#A7E8BD");
        else if (_interview?.IsFinishRequested == true || text.Contains("마무리 중", StringComparison.Ordinal) ||
            text.Contains("질문을 마칩니다", StringComparison.Ordinal))
            (state, title, detail, color) = ("finishing", "Auto Recommand 종료", "답변을 plan.md에 저장한 뒤 플랜으로 돌아갑니다.", "#C9D2CE");
        else if (Pane.IsWaitingForResources)
            (state, title, detail, color) = ("resources", "Auto Recommand 대기 · 자원", "터미널 실행 준비를 기다립니다.", "#C9D2CE");
        else if (text.Contains("접수를 확인하지 못", StringComparison.Ordinal))
            (state, title, detail, color) = ("manual-required", "Auto Recommand OFF · 직접 답변 필요", "전송 접수가 확인되지 않아 자동 선택을 멈췄습니다.", "#FFCC80");
        else if (!recommended)
            (state, title, detail, color) = ("manual", "Auto Recommand OFF · 직접 답변", "Auto Recommand를 켜면 자동 선택합니다.", "#C9D2CE");
        else if (!active || !_interviewReady)
            (state, title, detail, color) = ("stopped", "Auto Recommand 대기", "대화 연결과 상태를 확인합니다.", "#C9D2CE");
        else if (text.Contains("선택하고 전송 중", StringComparison.Ordinal))
            (state, title, detail, color) = ("submitting", "Auto Recommand ON · 답변 전송 중", "추천 선택지를 골라 전송하고 있습니다.", "#B4DBCF");
        else if (text.Contains("선택지와 추천 이유를 확인", StringComparison.Ordinal) ||
            text.Contains("보내기 전에 현재 질문을 확인", StringComparison.Ordinal))
            (state, title, detail, color) = ("processing", "Auto Recommand ON · 추천 확인 중", "설정된 모델의 추천과 현재 질문을 확인합니다.", "#B4DBCF");
        else if (text.Contains("명확한 추천 선택지가 없어", StringComparison.Ordinal))
            (state, title, detail, color) = ("manual-required", "Auto Recommand ON · 직접 답변 필요", "질문을 확인하고 직접 답변해주세요.", "#FFCC80");
        else if (text.Contains("입력 중", StringComparison.Ordinal))
            (state, title, detail, color) = ("input", "Auto Recommand ON · 직접 입력 중", "입력을 마칠 때까지 자동 선택을 멈춥니다.", "#FFCC80");
        else if (text.Contains("승인", StringComparison.Ordinal))
            (state, title, detail, color) = ("permission", "Auto Recommand ON · 승인 대기", "승인을 처리한 뒤 이어갑니다.", "#FFCC80");
        else if (_interview?.IsAwaitingContinuation == true)
            (state, title, detail, color) = ("paused", "Auto Recommand ON · 질문 일시정지", "질문 계속 또는 플랜으로 돌아가기를 선택하세요.", "#FFCC80");
        else if (text.Contains("답변을 반영하는 중", StringComparison.Ordinal))
            (state, title, detail, color) = ("processing", "Auto Recommand ON · 다음 질문 준비 중", "답변 반영 응답을 기다립니다.", "#B4DBCF");
        else
            (state, title, detail, color) = ("waiting", "Auto Recommand ON · 다음 질문 대기", "추천이 없으면 설정된 모델로 보완해 자동 선택합니다.", "#B4DBCF");
        _recommendationVisualState = state;
        _recommendationStatus.Text = title;
        _recommendationDetail.Text = detail;
        _recommendationStatus.ToolTip = detail;
        _recommendationDetail.Visibility = Visibility.Collapsed;
        _recommendationStatus.Foreground = (Brush)new BrushConverter().ConvertFromString(color)!;
        _recommendationIndicator.Stroke = _recommendationStatus.Foreground;
        _recommendationIndicator.Data = Geometry.Parse(state is "submitting" or "processing"
            ? "M 8,1 A 7,7 0 1 1 1,8" : state == "completed"
            ? "M 2,8 L 6,12 L 14,3" : "M 5,3 L 5,13 M 11,3 L 11,13");
        AutomationProperties.SetName(_recommendationStatus, title + " · " + detail);
        bool animate = (state == "submitting" || state == "processing") && IsShownToUser &&
            SystemParameters.ClientAreaAnimation;
        if (_recommendationAnimationActive == animate) return;
        _recommendationAnimationActive = animate;
        var rotation = (RotateTransform)_recommendationIndicator.RenderTransform;
        rotation.BeginAnimation(RotateTransform.AngleProperty, animate
            ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);
        if (!animate) rotation.Angle = 0;
    }

    internal async Task RunAsync(string prompt, Func<bool> stillOwned, Action<string> applyDraft)
    {
        void Start() => _operation ??= PlanAsync(prompt, stillOwned, applyDraft);
        if (_dockedRoot is { } docked)
        {
            if (docked.IsLoaded) Start();
            else docked.Loaded += (_, _) => Start();
        }
        else Loaded += (_, _) => Start();
        _retry.Click += (_, _) =>
        {
            if (_retrying || _cleanupStarted || _cancellation.IsCancellationRequested || Error is null ||
                _operation?.IsCompleted != true || !stillOwned()) return;
            _retrying = true;
            _retry.IsEnabled = false;
            _operation = RetryPlanAsync(prompt, stillOwned, applyDraft);
        };
        using var cancelled = _cancellation.Token.Register(() => Dispatcher.BeginInvoke((Action)(() =>
        { if (!_cleaned) Close(); })));
        await Task.Yield();
        try
        {
            if (_cancellation.IsCancellationRequested) return;
            if (!IsDocked) Show();
            await _closed.Task;
        }
        finally
        {
            if (!_cleaned)
            {
                _cancellation.Cancel();
                await CleanUpAsync();
            }
            _cancellation.Dispose();
        }
    }

    private TerminalPane CreatePlanningPane()
    {
        var pane = new TerminalPane(_project, startupCommand: "opencode", protectTuiViewport: true,
            paneKey: "planner-" + Guid.NewGuid().ToString("N"), useProjectContext: false);
        pane.UserWarning += SetPlanningWarning;
#if TN_WPF
        pane.UserWarning += _ => ApplicationDiagnostics.Record("planner_warning", "planner");
        pane.ProcessExited += code => ApplicationDiagnostics.Record("terminal_process_exited", "planner",
            "exit_" + code.ToString(global::System.Globalization.CultureInfo.InvariantCulture));
        pane.LifecycleFailureReported += (operation, exceptionType, _) =>
            ApplicationDiagnostics.Record("terminal_lifecycle_fault", operation, exceptionType);
        pane.LifecycleStageChanged += stage =>
        {
            if (stage.Contains("failed", StringComparison.Ordinal) || stage.StartsWith("ime_", StringComparison.Ordinal))
                ApplicationDiagnostics.Record("terminal_lifecycle_stage", "planner", stage);
        };
#endif
        pane.SetAutoResumeEnabled(false);
        pane.EnableResourceAdmissionRunOnce();
        return pane;
    }

    private async Task RetryPlanAsync(string prompt, Func<bool> stillOwned, Action<string> applyDraft)
    {
        try
        {
            SetPlanningStatus("플랜메이커 전용 터미널 다시 연결 중…");
            await Pane.StopForTerminalLifecycleAsync();
            Pane.Dispose();
            _cancellation.Token.ThrowIfCancellationRequested();
            if (!stillOwned()) throw new OperationCanceledException(_cancellation.Token);
            var layout = (Grid)Pane.Parent;
            var replacement = CreatePlanningPane();
            layout.Children.Remove(Pane);
            Pane = replacement;
            Grid.SetRow(Pane, 2); Grid.SetColumn(Pane, 2); layout.Children.Add(Pane);
            Error = null;
            _retry.Visibility = Visibility.Collapsed;
            await PlanAsync(prompt, stillOwned, applyDraft);
        }
        catch (OperationCanceledException)
        { SetPlanningResult("플랜메이커를 중단했습니다 · 반영된 초안은 유지합니다."); }
        catch (Exception exception)
        {
            Error = exception.Message;
            SetPlanningResult("플랜메이커 오류 · 초안은 유지합니다 · " + exception.Message);
            _retry.Visibility = _cleanupStarted ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            _retrying = false;
            _retry.IsEnabled = !_cleanupStarted && !_cancellation.IsCancellationRequested;
        }
    }

    private async Task PlanAsync(string prompt, Func<bool> stillOwned, Action<string> applyDraft)
    {
        _interview = new PlanMakerInterviewControl();
        _attemptStillOwned = stillOwned;
        _attemptActive = true;
        _attemptHasDraft = false;
        _interviewReady = false;
        _returnAfterFinish = false;
        PlanningCompleted = false;
        string retainedDraft = _confirmedGeneratedDraft;
        string attemptPrompt = string.IsNullOrEmpty(retainedDraft) ? prompt : prompt +
            "\n\n## 이전 대화에서 확인되어 반영된 플랜 내용\n\n" + retainedDraft +
            "\n\n위 내용의 구체적인 조건을 유지하고 아직 확인하지 않은 요구사항만 질문하세요. " +
            "기존 카테고리에 해당하면 같은 제목과 상위 제목을 그대로 사용하고, 이미 확인한 내용을 반복하지 말고 새 조건만 간결하게 작성하세요.";
        void ApplyConfirmedDraft(string generated)
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            if (!stillOwned()) throw new OperationCanceledException(_cancellation.Token);
            // Each attempt can revise its own draft, but cannot discard already confirmed
            // additions from an earlier attempt, even if a fresh model omits that context.
            string combined = PlanMakerWorkflow.MergePlanAdditions(retainedDraft, generated,
                "## 재시도에서 보완한 내용");
            applyDraft(combined);
            _confirmedGeneratedDraft = combined;
            _draft.Text = combined;
            _attemptHasDraft = !string.IsNullOrWhiteSpace(combined);
            UpdateInterviewActions();
        }
        try
        {
            if (string.IsNullOrWhiteSpace(prompt))
                throw new InvalidOperationException("플랜 요청을 입력해주세요. 확인된 플랜 초안은 유지합니다.");
            // Retried requests include all confirmed conditions and the retry
            // instructions. Validate their complete native envelope, never trim it.
            if (attemptPrompt.Length > PlanMakerWorkflow.ExpandedPromptLength)
                throw new PlanMakerPromptTooLongException(attemptPrompt.Length, PlanMakerWorkflow.ExpandedPromptLength);
            SetPlanningStatus("플랜메이커 전용 터미널 연결 중…");
            void ShowAdmissionStatus(string status)
            {
                if (Pane.IsWaitingForResources) SetPlanningStatus(Pane.ResourceWaitReason ?? status);
            }
            Pane.StatusChanged += ShowAdmissionStatus;
            try { await Pane.WaitForResourceAdmissionAsync(_cancellation.Token); }
            finally { Pane.StatusChanged -= ShowAdmissionStatus; }
            SetPlanningStatus("플랜메이커 전용 터미널 연결 중…");
            using var readiness = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
            readiness.CancelAfter(TerminalExecutionTiming.StartupReadyTimeout);
            await Pane.EnsureReadyAsync(readiness.Token);
            while (!Pane.CanStartNativeAutomation)
            {
                if (!stillOwned()) throw new OperationCanceledException(_cancellation.Token);
                if (!Pane.IsRunning || Pane.NativeAutomationDiagnostic.Failed)
                    throw new InvalidOperationException("플랜메이커 전용 터미널이 종료되었거나 시작에 실패했습니다.");
                await Task.Delay(100, readiness.Token);
            }
            _interviewReady = true;
            UpdateInterviewActions();
            string result = await Pane.CreatePlanWithQuestionsAsync(attemptPrompt,
                () => !_cancellation.IsCancellationRequested && stillOwned(), SetPlanningStatus,
                _cancellation.Token, ApplyConfirmedDraft, _interview);
            _cancellation.Token.ThrowIfCancellationRequested();
            if (!stillOwned()) throw new OperationCanceledException(_cancellation.Token);
            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidOperationException("플랜메이커가 플랜 내용을 반환하지 않았습니다. 확인된 초안을 유지한 채 다시 시도하세요.");
            ApplyConfirmedDraft(result);
            PlanningCompleted = true;
            if (CompletedWithoutRemainingDecisions)
            {
                string why = _autoStopReason ?? "더 이어갈 결정이 없어";
                SetPlanningResult($"질문을 마쳤습니다 · {why} 플랜을 완료했습니다. 내용을 확인한 뒤 ‘플랜으로 돌아가기’를 누르세요.");
                _statusNotifications?.PublishAttention($"플랜메이커 완료 · {why} Auto Recommand를 해제했습니다. 더 개선하려면 Auto Recommand를 다시 켜세요. 창은 열어 두었으니 확인 후 플랜으로 돌아가세요.", reopenDuplicate: true);
            }
            else SetPlanningResult(_interview.IsFinishRequested
                ? "질문을 마쳤습니다 · 확인된 내용은 plan.md에 저장했습니다. 플랜으로 돌아가면 이어서 정리합니다."
                : "플랜에 반영해 plan.md에 저장했습니다 · 플랜으로 돌아가면 이어서 정리합니다.");
        }
        catch (OperationCanceledException)
        { SetPlanningResult("플랜메이커를 중단했습니다 · 반영된 초안은 유지합니다."); }
        catch (Exception exception)
        {
            Error = exception.Message;
            SetPlanningResult("플랜메이커 오류 · 초안은 유지합니다 · " + exception.Message);
            _retry.Visibility = _cleanupStarted ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            _interview.SetRecommendedAnswersEnabled(false);
            _interviewReady = false;
            _attemptActive = false;
            UpdateInterviewActions();
            if (_returnAfterFinish && CanReturnToCompletedDraft())
            {
                PlanMakerInterviewControl completedInterview = _interview;
                _ = Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (!ReferenceEquals(_interview, completedInterview)) return;
                    if (_returnAfterFinish && CanReturnToCompletedDraft()) Close();
                    else { _returnAfterFinish = false; UpdateInterviewActions(); }
                }));
            }
            else _returnAfterFinish = false;
        }
    }

    // Returning to the plan (or closing) takes the PlanMaker off the screen at once (2026-11-06 사용자: "플랜메이커로 플랜계획을
    // 세우고 플랜으로 돌아가기 하면 멈추고 진행이 안되는거처럼 보이는데"); its terminal is shut down behind the scenes.
    internal event Action? Leaving;
    // After a completed interview the owner may still use this PlanMaker's OpenCode (the plan cleanup) before it closes, so the
    // cleanup needs no OpenCode of its own.
    internal Func<TerminalPane, Task>? BeforePaneCloses { get; set; }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_cleaned) return;
        e.Cancel = true;
        if (_cleanupStarted) return;
        _cleanupStarted = true;
        _interview?.SetRecommendedAnswersEnabled(false);
        UpdateInterviewActions();
        _cancellation.Cancel();
        try { Leaving?.Invoke(); }
        catch (Exception exception) { System.Diagnostics.Trace.WriteLine(exception); }
        // A window cannot hide while its Closing runs: right after it. The cleanup (which may open the plan cleanup's modal
        // window) also starts only after this handler has returned.
        _ = Dispatcher.BeginInvoke((Action)(() => { if (!_cleaned && !IsDocked && IsVisible) Hide(); }));
        await global::System.Windows.Threading.Dispatcher.Yield(global::System.Windows.Threading.DispatcherPriority.Background);
        try { await CleanUpAsync(); }
        catch (Exception exception) { Error = exception.Message; }
        _ = Dispatcher.BeginInvoke((Action)(() =>
        { if (!_closed.Task.IsCompleted) Close(); }));
    }

    private Task CleanUpAsync() => _cleanup ??= CleanUpCoreAsync();

    private async Task CleanUpCoreAsync()
    {
        try { if (_operation is not null) await _operation; }
        finally
        {
            try
            {
                if (BeforePaneCloses is { } before && PlanningCompleted && Error is null)
                {
                    try { await before(Pane); }
                    catch (Exception exception) { System.Diagnostics.Trace.WriteLine(exception); }
                }
            }
            finally
            {
                try { await Pane.StopForTerminalLifecycleAsync(); }
                finally { Pane.Dispose(); _cleaned = true; }
            }
        }
    }
}
}
#endregion
#endif

#if TN_WPF || TN_SOURCE_TASKGLYPH_CS
#region TaskGlyph.cs
namespace TerminalNotes
{
using global::System.Windows;
using global::System.Windows.Controls;
using global::System.Windows.Media;


internal enum TaskGlyphKind { Multi, Single, Play, Resume, Stop, Open, Wait, Pause, Attention, Complete, Folder, Loop, Clock, Hourglass }

/// <summary>Small, font-independent symbols shared by task actions and status cells.</summary>
internal sealed class TaskGlyph : Control
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(TaskGlyphKind), typeof(TaskGlyph),
        new FrameworkPropertyMetadata(TaskGlyphKind.Wait, FrameworkPropertyMetadataOptions.AffectsRender));

    public TaskGlyphKind Kind { get => (TaskGlyphKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    private static readonly IReadOnlyDictionary<TaskGlyphKind, Geometry> Outlines = new Dictionary<TaskGlyphKind, Geometry>
    {
        [TaskGlyphKind.Multi] = Shape("M2,6 L2,2 11,2 11,4 M5,5 L14,5 14,14 5,14 Z"),
        [TaskGlyphKind.Single] = Shape("M2,3 L14,3 14,13 2,13 Z M2,6 L14,6"),
        [TaskGlyphKind.Play] = Shape("M5,3 L13,8 5,13 Z"),
        [TaskGlyphKind.Resume] = Shape("M2,3 L2,13 M6,3 L14,8 6,13 Z"),
        [TaskGlyphKind.Stop] = Shape("M4,4 L12,4 12,12 4,12 Z"),
        [TaskGlyphKind.Open] = Shape("M7,3 L3,3 3,13 13,13 13,9 M8,2 L14,2 14,8 M14,2 L7,9"),
        [TaskGlyphKind.Pause] = Shape("M5,3 L5,13 M11,3 L11,13"),
        [TaskGlyphKind.Attention] = Shape("M8,2 L15,14 1,14 Z M8,6 L8,9 M8,11.5 L8,11.6"),
        [TaskGlyphKind.Complete] = Shape("M3,8 L6.5,11.5 13,4.5"),
        [TaskGlyphKind.Folder] = Shape("M1.5,4 L6,4 8,6 14.5,6 14.5,13 1.5,13 Z M1.5,4 L1.5,2.5 6,2.5 8,4 13,4 13,6"),
        [TaskGlyphKind.Clock] = Shape("M8,2 A6,6 0 1 1 7.99,2 Z M8,4.8 L8,8 10.4,9.6"),
        // Loop end time: an hourglass, so it never looks like the start schedule's clock.
        [TaskGlyphKind.Hourglass] = Shape("M4,2 L12,2 M4,14 L12,14 M5,2 C5,5.6 7.2,6.6 7.2,8 C7.2,9.4 5,10.4 5,14 M11,2 C11,5.6 8.8,6.6 8.8,8 C8.8,9.4 11,10.4 11,14 M6.4,12.6 L9.6,12.6"),
        // Ouroboros body: a ring that runs from just behind the tail around to the head.
        [TaskGlyphKind.Loop] = Shape("M12.6,7.4 A4.8,4.8 0 1 1 3.6,6.6")
    };

    public TaskGlyph()
    {
        Width = Height = 16;
        Focusable = false;
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
    }

    // Ouroboros emblem seen from above: a gilded serpent ring whose viper head, at the top, swallows its own tail.
    // Built once on the 16-unit grid in ring coordinates (s = degrees along the ring from the head centre,
    // d = offset from the ring's centreline); OnRender shades it from Foreground so every state colour works.
    private const double OuroborosCenterX = 8.0, OuroborosCenterY = 8.0, OuroborosRadius = 5.15;
    private const double OuroborosHeadAngle = -90;                     // head centre at the top, facing clockwise
    private const double OuroborosNeckS = -24, OuroborosTailEndS = 6;  // body leaves the head's back, ends inside the mouth
    private const double OuroborosSweep = 360 + OuroborosNeckS - OuroborosTailEndS;
    private const double OuroborosBodyWidth = 2.25, OuroborosMouthWidth = 1.05;
    // The head is drawn larger below 40px so it still reads in task rows; engraving and eyes appear from 40px.
    private const double OuroborosHeadScale = 1.12, OuroborosSmallHeadScale = 1.62, OuroborosDetailSize = 40;
    private static readonly (double S, double D)[] OuroborosHeadSide =
    [
        (-25, 1.02), (-21, 1.2), (-16, 1.55), (-11, 1.78), (-6, 1.82), (-1, 1.7), (4, 1.45), (9, 1.12), (13, 0.86), (15.5, 0.74)
    ];
    private static readonly Geometry OuroborosBody = Frozen(BuildOuroborosBody());
    private static readonly Geometry OuroborosHead = Frozen(BuildOuroborosHead(OuroborosHeadScale));
    private static readonly Geometry OuroborosSmallHead = Frozen(BuildOuroborosHead(OuroborosSmallHeadScale));
    private static readonly Geometry OuroborosScales = Frozen(BuildOuroborosScales());
    private static readonly Geometry OuroborosBackHighlight = Frozen(BuildOuroborosAlong(0.02, 0.6, 0.3));
    private static readonly Geometry OuroborosSilhouette = Frozen(Geometry.Combine(OuroborosBody, OuroborosHead, GeometryCombineMode.Union, null));

    private static Point OnRing(double s, double d)
    {
        double angle = (OuroborosHeadAngle + s) * Math.PI / 180;
        return new(OuroborosCenterX + (OuroborosRadius + d) * Math.Cos(angle), OuroborosCenterY + (OuroborosRadius + d) * Math.Sin(angle));
    }

    // Body position t (0 = neck, 1 = tail end) to ring degrees; the body runs counter-clockwise from the neck.
    private static double OuroborosBodyS(double t) => OuroborosNeckS - OuroborosSweep * t;

    // Full width for most of the ring, then an even taper so the tail still shows where the jaws close on it.
    private static double OuroborosWidth(double t)
    {
        if (t <= 0.58) return OuroborosBodyWidth;
        double x = Math.Min(1, (t - 0.58) / 0.36), ease = x * x * (3 - 2 * x);
        return OuroborosBodyWidth - (OuroborosBodyWidth - OuroborosMouthWidth) * ease;
    }

    private static Geometry BuildOuroborosBody()
    {
        var outer = new List<Point>();
        var inner = new List<Point>();
        // Start a little under the head so the neck's cut end never shows.
        for (double lead = 12; lead > 0; lead -= 2)
        {
            outer.Add(OnRing(OuroborosNeckS + lead, OuroborosBodyWidth / 2 * 0.9));
            inner.Add(OnRing(OuroborosNeckS + lead, -OuroborosBodyWidth / 2 * 0.9));
        }
        const int steps = 220;
        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps, width = OuroborosWidth(t), s = OuroborosBodyS(t);
            outer.Add(OnRing(s, width / 2));
            inner.Add(OnRing(s, -width / 2));
        }
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(outer[0], true, true);
            context.PolyLineTo(outer.Skip(1).ToList(), true, true);
            context.ArcTo(inner[^1], new Size(OuroborosMouthWidth / 2, OuroborosMouthWidth / 2), 0, false, SweepDirection.Clockwise, true, true);
            inner.Reverse();
            context.PolyLineTo(inner.Skip(1).ToList(), true, true);
        }
        return geometry;
    }

    // Viper head from above: narrow at the neck, broad jowls, a tapering snout, and an open mouth whose two lips
    // close on the sides of the tail (the tail's end hides at the mouth corner).
    private static Geometry BuildOuroborosHead(double k)
    {
        Point Q(double s, double d) => OnRing(s * k, d * k);
        var side = OuroborosHeadSide.Select(p => Q(p.S, p.D)).ToList();
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(side[0], true, true);
            context.PolyBezierTo(SmoothThrough(side), true, true);
            context.BezierTo(Q(16.6, 0.66), Q(16.5, 0.5), Q(15.6, 0.44), true, true);   // lip tip
            context.BezierTo(Q(13.8, 0.4), Q(12.0, 0.3), Q(10.6, 0.0), true, true);      // mouth corner over the tail
            context.BezierTo(Q(12.0, -0.3), Q(13.8, -0.4), Q(15.6, -0.44), true, true);
            var mirrored = OuroborosHeadSide.Reverse().Select(p => Q(p.S, -p.D)).ToList();
            context.BezierTo(Q(16.5, -0.5), Q(16.6, -0.66), mirrored[0], true, true);
            context.PolyBezierTo(SmoothThrough(mirrored), true, true);
        }
        return geometry;
    }

    // Catmull-Rom through the points as cubic Bezier segments (the first point is the current position).
    private static IList<Point> SmoothThrough(IList<Point> points)
    {
        var result = new List<Point>();
        for (int i = 0; i < points.Count - 1; i++)
        {
            Point p0 = points[Math.Max(0, i - 1)], p1 = points[i], p2 = points[i + 1], p3 = points[Math.Min(points.Count - 1, i + 2)];
            result.Add(p1 + (p2 - p0) / 6);
            result.Add(p2 - (p3 - p1) / 6);
            result.Add(p2);
        }
        return result;
    }

    private static Geometry OuroborosPath(params (double S, double D)[] points)
    {
        var mapped = points.Select(p => OnRing(p.S, p.D)).ToList();
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(mapped[0], false, false);
            context.PolyBezierTo(SmoothThrough(mapped), true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    // Engraved scales: staggered crescents in three rows, thinning out where the tail narrows.
    private static Geometry BuildOuroborosScales()
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (int row = 0; row < 3; row++)
            {
                double across = row switch { 0 => 0.27, 1 => 0.0, _ => -0.27 };
                for (double step = 4 + row * 5; step < OuroborosSweep * 0.9; step += 10)
                {
                    double t = step / OuroborosSweep, width = OuroborosWidth(t), s = OuroborosBodyS(t);
                    if (width < 1.3 && row != 1) continue;
                    double d = width * across, span = 2.2 * width / OuroborosBodyWidth;
                    context.BeginFigure(OnRing(s + span, d + width * 0.1), false, false);
                    context.QuadraticBezierTo(OnRing(s, d - width * 0.13), OnRing(s - span, d + width * 0.1), true, true);
                }
            }
        }
        return geometry;
    }

    private static Geometry BuildOuroborosAlong(double from, double to, double across)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            const int steps = 100;
            for (int i = 0; i <= steps; i++)
            {
                double t = from + (to - from) * i / steps;
                var point = OnRing(OuroborosBodyS(t), OuroborosWidth(t) * across);
                if (i == 0) context.BeginFigure(point, false, false);
                else context.LineTo(point, true, true);
            }
        }
        return geometry;
    }

    private static Geometry OuroborosAlmond(double s, double d, double length, double height)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(OnRing(s - length, d), true, true);
            context.BezierTo(OnRing(s - length * 0.5, d + height), OnRing(s + length * 0.5, d + height), OnRing(s + length, d), true, true);
            context.BezierTo(OnRing(s + length * 0.5, d - height), OnRing(s - length * 0.5, d - height), OnRing(s - length, d), true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Color Mix(Color from, Color to, double amount) => Color.FromArgb(
        (byte)(from.A + (to.A - from.A) * amount), (byte)(from.R + (to.R - from.R) * amount),
        (byte)(from.G + (to.G - from.G) * amount), (byte)(from.B + (to.B - from.B) * amount));

    private static Pen RoundPen(Color color, double width) => new(new SolidColorBrush(color), width)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

    private void DrawOuroboros(DrawingContext drawing)
    {
        // Polished metal from the state colour, lit from the upper left, with tube shading that rounds the body;
        // shadows are neutral black so no brown tones appear. From 40px: drop shadow, fine outline, engraved scales,
        // brows and slit-pupil eyes. Smaller: a larger head with dark eyes and mouth lines.
        Color baseColor = Foreground is SolidColorBrush solid ? solid.Color : Colors.Gainsboro;
        bool detailed = Math.Min(ActualWidth, ActualHeight) >= OuroborosDetailSize;
        Color light = Mix(baseColor, Colors.White, 0.6), deep = Mix(baseColor, Color.FromRgb(0x22, 0x22, 0x22), 0.52);
        Color ink = Color.FromRgb(0x12, 0x12, 0x12);
        var metal = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(3, 2), EndPoint = new Point(13.5, 14.5),
            GradientStops = { new GradientStop(light, 0), new GradientStop(baseColor, 0.38), new GradientStop(Mix(baseColor, deep, 0.5), 0.74), new GradientStop(deep, 1) }
        };
        var center = new Point(OuroborosCenterX, OuroborosCenterY);
        RadialGradientBrush Tube(double halfWidth, byte innerShade, byte gloss, byte outerShade)
        {
            double extent = OuroborosRadius + halfWidth;
            return new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute, Center = center, GradientOrigin = center, RadiusX = extent, RadiusY = extent,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(innerShade, 0, 0, 0), (OuroborosRadius - halfWidth) / extent),
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), (OuroborosRadius - halfWidth * 0.24) / extent),
                    new GradientStop(Color.FromArgb(gloss, 255, 255, 255), (OuroborosRadius + halfWidth * 0.36) / extent),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), (OuroborosRadius + halfWidth * 0.7) / extent),
                    new GradientStop(Color.FromArgb(outerShade, 0, 0, 0), 1)
                }
            };
        }
        var outline = detailed
            ? new Pen(new SolidColorBrush(WithAlpha(ink, 210)), 0.13) { LineJoin = PenLineJoin.Round }
            : new Pen(new SolidColorBrush(WithAlpha(ink, 200)), 0.3) { LineJoin = PenLineJoin.Round };

        if (detailed)
        {
            drawing.PushTransform(new TranslateTransform(0.16, 0.3));
            drawing.DrawGeometry(new SolidColorBrush(Color.FromArgb(85, 0, 0, 0)), null, OuroborosSilhouette);
            drawing.Pop();
        }
        drawing.DrawGeometry(metal, detailed ? outline : null, OuroborosBody);
        drawing.DrawGeometry(Tube(OuroborosBodyWidth / 2, 140, 85, 110), null, OuroborosBody);
        if (detailed) drawing.DrawGeometry(null, RoundPen(WithAlpha(ink, 70), 0.1), OuroborosScales);
        drawing.DrawGeometry(null, RoundPen(Color.FromArgb((byte)(detailed ? 165 : 125), 255, 255, 255), detailed ? 0.2 : 0.32), OuroborosBackHighlight);

        double k = detailed ? OuroborosHeadScale : OuroborosSmallHeadScale;
        Geometry head = detailed ? OuroborosHead : OuroborosSmallHead;
        drawing.DrawGeometry(metal, outline, head);
        drawing.DrawGeometry(Tube(1.9, 120, 90, 100), null, head);
        drawing.DrawGeometry(null, RoundPen(Color.FromArgb((byte)(detailed ? 180 : 130), 255, 255, 255), detailed ? 0.2 : 0.3),
            OuroborosPath((-20 * k, 0.32 * k), (-12 * k, 0.5 * k), (-3 * k, 0.5 * k), (6 * k, 0.36 * k), (11 * k, 0.22 * k)));
        if (detailed)
        {
            drawing.DrawGeometry(null, RoundPen(WithAlpha(ink, 110), 0.1), OuroborosPath((-19 * k, -0.1 * k), (-10 * k, -0.12 * k), (0, -0.1 * k), (8 * k, -0.05 * k)));
            foreach (int side in new[] { 1, -1 })
            {
                drawing.DrawGeometry(null, RoundPen(WithAlpha(ink, 160), 0.12),
                    OuroborosPath((-9 * k, side * 1.05 * k), (-4 * k, side * 1.38 * k), (1.5 * k, side * 1.36 * k), (5 * k, side * 1.12 * k)));
                drawing.DrawGeometry(new SolidColorBrush(Mix(light, Colors.White, 0.5)), new Pen(new SolidColorBrush(WithAlpha(ink, 230)), 0.07),
                    OuroborosAlmond(-1.5 * k, side * 1.12 * k, 2.6 * k, side * 0.22 * k));
                drawing.DrawGeometry(new SolidColorBrush(ink), null, OuroborosAlmond(-1.5 * k, side * 1.12 * k, 0.55 * k, side * 0.17 * k));
                drawing.DrawEllipse(new SolidColorBrush(WithAlpha(ink, 150)), null, OnRing(13.2 * k, side * 0.42 * k), 0.06, 0.06);
            }
        }
        else
        {
            foreach (int side in new[] { 1, -1 })
            {
                drawing.DrawEllipse(new SolidColorBrush(WithAlpha(ink, 240)), null, OnRing(-1.2 * k, side * 1.0 * k), 0.42, 0.42);
                drawing.DrawGeometry(null, RoundPen(WithAlpha(ink, 220), 0.34), OuroborosPath((10.6 * k, 0), (13.5 * k, side * 0.32 * k), (15.4 * k, side * 0.4 * k)));
            }
        }
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Brush FrozenBrush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }

    private static Geometry Frozen(Geometry geometry) { geometry.Freeze(); return geometry; }

    private static Geometry Shape(string path)
    {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        return geometry;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        double scale = Math.Min(ActualWidth, ActualHeight) / 16;
        if (scale <= 0) return;
        drawing.PushTransform(new ScaleTransform(scale, scale));
        var pen = new Pen(Foreground, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (Kind == TaskGlyphKind.Wait)
            foreach (double x in new[] { 3d, 8d, 13d }) drawing.DrawEllipse(Foreground, null, new Point(x, 8), 1, 1);
        else if (Kind == TaskGlyphKind.Loop) DrawOuroboros(drawing);
        else drawing.DrawGeometry(null, pen, Outlines[Kind]);
        drawing.Pop();
    }
}
}
#endregion
#endif

#if TN_WPF || TN_SOURCE_TASKNOTIFICATIONMINUTESDIALOG_CS
#region TaskNotificationMinutesDialog.cs
namespace TerminalNotes
{
using global::TerminalNotes.Services;
using global::System.Windows;
using global::System.Windows.Controls;
using global::System.Windows.Input;


internal sealed class TaskNotificationMinutesDialog : Window
{
    private readonly TextBox _minutesInput;
    private readonly TextBlock _errorText;

    public int Minutes { get; private set; }

    public TaskNotificationMinutesDialog(Window owner)
    {
        Owner = owner;
        Title = "\uC54C\uB9BC \uBBF8\uB8E8\uAE30";
        Width = 380;
        Height = 190;
        SizeToContent = SizeToContent.Height;
        MinWidth = Width;
        MinHeight = Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 17, 17));
        Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 233, 216));
        FontFamily = new System.Windows.Media.FontFamily(new Uri("pack://application:,,,/TerminalNotes;component/"), "./Assets/Fonts/#Pretendard, Segoe UI, Malgun Gothic");
        FontSize = 14;
        if (owner.TryFindResource(typeof(Button)) is Style buttonStyle) Resources[typeof(Button)] = buttonStyle;

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = "미룰 시간 (분)",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        };
        root.Children.Add(heading);

        var description = new TextBlock
        {
            Text = "\uBD84 \uB2E8\uC704\uB85C \uC591\uC218\uB97C \uC785\uB825\uD558\uC138\uC694.",
            Visibility = Visibility.Collapsed,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(201, 192, 174)),
            Margin = new Thickness(0, 0, 0, 7)
        };
        Grid.SetRow(description, 1);
        root.Children.Add(description);

        _minutesInput = new TextBox
        {
            Text = "15",
            Padding = new Thickness(8, 6, 8, 6),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(9, 9, 9)),
            Foreground = Foreground,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(102, 82, 57)),
            BorderThickness = new Thickness(1),
            CaretBrush = Foreground
        };
        System.Windows.Automation.AutomationProperties.SetName(_minutesInput, "알림을 미룰 시간 (분)");
        _minutesInput.PreviewKeyDown += MinutesInput_PreviewKeyDown;
        Grid.SetRow(_minutesInput, 2);
        root.Children.Add(_minutesInput);

        _errorText = new TextBlock
        {
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 167, 167)),
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed
        };
        Grid.SetRow(_errorText, 3);
        root.Children.Add(_errorText);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = CreateButton("\uCDE8\uC18C");
        cancel.IsCancel = true;
        cancel.Click += (_, _) => DialogResult = false;
        var apply = CreateButton("\uBBF8\uB8E8\uAE30", primary: true);
        if (owner.TryFindResource("PrimaryButton") is Style primaryStyle)
        {
            apply.Style = primaryStyle;
            apply.ClearValue(Control.BackgroundProperty);
        }
        apply.IsDefault = true;
        apply.Click += (_, _) => AcceptMinutes();
        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);
        Content = new Border { Child = root, BorderThickness = new Thickness(1),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(102, 82, 57)) };

        Loaded += (_, _) =>
        {
            _minutesInput.Focus();
            _minutesInput.SelectAll();
        };
    }

    private static Button CreateButton(string text, bool primary = false) => new()
    {
        Content = text,
        Padding = new Thickness(13, 7, 13, 7),
        Margin = new Thickness(8, 10, 0, 0),
        MinWidth = 72,
        Background = new System.Windows.Media.SolidColorBrush(primary
            ? System.Windows.Media.Color.FromRgb(216, 188, 130)
            : System.Windows.Media.Color.FromRgb(24, 24, 24)),
        Foreground = new System.Windows.Media.SolidColorBrush(primary
            ? System.Windows.Media.Color.FromRgb(33, 28, 20)
            : System.Windows.Media.Color.FromRgb(241, 233, 216)),
        BorderBrush = new System.Windows.Media.SolidColorBrush(primary
            ? System.Windows.Media.Color.FromRgb(216, 188, 130)
            : System.Windows.Media.Color.FromRgb(102, 82, 57)),
        BorderThickness = new Thickness(1)
    };

    private void MinutesInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            AcceptMinutes();
        }
    }

    private void AcceptMinutes()
    {
        if (!TaskNotificationPreferences.TryGetSnoozeUntil(_minutesInput.Text, DateTimeOffset.UtcNow, out var untilUtc))
        {
            _errorText.Text = "\uC624\uB958: 1\uBD84 \uC774\uC0C1\uC758 \uC720\uD6A8\uD55C \uC815\uC218\uB97C \uC785\uB825\uD558\uC138\uC694.";
            _errorText.Visibility = Visibility.Visible;
            _minutesInput.Focus();
            _minutesInput.SelectAll();
            return;
        }

        Minutes = checked((int)Math.Ceiling((untilUtc - DateTimeOffset.UtcNow).TotalMinutes));
        DialogResult = true;
    }
}
}
#endregion
#endif

#if TN_WPF || TN_SOURCE_TERMINALPROFILESDIALOG_XAML_CS
#region TerminalProfilesDialog.xaml.cs
namespace TerminalNotes
{
using global::System.IO;
using global::System.Security;
using global::System.Windows;
using global::Microsoft.Win32;


public partial class TerminalProfilesDialog : Window
{
    private const string FallbackWorkspace = @"C:\opencode";
    private readonly string _diagnosticSettingsPath;
    private readonly CancellationTokenSource _autoInstallCancellation = new();
    private bool _autoInstallBusy;
    private bool _updatingWorkArea;

    private readonly bool _firstRun;

    public TerminalProfilesDialog(string defaultWorkspace, bool useAutoMode, bool firstRun = false, string? diagnosticSettingsPath = null, bool basicWorkMode = true, bool planWorkMode = false,
        global::TerminalNotes.Services.TerminalTool[]? terminalAgents = null)
    {
        _firstRun = firstRun;
        InitializeComponent();
        _diagnosticSettingsPath = diagnosticSettingsPath ?? MainWindow.DefaultSettingsPath;
        Title = firstRun ? "작업 시작" : "작업 설정";
        DialogHeading.Text = Title;
        SaveButton.Content = firstRun ? "시작" : "저장";
        // First run asks only for the work folder: beginners start in 일반 mode with Auto Mode on.
        // The mode choice stays out of this window; 작업 설정 and the top steps change both later.
        FirstRunWorkModeSection.Visibility = Visibility.Collapsed;
        BasicWorkModeRadio.IsChecked = firstRun || basicWorkMode;
        PlanWorkModeRadio.IsChecked = !firstRun && !basicWorkMode && planWorkMode;
        AdvancedWorkModeRadio.IsChecked = !firstRun && !basicWorkMode && !planWorkMode;
        // Loop is offered only to the accounts in LoopAutoRecommandUsers.AllowedUsers, so others get the description without it.
        if (!global::TerminalNotes.Services.LoopAutoRecommandUsers.Allowed)
        {
            AdvancedWorkModeDescription.Text = "여러 태스크·터미널을 동시에 실행";
            System.Windows.Automation.AutomationProperties.SetHelpText(AdvancedWorkModeRadio, "3단계 · 여러 터미널 동시 실행");
        }
        // The work folder defaults to C:\opencode (first run too); ‘찾아보기…’ picks another one.
        DefaultWorkspaceInput.Text = firstRun || string.IsNullOrWhiteSpace(defaultWorkspace)
            ? FallbackWorkspace
            : defaultWorkspace.Trim();
        WorkspaceFirstRunHint.Visibility = firstRun ? Visibility.Visible : Visibility.Collapsed;
        GlobalAutoModeToggle.IsChecked = firstRun || useAutoMode;
        BuildTerminalAgentPickers(terminalAgents);
        if (firstRun)
        {
            // Only the work folder: no Auto Mode switch, tools, instructions or per-terminal agents, in a narrow window.
            WorkEnvironmentHeading.Text = "작업 폴더";
            WorkspaceCaption.Visibility = Visibility.Collapsed;
            AutoModeSection.Visibility = Visibility.Collapsed;
            ToolsColumn.Visibility = Visibility.Collapsed;
            TerminalAgentSection.Visibility = Visibility.Collapsed;
            Width = 560;
            MinHeight = 0;
        }
        DefaultWorkspaceInput.TextChanged += (_, _) => WorkspaceErrorText.Text = string.Empty;
        Closed += (_, _) => _autoInstallCancellation.Cancel();
        SourceInitialized += (_, _) => UpdateWorkAreaBounds(positionWindow: false);
        Loaded += (_, _) => UpdateWorkAreaBounds(positionWindow: true);
        LocationChanged += (_, _) => UpdateWorkAreaBounds(positionWindow: false);
        DpiChanged += (_, _) => UpdateWorkAreaBounds(positionWindow: true);
    }

    private void UpdateWorkAreaBounds(bool positionWindow)
    {
        if (_updatingWorkArea) return;
        _updatingWorkArea = true;
        try
        {
            Window monitorWindow = !IsLoaded && Owner is not null ? Owner : this;
            var handle = new System.Windows.Interop.WindowInteropHelper(monitorWindow).Handle;
            if (handle == IntPtr.Zero) return;
            var workArea = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            var transform = System.Windows.Interop.HwndSource.FromHwnd(handle)?.CompositionTarget?.TransformFromDevice
                ?? System.Windows.Media.Matrix.Identity;
            Rect available = Rect.Transform(new Rect(workArea.X, workArea.Y, workArea.Width, workArea.Height), transform);
            double width = Math.Max(1, available.Width - 24);
            double height = Math.Max(1, available.Height - 24);
            MinWidth = Math.Min(480, width);
            // The first-run window holds only the folder row, so its height simply follows the content.
            MinHeight = Math.Min(_firstRun ? 0 : 360, height);
            MaxWidth = width;
            MaxHeight = height;
            if (WindowState != WindowState.Normal) return;
            Width = Math.Min(Width, width);
            // The height follows the content (SizeToContent) up to MaxHeight; setting Height would switch that off.
            if (SizeToContent is not (SizeToContent.Height or SizeToContent.WidthAndHeight)) Height = Math.Min(Height, height);
            if (positionWindow)
            {
                double shownHeight = double.IsNaN(Height) ? ActualHeight : Height;
                Left = Math.Clamp(Left, available.Left + 12, Math.Max(available.Left + 12, available.Right - Width - 12));
                Top = Math.Clamp(Top, available.Top + 12, Math.Max(available.Top + 12, available.Bottom - shownHeight - 12));
            }
        }
        finally { _updatingWorkArea = false; }
    }

    public string DefaultWorkspace { get; private set; } = FallbackWorkspace;

    public bool UseAutoMode { get; private set; }

    public bool UseBasicWorkMode => BasicWorkModeRadio.IsChecked == true;

    private readonly List<System.Windows.Controls.ComboBox> _terminalAgentPickers = [];

    public global::TerminalNotes.Services.TerminalTool[] TerminalAgents => _terminalAgentPickers
        .Select(picker => picker.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: global::TerminalNotes.Services.TerminalTool tool } ? tool : global::TerminalNotes.Services.TerminalTool.OpenCode).ToArray();

    // Terminal 1-8 agent pickers; hidden (all OpenCode) unless the build enables AGENT_SELECTION in .env.
    private void BuildTerminalAgentPickers(global::TerminalNotes.Services.TerminalTool[]? agents)
    {
        bool enabled = global::TerminalNotes.Services.AppFeatures.AgentSelection;
        TerminalAgentSection.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        for (int slot = 1; slot <= 8; slot++)
        {
            global::TerminalNotes.Services.TerminalTool current = enabled && agents is { Length: >= 8 } ? agents[slot - 1] : global::TerminalNotes.Services.TerminalTool.OpenCode;
            var picker = new System.Windows.Controls.ComboBox { Margin = new Thickness(0, 0, 8, 8), MinWidth = 0 };
            foreach (var tool in new[] { global::TerminalNotes.Services.TerminalTool.OpenCode, global::TerminalNotes.Services.TerminalTool.ClaudeCode, global::TerminalNotes.Services.TerminalTool.Codex })
            {
                var item = new System.Windows.Controls.ComboBoxItem { Content = global::TerminalNotes.Services.AgentCli.DisplayName(tool), Tag = tool };
                picker.Items.Add(item);
                if (tool == current) picker.SelectedItem = item;
            }
            picker.SelectionChanged += (_, _) => UpdateAutoModeDescription();
            System.Windows.Automation.AutomationProperties.SetName(picker, $"터미널 {slot} 에이전트");
            _terminalAgentPickers.Add(picker);
            var cell = new System.Windows.Controls.StackPanel();
            cell.Children.Add(new System.Windows.Controls.TextBlock { Text = $"터미널 {slot}", FontSize = 12, Margin = new Thickness(2, 0, 0, 3),
                Foreground = (System.Windows.Media.Brush)FindResource("Muted") });
            cell.Children.Add(picker);
            TerminalAgentGrid.Children.Add(cell);
        }
        UpdateAutoModeDescription();
    }

    // Auto Mode launches: OpenCode --auto, Claude Code bypass-permissions mode, Codex YOLO mode.
    private void UpdateAutoModeDescription()
    {
        if (AutoModeDescription is null) return;
        var used = TerminalAgents.Distinct().ToArray();
        var parts = new List<string>();
        if (used.Contains(global::TerminalNotes.Services.TerminalTool.OpenCode)) parts.Add("OpenCode --auto");
        if (used.Contains(global::TerminalNotes.Services.TerminalTool.ClaudeCode)) parts.Add("Claude Code 바이패스");
        if (used.Contains(global::TerminalNotes.Services.TerminalTool.Codex)) parts.Add("Codex YOLO(--yolo)");
        AutoModeDescription.Text = string.Join(" · ", parts) + " · 파일 수정과 명령 실행을 묻지 않습니다.";
    }

    public bool UsePlanWorkMode => PlanWorkModeRadio.IsChecked == true;

    private async void OpenAutoInstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_autoInstallBusy) return;
        _autoInstallBusy = true;
        OpenAutoInstallButton.IsEnabled = false;
        AutoInstallStatusText.Visibility = Visibility.Visible;
        AutoInstallStatusText.Text = "OpenCode 설치 명령 실행 중…";
        try
        {
            AutoInstallResult result = await AutoInstall.RunAsync(_autoInstallCancellation.Token);
#if TN_WPF
            if (result.Status == AutoInstallStatus.Failed)
                global::TerminalNotes.Services.ApplicationDiagnostics.Record("installation_failed", "settings", "installer_failed");
#endif
            _autoInstallCancellation.Token.ThrowIfCancellationRequested();
            if (IsLoaded) AutoInstallStatusText.Text = "OpenCode 설치 확인 중…";
            AutoInstallDetectionResult detection = await AutoInstall.CheckAsync(_autoInstallCancellation.Token);
            _autoInstallCancellation.Token.ThrowIfCancellationRequested();
            if (IsLoaded)
                AutoInstallStatusText.Text = detection.IsInstalled
                    ? (result.Status == AutoInstallStatus.Failed ? result.Message + " · " : string.Empty) +
                        "OpenCode 실행 확인 완료 · " + detection.Message
                    : result.Message + " · " + detection.Message;
        }
        catch (OperationCanceledException) when (_autoInstallCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
#if TN_WPF
            global::TerminalNotes.Services.ApplicationDiagnostics.RecordException("installation_failed", exception);
#endif
            if (IsLoaded) AutoInstallStatusText.Text = "설치 명령 실행 또는 설치 확인 실패 · " + exception.Message;
        }
        finally
        {
            _autoInstallBusy = false;
            if (IsLoaded) OpenAutoInstallButton.IsEnabled = true;
        }
    }

    private void AnalyzeLogsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DiagnosticLogAnalysisDialog(_diagnosticSettingsPath) { Owner = this };
        dialog.ShowDialog();
    }

    private void EditGlobalInstructionsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new GlobalAgentInstructionsDialog { Owner = this };
            dialog.ShowDialog();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            WorkspaceErrorText.Text = "전역 지침 파일을 열지 못했습니다 · " + exception.Message;
        }
    }

    private void EditPlanMakerInstructionsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = GlobalAgentInstructionsDialog.CreatePlanMaker();
            dialog.Owner = this;
            dialog.ShowDialog();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            WorkspaceErrorText.Text = "플랜메이커 지침 파일을 열지 못했습니다 · " + exception.Message;
        }
    }

    private void EditOpenCodeConfigButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = GlobalAgentInstructionsDialog.CreateOpenCodeConfig();
            dialog.Owner = this;
            dialog.ShowDialog();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            WorkspaceErrorText.Text = "OpenCode 설정 파일을 열지 못했습니다 · " + exception.Message;
        }
    }

    private void WorkspaceBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        string? initialDirectory = FindClosestExistingDirectory(DefaultWorkspaceInput.Text);
        var dialog = new OpenFolderDialog
        {
            Title = "기본 작업 폴더 선택",
            Multiselect = false
        };
        if (initialDirectory is not null)
            dialog.InitialDirectory = initialDirectory;

        if (dialog.ShowDialog(this) == true)
        {
            DefaultWorkspaceInput.Text = dialog.FolderName;
            WorkspaceErrorText.Text = string.Empty;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_firstRun && string.IsNullOrWhiteSpace(DefaultWorkspaceInput.Text))
        {
            WorkspaceErrorText.Text = "작업 폴더를 선택하세요. 직접 입력하거나 ‘찾아보기…’를 누르세요.";
            DefaultWorkspaceInput.Focus();
            return;
        }
        string requestedWorkspace = string.IsNullOrWhiteSpace(DefaultWorkspaceInput.Text)
            ? FallbackWorkspace
            : DefaultWorkspaceInput.Text.Trim();
        string normalizedWorkspace;
        try
        {
            normalizedWorkspace = Path.GetFullPath(requestedWorkspace);
            Directory.CreateDirectory(normalizedWorkspace);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or SecurityException)
        {
            WorkspaceErrorText.Text = "작업 폴더를 확인하거나 만들지 못했습니다. 경로와 권한을 확인하세요. " + exception.Message;
            return;
        }

        if (!Directory.Exists(normalizedWorkspace))
        {
            WorkspaceErrorText.Text = "작업 폴더를 만들지 못했습니다. 다른 폴더를 선택하세요.";
            return;
        }

        DefaultWorkspace = normalizedWorkspace;
        UseAutoMode = GlobalAutoModeToggle.IsChecked == true;
        DialogResult = true;
    }

    private static string? FindClosestExistingDirectory(string path)
    {
        try
        {
            string candidate = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? FallbackWorkspace : path.Trim());
            string? current = candidate;
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (Directory.Exists(current))
                    return current;

                string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
                if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    break;
                current = parent;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or SecurityException)
        {
            // Invalid text should not prevent opening the folder picker.
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(profile) ? profile : null;
    }
}
}
#endregion
#endif

#if TN_WPF || TN_SOURCE_TERMINALTITLEDIALOG_XAML_CS
#region TerminalTitleDialog.xaml.cs
namespace TerminalNotes
{
using global::System.Windows;


public partial class TerminalTitleDialog : Window
{
    public string TerminalTitle => TitleInput.Text.Trim();
    public TerminalTitleDialog(string initialTitle, bool rename = false, string? heading = null)
    {
        InitializeComponent();
        TitleInput.Text = initialTitle;
        if (rename) { Heading.Text = heading ?? "터미널 이름 바꾸기"; ConfirmButton.Content = "변경"; }
        Loaded += (_, _) => { TitleInput.Focus(); TitleInput.SelectAll(); };
    }
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TerminalTitle))
        {
            ValidationMessage.Text = "구분할 수 있는 이름을 입력해주세요.";
            TitleInput.Focus();
            return;
        }
        DialogResult = true;
    }
}
}
#endregion
#endif

#if TN_ENGINE || TN_SOURCE_TERMINALENGINE_TERMINALEMULATOR_CS
#region TerminalEngine/TerminalEmulator.cs
namespace TerminalNotes.TerminalEngine
{
using global::System.Globalization;
using global::System.Text;


/// <summary>A small, dependency-free VT screen emulator for common VT100/VT220/xterm applications.</summary>
/// <remarks>
/// VT support is intentionally partial. Callers must serialize Feed, Resize, and snapshot/selection access;
/// this type does not synchronize concurrent operations internally.
/// </remarks>
public sealed class TerminalEmulator
{
    private const int MaxControlLength = 8192;
    private const int MaxCellTextLength = 128;
    private static readonly IReadOnlyList<TerminalColor> AnsiColors = Array.AsReadOnly<TerminalColor>(
    [
        new(0, 0, 0), new(205, 0, 0), new(0, 205, 0), new(205, 205, 0),
        new(0, 0, 238), new(205, 0, 205), new(0, 205, 205), new(229, 229, 229),
        new(127, 127, 127), new(255, 0, 0), new(0, 255, 0), new(255, 255, 0),
        new(92, 92, 255), new(255, 0, 255), new(0, 255, 255), new(255, 255, 255)
    ]);
    private readonly int _scrollbackLimit;
    private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();
    private readonly List<MutableLine> _normal = [];
    private readonly List<MutableLine> _alternate = [];
    private readonly HashSet<int> _tabStops = [];
    private List<MutableLine> _active;
    private readonly StringBuilder _control = new();
    private readonly StringBuilder _graphemePending = new();
    private ParserState _parser;
    private TerminalStyle _style = TerminalStyle.Default;
    private TerminalStyle _savedStyle = TerminalStyle.Default;
    private int _columns, _rows, _x, _y, _savedX, _savedY, _topMargin, _bottomMargin;
    private int _lastCellColumn = -1, _lastCellRow = -1;
    private bool _feeding, _dirty;
    private bool _wrapPending, _originMode, _autoWrap = true, _savedWrap = true;
    private bool _joinNextRune;
    private bool _cursorVisible = true, _alternateScreen, _applicationCursorKeys, _bracketedPaste, _sgrMouse;
    private bool _g0DecSpecialGraphics, _g1DecSpecialGraphics, _useG1Charset;
    private int _mouseMode;
    private string? _title;
    private long _version;
    private TerminalSnapshot? _cachedSnapshot;
    private int _cachedSnapshotOffset;

    public event Action<string>? Response;
    public event Action? Changed;

    public TerminalEmulator(int columns = 80, int rows = 24, int scrollbackLimit = 5000)
    {
        if (scrollbackLimit < 0) throw new ArgumentOutOfRangeException(nameof(scrollbackLimit));
        _scrollbackLimit = scrollbackLimit;
        _columns = Math.Clamp(columns, 2, 500);
        _rows = Math.Clamp(rows, 2, 300);
        _topMargin = 0; _bottomMargin = _rows - 1;
        ResetTabStops();
        for (int i = 0; i < _rows; i++) { _normal.Add(NewLine()); _alternate.Add(NewLine()); }
        _active = _normal;
    }

    public void Feed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        // A combining-only Feed can update an existing grapheme without a new
        // version. Never retain its previous immutable view across Feed calls.
        _cachedSnapshot = null;
        _feeding = true;
        try { foreach (Rune rune in text.EnumerateRunes()) ProcessRune(rune); }
        finally { _feeding = false; PublishChange(); }
    }

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        _cachedSnapshot = null;
        char[] buffer = new char[Math.Max(4, Encoding.UTF8.GetMaxCharCount(bytes.Length))];
        _decoder.Convert(bytes, buffer, flush: false, out _, out int charsUsed, out _);
        _feeding = true;
        try
        {
            for (int i = 0; i < charsUsed; i++)
            {
                char c = buffer[i];
                if (char.IsHighSurrogate(c) && i + 1 < charsUsed && char.IsLowSurrogate(buffer[i + 1]))
                    ProcessRune(new Rune(c, buffer[++i]));
                else if (Rune.TryCreate(c, out Rune rune)) ProcessRune(rune);
                else ProcessRune(Rune.ReplacementChar);
            }
        }
        finally { _feeding = false; PublishChange(); }
    }

    public void Resize(int columns, int rows)
    {
        int cols = Math.Clamp(columns, 2, 500), height = Math.Clamp(rows, 2, 300);
        if (cols == _columns && height == _rows) return;
        int oldRows = _rows, oldCols = _columns;
        int oldNormalHistory = Math.Max(0, _normal.Count - oldRows);
        int resizedCursorRow = _y;
        int resizedSavedRow = _savedY;
        if (height < oldRows)
        {
            resizedCursorRow = Math.Max(0, _y - (oldRows - height));
            resizedSavedRow = Math.Max(0, _savedY - (oldRows - height));
        }
        else if (height > oldRows && !_alternateScreen)
        {
            int exposedHistory = Math.Min(oldNormalHistory, height - oldRows);
            resizedCursorRow = _y + exposedHistory;
            resizedSavedRow = _savedY + exposedHistory;
        }
        _columns = cols; _rows = height;
        _tabStops.RemoveWhere(stop => stop >= cols);
        if (cols > oldCols)
            for (int column = ((oldCols / 8) + 1) * 8; column < cols; column += 8) _tabStops.Add(column);
        ResizeBuffer(_normal, oldRows, oldCols, height, cols);
        ResizeBuffer(_alternate, oldRows, oldCols, height, cols);
        _x = Math.Clamp(_x, 0, cols - 1); _y = Math.Clamp(resizedCursorRow, 0, height - 1);
        _savedX = Math.Clamp(_savedX, 0, cols - 1); _savedY = Math.Clamp(resizedSavedRow, 0, height - 1);
        _topMargin = 0; _bottomMargin = height - 1; _wrapPending = false;
        ChangedNow();
    }

    public TerminalSnapshot GetSnapshot(int scrollbackOffset = 0)
    {
        int history = _alternateScreen ? 0 : Math.Max(0, _active.Count - _rows);
        int offset = Math.Clamp(scrollbackOffset, 0, history);
        // A device response handler can inspect a partially processed Feed.
        // Such a view must neither read nor populate the published-view cache.
        if (!_feeding && _cachedSnapshot is { } cached && cached.Version == _version &&
            _cachedSnapshotOffset == offset) return cached;
        int start = Math.Max(0, _active.Count - _rows - offset);
        var lines = new List<TerminalLine>(_rows);
        for (int i = 0; i < _rows; i++)
            lines.Add(ToImmutableLine(start + i < _active.Count ? _active[start + i] : NewLine()));
        int cursorRow = _y;
        var snapshot = new TerminalSnapshot(_columns, _rows, _x, cursorRow, _cursorVisible, _alternateScreen,
            _applicationCursorKeys, _bracketedPaste, _mouseMode, _sgrMouse, history, _title, _version, lines);
        if (!_feeding) { _cachedSnapshot = snapshot; _cachedSnapshotOffset = offset; }
        return snapshot;
    }

    /// <summary>Returns the active buffer as text in one pass, joining soft-wrapped rows and trimming row padding.</summary>
    public string GetAllText()
    {
        var rowTexts = new List<(string Text, bool Wrapped)>(_active.Count);
        int lastNonEmpty = -1;
        for (int rowIndex = 0; rowIndex < _active.Count; rowIndex++)
        {
            MutableLine line = _active[rowIndex];
            int lastCell = line.FindLastIndex(cell => cell.Text.Length > 0 && cell.Text != " ");
            var row = new StringBuilder(Math.Max(0, lastCell + 1));
            for (int column = 0; column <= lastCell; column++)
                if (line[column].Width != 0) row.Append(line[column].Text);
            string text = row.ToString();
            rowTexts.Add((text, line.Wrapped));
            if (text.Length != 0) lastNonEmpty = rowIndex;
        }

        if (lastNonEmpty < 0) return string.Empty;
        var result = new StringBuilder();
        for (int rowIndex = 0; rowIndex <= lastNonEmpty; rowIndex++)
        {
            (string text, bool wrapped) = rowTexts[rowIndex];
            result.Append(text);
            if (rowIndex < lastNonEmpty && !wrapped) result.AppendLine();
        }
        return result.ToString();
    }

    public string GetSelectionText(int startColumn, int startRow, int endColumn, int endRow, int scrollbackOffset = 0)
    {
        TerminalSnapshot snapshot = GetSnapshot(scrollbackOffset);
        int startIndex = Math.Clamp(startRow, 0, snapshot.Rows - 1) * snapshot.Columns + Math.Clamp(startColumn, 0, snapshot.Columns - 1);
        int endIndex = Math.Clamp(endRow, 0, snapshot.Rows - 1) * snapshot.Columns + Math.Clamp(endColumn, 0, snapshot.Columns - 1);
        if (startIndex > endIndex) (startIndex, endIndex) = (endIndex, startIndex);
        var result = new StringBuilder();
        int firstRow = startIndex / snapshot.Columns, firstColumn = startIndex % snapshot.Columns;
        if (snapshot.Lines[firstRow].Cells[firstColumn].Width == 0 && firstColumn > 0) startIndex--;
        int lastRow = endIndex / snapshot.Columns, lastColumn = endIndex % snapshot.Columns;
        if (snapshot.Lines[lastRow].Cells[lastColumn].Width == 0 && lastColumn > 0) endIndex--;
        for (int flat = startIndex; flat <= endIndex;)
        {
            int row = flat / snapshot.Columns, col = flat % snapshot.Columns;
            TerminalCell cell = snapshot.Lines[row].Cells[col];
            if (cell.Width != 0) result.Append(cell.Text);
            flat += Math.Max(cell.Width, 1);
            if (flat <= endIndex && flat / snapshot.Columns > row && !snapshot.Lines[row].Wrapped)
                result.AppendLine();
        }
        return result.ToString();
    }

    public void Clear()
    {
        _normal.Clear(); _alternate.Clear();
        for (int i = 0; i < _rows; i++) { _normal.Add(NewLine()); _alternate.Add(NewLine()); }
        _active = _alternateScreen ? _alternate : _normal;
        _x = _y = 0; _wrapPending = false; _topMargin = 0; _bottomMargin = _rows - 1;
        _joinNextRune = false; _graphemePending.Clear(); _lastCellColumn = _lastCellRow = -1;
        ChangedNow();
    }

    private void ProcessRune(Rune rune)
    {
        if (_parser is ParserState.DiscardCsi or ParserState.DiscardOsc or ParserState.DiscardOscEscape or ParserState.DiscardString or ParserState.DiscardStringEscape)
        {
            ProcessDiscardedControl(rune);
            return;
        }
        if (_parser == ParserState.Osc || _parser == ParserState.OscEscape || _parser == ParserState.String || _parser == ParserState.StringEscape)
        {
            ProcessStringControl(rune);
            return;
        }
        if (_parser == ParserState.Csi)
        {
            if (rune.Value is >= 0x40 and <= 0x7e) { ExecuteCsi((char)rune.Value, _control.ToString()); _control.Clear(); _parser = ParserState.Ground; ChangedNow(); }
            else if (rune.Value >= 0x20 && rune.Value <= 0x3f) AppendControl(rune);
            else if (rune.Value == 0x1b) { _control.Clear(); _parser = ParserState.Escape; }
            return;
        }
        if (_parser is ParserState.CharsetDesignationG0 or ParserState.CharsetDesignationG1)
        {
            bool decSpecial = rune.Value == '0';
            if (_parser == ParserState.CharsetDesignationG0) _g0DecSpecialGraphics = decSpecial;
            else _g1DecSpecialGraphics = decSpecial;
            _parser = ParserState.Ground;
            ChangedNow();
            return;
        }
        if (_parser == ParserState.Escape)
        {
            _parser = ParserState.Ground;
            switch (rune.Value)
            {
                case '[': _control.Clear(); _parser = ParserState.Csi; break;
                case ']': _control.Clear(); _parser = ParserState.Osc; break;
                case 'P': case '^': case '_': _control.Clear(); _parser = ParserState.String; break;
                case '(': _parser = ParserState.CharsetDesignationG0; break;
                case ')': _parser = ParserState.CharsetDesignationG1; break;
                case 'H': _tabStops.Add(_x); break;
                case '7': SaveCursor(); break;
                case '8': RestoreCursor(); break;
                case 'D': LineFeed(false); break;
                case 'E': _x = 0; LineFeed(false); break;
                case 'M': ReverseIndex(); break;
                case 'c': Reset(); break;
                case '=': break;
                case '>': break;
            }
            ChangedNow(); return;
        }
        if (rune.Value == 0x1b) { FlushPendingGrapheme(); _parser = ParserState.Escape; return; }
        if (rune.Value < 0x20 || rune.Value == 0x7f) { HandleControl((char)rune.Value); return; }
        PutRune(rune);
    }

    private void HandleControl(char c)
    {
        FlushPendingGrapheme();
        switch (c)
        {
            case '\a': break;
            case '\b': _x = Math.Max(0, _x - 1); _wrapPending = false; break;
            case '\t': _x = NextTabStop(_x); _wrapPending = false; break;
            case '\n': case '\v': case '\f': LineFeed(false); break;
            case '\r': _x = 0; _wrapPending = false; break;
            case '\x0e': _useG1Charset = true; break;
            case '\x0f': _useG1Charset = false; break;
            case '\x05': Response?.Invoke("TerminalNotes terminal"); break;
        }
        ChangedNow();
    }

    private void ProcessStringControl(Rune rune)
    {
        if (_parser is ParserState.OscEscape or ParserState.StringEscape)
        {
            if (rune.Value == '\\') FinishStringControl();
            else { AppendControl(new Rune(0x1b)); AppendControl(rune); _parser = _parser == ParserState.OscEscape ? ParserState.Osc : ParserState.String; }
            return;
        }
        if (_parser == ParserState.Osc && rune.Value == 0x07) { FinishStringControl(); return; }
        if (rune.Value == 0x1b) { _parser = _parser == ParserState.Osc ? ParserState.OscEscape : ParserState.StringEscape; return; }
        AppendControl(rune);
    }

    private void ProcessDiscardedControl(Rune rune)
    {
        switch (_parser)
        {
            case ParserState.DiscardCsi:
                if (rune.Value is >= 0x40 and <= 0x7e) _parser = ParserState.Ground;
                else if (rune.Value == 0x1b) _parser = ParserState.Escape;
                break;
            case ParserState.DiscardOsc:
                if (rune.Value == 0x07) _parser = ParserState.Ground;
                else if (rune.Value == 0x1b) _parser = ParserState.DiscardOscEscape;
                break;
            case ParserState.DiscardOscEscape:
                _parser = rune.Value == '\\' ? ParserState.Ground : rune.Value == 0x1b ? ParserState.DiscardOscEscape : ParserState.DiscardOsc;
                break;
            case ParserState.DiscardString:
                if (rune.Value == 0x1b) _parser = ParserState.DiscardStringEscape;
                break;
            case ParserState.DiscardStringEscape:
                _parser = rune.Value == '\\' ? ParserState.Ground : rune.Value == 0x1b ? ParserState.DiscardStringEscape : ParserState.DiscardString;
                break;
        }
    }

    private void FinishStringControl()
    {
        if (_parser is ParserState.Osc or ParserState.OscEscape)
        {
            string payload = _control.ToString();
            int separator = payload.IndexOf(';');
            if (separator > 0 && int.TryParse(payload[..separator], out int command) && command is 0 or 2)
            {
                _title = payload[(separator + 1)..];
                ChangedNow();
            }
        }
        _control.Clear(); _parser = ParserState.Ground;
    }

    private void AppendControl(Rune rune)
    {
        if (_control.Length < MaxControlLength) { _control.Append(rune.ToString()); return; }
        _control.Clear();
        _parser = _parser switch
        {
            ParserState.Csi => ParserState.DiscardCsi,
            ParserState.Osc or ParserState.OscEscape => ParserState.DiscardOsc,
            _ => ParserState.DiscardString
        };
    }

    private void PutRune(Rune rune)
    {
        if (rune.Value is >= 0x5f and <= 0x7e && (_useG1Charset ? _g1DecSpecialGraphics : _g0DecSpecialGraphics))
            rune = DecSpecialGraphics(rune);
        int width = RuneWidth(rune);
        if (width == 0)
        {
            int col = _lastCellColumn;
            int row = _lastCellRow;
            if (row >= 0 && row < _rows && col >= 0 && col < _columns)
                AppendCellText(_active[ScreenIndex(row)][col], rune);
            if (rune.Value == 0x200d) _joinNextRune = true;
            return;
        }
        if (_joinNextRune && _lastCellRow >= 0)
        {
            AppendCellText(_active[ScreenIndex(_lastCellRow)][_lastCellColumn], rune);
            _joinNextRune = false;
            return;
        }
        FlushPendingGrapheme();
        if (_wrapPending)
        {
            if (_autoWrap) { _active[ScreenIndex(_y)].Wrapped = true; _x = 0; LineFeed(true); }
            _wrapPending = false;
        }
        if (width == 2 && _x == _columns - 1)
        {
            if (_autoWrap) { _active[ScreenIndex(_y)].Wrapped = true; _x = 0; LineFeed(true); }
            else width = 1;
        }
        var line = _active[ScreenIndex(_y)];
        _lastCellRow = _y; _lastCellColumn = _x;
        ClearCellAt(line, _x);
        line[_x] = new MutableCell(rune.ToString(), width, _style);
        if (width == 2) line[_x + 1] = new MutableCell("", 0, _style);
        if (_x + width >= _columns) { _x = _columns - 1; _wrapPending = true; }
        else _x += width;
        ChangedNow();
    }

    private void FlushPendingGrapheme()
    {
        _joinNextRune = false;
        _lastCellColumn = _lastCellRow = -1;
    }
    private static void AppendCellText(MutableCell cell, Rune rune)
    {
        string text = rune.ToString();
        if (cell.Text.Length + text.Length <= MaxCellTextLength) cell.Text += text;
    }

    private void ExecuteCsi(char final, string raw)
    {
        char prefix = raw.Length > 0 && raw[0] is '?' or '>' or '!' or '<' or '=' or ':' ? raw[0] : '\0';
        if (prefix == '?' && final is not ('h' or 'l') || prefix == '>' && final != 'c' ||
            prefix == '!' && (final != 'p' || raw != "!") || prefix is '<' or '=' or ':')
            return;
        bool privateMode = raw.StartsWith('?');
        bool bang = raw.StartsWith('!');
        string cleaned = raw.TrimStart('?', '>', '!', '<', '=', ':');
        int[] p = cleaned.Length == 0 ? [] : cleaned.Split(';').Select(ParseNumber).ToArray();
        int n = P(p, 0, 1, zeroIsOne: true);
        switch (final)
        {
            case 'A': _y = Math.Max(_originMode ? _topMargin : 0, _y - n); break;
            case 'B': _y = Math.Min(_originMode ? _bottomMargin : _rows - 1, _y + n); break;
            case 'C': _x = Math.Min(_columns - 1, _x + n); break;
            case 'D': _x = Math.Max(0, _x - n); break;
            case 'E': _y = Math.Min(_rows - 1, _y + n); _x = 0; break;
            case 'F': _y = Math.Max(0, _y - n); _x = 0; break;
            case 'G': case '`': _x = Math.Clamp(P(p, 0, 1) - 1, 0, _columns - 1); break;
            case 'd': _y = Math.Clamp(P(p, 0, 1) - 1 + (_originMode ? _topMargin : 0), _originMode ? _topMargin : 0, _originMode ? _bottomMargin : _rows - 1); break;
            case 'H': case 'f': _y = Math.Clamp(P(p, 0, 1) - 1 + (_originMode ? _topMargin : 0), _originMode ? _topMargin : 0, _originMode ? _bottomMargin : _rows - 1); _x = Math.Clamp(P(p, 1, 1) - 1, 0, _columns - 1); break;
            case 'J': EraseDisplay(P(p, 0, 0)); break;
            case 'K': EraseLine(P(p, 0, 0)); break;
            case 'X': EraseChars(n); break;
            case '@': InsertChars(n); break;
            case 'P': DeleteChars(n); break;
            case 'L': InsertLines(n); break;
            case 'M': DeleteLines(n); break;
            case 'S': ScrollUp(n, false); break;
            case 'T': ScrollDown(n); break;
            case 'I': for (int i = 0; i < n; i++) _x = NextTabStop(_x); break;
            case 'Z': for (int i = 0; i < n; i++) _x = PreviousTabStop(_x); break;
            case 'm': ApplySgr(cleaned); break;
            case 'r': _topMargin = Math.Clamp(P(p, 0, 1) - 1, 0, _rows - 1); _bottomMargin = Math.Clamp(P(p, 1, _rows) - 1, _topMargin, _rows - 1); _x = 0; _y = _originMode ? _topMargin : 0; break;
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            case 'h': case 'l': SetModes(p, privateMode, final == 'h'); break;
            case 'g':
                if (P(p, 0, 0) == 3) _tabStops.Clear();
                else if (P(p, 0, 0) == 0) _tabStops.Remove(_x);
                break;
            case 'n': if (P(p, 0, 0) == 5) Response?.Invoke("\x1b[0n"); else if (P(p, 0, 0) == 6) Response?.Invoke($"\x1b[{_y + 1};{_x + 1}R"); break;
            case 'c':
                if (prefix == '>' && (p.Length == 0 || p.Length == 1 && p[0] == 0)) Response?.Invoke("\x1b[>0;1;0c");
                else if (prefix == '\0' && (p.Length == 0 || p.Length == 1 && p[0] == 0)) Response?.Invoke("\x1b[?1;2c");
                break;
            case 'q': if (raw.EndsWith('"')) { } break;
            case 'p': if (bang) Reset(); break;
        }
        _x = Math.Clamp(_x, 0, _columns - 1); _y = Math.Clamp(_y, 0, _rows - 1); _wrapPending = false;
    }

    private void SetModes(int[] values, bool dec, bool enabled)
    {
        foreach (int mode in values)
        {
            if (!dec) { if (mode == 4) { } continue; }
            switch (mode)
            {
                case 6: _originMode = enabled; _x = 0; _y = enabled ? _topMargin : 0; break;
                case 7: _autoWrap = enabled; break;
                case 25: _cursorVisible = enabled; break;
                case 1: _applicationCursorKeys = enabled; break;
                case 47: case 1047: case 1049: SetAlternate(enabled, mode == 1049, enabled && mode is 1047 or 1049); break;
                case 1000: case 1002: case 1003: _mouseMode = enabled ? mode : (_mouseMode == mode ? 0 : _mouseMode); break;
                case 1006: _sgrMouse = enabled; break;
                case 2004: _bracketedPaste = enabled; break;
            }
        }
    }

    private void SetAlternate(bool enabled, bool saveRestore, bool clearOnEnter)
    {
        if (enabled == _alternateScreen) return;
        if (enabled)
        {
            if (saveRestore) SaveCursor();
            _alternateScreen = true; _active = _alternate; _x = _y = 0;
            if (clearOnEnter || _active.Count != _rows) { _active.Clear(); for (int i = 0; i < _rows; i++) _active.Add(NewLine()); }
        }
        else
        {
            _alternateScreen = false; _active = _normal;
            if (saveRestore) RestoreCursor();
        }
        _wrapPending = false;
    }

    private void ApplySgr(string parameters)
    {
        if (!parameters.Contains(':'))
        {
            ApplySgr(parameters.Length == 0 ? [] : parameters.Split(';').Select(ParseNumber).ToArray());
            return;
        }
        foreach (string item in parameters.Split(';'))
        {
            string[] fields = item.Split(':');
            int[] values = fields.Select(ParseNumber).ToArray();
            if (values.Length >= 3 && values[0] is 38 or 48 && values[1] == 2)
            {
                int[] rgb = fields.Skip(2).Where(field => field.Length != 0).Select(ParseNumber).TakeLast(3).ToArray();
                if (rgb.Length == 3)
                {
                    TerminalColor color = new((byte)Math.Clamp(rgb[0], 0, 255), (byte)Math.Clamp(rgb[1], 0, 255), (byte)Math.Clamp(rgb[2], 0, 255));
                    _style = values[0] == 38 ? _style with { Foreground = color } : _style with { Background = color };
                }
            }
            else if (values.Length >= 3 && values[0] is 38 or 48 && values[1] == 5)
            {
                TerminalColor color = Palette(values[^1]);
                _style = values[0] == 38 ? _style with { Foreground = color } : _style with { Background = color };
            }
            else ApplySgr(values);
        }
    }

    private void ApplySgr(int[] p)
    {
        if (p.Length == 0) p = [0];
        for (int i = 0; i < p.Length; i++)
        {
            int value = p[i];
            switch (value)
            {
                case 0: _style = TerminalStyle.Default; break;
                case 1: _style = _style with { Bold = true }; break;
                case 3: _style = _style with { Italic = true }; break;
                case 4: _style = _style with { Underline = true }; break;
                case 7: _style = _style with { Inverse = true }; break;
                case 9: _style = _style with { Strike = true }; break;
                case 22: _style = _style with { Bold = false }; break;
                case 23: _style = _style with { Italic = false }; break;
                case 24: _style = _style with { Underline = false }; break;
                case 27: _style = _style with { Inverse = false }; break;
                case 29: _style = _style with { Strike = false }; break;
                case >= 30 and <= 37: _style = _style with { Foreground = AnsiColor(value - 30, false) }; break;
                case >= 40 and <= 47: _style = _style with { Background = AnsiColor(value - 40, false) }; break;
                case >= 90 and <= 97: _style = _style with { Foreground = AnsiColor(value - 90 + 8, false) }; break;
                case >= 100 and <= 107: _style = _style with { Background = AnsiColor(value - 100 + 8, false) }; break;
                case 39: _style = _style with { Foreground = TerminalColor.Default }; break;
                case 49: _style = _style with { Background = TerminalColor.Default }; break;
                case 38: case 48:
                    bool foreground = value == 38;
                    if (i + 1 < p.Length && p[i + 1] == 5 && i + 2 < p.Length)
                    {
                        TerminalColor color = Palette(p[i + 2]); i += 2;
                        _style = foreground ? _style with { Foreground = color } : _style with { Background = color };
                    }
                    else if (i + 4 < p.Length && p[i + 1] == 2)
                    {
                        TerminalColor color = new((byte)Math.Clamp(p[i + 2], 0, 255), (byte)Math.Clamp(p[i + 3], 0, 255), (byte)Math.Clamp(p[i + 4], 0, 255)); i += 4;
                        _style = foreground ? _style with { Foreground = color } : _style with { Background = color };
                    }
                    break;
            }
        }
    }

    private void EraseDisplay(int mode)
    {
        if (mode == 3)
        {
            if (!_alternateScreen) while (_active.Count > _rows) _active.RemoveAt(0);
            return;
        }
        if (mode == 2)
        {
            for (int row = 0; row < _rows; row++) EraseLineAt(ScreenIndex(row), 0, _columns - 1);
            return;
        }
        int current = ScreenIndex(_y);
        if (mode == 0)
        {
            EraseLineAt(current, _x, _columns - 1);
            for (int i = current + 1; i < _active.Count; i++) EraseLineAt(i, 0, _columns - 1);
        }
        else if (mode == 1)
        {
            for (int i = ScreenIndex(0); i < current; i++) EraseLineAt(i, 0, _columns - 1);
            EraseLineAt(current, 0, _x);
        }
    }

    private void EraseLine(int mode) => EraseLineAt(ScreenIndex(_y), mode == 0 ? _x : 0, mode == 1 ? _x : _columns - 1);
    private void EraseChars(int count) => EraseLineAt(ScreenIndex(_y), _x, Math.Min(_columns - 1, _x + count - 1));
    private void EraseLineAt(int index, int from, int to)
    {
        if (index < 0 || index >= _active.Count) return;
        MutableLine line = _active[index];
        for (int col = Math.Max(0, from); col <= Math.Min(to, _columns - 1); col++)
        {
            ClearCellAt(line, col);
            line[col] = new MutableCell(" ", 1, _style with { Inverse = false });
        }
        line.Wrapped = false;
    }

    private void InsertChars(int count)
    {
        MutableLine line = _active[ScreenIndex(_y)];
        for (int i = _columns - 1; i >= _x + count; i--) line[i] = line[i - count];
        for (int i = _x; i < Math.Min(_columns, _x + count); i++) line[i] = new MutableCell(" ", 1, _style);
        SanitizeWideCells(line);
    }
    private void DeleteChars(int count)
    {
        MutableLine line = _active[ScreenIndex(_y)];
        for (int i = _x; i < _columns; i++) line[i] = i + count < _columns ? line[i + count] : new MutableCell(" ", 1, _style);
        SanitizeWideCells(line);
    }
    private void InsertLines(int count)
    {
        if (_y < _topMargin || _y > _bottomMargin) return;
        int baseIndex = _alternateScreen ? 0 : _active.Count - _rows;
        count = Math.Min(count, _bottomMargin - _y + 1);
        for (int i = 0; i < count; i++) { _active.Insert(baseIndex + _y, NewLine()); _active.RemoveAt(baseIndex + _bottomMargin + 1); }
    }
    private void DeleteLines(int count)
    {
        if (_y < _topMargin || _y > _bottomMargin) return;
        int baseIndex = _alternateScreen ? 0 : _active.Count - _rows;
        count = Math.Min(count, _bottomMargin - _y + 1);
        for (int i = 0; i < count; i++) { _active.RemoveAt(baseIndex + _y); _active.Insert(baseIndex + _bottomMargin, NewLine()); }
    }
    private void ScrollUp(int count, bool saveHistory)
    {
        count = Math.Min(count, _bottomMargin - _topMargin + 1);
        for (int i = 0; i < count; i++)
        {
            int top = ScreenIndex(_topMargin), bottom = ScreenIndex(_bottomMargin);
            MutableLine removed = _active[top];
            _active.RemoveAt(top); _active.Insert(bottom, NewLine());
            if (saveHistory && !_alternateScreen && _topMargin == 0 && _bottomMargin == _rows - 1)
            {
                _normal.Insert(Math.Max(0, _normal.Count - _rows), removed);
                TrimHistory();
            }
        }
    }
    private void ScrollDown(int count)
    {
        count = Math.Min(count, _bottomMargin - _topMargin + 1);
        for (int i = 0; i < count; i++) { _active.RemoveAt(ScreenIndex(_bottomMargin)); _active.Insert(ScreenIndex(_topMargin), NewLine()); }
    }

    private void LineFeed(bool carriageReturn)
    {
        if (carriageReturn) _x = 0;
        if (_y == _bottomMargin) ScrollUp(1, saveHistory: true);
        else if (_y < _rows - 1) _y++;
    }
    private void ReverseIndex()
    {
        if (_y == _topMargin) ScrollDown(1);
        else _y = Math.Max(0, _y - 1);
    }
    private int ScreenIndex(int row) => _alternateScreen ? Math.Clamp(row, 0, _active.Count - 1) : Math.Clamp(_active.Count - _rows + row, 0, _active.Count - 1);

    private void SaveCursor() { _savedX = _x; _savedY = _y; _savedStyle = _style; _savedWrap = _autoWrap; }
    private void RestoreCursor() { _x = Math.Clamp(_savedX, 0, _columns - 1); _y = Math.Clamp(_savedY, 0, _rows - 1); _style = _savedStyle; _autoWrap = _savedWrap; }
    private void Reset()
    {
        _style = TerminalStyle.Default; _originMode = false; _autoWrap = true; _cursorVisible = true;
        _applicationCursorKeys = false; _bracketedPaste = false; _mouseMode = 0; _sgrMouse = false;
        _g0DecSpecialGraphics = _g1DecSpecialGraphics = _useG1Charset = false;
        ResetTabStops();
        _alternateScreen = false; _active = _normal; _title = null; Clear();
    }

    private void ResetTabStops()
    {
        _tabStops.Clear();
        for (int column = 8; column < _columns; column += 8) _tabStops.Add(column);
    }

    private int NextTabStop(int column)
    {
        int next = _columns - 1;
        foreach (int stop in _tabStops)
            if (stop > column && stop < next) next = stop;
        return next;
    }

    private int PreviousTabStop(int column)
    {
        int previous = 0;
        foreach (int stop in _tabStops)
            if (stop < column && stop > previous) previous = stop;
        return previous;
    }

    private static Rune DecSpecialGraphics(Rune rune) => rune.Value switch
    {
        0x5f => new Rune(' '), 0x60 => new Rune('◆'), 0x61 => new Rune('▒'),
        0x62 => new Rune('␉'), 0x63 => new Rune('␌'), 0x64 => new Rune('␍'),
        0x65 => new Rune('␊'), 0x66 => new Rune('°'), 0x67 => new Rune('±'),
        0x68 => new Rune('␤'), 0x69 => new Rune('␋'), 0x6a => new Rune('┘'),
        0x6b => new Rune('┐'), 0x6c => new Rune('┌'), 0x6d => new Rune('└'),
        0x6e => new Rune('┼'), 0x6f => new Rune('⎺'), 0x70 => new Rune('⎻'),
        0x71 => new Rune('─'), 0x72 => new Rune('⎼'), 0x73 => new Rune('⎽'),
        0x74 => new Rune('├'), 0x75 => new Rune('┤'), 0x76 => new Rune('┴'),
        0x77 => new Rune('┬'), 0x78 => new Rune('│'), 0x79 => new Rune('≤'),
        0x7a => new Rune('≥'), 0x7b => new Rune('π'), 0x7c => new Rune('≠'),
        0x7d => new Rune('£'), 0x7e => new Rune('·'), _ => rune
    };

    private void ResizeBuffer(List<MutableLine> buffer, int oldRows, int oldCols, int rows, int cols)
    {
        int screenStart = Math.Max(0, buffer.Count - oldRows);
        if (cols < oldCols)
        {
            for (int i = 0; i < buffer.Count; i++)
            {
                MutableLine line = buffer[i];
                if (cols < line.Count && line[cols].Width == 0 && cols > 0) line[cols - 1] = new MutableCell(" ", 1, _style);
                if (line.Count > cols) line.RemoveRange(cols, line.Count - cols);
                Pad(line, cols);
            }
        }
        else for (int i = 0; i < buffer.Count; i++) Pad(buffer[i], cols);
        if (rows < oldRows)
        {
            int remove = Math.Min(oldRows - rows, Math.Max(0, buffer.Count - rows));
            if (buffer == _alternate) remove = Math.Min(remove, buffer.Count - rows);
            if (remove > 0) buffer.RemoveRange(screenStart, remove);
        }
        else if (rows > oldRows)
        {
            int add = rows - oldRows;
            if (buffer == _normal)
            {
                int history = Math.Max(0, buffer.Count - oldRows);
                if (history >= add) { /* expose existing scrollback rows as screen */ }
                else for (int i = history; i < add; i++) buffer.Add(NewLine());
            }
            else for (int i = 0; i < add; i++) buffer.Add(NewLine());
        }
        if (buffer.Count < rows) for (int i = buffer.Count; i < rows; i++) buffer.Add(NewLine());
        if (buffer == _alternate && buffer.Count > rows) buffer.RemoveRange(0, buffer.Count - rows);
    }

    private void TrimHistory()
    {
        while (_normal.Count > _rows + _scrollbackLimit) _normal.RemoveAt(0);
    }
    private MutableLine NewLine() { var line = new MutableLine(); for (int i = 0; i < _columns; i++) line.Add(new MutableCell(" ", 1, _style with { Inverse = false })); return line; }
    // A row's previous immutable copy while its cells still hold the same values: unchanged rows of a mostly static screen
    // (OpenCode's) cost no copies, and the terminal surface reuses their drawing.
    private static TerminalLine ToImmutableLine(MutableLine line)
    {
        if (line.Published is { } published && published.Wrapped == line.Wrapped && published.Cells.Count == line.Count)
        {
            bool same = true;
            for (int index = 0; same && index < line.Count; index++)
            {
                TerminalCell copy = published.Cells[index];
                MutableCell cell = line[index];
                same = copy.Width == cell.Width && (ReferenceEquals(copy.Text, cell.Text) || string.Equals(copy.Text, cell.Text, StringComparison.Ordinal)) &&
                    copy.Style.Equals(cell.Style);
            }
            if (same) return published;
        }
        return line.Published = new(line.Select(c => new TerminalCell(c.Text, c.Width, c.Style)), line.Wrapped);
    }
    private static void Pad(List<MutableCell> line, int columns) { while (line.Count < columns) line.Add(new MutableCell(" ", 1, TerminalStyle.Default)); }
    private static void SanitizeWideCells(List<MutableCell> line) { for (int i = 0; i < line.Count; i++) if (line[i].Width == 2 && (i + 1 >= line.Count || line[i + 1].Width != 0)) line[i] = new MutableCell(" ", 1, line[i].Style); else if (line[i].Width == 0 && (i == 0 || line[i - 1].Width != 2)) line[i] = new MutableCell(" ", 1, line[i].Style); }
    private static void ClearCellAt(List<MutableCell> line, int x) { if (line[x].Width == 0 && x > 0) line[x - 1] = new MutableCell(" ", 1, line[x - 1].Style); if (line[x].Width == 2 && x + 1 < line.Count) line[x + 1] = new MutableCell(" ", 1, line[x + 1].Style); }
    private static int ParseNumber(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? Math.Clamp(result, 0, 100000) : 0;
    private static int P(int[] values, int index, int fallback, bool zeroIsOne = false) { int value = index < values.Length ? values[index] : fallback; return zeroIsOne && value == 0 ? 1 : value; }
    private void ChangedNow() { _dirty = true; if (!_feeding) PublishChange(); }
    private void PublishChange() { if (!_dirty) return; _dirty = false; _version++; Changed?.Invoke(); }

    private static TerminalColor AnsiColor(int index, bool bright)
    {
        return AnsiColors[Math.Clamp(index, 0, 15)];
    }
    private static TerminalColor Palette(int index)
    {
        index = Math.Clamp(index, 0, 255);
        if (index < 16) return AnsiColors[index];
        if (index >= 232) { byte gray = (byte)(8 + (index - 232) * 10); return new(gray, gray, gray); }
        int cube = index - 16; return new((byte)(cube / 36 == 0 ? 0 : 55 + cube / 36 % 6 * 40), (byte)(cube / 6 % 6 == 0 ? 0 : 55 + cube / 6 % 6 * 40), (byte)(cube % 6 == 0 ? 0 : 55 + cube % 6 * 40));
    }
    private static int RuneWidth(Rune rune)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        int value = rune.Value;
        return value >= 0x1100 && (value <= 0x115f || value is 0x2329 or 0x232a || value is >= 0x2e80 and <= 0xa4cf || value is >= 0xac00 and <= 0xd7a3 || value is >= 0xf900 and <= 0xfaff || value is >= 0xfe10 and <= 0xfe6f || value is >= 0xff00 and <= 0xff60 || value is >= 0xffe0 and <= 0xffe6 || value is >= 0x1f300 and <= 0x1faff || value is >= 0x20000 and <= 0x3fffd) ? 2 : 1;
    }

    private enum ParserState { Ground, Escape, CharsetDesignationG0, CharsetDesignationG1, Csi, Osc, OscEscape, String, StringEscape, DiscardCsi, DiscardOsc, DiscardOscEscape, DiscardString, DiscardStringEscape }
    private sealed class MutableCell(string text, int width, TerminalStyle style) { public string Text = text; public int Width = width; public TerminalStyle Style = style; }
    private sealed class MutableLine : List<MutableCell> { public bool Wrapped; public TerminalLine? Published; }
}
}
#endregion
#endif

#if TN_ENGINE || TN_SOURCE_TERMINALENGINE_TERMINALINPUTENCODER_CS
#region TerminalEngine/TerminalInputEncoder.cs
namespace TerminalNotes.TerminalEngine
{
using global::System.Text;


public static class TerminalInputEncoder
{
    public static byte[] EncodeKey(TerminalKey key, bool shift = false, bool alt = false,
        bool control = false, bool applicationCursorKeys = false)
    {
        string value;
        int modifier = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (control ? 4 : 0);
        bool modified = modifier != 1;
        value = key switch
        {
            TerminalKey.Enter => alt ? "\x1b\r" : "\r",
            TerminalKey.Backspace => alt ? "\x1b\x7f" : "\x7f",
            TerminalKey.Tab => shift ? (modified ? $"\x1b[1;{modifier}Z" : "\x1b[Z") : (alt ? "\x1b\t" : "\t"),
            TerminalKey.Escape => alt ? "\x1b\x1b" : "\x1b",
            TerminalKey.Up => Cursor("A", "A", applicationCursorKeys, modifier),
            TerminalKey.Down => Cursor("B", "B", applicationCursorKeys, modifier),
            TerminalKey.Right => Cursor("C", "C", applicationCursorKeys, modifier),
            TerminalKey.Left => Cursor("D", "D", applicationCursorKeys, modifier),
            TerminalKey.Home => Cursor("H", "H", applicationCursorKeys, modifier),
            TerminalKey.End => Cursor("F", "F", applicationCursorKeys, modifier),
            TerminalKey.Insert => Tilde(2, modifier),
            TerminalKey.Delete => Tilde(3, modifier),
            TerminalKey.PageUp => Tilde(5, modifier),
            TerminalKey.PageDown => Tilde(6, modifier),
            >= TerminalKey.F1 and <= TerminalKey.F4 => Function(key, modifier),
            >= TerminalKey.F5 and <= TerminalKey.F12 => Tilde(key switch
            {
                TerminalKey.F5 => 15, TerminalKey.F6 => 17, TerminalKey.F7 => 18,
                TerminalKey.F8 => 19, TerminalKey.F9 => 20, TerminalKey.F10 => 21,
                TerminalKey.F11 => 23, _ => 24
            }, modifier),
            _ => ""
        };
        return Encoding.UTF8.GetBytes(value);
    }

    public static byte[] EncodeText(string text, bool alt = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        if (!alt || bytes.Length == 0) return bytes;
        byte[] prefixed = new byte[bytes.Length + 1];
        prefixed[0] = 0x1b;
        bytes.CopyTo(prefixed, 1);
        return prefixed;
    }

    /// <summary>Encodes conventional Ctrl+character input to its C0/DEL byte.</summary>
    public static byte[] EncodeControl(char character)
    {
        char value = char.ToUpperInvariant(character);
        int code = value switch
        {
            ' ' => 0,
            >= '@' and <= '_' => value - '@',
            '?' => 0x7f,
            _ => throw new ArgumentOutOfRangeException(nameof(character), "Control input supports @ through _ and ?.")
        };
        return [(byte)code];
    }

    public static byte[] EncodePaste(string text, bool bracketedPaste)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] content = Encoding.UTF8.GetBytes(text);
        if (!bracketedPaste) return content;
        byte[] result = new byte[content.Length + 12];
        Encoding.ASCII.GetBytes("\x1b[200~").CopyTo(result, 0);
        content.CopyTo(result, 6);
        Encoding.ASCII.GetBytes("\x1b[201~").CopyTo(result, 6 + content.Length);
        return result;
    }

    /// <summary>Encodes a one-based SGR mouse report; button 0-2, wheel 64-67.</summary>
    public static byte[] EncodeMouse(int button, int column, int row, bool release = false,
        bool shift = false, bool alt = false, bool control = false, bool motion = false)
    {
        int code = Math.Clamp(button, 0, 127) + (motion ? 32 : 0) + (shift ? 4 : 0) + (alt ? 8 : 0) + (control ? 16 : 0);
        return Encoding.ASCII.GetBytes($"\x1b[<{code};{Math.Max(1, column)};{Math.Max(1, row)}{(release ? 'm' : 'M')}");
    }

    private static string Cursor(string normal, string application, bool applicationMode, int modifier) =>
        modifier == 1 ? (applicationMode ? $"\x1bO{application}" : $"\x1b[{normal}") : $"\x1b[1;{modifier}{normal}";
    private static string Tilde(int number, int modifier) => modifier == 1 ? $"\x1b[{number}~" : $"\x1b[{number};{modifier}~";
    private static string TildeOrCursor(string final, int number, int modifier) => modifier == 1 ? $"\x1b[{final}" : $"\x1b[1;{modifier}{final}";
    private static string Function(TerminalKey key, int modifier)
    {
        char final = key switch { TerminalKey.F1 => 'P', TerminalKey.F2 => 'Q', TerminalKey.F3 => 'R', _ => 'S' };
        return modifier == 1 ? $"\x1bO{final}" : $"\x1b[1;{modifier}{final}";
    }
}
}
#endregion
#endif

#if TN_ENGINE || TN_SOURCE_TERMINALENGINE_TERMINALMODEL_CS
#region TerminalEngine/TerminalModel.cs
namespace TerminalNotes.TerminalEngine
{
using global::System.Collections.ObjectModel;


public readonly record struct TerminalColor(byte R, byte G, byte B, bool IsDefault = false)
{
    public static TerminalColor Default => new(0, 0, 0, true);
}

public readonly record struct TerminalStyle(
    TerminalColor Foreground, TerminalColor Background, bool Bold = false, bool Italic = false,
    bool Underline = false, bool Inverse = false, bool Strike = false)
{
    public static TerminalStyle Default => new(TerminalColor.Default, TerminalColor.Default);
}

public sealed record TerminalCell(string Text, int Width, TerminalStyle Style);

public sealed class TerminalLine
{
    public IReadOnlyList<TerminalCell> Cells { get; }
    public bool Wrapped { get; }
    public TerminalLine(IEnumerable<TerminalCell> cells, bool wrapped = false)
    {
        Cells = new ReadOnlyCollection<TerminalCell>(cells.ToArray());
        Wrapped = wrapped;
    }
}

public sealed class TerminalSnapshot
{
    public int Columns { get; }
    public int Rows { get; }
    public int CursorColumn { get; }
    public int CursorRow { get; }
    public bool CursorVisible { get; }
    public bool AlternateScreen { get; }
    public bool ApplicationCursorKeys { get; }
    public bool BracketedPaste { get; }
    public int MouseMode { get; }
    public bool SgrMouse { get; }
    public int ScrollbackCount { get; }
    public string? Title { get; }
    public long Version { get; }
    public IReadOnlyList<TerminalLine> Lines { get; }
    internal TerminalSnapshot(int columns, int rows, int cursorColumn, int cursorRow, bool cursorVisible,
        bool alternateScreen, bool applicationCursorKeys, bool bracketedPaste, int mouseMode, bool sgrMouse,
        int scrollbackCount, string? title, long version, IEnumerable<TerminalLine> lines)
    {
        Columns = columns; Rows = rows; CursorColumn = cursorColumn; CursorRow = cursorRow;
        CursorVisible = cursorVisible; AlternateScreen = alternateScreen; ApplicationCursorKeys = applicationCursorKeys;
        BracketedPaste = bracketedPaste; MouseMode = mouseMode; SgrMouse = sgrMouse;
        ScrollbackCount = scrollbackCount; Title = title; Version = version;
        Lines = new ReadOnlyCollection<TerminalLine>(lines.ToArray());
    }
}

public enum TerminalKey { Enter, Backspace, Tab, Escape, Up, Down, Left, Right, Home, End, Insert, Delete, PageUp, PageDown, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12 }
}
#endregion
#endif

#if TN_TRANSPORT || TN_SOURCE_TERMINALTRANSPORT_ITERMINALPROCESS_CS
#region TerminalTransport/ITerminalProcess.cs
namespace TerminalNotes.TerminalTransport
{

public interface ITerminalProcess : IAsyncDisposable
{
    event Action<byte[]>? OutputReceived;
    event Action<TerminalProcessExit>? Exited;
    event Action<Exception>? InputFailed;
    bool IsRunning { get; }
    Task StartAsync(TerminalProcessOptions options, CancellationToken cancellationToken = default);
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    void Resize(int columns, int rows);
    Task CloseAsync(CancellationToken cancellationToken = default);
}
}
#endregion
#endif

#if TN_TRANSPORT || TN_SOURCE_TERMINALTRANSPORT_TERMINALPROCESSEXIT_CS
#region TerminalTransport/TerminalProcessExit.cs
namespace TerminalNotes.TerminalTransport
{

public sealed record TerminalProcessExit(int? ExitCode, string? Error = null);
}
#endregion
#endif

#if TN_TRANSPORT || TN_SOURCE_TERMINALTRANSPORT_TERMINALPROCESSFACTORY_CS
#region TerminalTransport/TerminalProcessFactory.cs
namespace TerminalNotes.TerminalTransport
{

public static class TerminalProcessFactory
{
    public static ITerminalProcess Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowsConPtyProcess();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) return new UnixPtyProcess();
        throw new PlatformNotSupportedException("Terminal transport is supported only on Windows, Linux and macOS.");
    }
}
}
#endregion
#endif

#if TN_TRANSPORT || TN_SOURCE_TERMINALTRANSPORT_TERMINALPROCESSOPTIONS_CS
#region TerminalTransport/TerminalProcessOptions.cs
namespace TerminalNotes.TerminalTransport
{

public sealed record TerminalProcessOptions
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public required string WorkingDirectory { get; init; }
    public int Columns { get; init; } = 80;
    public int Rows { get; init; } = 24;
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
    public string? UnixHelperPath { get; init; }
}
}
#endregion
#endif

#if TN_TRANSPORT || TN_SOURCE_TERMINALTRANSPORT_UNIXPTYPROCESS_CS
#region TerminalTransport/UnixPtyProcess.cs
namespace TerminalNotes.TerminalTransport
{
using global::System.Buffers.Binary;
using global::System.Diagnostics;
using global::System.Text;


/// <summary>Unix PTY transport hosted by the bundled native forkpty helper.</summary>
internal sealed class UnixPtyProcess : ITerminalProcess
{
    private const int MaxControlPayload = 16 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _controlWrite = new(1, 1);
    private Process? _helper;
    private Task? _readTask;
    private Task? _stderrTask;
    private Task? _waitTask;
    private Task? _closeTask;
    private Exception? _inputError;
    private StringBuilder _stderr = new();
    private bool _started;
    private bool _running;

    public event Action<byte[]>? OutputReceived;
    public event Action<TerminalProcessExit>? Exited;
    public event Action<Exception>? InputFailed;
    public bool IsRunning { get { lock (_gate) return _running; } }

    public Task StartAsync(TerminalProcessOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOptions(options);
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();

        string cwd = Path.GetFullPath(options.WorkingDirectory);
        if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException(cwd);
        string helperPath = Path.GetFullPath(options.UnixHelperPath ?? Path.Combine(AppContext.BaseDirectory, "terminalnotes-pty"));
        if (!File.Exists(helperPath)) throw new FileNotFoundException("The native PTY helper was not found. Build/package TerminalTransport/native/terminalnotes-pty for this OS and architecture.", helperPath);

        lock (_gate)
        {
            if (_started) throw new InvalidOperationException("A terminal process can be started only once.");
            _started = true;
            var start = new ProcessStartInfo(helperPath)
            {
                WorkingDirectory = cwd,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("--cwd"); start.ArgumentList.Add(cwd);
            start.ArgumentList.Add("--columns"); start.ArgumentList.Add(options.Columns.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--rows"); start.ArgumentList.Add(options.Rows.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--");
            start.ArgumentList.Add(options.FileName);
            foreach (var arg in options.Arguments) start.ArgumentList.Add(arg);
            foreach (var (key, value) in options.Environment)
            {
                if (value is null) start.Environment.Remove(key);
                else start.Environment[key] = value;
            }
            start.Environment.TryAdd("TERM", "xterm-256color");
            start.Environment.TryAdd("COLORTERM", "truecolor");
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            try
            {
                if (!process.Start()) throw new InvalidOperationException("The native PTY helper did not start.");
            }
            catch { process.Dispose(); throw; }
            _helper = process;
            _running = true;
            _readTask = ReadOutputAsync(process);
            _stderrTask = ReadErrorAsync(process);
            _waitTask = WatchExitAsync(process);
        }
        return Task.CompletedTask;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty) return;
        if (data.Length > MaxControlPayload) throw new ArgumentOutOfRangeException(nameof(data), "A single terminal write is limited to 16 MiB.");
        await SendControlAsync(1, data, cancellationToken).ConfigureAwait(false);
    }

    public void Resize(int columns, int rows)
    {
        ValidateSize(columns, rows);
        Span<byte> size = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(size, columns);
        BinaryPrimitives.WriteInt32LittleEndian(size[4..], rows);
        SendControlAsync(2, size.ToArray(), CancellationToken.None).GetAwaiter().GetResult();
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        Task close;
        lock (_gate)
        {
            if (_closeTask is null)
            {
                _running = false;
                _closeTask = CloseCoreAsync();
            }
            close = _closeTask;
        }
        return cancellationToken.CanBeCanceled ? close.WaitAsync(cancellationToken) : close;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _controlWrite.Dispose();
    }

    private async Task SendControlAsync(byte type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        Process process;
        lock (_gate)
        {
            if (!_running || _helper is null) throw new InvalidOperationException("The terminal process is not running.");
            if (_inputError is { } inputError) throw new IOException("The terminal control writer has failed.", inputError);
            process = _helper;
        }
        await _controlWrite.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] header = new byte[5];
            header[0] = type;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);
            await process.StandardInput.BaseStream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            if (!payload.IsEmpty) await process.StandardInput.BaseStream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            var failure = new IOException("The native PTY control pipe is unavailable.", ex);
            bool notify;
            lock (_gate)
            {
                notify = _inputError is null && _closeTask is null;
                if (notify) _inputError = failure;
            }
            if (notify)
                foreach (Action<Exception> handler in InputFailed?.GetInvocationList() ?? [])
                    try { handler(failure); } catch { }
            throw failure;
        }
        finally { _controlWrite.Release(); }
    }

    private async Task ReadOutputAsync(Process process)
    {
        byte[] buffer = new byte[8192];
        try
        {
            while (true)
            {
                int count = await process.StandardOutput.BaseStream.ReadAsync(buffer).ConfigureAwait(false);
                if (count == 0) break;
                byte[] bytes = buffer.AsSpan(0, count).ToArray();
                foreach (Action<byte[]> handler in OutputReceived?.GetInvocationList() ?? [])
                    try { handler(bytes); } catch { }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private async Task ReadErrorAsync(Process process)
    {
        char[] buffer = new char[1024];
        try
        {
            while (true)
            {
                int count = await process.StandardError.ReadAsync(buffer).ConfigureAwait(false);
                if (count == 0) break;
                lock (_gate)
                {
                    _stderr.Append(buffer, 0, count);
                    if (_stderr.Length > 4096) _stderr.Remove(0, _stderr.Length - 4096);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private async Task WatchExitAsync(Process process)
    {
        int? exitCode = null;
        string? error = null;
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            exitCode = process.ExitCode;
            if (exitCode != 0)
            {
                if (_stderrTask is not null) await _stderrTask.ConfigureAwait(false);
                lock (_gate) error = string.IsNullOrWhiteSpace(_stderr.ToString()) ? "The native PTY helper exited with an error." : _stderr.ToString().Trim();
            }
        }
        catch (Exception ex) { error = ex.GetType().Name; }
        lock (_gate) _running = false;
        foreach (Action<TerminalProcessExit> handler in Exited?.GetInvocationList() ?? [])
            try { handler(new TerminalProcessExit(exitCode, error)); } catch { }
    }

    private async Task CloseCoreAsync()
    {
        Process? process;
        lock (_gate) process = _helper;
        if (process is null) return;
        try
        {
            await _controlWrite.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!process.HasExited)
                {
                    byte[] close = [3, 0, 0, 0, 0];
                    await process.StandardInput.BaseStream.WriteAsync(close).ConfigureAwait(false);
                    await process.StandardInput.BaseStream.FlushAsync().ConfigureAwait(false);
                }
                process.StandardInput.Close();
            }
            finally { _controlWrite.Release(); }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
        if (_readTask is not null) try { await _readTask.ConfigureAwait(false); } catch { }
        if (_stderrTask is not null) try { await _stderrTask.ConfigureAwait(false); } catch { }
        if (_waitTask is not null) try { await _waitTask.ConfigureAwait(false); } catch { }
        lock (_gate) { _helper?.Dispose(); _helper = null; }
    }

    private static void ValidateOptions(TerminalProcessOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        ValidateSize(options.Columns, options.Rows);
        ValidateArgument(options.FileName, nameof(options.FileName));
        foreach (string? arg in options.Arguments) ValidateArgument(arg, nameof(options.Arguments));
        foreach (var (key, value) in options.Environment)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Contains('=') || key.Contains('\0')) throw new ArgumentException("Invalid environment variable name.", nameof(options.Environment));
            if (value?.Contains('\0') == true) throw new ArgumentException("Environment values cannot contain NUL.", nameof(options.Environment));
        }
    }
    private static void ValidateArgument(string? value, string name)
    {
        if (value is null || value.Contains('\0')) throw new ArgumentException("Arguments must be non-null and cannot contain NUL.", name);
    }
    private static void ValidateSize(int columns, int rows)
    {
        if (columns is < 1 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(columns));
        if (rows is < 1 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(rows));
    }
}
}
#endregion
#endif

#if TN_TRANSPORT || TN_SOURCE_TERMINALTRANSPORT_WINDOWSCONPTYPROCESS_CS
#region TerminalTransport/WindowsConPtyProcess.cs
namespace TerminalNotes.TerminalTransport
{
using global::System.Collections;
using global::System.ComponentModel;
using global::System.Runtime.InteropServices;
using global::System.Text;
using global::System.Threading.Channels;
using global::Microsoft.Win32.SafeHandles;


/// <summary>Windows terminal process backed by the Windows 10 1809+ ConPTY API.</summary>
internal sealed class WindowsConPtyProcess : ITerminalProcess
{
    private const uint ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateSuspended = 0x00000004;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint Infinite = 0xFFFFFFFF;
    private readonly object _gate = new();
    private SafePseudoConsoleHandle? _pseudoConsole;
    private SafeKernelHandle? _process;
    private SafeKernelHandle? _job;
    private FileStream? _input;
    private FileStream? _output;
    private Channel<byte[]>? _writes;
    private Task? _outputTask;
    private Task? _writeTask;
    private Task? _exitTask;
    private Task? _closeTask;
    private Exception? _inputError;
    private bool _started;
    private bool _running;
    private int _columns;
    private int _rows;

    public event Action<byte[]>? OutputReceived;
    public event Action<TerminalProcessExit>? Exited;
    public event Action<Exception>? InputFailed;
    public bool IsRunning { get { lock (_gate) return _running; } }

    public Task StartAsync(TerminalProcessOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOptions(options);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new PlatformNotSupportedException("ConPTY requires Windows 10 version 1809 or newer.");

        string workingDirectory = Path.GetFullPath(options.WorkingDirectory);
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException(workingDirectory);
        SortedDictionary<string, string> environment = CreateEffectiveEnvironment(options.Environment);
        string executablePath = ResolveExecutablePath(options.FileName, workingDirectory, environment);
        lock (_gate)
        {
            if (_started) throw new InvalidOperationException("A terminal process can be started only once.");
            _started = true;
            StartCore(options, workingDirectory, executablePath, environment);
        }
        return Task.CompletedTask;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty) return;
        Channel<byte[]> queue;
        lock (_gate)
        {
            if (!_running || _writes is null) throw new InvalidOperationException("The terminal process is not running.");
            if (_inputError is { } inputError) throw new IOException("The terminal input writer has failed.", inputError);
            queue = _writes;
        }
        try { await queue.Writer.WriteAsync(data.ToArray(), cancellationToken).ConfigureAwait(false); }
        catch (ChannelClosedException ex) { throw new IOException("The terminal input writer is no longer available.", ex.InnerException ?? ex); }
    }

    public void Resize(int columns, int rows)
    {
        ValidateSize(columns, rows);
        lock (_gate)
        {
            if (!_running || _pseudoConsole is null) throw new InvalidOperationException("The terminal process is not running.");
            _columns = columns;
            _rows = rows;
        }
        // ConPTY resize can block in the native API. Run it away from the UI and
        // coalesce bursts to the latest dimensions; CloseAsync owns another thread.
        EnsureResizeWorker();
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        Task close;
        lock (_gate)
        {
            if (_closeTask is null)
            {
                _running = false;
                _writes?.Writer.TryComplete();
                _closeTask = Task.Run(CloseCoreAsync);
            }
            close = _closeTask;
        }
        return cancellationToken.CanBeCanceled ? close.WaitAsync(cancellationToken) : close;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    private void StartCore(TerminalProcessOptions options, string workingDirectory, string executablePath,
        IReadOnlyDictionary<string, string> environmentVariables)
    {
        SafeFileHandle? conIn = null, terminalIn = null, terminalOut = null, conOut = null;
        SafePseudoConsoleHandle? pseudo = null;
        SafeKernelHandle? process = null, thread = null, job = null;
        FileStream? input = null, output = null;
        AttributeList? attributes = null;
        bool assigned = false;
        try
        {
            CreatePipeChecked(out conIn, out terminalIn);
            CreatePipeChecked(out terminalOut, out conOut);
            int hr = CreatePseudoConsole(new Coord(ClampDimension(options.Columns), ClampDimension(options.Rows)), conIn, conOut, 0, out var hpcon);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            pseudo = new SafePseudoConsoleHandle(hpcon);
            conIn.Dispose(); conIn = null;
            conOut.Dispose(); conOut = null;

            attributes = new AttributeList(pseudo);
            job = CreateKillOnCloseJob();
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo { Cb = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100 },
                AttributeList = attributes.Pointer
            };
            string commandLine = string.Join(" ", new[] { executablePath }.Concat(options.Arguments).Select(QuoteArgument));
            IntPtr environment = CreateEnvironmentBlock(environmentVariables);
            try
            {
                if (!CreateProcess(executablePath, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateSuspended, environment,
                    workingDirectory, ref startup, out var info))
                    ThrowLastError("starting the terminal process");
                process = new SafeKernelHandle(info.Process);
                thread = new SafeKernelHandle(info.Thread);
                if (!AssignProcessToJobObject(job, process)) ThrowLastError("assigning the terminal process tree to its job");
                assigned = true;

                input = new FileStream(terminalIn, FileAccess.Write, 4096, isAsync: false); terminalIn = null;
                output = new FileStream(terminalOut, FileAccess.Read, 8192, isAsync: false); terminalOut = null;
                _columns = options.Columns; _rows = options.Rows;
                _pseudoConsole = pseudo; pseudo = null;
                _process = process; process = null;
                _job = job; job = null;
                _input = input; input = null;
                _output = output; output = null;
                _writes = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
                _running = true;
                _outputTask = Task.Factory.StartNew(ReadOutput, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                _writeTask = Task.Factory.StartNew(WriteInput, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
                if (ResumeThread(thread) == uint.MaxValue) ThrowLastError("resuming the terminal process");
                thread.Dispose(); thread = null;
                var watchedProcess = _process!;
                bool watcherReference = false;
                watchedProcess.DangerousAddRef(ref watcherReference);
                try
                {
                    _exitTask = Task.Factory.StartNew(() => WatchExit(watchedProcess), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    watcherReference = false; // WatchExit now owns the reference.
                }
                finally { if (watcherReference) watchedProcess.DangerousRelease(); }
            }
            finally { Marshal.FreeHGlobal(environment); }
        }
        catch
        {
            _running = false;
            if (process is not null && !assigned) _ = TerminateProcess(process, 1);
            job?.Dispose(); input?.Dispose(); output?.Dispose(); process?.Dispose(); pseudo?.Dispose();
            _job?.Dispose(); _job = null; _process?.Dispose(); _process = null;
            _input?.Dispose(); _input = null; _output?.Dispose(); _output = null;
            _pseudoConsole?.Dispose(); _pseudoConsole = null;
            _writes?.Writer.TryComplete(); _writes = null;
            throw;
        }
        finally
        {
            attributes?.Dispose(); thread?.Dispose(); conIn?.Dispose(); terminalIn?.Dispose(); terminalOut?.Dispose(); conOut?.Dispose();
        }
    }

    private async Task WriteInput()
    {
        var input = _input!;
        var reader = _writes!.Reader;
        try
        {
            await foreach (var bytes in reader.ReadAllAsync().ConfigureAwait(false))
            {
                input.Write(bytes, 0, bytes.Length);
                input.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            bool notify;
            lock (_gate)
            {
                notify = _closeTask is null && _inputError is null;
                if (notify) _inputError = ex;
                if (notify) _writes?.Writer.TryComplete(ex);
            }
            if (notify)
                foreach (Action<Exception> handler in InputFailed?.GetInvocationList() ?? [])
                    try { handler(ex); } catch { }
        }
    }

    private Task? _resizeWorker;
    private bool _resizePending;
    private void EnsureResizeWorker()
    {
        lock (_gate)
        {
            _resizePending = true;
            if (_resizeWorker is { IsCompleted: false }) return;
            _resizeWorker = Task.Factory.StartNew(ResizeLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    private void ResizeLoop()
    {
        while (true)
        {
            SafePseudoConsoleHandle? console;
            int columns, rows;
            lock (_gate)
            {
                if (!_resizePending || !_running || _pseudoConsole is null) { _resizeWorker = null; return; }
                _resizePending = false;
                columns = _columns; rows = _rows; console = _pseudoConsole;
                console.DangerousAddRef(ref _resizeReferenceAdded);
            }
            try
            {
                if (_resizeReferenceAdded)
                {
                    int hr = ResizePseudoConsole(console!, new Coord(ClampDimension(columns), ClampDimension(rows)));
                    if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                }
            }
            catch { /* Resize errors do not terminate process I/O; next resize can recover. */ }
            finally
            {
                if (_resizeReferenceAdded) { console!.DangerousRelease(); _resizeReferenceAdded = false; }
            }
        }
    }
    private bool _resizeReferenceAdded;

    private void ReadOutput()
    {
        byte[] buffer = new byte[8192];
        try
        {
            while (true)
            {
                int count = _output!.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                var bytes = buffer.AsSpan(0, count).ToArray();
                foreach (Action<byte[]> handler in OutputReceived?.GetInvocationList() ?? [])
                    try { handler(bytes); } catch { }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private void WatchExit(SafeKernelHandle process)
    {
        int? code = null;
        try
        {
            if (WaitForSingleObject(process.DangerousGetHandle(), Infinite) == 0 && GetExitCodeProcess(process.DangerousGetHandle(), out uint nativeCode))
                code = unchecked((int)nativeCode);
        }
        catch { }
        finally { process.DangerousRelease(); }
        lock (_gate) _running = false;
        foreach (Action<TerminalProcessExit> handler in Exited?.GetInvocationList() ?? [])
            try { handler(new TerminalProcessExit(code)); } catch { }
    }

    private async Task CloseCoreAsync()
    {
        SafeKernelHandle? job, process;
        SafePseudoConsoleHandle? pseudo;
        FileStream? input, output;
        lock (_gate)
        {
            job = _job; _job = null; process = _process; _process = null;
            pseudo = _pseudoConsole; _pseudoConsole = null; input = _input; _input = null;
            output = _output; _output = null;
        }
        job?.Dispose(); // Kill only processes assigned to this session's private job.
        _writes?.Writer.TryComplete();
        input?.Dispose();
        pseudo?.Dispose(); // Reader stays alive while ConPTY drains output.
        output?.Dispose();
        process?.Dispose();
        if (_writeTask is not null) try { await _writeTask.ConfigureAwait(false); } catch { }
        if (_outputTask is not null) try { await _outputTask.ConfigureAwait(false); } catch { }
        if (_exitTask is not null) try { await _exitTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch { }
    }

    private static void ValidateOptions(TerminalProcessOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        ValidateSize(options.Columns, options.Rows);
        ValidateArgument(options.FileName, nameof(options.FileName));
        foreach (var arg in options.Arguments) ValidateArgument(arg, nameof(options.Arguments));
        foreach (var (key, value) in options.Environment)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Contains('=') || key.Contains('\0')) throw new ArgumentException("Invalid environment variable name.", nameof(options.Environment));
            if (value?.Contains('\0') == true) throw new ArgumentException("Environment values cannot contain NUL.", nameof(options.Environment));
        }
    }
    private static void ValidateArgument(string? value, string name)
    {
        if (value is null || value.Contains('\0')) throw new ArgumentException("Arguments must be non-null and cannot contain NUL.", name);
    }
    private static void ValidateSize(int columns, int rows)
    {
        if (columns is < 1 or > short.MaxValue) throw new ArgumentOutOfRangeException(nameof(columns));
        if (rows is < 1 or > short.MaxValue) throw new ArgumentOutOfRangeException(nameof(rows));
    }
    private static short ClampDimension(int value) => checked((short)Math.Clamp(value, 1, short.MaxValue));

    private static string QuoteArgument(string value)
    {
        if (value.Length != 0 && !value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { result.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            result.Append('\\', slashes).Append(c); slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }

    private static SortedDictionary<string, string> CreateEffectiveEnvironment(IReadOnlyDictionary<string, string?> overrides)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && entry.Value is string value) vars[key] = value;
        foreach (var (key, value) in overrides)
        {
            if (value is null) vars.Remove(key); else vars[key] = value;
        }
        vars.TryAdd("TERM", "xterm-256color");
        vars.TryAdd("COLORTERM", "truecolor");
        return vars;
    }

    private static IntPtr CreateEnvironmentBlock(IReadOnlyDictionary<string, string> vars)
    {
        var block = new StringBuilder();
        foreach (var (key, value) in vars) block.Append(key).Append('=').Append(value).Append('\0');
        block.Append('\0');
        return Marshal.StringToHGlobalUni(block.ToString());
    }

    private static string ResolveExecutablePath(string fileName, string workingDirectory,
        IReadOnlyDictionary<string, string> environment)
    {
        string extension = Path.GetExtension(fileName);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Batch files are not launched implicitly; start cmd.exe with the batch path as an argument.");

        if (Path.IsPathRooted(fileName) || fileName.Contains('\\') || fileName.Contains('/'))
        {
            string explicitPath = Path.GetFullPath(fileName, workingDirectory);
            return FindExecutableAtPath(explicitPath, fileName);
        }

        if (TryFindExecutableInDirectory(workingDirectory, fileName, out string executable)) return executable;
        if (environment.TryGetValue("PATH", out string? path))
        {
            foreach (string entry in path.Split(';'))
            {
                string directory = entry.Trim().Trim('"');
                if (directory.Length == 0) directory = workingDirectory;
                else if (!Path.IsPathFullyQualified(directory)) directory = Path.GetFullPath(directory, workingDirectory);
                if (TryFindExecutableInDirectory(directory, fileName, out executable)) return executable;
            }
        }

        throw new FileNotFoundException(
            $"Could not find Windows terminal executable '{fileName}' in the working directory or effective child PATH.", fileName);
    }

    private static string FindExecutableAtPath(string path, string originalName)
    {
        if (File.Exists(path)) return path;
        if (Path.GetExtension(path).Length == 0 && File.Exists(path + ".exe")) return path + ".exe";
        throw new FileNotFoundException($"Could not find Windows terminal executable '{originalName}'.", originalName);
    }

    private static bool TryFindExecutableInDirectory(string directory, string fileName, out string executable)
    {
        string candidate = Path.GetFullPath(Path.Combine(directory, fileName));
        if (File.Exists(candidate)) { executable = candidate; return true; }
        if (Path.GetExtension(fileName).Length == 0 && File.Exists(candidate + ".exe"))
        {
            executable = candidate + ".exe";
            return true;
        }
        executable = string.Empty;
        return false;
    }

    private static SafeKernelHandle CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) ThrowLastError("creating the process job");
        var limits = new JobObjectExtendedLimitInformation { BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose } };
        int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        IntPtr mem = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, mem, false);
            if (!SetInformationJobObject(job, 9, mem, (uint)size)) ThrowLastError("setting process job limits");
            return job;
        }
        catch { job.Dispose(); throw; }
        finally { Marshal.FreeHGlobal(mem); }
    }
    private static void CreatePipeChecked(out SafeFileHandle read, out SafeFileHandle write)
    {
        if (!CreatePipe(out read, out write, IntPtr.Zero, 0)) ThrowLastError("creating a ConPTY pipe");
    }
    private static void ThrowLastError(string action) => throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed while {action}.");

    [StructLayout(LayoutKind.Sequential)] private readonly struct Coord(short x, short y) { public readonly short X = x; public readonly short Y = y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Cb; public IntPtr Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags; public ushort ShowWindow, Reserved2; public IntPtr Reserved2Pointer, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct JobObjectBasicLimitInformation { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public nuint MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JobObjectExtendedLimitInformation { public JobObjectBasicLimitInformation BasicLimitInformation; public IoCounters IoInfo; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }

    private sealed class AttributeList : IDisposable
    {
        private IntPtr _pointer;
        public IntPtr Pointer => _pointer;
        public AttributeList(SafePseudoConsoleHandle console)
        {
            nuint bytes = 0; _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            if (bytes == 0) ThrowLastError("sizing the process attribute list");
            _pointer = Marshal.AllocHGlobal(checked((nint)bytes));
            if (!InitializeProcThreadAttributeList(_pointer, 1, 0, ref bytes)) { int error = Marshal.GetLastWin32Error(); Dispose(); throw new Win32Exception(error); }
            if (!UpdateProcThreadAttribute(_pointer, 0, (nuint)ProcThreadAttributePseudoConsole, console.DangerousGetHandle(), (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) { int error = Marshal.GetLastWin32Error(); DeleteProcThreadAttributeList(_pointer); Marshal.FreeHGlobal(_pointer); _pointer = IntPtr.Zero; throw new Win32Exception(error); }
        }
        public void Dispose() { var pointer = Interlocked.Exchange(ref _pointer, IntPtr.Zero); if (pointer != IntPtr.Zero) { DeleteProcThreadAttributeList(pointer); Marshal.FreeHGlobal(pointer); } }
    }
    private sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeKernelHandle() : base(true) { }
        public SafeKernelHandle(IntPtr handle) : base(true) => SetHandle(handle);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    private sealed class SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafePseudoConsoleHandle() : base(true) { }
        public SafePseudoConsoleHandle(IntPtr handle) : base(true) => SetHandle(handle);
        protected override bool ReleaseHandle() { ClosePseudoConsole(handle); return true; }
    }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, uint size);
    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(SafePseudoConsoleHandle console, Coord size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string cwd, ref StartupInfoEx startup, out ProcessInformation info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeKernelHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeKernelHandle job, int kind, IntPtr info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeKernelHandle job, SafeKernelHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeKernelHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeKernelHandle process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
}
#endregion
#endif

#if TN_DESKTOP || TN_SOURCE_TERMINALDESKTOP_PROGRAM_CS
#region TerminalDesktop/Program.cs
namespace TerminalNotes.TerminalDesktop
{
using global::Avalonia;
using global::Avalonia.Controls.ApplicationLifetimes;
using global::Avalonia.Themes.Fluent;


internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => AppBuilder.Configure<TerminalApplication>()
        .UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}

public sealed class TerminalApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnLastWindowClose;
            desktop.MainWindow = new TerminalWindow(LaunchSettings.Parse(desktop.Args ?? []));
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed record LaunchSettings(string Directory, string Executable, string[] Arguments, bool Smoke = false)
{
    public static LaunchSettings Parse(string[] args)
    {
        string cwd = Environment.CurrentDirectory;
        string shell = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
            : Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh";
        string[] shellArgs = OperatingSystem.IsWindows()
            ? ["-NoLogo", "-NoProfile", "-NoExit", "-Command",
                "[Console]::InputEncoding = [Text.UTF8Encoding]::new($false); [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::OutputEncoding"]
            : ["-i"];
        bool smoke = false;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--cwd" when index + 1 < args.Length: cwd = Path.GetFullPath(args[++index]); break;
                case "--shell" when index + 1 < args.Length: shell = args[++index]; shellArgs = []; break;
                case "--smoke": smoke = true; break;
                case "--": shellArgs = args[(index + 1)..]; index = args.Length; break;
                default: throw new ArgumentException("Usage: TerminalNotes.TerminalDesktop [--cwd directory] [--shell executable] [-- arguments]");
            }
        }
        return new(cwd, shell, shellArgs, smoke);
    }
}
}
#endregion
#endif

#if TN_DESKTOP || TN_SOURCE_TERMINALDESKTOP_TERMINALSURFACE_CS
#region TerminalDesktop/TerminalSurface.cs
namespace TerminalNotes.TerminalDesktop
{
using global::System.Globalization;
using global::System.Text;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Input.Platform;
using global::Avalonia.Input.TextInput;
using global::Avalonia.Interactivity;
using global::Avalonia.Media;
using global::Avalonia.Threading;
using global::TerminalNotes.TerminalEngine;


internal sealed class TerminalSurface : Control
{
    private const double FontSize = 15, PaddingSize = 3;
    private readonly TerminalEmulator _engine;
    private readonly object _engineGate;
    private readonly Typeface _font = new(OperatingSystem.IsWindows() ? "Cascadia Mono,Consolas" : "DejaVu Sans Mono,Menlo,monospace");
    private readonly IBrush _background = new SolidColorBrush(Color.Parse("#14171d"));
    private readonly IBrush _foreground = new SolidColorBrush(Color.Parse("#e3e6ec"));
    private readonly IBrush _selection = new SolidColorBrush(Color.Parse("#385575"));
    private readonly Dictionary<(string, TerminalStyle), FormattedText> _glyphs = [];
    private readonly Dictionary<TerminalColor, IBrush> _colors = [];
    private readonly DispatcherTimer _refresh;
    private readonly TerminalImeClient _ime;
    private TerminalSnapshot _snapshot;
    private readonly double _cellWidth, _cellHeight;
    private int _dirty = 1, _offset;
    private (int X, int Y)? _anchor, _end;
    private bool _dragging;
    private bool _allSelected;
    private int _reportedButton = -1;
    private KeyModifiers _modifiers;
    private string _preedit = "";
    private string? _lastTitle;
    public event Action<byte[]>? Input;
    public event Action<int, int>? GridResized;
    public event Action<string?>? TitleChanged;
    public event Action<string>? Error;

    public TerminalSurface(TerminalEmulator engine, object engineGate)
    {
        _engine = engine; _engineGate = engineGate; _snapshot = Snapshot(); Focusable = true; ClipToBounds = true;
        var measure = Text("M", TerminalStyle.Default);
        _cellWidth = Math.Ceiling(measure.WidthIncludingTrailingWhitespace);
        _cellHeight = Math.Ceiling(measure.Height + 2);
        _ime = new TerminalImeClient(this);
        AddHandler(TextInputMethodClientRequestedEvent, (_, args) => args.Client = _ime);
        _engine.Changed += MarkDirty;
        _refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Background, (_, _) => Refresh());
        _refresh.Start();
        var copy = new MenuItem { Header = "복사" }; copy.Click += async (_, _) => await CopyAsync();
        var paste = new MenuItem { Header = "붙여넣기" }; paste.Click += async (_, _) => await PasteAsync();
        var all = new MenuItem { Header = "전체 선택" }; all.Click += (_, _) => SelectAll();
        var clear = new MenuItem { Header = "선택 해제" }; clear.Click += (_, _) => ClearSelection();
        ContextMenu = new ContextMenu { ItemsSource = new[] { copy, paste, all, clear } };
        SizeChanged += (_, _) => Fit();
    }

    public void Stop() { _engine.Changed -= MarkDirty; _refresh.Stop(); }
    private void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);
    private TerminalSnapshot Snapshot(int offset = 0) { lock (_engineGate) return _engine.GetSnapshot(offset); }
    private void Refresh()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
        _snapshot = Snapshot(_offset);
        _offset = Math.Min(_offset, _snapshot.ScrollbackCount);
        if (_snapshot.AlternateScreen) _offset = 0;
        if (_lastTitle != _snapshot.Title) { _lastTitle = _snapshot.Title; TitleChanged?.Invoke(_lastTitle); }
        _ime.CursorMoved(); InvalidateVisual();
    }
    private void Fit()
    {
        int columns = Math.Clamp((int)((Bounds.Width - 2 * PaddingSize) / _cellWidth), 2, 512);
        int rows = Math.Clamp((int)((Bounds.Height - 2 * PaddingSize) / _cellHeight), 1, 256);
        if (columns == _snapshot.Columns && rows == _snapshot.Rows) return;
        lock (_engineGate) { _engine.Resize(columns, rows); _snapshot = _engine.GetSnapshot(_offset); }
        ClearSelection(); GridResized?.Invoke(columns, rows); MarkDirty();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(_background, new Rect(Bounds.Size));
        using var clip = context.PushClip(new Rect(Bounds.Size));
        for (int row = 0; row < _snapshot.Lines.Count; row++)
        {
            var cells = _snapshot.Lines[row].Cells;
            for (int column = 0; column < cells.Count; column++)
            {
                TerminalCell cell = cells[column];
                if (cell.Width == 0) continue;
                var rect = CellRect(column, row, Math.Max(1, cell.Width));
                bool selected = IsSelected(column, row) || cell.Width == 2 && IsSelected(column + 1, row);
                var background = cell.Style.Inverse ? Brush(cell.Style.Foreground, _foreground) : Brush(cell.Style.Background, _background);
                context.FillRectangle(selected ? _selection : background, rect);
                if (cell.Text.Length > 0 && cell.Text != " ")
                {
                    using var cellClip = context.PushClip(rect);
                    context.DrawText(Text(cell.Text, cell.Style), new Point(rect.X, rect.Y));
                }
                var foreground = cell.Style.Inverse ? Brush(cell.Style.Background, _background) : Brush(cell.Style.Foreground, _foreground);
                if (cell.Style.Underline) context.DrawLine(new Pen(foreground), new Point(rect.X, rect.Bottom - 2), new Point(rect.Right, rect.Bottom - 2));
                if (cell.Style.Strike) context.DrawLine(new Pen(foreground), new Point(rect.X, rect.Center.Y), new Point(rect.Right, rect.Center.Y));
            }
        }
        if (_offset == 0 && _snapshot.CursorVisible)
        {
            var rect = CursorRectangle;
            context.DrawRectangle(null, new Pen(_foreground, IsFocused ? 2 : 1), rect);
            if (_preedit.Length > 0)
            {
                FormattedText preedit = Text(_preedit, TerminalStyle.Default);
                context.FillRectangle(_background, new Rect(rect.X, rect.Y, preedit.WidthIncludingTrailingWhitespace, _cellHeight));
                context.DrawText(preedit, rect.Position);
                context.DrawLine(new Pen(_foreground), new Point(rect.X, rect.Bottom - 1), new Point(rect.X + preedit.WidthIncludingTrailingWhitespace, rect.Bottom - 1));
            }
        }
    }
    private IBrush Brush(TerminalColor color, IBrush fallback)
    {
        if (color.IsDefault) return fallback;
        if (_colors.TryGetValue(color, out var brush)) return brush;
        if (_colors.Count > 1024) _colors.Clear();
        return _colors[color] = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
    }
    private FormattedText Text(string value, TerminalStyle style)
    {
        if (_glyphs.TryGetValue((value, style), out var text)) return text;
        if (_glyphs.Count > 4096) _glyphs.Clear();
        var typeface = new Typeface(_font.FontFamily, style.Italic ? FontStyle.Italic : FontStyle.Normal, style.Bold ? FontWeight.Bold : FontWeight.Normal);
        var brush = style.Inverse ? Brush(style.Background, _background) : Brush(style.Foreground, _foreground);
        return _glyphs[(value, style)] = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, FontSize, brush);
    }
    private Rect CellRect(int x, int y, int width = 1) => new(PaddingSize + x * _cellWidth, PaddingSize + y * _cellHeight, width * _cellWidth, _cellHeight);
    internal Rect CursorRectangle => CellRect(_snapshot.CursorColumn, _snapshot.CursorRow);

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (!string.IsNullOrEmpty(e.Text))
        { _preedit = ""; Send(TerminalInputEncoder.EncodeText(e.Text, _modifiers.HasFlag(KeyModifiers.Alt) && !_modifiers.HasFlag(KeyModifiers.Control))); e.Handled = true; }
    }
    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); _modifiers = e.KeyModifiers;
        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift), alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (control && shift && e.Key == Key.C) { e.Handled = true; await CopyAsync(); return; }
        if ((control && shift && e.Key == Key.V) || (shift && e.Key == Key.Insert)) { e.Handled = true; await PasteAsync(); return; }
        // An IME owns the keys while it is composing; only committed TextInput is sent.
        if (_preedit.Length > 0) return;
        if (shift && (e.Key == Key.PageUp || e.Key == Key.PageDown))
        { Scroll((e.Key == Key.PageUp ? 1 : -1) * _snapshot.Rows); e.Handled = true; return; }
        if (control && !alt)
        {
            int key = (int)e.Key;
            int code = e.Key >= Key.A && e.Key <= Key.Z ? key - (int)Key.A + 1
                : e.Key is Key.Space or Key.D2 ? 0 : e.Key == Key.OemOpenBrackets ? 27
                : e.Key == Key.OemPipe ? 28 : e.Key == Key.OemCloseBrackets ? 29
                : e.Key == Key.D6 ? 30 : e.Key is Key.OemMinus or Key.Subtract ? 31 : -1;
            if (code >= 0) { Send([(byte)code]); e.Handled = true; return; }
        }
        if (MapKey(e.Key) is { } terminalKey)
        { Send(TerminalInputEncoder.EncodeKey(terminalKey, shift, alt, control, _snapshot.ApplicationCursorKeys)); e.Handled = true; }
    }
    private static TerminalKey? MapKey(Key key) => key switch
    {
        Key.Enter => TerminalKey.Enter, Key.Back => TerminalKey.Backspace,
        Key.Tab => TerminalKey.Tab, Key.Escape => TerminalKey.Escape,
        Key.Up => TerminalKey.Up, Key.Down => TerminalKey.Down,
        Key.Left => TerminalKey.Left, Key.Right => TerminalKey.Right,
        Key.Home => TerminalKey.Home, Key.End => TerminalKey.End,
        Key.Insert => TerminalKey.Insert, Key.Delete => TerminalKey.Delete,
        Key.PageUp => TerminalKey.PageUp, Key.PageDown => TerminalKey.PageDown,
        >= Key.F1 and <= Key.F12 => TerminalKey.F1 + (key - Key.F1),
        _ => null
    };
    protected override void OnKeyUp(KeyEventArgs e) { _modifiers = e.KeyModifiers; base.OnKeyUp(e); }
    protected override void OnLostFocus(FocusChangedEventArgs e)
    { _modifiers = KeyModifiers.None; _preedit = ""; ReleaseReportedMouse(); _dragging = false; base.OnLostFocus(e); InvalidateVisual(); }
    private void Send(byte[] bytes) { _offset = 0; ClearSelection(); Input?.Invoke(bytes); MarkDirty(); }

    private (int X, int Y) Cell(Point point) => (Math.Clamp((int)((point.X - PaddingSize) / _cellWidth), 0, _snapshot.Columns - 1),
        Math.Clamp((int)((point.Y - PaddingSize) / _cellHeight), 0, _snapshot.Rows - 1));
    private bool ReportsMouse(KeyModifiers modifiers) => _snapshot.MouseMode != 0 && !modifiers.HasFlag(KeyModifiers.Shift) && _offset == 0;
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); Focus();
        var point = e.GetCurrentPoint(this); var cell = Cell(point.Position);
        int button = point.Properties.PointerUpdateKind switch
        { PointerUpdateKind.LeftButtonPressed => 0, PointerUpdateKind.MiddleButtonPressed => 1, PointerUpdateKind.RightButtonPressed => 2, _ => -1 };
        if (button < 0) return;
        if (ReportsMouse(e.KeyModifiers))
        { _reportedButton = button; ReportMouse(button, cell, e.KeyModifiers); e.Pointer.Capture(this); e.Handled = true; }
        else if (button == 0)
        { _allSelected = false; _anchor = cell; _end = cell; _dragging = true; e.Pointer.Capture(this); e.Handled = true; InvalidateVisual(); }
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); var cell = Cell(e.GetPosition(this));
        if (_dragging) { _end = cell; InvalidateVisual(); e.Handled = true; }
        else if (ReportsMouse(e.KeyModifiers) && (_snapshot.MouseMode == 1003 || _snapshot.MouseMode == 1002 && _reportedButton >= 0))
        { ReportMouse(_reportedButton < 0 ? 3 : _reportedButton, cell, e.KeyModifiers, motion: true); e.Handled = true; }
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_reportedButton >= 0) { ReportMouse(_reportedButton, Cell(e.GetPosition(this)), e.KeyModifiers, release: true); _reportedButton = -1; e.Handled = true; }
        if (_dragging) { _end = Cell(e.GetPosition(this)); _dragging = false; e.Handled = true; InvalidateVisual(); }
        e.Pointer.Capture(null);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    { ReleaseReportedMouse(); _dragging = false; base.OnPointerCaptureLost(e); }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (ReportsMouse(e.KeyModifiers))
            for (int i = 0; i < Math.Clamp((int)Math.Ceiling(Math.Abs(e.Delta.Y)), 1, 12); i++) ReportMouse(e.Delta.Y > 0 ? 64 : 65, Cell(e.GetPosition(this)), e.KeyModifiers);
        else Scroll((int)Math.Round(e.Delta.Y * 3));
        e.Handled = true;
    }
    private void ReportMouse(int button, (int X, int Y) cell, KeyModifiers modifiers, bool release = false, bool motion = false)
    {
        bool shift = modifiers.HasFlag(KeyModifiers.Shift), alt = modifiers.HasFlag(KeyModifiers.Alt), control = modifiers.HasFlag(KeyModifiers.Control);
        if (_snapshot.SgrMouse)
            Input?.Invoke(TerminalInputEncoder.EncodeMouse(button, cell.X + 1, cell.Y + 1, release, shift, alt, control, motion));
        else if (cell.X < 223 && cell.Y < 223)
        {
            int code = (release ? 3 : button) + (motion ? 32 : 0) + (shift ? 4 : 0) + (alt ? 8 : 0) + (control ? 16 : 0);
            Input?.Invoke([27, 91, 77, (byte)(32 + code), (byte)(33 + cell.X), (byte)(33 + cell.Y)]);
        }
    }
    private void ReleaseReportedMouse()
    { if (_reportedButton >= 0) { ReportMouse(_reportedButton, (_snapshot.CursorColumn, _snapshot.CursorRow), KeyModifiers.None, release: true); _reportedButton = -1; } }
    private void Scroll(int amount) { _offset = Math.Clamp(_offset + amount, 0, _snapshot.ScrollbackCount); ClearSelection(); MarkDirty(); Refresh(); }
    private void ClearSelection() { _allSelected = false; _anchor = _end = null; InvalidateVisual(); }
    private void SelectAll() { _allSelected = true; _anchor = (0, 0); _end = (_snapshot.Columns - 1, _snapshot.Rows - 1); InvalidateVisual(); }
    private bool IsSelected(int x, int y)
    {
        if (_allSelected) return true;
        if (_anchor is not { } a || _end is not { } b) return false;
        int start = a.Y * _snapshot.Columns + a.X, end = b.Y * _snapshot.Columns + b.X, current = y * _snapshot.Columns + x;
        return current >= Math.Min(start, end) && current <= Math.Max(start, end);
    }
    private async Task CopyAsync()
    {
        try
        {
            if (_anchor is { } a && _end is { } b && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                string text;
                lock (_engineGate) text = _allSelected ? _engine.GetAllText() : _engine.GetSelectionText(a.X, a.Y, b.X, b.Y, _offset);
                await clipboard.SetTextAsync(text);
            }
        }
        catch (Exception error) { Error?.Invoke("복사 실패: " + error.Message); }
    }
    private async Task PasteAsync()
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && await clipboard.TryGetTextAsync() is { } value)
                Send(TerminalInputEncoder.EncodePaste(value, Snapshot().BracketedPaste));
        }
        catch (Exception error) { Error?.Invoke("붙여넣기 실패: " + error.Message); }
    }

    private sealed class TerminalImeClient(TerminalSurface owner) : TextInputMethodClient
    {
        public override Visual TextViewVisual => owner;
        public override bool SupportsPreedit => true;
        public override bool SupportsSurroundingText => false;
        public override string SurroundingText => "";
        public override Rect CursorRectangle => owner.CursorRectangle;
        public override TextSelection Selection { get; set; }
        public override void SetPreeditText(string? preeditText) { owner._preedit = preeditText ?? ""; owner.InvalidateVisual(); }
        public void CursorMoved() => RaiseCursorRectangleChanged();
    }
}
}
#endregion
#endif

#if TN_DESKTOP || TN_SOURCE_TERMINALDESKTOP_TERMINALWINDOW_CS
#region TerminalDesktop/TerminalWindow.cs
namespace TerminalNotes.TerminalDesktop
{
using global::System.Text;
using global::System.Threading.Channels;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Controls.ApplicationLifetimes;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Layout;
using global::Avalonia.Threading;
using global::TerminalNotes.TerminalEngine;
using global::TerminalNotes.TerminalTransport;


internal sealed class TerminalWindow : Window
{
    private readonly LaunchSettings _settings;
    private readonly TerminalEmulator _engine = new(100, 30);
    private readonly object _engineGate = new();
    private readonly ITerminalProcess _process = TerminalProcessFactory.Create();
    private readonly TerminalSurface _surface;
    private readonly TextBlock _status = new() { Text = "시작 중…", Margin = new Thickness(8, 3), FontSize = 12 };
    private readonly Channel<byte[]> _input = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4096)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _inputPump;
    private bool _started, _closing, _closed;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly MemoryStream _smokeOutput = new();

    public TerminalWindow(LaunchSettings settings)
    {
        _settings = settings;
        Title = "TerminalNotes Terminal";
        Width = 1000; Height = 650; MinWidth = 320; MinHeight = 200;
        _surface = new TerminalSurface(_engine, _engineGate);
        var add = new Button { Content = "+ 새 터미널", Margin = new Thickness(4), Padding = new Thickness(10, 4) };
        add.Click += (_, _) => OpenNewTerminal();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Children = { add,
            new TextBlock { Text = "Ctrl+Shift+T 새 창 · Ctrl+Shift+C/V 복사/붙여넣기 · Shift+드래그 선택",
                VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(8, 0) } } };
        var layout = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(_status, Dock.Bottom);
        layout.Children.Add(top); layout.Children.Add(_status); layout.Children.Add(_surface); Content = layout;
        _surface.Input += QueueInput;
        _surface.GridResized += ResizeProcess;
        _surface.TitleChanged += title => Title = string.IsNullOrWhiteSpace(title) ? "TerminalNotes Terminal" : title + " — TerminalNotes";
        _surface.Error += SetStatus;
        _engine.Response += response => QueueInput(Encoding.UTF8.GetBytes(response));
        _process.OutputReceived += bytes =>
        {
            if (_settings.Smoke) lock (_smokeOutput) _smokeOutput.Write(bytes);
            lock (_engineGate) _engine.Feed(bytes.AsSpan());
        };
        _process.Exited += result => Dispatcher.UIThread.Post(() => SetStatus(result.Error is null
            ? $"프로세스 종료 ({result.ExitCode?.ToString() ?? "?"})" : "연결 오류: " + result.Error));
        _process.InputFailed += error => Dispatcher.UIThread.Post(() => SetStatus("입력 전송 실패: " + error.Message));
        _inputPump = PumpInputAsync();
        Opened += async (_, _) => await StartAsync();
        Closing += OnClosing;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    private void OpenNewTerminal() => new TerminalWindow(_settings with { Smoke = false }).Show();
    private void ResizeProcess(int columns, int rows)
    {
        if (!_started || _closing || !_process.IsRunning) return;
        try { _process.Resize(columns, rows); }
        catch (Exception error) { SetStatus("크기 변경 실패: " + error.Message); }
    }
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.T && e.KeyModifiers.HasFlag(KeyModifiers.Control | KeyModifiers.Shift))
        { e.Handled = true; OpenNewTerminal(); }
    }

    private async Task StartAsync()
    {
        try
        {
            var snapshot = Snapshot();
            await _process.StartAsync(new TerminalProcessOptions
            {
                FileName = _settings.Executable, Arguments = _settings.Arguments,
                WorkingDirectory = _settings.Directory, Columns = snapshot.Columns, Rows = snapshot.Rows,
                Environment = new Dictionary<string, string?> { ["TERM"] = "xterm-256color", ["COLORTERM"] = "truecolor" }
            }, _lifetime.Token);
            if (_closing) return;
            _started = true; _ready.TrySetResult();
            snapshot = Snapshot();
            ResizeProcess(snapshot.Columns, snapshot.Rows);
            SetStatus(_settings.Directory);
            _surface.Focus();
            if (_settings.Smoke) _ = RunSmokeAsync();
        }
        catch (Exception error)
        {
            _ready.TrySetCanceled();
            SetStatus("시작 실패: " + error.Message);
            if (_settings.Smoke) await FinishSmokeAsync(error);
        }
    }

    private void QueueInput(byte[] bytes)
    {
        if (_closing || bytes.Length == 0) return;
        if (!_input.Writer.TryWrite(bytes))
            Dispatcher.UIThread.Post(() => SetStatus("입력 대기열이 가득 찼습니다. 방금 입력은 전송되지 않았습니다."));
    }

    private async Task PumpInputAsync()
    {
        try
        {
            await _ready.Task.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            await foreach (byte[] bytes in _input.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
                await _process.WriteAsync(bytes, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Dispatcher.UIThread.Post(() => SetStatus("입력 전송 실패: " + error.Message)); }
    }

    private void SetStatus(string text) => _status.Text = text;
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        await CloseSessionAsync();
        Close();
    }

    private async Task CloseSessionAsync()
    {
        if (_closing) return;
        _closing = true; _surface.Stop(); _input.Writer.TryComplete(); _lifetime.Cancel(); _ready.TrySetCanceled();
        try { await _process.CloseAsync(); await _process.DisposeAsync(); await _inputPump; }
        catch (Exception error) { SetStatus("종료 오류: " + error.Message); }
        finally { _closed = true; _lifetime.Dispose(); }
    }

    // Explicit, isolated acceptance mode. It writes only to this window's own shell.
    private async Task RunSmokeAsync()
    {
        try
        {
            await Task.Delay(1000);
            string command = OperatingSystem.IsWindows()
                ? "[Console]::WriteLine('NATIVE_' + 'READY 한글✓')\r"
                : "printf '\\033[32m%s\\033[0m\\n' 'NATIVE_'\"READY 한글✓\"\r";
            _surface.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = command.TrimEnd('\r') });
            _surface.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await UntilAsync(() => ScreenText().Contains("NATIVE_READY 한글✓", StringComparison.Ordinal), "Unicode output");
            for (int i = 0; i < 20; i++) { Width = 700 + i * 11; Height = 400 + i * 7; await Task.Delay(30); }
            if (!_process.IsRunning) throw new InvalidOperationException("Child exited during resize");
            QueueInput(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows()
                ? "[Console]::WriteLine('AFTER_' + 'RESIZE')\r" : "printf '%s\\n' 'AFTER_'RESIZE\r"));
            await UntilAsync(() => ScreenText().Contains("AFTER_RESIZE", StringComparison.Ordinal), "Output after resize");
            var state = Snapshot();
            if (state.Columns < 10 || state.Rows < 5) throw new InvalidOperationException("Invalid viewport");
            var second = new TerminalWindow(_settings with { Smoke = false });
            try
            {
                second.Show();
                await second._ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
                if (!second._process.IsRunning || ReferenceEquals(second._engine, _engine)) throw new InvalidOperationException("New terminal is not independent");
                await second.CloseSessionAsync();
                if (!_process.IsRunning) throw new InvalidOperationException("Closing another terminal terminated the original");
            }
            finally { await second.CloseSessionAsync(); second.Close(); }
            string? screenshot = Environment.GetEnvironmentVariable("TERMINALNOTES_DESKTOP_SMOKE_SCREENSHOT");
            if (!string.IsNullOrEmpty(screenshot))
            {
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height));
                bitmap.Render(this); bitmap.Save(screenshot, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            await FinishSmokeAsync(null);
        }
        catch (Exception error) { await FinishSmokeAsync(error); }
    }

    private TerminalSnapshot Snapshot() { lock (_engineGate) return _engine.GetSnapshot(); }
    private string ScreenText() => string.Join("\n", Snapshot().Lines.Select(line => string.Concat(line.Cells.Select(cell => cell.Text))));
    private static async Task UntilAsync(Func<bool> condition, string name)
    {
        var end = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < end) { if (condition()) return; await Task.Delay(100); }
        throw new TimeoutException(name);
    }
    private async Task FinishSmokeAsync(Exception? error)
    {
        string? log = Environment.GetEnvironmentVariable("TERMINALNOTES_DESKTOP_SMOKE_LOG");
        if (!string.IsNullOrEmpty(log))
        {
            string raw;
            lock (_smokeOutput) raw = Encoding.UTF8.GetString(_smokeOutput.ToArray());
            await File.WriteAllTextAsync(log, error is null
                ? "PASS portable desktop: real PTY, TextInput/Enter, Unicode, repeated resize, independent windows and close ownership\n"
                : "FAIL " + error + "\nSTATUS: " + _status.Text + "\nSCREEN:\n" + ScreenText() + "\nRAW:\n" + raw);
        }
        await CloseSessionAsync();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown(error is null ? 0 : 1);
    }
}
}
#endregion
#endif

