// Consolidated TerminalNotes source. Each region preserves an original compilation unit.
// TN_SOURCE_* symbols let focused tests compile their original dependency set.

#if TN_WPF || TN_SOURCE_TERMINALPANE_AUTOMATION_CS
#region TerminalPane.Automation.cs
namespace TerminalNotes
{
using global::System.Windows.Threading;
using global::TerminalNotes.Services;


public sealed partial class TerminalPane
{
    private CancellationTokenSource _nativeAutomationCancellation = new();
    private bool _nativeAutomationActive;
    private string? _nativeAutomationSupervisionNotice;
    private bool _allowBackgroundAutomationStartup;
    private DateTimeOffset? _startupUnresponsiveSince;

    internal void UseDedicatedAutomationSession() { _boundAutomationSession = null; _externalContinuationSession = null; _openCodeStatus?.SetSessionScope(null); }
    internal void BindNativeAutomationSession(string sessionId)
    {
        _boundAutomationSession = sessionId;
        _externalContinuationSession = null;
        _openCodeStatus?.SetSessionScope(sessionId);
        if (_usesNativeOpenCodeStatus && _headlessSessionId != sessionId)
        {
            _headlessSessionId = sessionId;
            _nativeViewGeneration++;
            PersistNativeSession(sessionId);
            ScheduleNativeStateRefresh();
        }
    }
    internal void ObserveOwnedAutomationSessions(IEnumerable<string> sessionIds) => _openCodeStatus?.ObserveOwnedSessionTree(sessionIds);

    internal void ObserveStartupReadiness()
    {
        if (IsReady && NativeOpenCodeStatus == OpenCodeStatusKind.Unknown &&
            !HasLocalPendingInput() && !_manualInputOverride && !IsUserInputBlocked)
            _startupUnresponsiveSince ??= DateTimeOffset.UtcNow;
        else _startupUnresponsiveSince = null;
    }

    internal void AllowBackgroundAutomationStartup()
    {
        _allowBackgroundAutomationStartup = true;
        TryStartTerminalWhenLaidOut();
    }

    internal void SetNativeAutomationSupervisionNotice(string? notice)
    {
        if (_nativeAutomationSupervisionNotice == notice) return;
        _nativeAutomationSupervisionNotice = notice;
        ApplyActivityChange();
    }

    internal Uri? NativeOpenCodeEndpoint { get; private set; }

    /// <summary>
    /// The cancellation token for the current native automation dispatch. A new
    /// token is created for each dispatch and canceled by Interrupt or Dispose.
    /// </summary>
    internal CancellationToken NativeAutomationCancellation => _nativeAutomationCancellation.Token;

    internal bool CanStartNativeAutomation => OnDispatcher(CanStartNativeAutomationCore, false);

    /// <summary>
    /// True while a dispatch still owns this pane. Native busy and approval
    /// states are valid during an active dispatch; local pending input is not.
    /// </summary>
    internal bool IsNativeAutomationActive => OnDispatcher(IsNativeAutomationActiveCore, false);

    internal OpenCodeStatusKind NativeOpenCodeStatus => OnDispatcher(
        () => WasContinuedExternally || _disposed || _failed || (_ready && !IsRunning) || _activityTracker.Activity == TerminalActivity.Exited
            ? OpenCodeStatusKind.Unknown : _openCodeStatus?.Snapshot.Kind ?? OpenCodeStatusKind.Unknown,
        OpenCodeStatusKind.Unknown);

    internal OpenCodeStatusMonitorPhase NativeStatusMonitorPhase => OnDispatcher(
        () => !_usesNativeOpenCodeStatus || WasContinuedExternally || _disposed || _failed || !IsRunning
            ? OpenCodeStatusMonitorPhase.NotMonitoring
            : _openCodeStatus?.MonitorPhase ?? OpenCodeStatusMonitorPhase.NotMonitoring,
        OpenCodeStatusMonitorPhase.NotMonitoring);

    // Readiness diagnostics contain flags only, never the user's pending input.
    internal AutomationPaneDiagnostic NativeAutomationDiagnostic => OnDispatcher(() => new AutomationPaneDiagnostic(
        NativeOpenCodeStatus.ToString(), IsReady, IsRunning, CanStartNativeAutomationCore(),
        _nativeAutomationActive, NativeOpenCodeEndpoint is not null, HasLocalPendingInput(),
        IsUserInputBlocked, _manualInputOverride, _failed, _disposed, _nativeAutomationCancellation.IsCancellationRequested)
        { ConnectionFailure = _openCodeStatus?.LastConnectionFailure },
        new AutomationPaneDiagnostic("Unknown", false, false, false, false, false, false, false, false, false, true, true));

    internal void BeginNativeAutomation()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(BeginNativeAutomation);
            return;
        }

        if (!CanStartNativeAutomationCore())
            throw new InvalidOperationException("The native OpenCode pane is not ready for automation.");

        _nativeAutomationCancellation.Cancel();
        _nativeAutomationCancellation.Dispose();
        _nativeAutomationCancellation = new CancellationTokenSource();
        _nativeAutomationActive = true;
        _startupUnresponsiveSince = null;
        _autoResume?.Suspend("자동화 진행 · 자동 진행 중복 방지");
        SetAutomationBusy(true);
        SetNativeAutomationSupervisionNotice("응답 변화 대기");
    }

    /// <summary>
    /// Records the native submission immediately before an automation prompt is
    /// posted. Invalid state throws so a caller cannot silently continue to POST.
    /// </summary>
    internal void ObserveNativeAutomationSubmission()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ObserveNativeAutomationSubmissionCore);
            return;
        }

        ObserveNativeAutomationSubmissionCore();
    }

    private void ObserveNativeAutomationSubmissionCore()
    {
        if (!IsNativeAutomationActiveCore() || _openCodeStatus?.Snapshot.Kind != OpenCodeStatusKind.Idle)
            throw new InvalidOperationException("Native automation state changed before the prompt was submitted.");

        _openCodeStatus.ObserveSubmission();
    }

    private bool CanStartNativeAutomationCore()
    {
        if (!_usesNativeOpenCodeStatus || _openCodeStatus is null || !IsReady || !IsRunning ||
            _disposed || _failed || _automationBusy || _nativeAutomationActive ||
            IsUserInputBlocked || _manualInputOverride || HasLocalPendingInput())
        {
            return false;
        }

        return _openCodeStatus.Snapshot.Kind == OpenCodeStatusKind.Idle;
    }

    private bool IsNativeAutomationActiveCore() =>
        _usesNativeOpenCodeStatus && _nativeAutomationActive && !_nativeAutomationCancellation.IsCancellationRequested &&
        _automationBusy && IsReady && IsRunning && !_disposed && !_failed &&
        !IsUserInputBlocked && !_manualInputOverride && !HasLocalPendingInput();

    private bool HasLocalPendingInput() =>
        _heldStartupInput.Length > 0 || _inputCapture.HasPendingInput || _savedPlanComposerMayContainPendingInput || _planMakerQuestionDrafts.Count != 0 ||
        _pendingTextSends.Count != 0 || _savedPlanDispatchForNextSend is not null ||
        _savedPlanDispatches.Count != 0 || (_usesNativeOpenCodeStatus && _nativeComposerDraft.Length > 0);

    private T OnDispatcher<T>(Func<T> callback, T fallback)
    {
        if (Dispatcher.CheckAccess()) return callback();
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return fallback;

        try { return Dispatcher.Invoke(callback); }
        catch (InvalidOperationException) { return fallback; }
        catch (TaskCanceledException) { return fallback; }
    }

    private void CancelNativeAutomation()
    {
        _nativeAutomationActive = false;
        try { _nativeAutomationCancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Closes only this pane's existing ConPTY job; retains its view and
    /// dispatch token so automatic recovery cannot be mistaken for a manual stop.</summary>
    internal async Task<bool> StopNativeAutomationProcessAsync()
    {
        if (!Dispatcher.CheckAccess()) throw new InvalidOperationException("Recovery must start on the pane dispatcher.");
        if (!_nativeAutomationActive || _nativeAutomationCancellation.IsCancellationRequested ||
            _disposed || IsUserInputBlocked || HasLocalPendingInput() || _manualInputOverride ||
            NativeOpenCodeStatus == OpenCodeStatusKind.NeedsInput) return false;
        await StopOwnedOpenCodeProcessAsync();
        _failed = true;
        _flushTimer.Stop();
        MarkWorkFailed();
        return true;
    }

    internal async Task<bool> StopUnresponsiveStartupProcessAsync()
    {
        if (!Dispatcher.CheckAccess()) throw new InvalidOperationException("Startup recovery must start on the pane dispatcher.");
        if (_nativeAutomationActive || _nativeAutomationCancellation.IsCancellationRequested ||
            _disposed || IsUserInputBlocked || HasLocalPendingInput() || _manualInputOverride || !IsReady ||
            NativeOpenCodeStatus != OpenCodeStatusKind.Unknown || _startupUnresponsiveSince is not { } since ||
            DateTimeOffset.UtcNow - since < TimeSpan.FromMinutes(3)) return false;
        await StopOwnedOpenCodeProcessAsync();
        _failed = true;
        _flushTimer.Stop();
        MarkWorkFailed();
        return true;
    }
}
}
#endregion
#endif

#if TN_WPF || TN_SOURCE_TERMINALPANE_CS
#region TerminalPane.cs
namespace TerminalNotes
{
using global::System.IO;
using global::System.Net;
using global::System.Net.Sockets;
using global::System.Runtime.ExceptionServices;
using global::System.Text;
using global::System.Text.Json;
using global::System.Windows;
using global::System.Windows.Controls;
using global::System.Windows.Media;
using global::System.Windows.Threading;
using global::Microsoft.Web.WebView2.Core;
using global::Microsoft.Web.WebView2.Wpf;
using global::TerminalNotes.Controls;
using global::TerminalNotes.Services;
using global::TerminalNotes.TerminalEngine;


public sealed partial class TerminalPane : Grid, IDisposable
{
    private const string Origin = "https://terminalnotes.local/";
    private readonly WebView2 _browser = new() { DefaultBackgroundColor = System.Drawing.Color.FromArgb(12, 16, 22) };
    private readonly TextBlock _notice = new() { Text = "터미널을 시작하고 있습니다…", Margin = new Thickness(24), Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap };
    private ConPtySession _session = new();
    private readonly TerminalActivityTracker _activityTracker = new();
    private readonly TerminalInputCapture _inputCapture = new();
    private readonly TerminalTokenUsage _tokenUsage = new();
    private readonly OpenCodeProjectContext? _openCodeProjectContext;
    private readonly string? _openCodeConfigContent;
    private readonly Queue<string> _pending = new();
    private readonly Dictionary<long, TaskCompletionSource> _pendingTextSends = [];
    private long _nextTextSendId;
    private int _queuedCharacters;
    private bool _writeInFlight, _failed;
    private string? _retryOutput;
    private int _outputPostFailures;
    private readonly object _outputLock = new();
    private readonly DispatcherTimer _flushTimer;
    private readonly DispatcherTimer _activityTimer;
    private bool _ready, _disposed, _automationBusy, _workFailed;
    private readonly TaskCompletionSource<Exception?> _loadedCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<Exception?> _terminalStartedCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _initializeTask;
    private (short Columns, short Rows)? _pendingWebReadyDimensions;
    private readonly bool _startupWork;
    private readonly bool _protectTuiViewport;
    private readonly bool _usesNativeOpenCodeStatus;
    private readonly bool _usesNativeOpenCodeTui;
    private readonly bool _useNativeTerminalSurface;
    private readonly string? _effectiveStartupCommand;
    private string _openCodeSessionTitle;
    private readonly OpenCodeStatusMonitor? _openCodeStatus;
    private int _sessionCloseStarted;
    private string _lastLifecycleStage = "initializing";
    private OpenCodeStatusSnapshot? _openCodeSnapshot;
    private bool _manualInputOverride;
    private bool _lastNativeRecentActivity;
    private OpenCodeSubmissionObservation? _lastManualSubmissionObservation;
    public string WorkingDirectory { get; }
    public bool LaunchesCodex { get; }
    public string? StartupCommand { get; }
    internal bool IsOpenCodeTui => _usesNativeOpenCodeTui;
    internal bool IsOpenCodeTuiAttached => _usesNativeOpenCodeTui && _session.IsRunning;
    internal uint? OpenCodeTuiProcessId => _usesNativeOpenCodeTui ? _session.ProcessId : null;
    internal uint? OpenCodeServerProcessId => _usesNativeOpenCodeStatus ? _headlessOpenCodeHost?.ProcessId : null;
    internal string? OpenCodeTuiSessionId => _usesNativeOpenCodeTui ? _headlessSessionId : null;
    internal string LastLifecycleStage => _lastLifecycleStage;
    public bool IsRunning => _usesNativeOpenCodeStatus ? _headlessOpenCodeHost?.IsRunning == true : _session.IsRunning;
    internal uint? SessionProcessId => _usesNativeOpenCodeStatus ? _headlessOpenCodeHost?.ProcessId : _session.ProcessId;
    public bool IsReady => _ready && !_disposed && !_failed && IsRunning && (!_usesNativeOpenCodeTui || _session.IsRunning);
    public TerminalActivity Activity
    {
        get
        {
            TerminalActivity tracked = _activityTracker.Activity;
            if (_usesNativeOpenCodeStatus && tracked is not (TerminalActivity.Error or TerminalActivity.Exited))
            {
                if (_failed || (_ready && !IsRunning)) return TerminalActivity.Exited;
                if (_manualInputOverride) return TerminalActivity.NeedsInput;
                if (_autoResume?.IsStalled == true && _openCodeSnapshot?.Kind is not (OpenCodeStatusKind.NeedsInput or OpenCodeStatusKind.Busy)) return TerminalActivity.Quiet;
                bool recentActivity = _openCodeStatus?.HasRecentActivity == true;
                // Display notices and recovery diagnostics cannot override exact native busy.
                if (!recentActivity && _nativeAutomationSupervisionNotice is not null &&
                    _openCodeSnapshot?.Kind is not (OpenCodeStatusKind.NeedsInput or OpenCodeStatusKind.Pending or OpenCodeStatusKind.Busy))
                    return TerminalActivity.Quiet;
                return _openCodeSnapshot?.Kind switch
                {
                    OpenCodeStatusKind.Busy => TerminalActivity.Active,
                    OpenCodeStatusKind.NeedsInput => TerminalActivity.NeedsInput,
                    OpenCodeStatusKind.Idle => TerminalActivity.Idle,
                    OpenCodeStatusKind.Pending => TerminalActivity.Starting,
                    _ => TerminalActivity.Quiet
                };
            }
            if (tracked is TerminalActivity.Error or TerminalActivity.RateLimited or TerminalActivity.NeedsInput or TerminalActivity.Exited) return tracked;
            if (_workFailed) return TerminalActivity.Error;
            return _automationBusy ? TerminalActivity.Active : tracked;
        }
    }
    public string ActivityLabel
    {
        get
        {
            if (_externalContinuationNotice is not null) return _externalContinuationNotice;
            if (WasContinuedExternally) return "CMD에서 이어가기";
            TerminalActivity tracked = _activityTracker.Activity;
            if (_usesNativeOpenCodeStatus && tracked is not (TerminalActivity.Error or TerminalActivity.Exited))
            {
                if (_failed || (_ready && !IsRunning)) return "터미널 종료";
                if (_manualInputOverride) return "확인 필요 (수동)";
                if (_autoResume?.IsStalled == true && _openCodeSnapshot?.Kind is not (OpenCodeStatusKind.NeedsInput or OpenCodeStatusKind.Busy)) return _autoResume.Status;
                // Supervision notices ("지휘 지시로 중단 확인 중" and the like) are no longer shown on the terminal: its plain state is.
                string nativeLabel = _openCodeSnapshot?.Kind switch
                {
                    OpenCodeStatusKind.Busy => "진행 중",
                    OpenCodeStatusKind.NeedsInput => "승인·선택 대기",
                    OpenCodeStatusKind.Idle => HasCompletedWork ? "응답 완료" : "대기 중",
                    OpenCodeStatusKind.Pending => "요청 전달 중",
                    _ => "OpenCode 상태 확인 불가"
                };
                if (_workFailed && _openCodeSnapshot?.Kind == OpenCodeStatusKind.Idle)
                    return "작업 실패";
                return string.IsNullOrEmpty(_autoResumeStatus) ? nativeLabel : nativeLabel + " · " + _autoResumeStatus;
            }
            if (tracked is TerminalActivity.Error or TerminalActivity.RateLimited or TerminalActivity.NeedsInput or TerminalActivity.Exited)
                return _activityTracker.ActivityLabel;
            if (_workFailed) return "작업 실패";
            if (_automationBusy) return "진행 중";
            return HasCompletedWork ? "응답 완료" : _activityTracker.ActivityLabel;
        }
    }
    public bool HasCompletedWork => !_automationBusy && !_workFailed && !_manualInputOverride &&
        (_usesNativeOpenCodeStatus
            ? _openCodeSnapshot is { Kind: OpenCodeStatusKind.Idle, HasCompletedTurn: true } && IsRunning
            : _activityTracker.HasCompletedWork);
    internal bool IsUserInputBlocked { get; set; }
    internal OpenCodeSubmissionObservation? LastManualSubmissionObservation => _lastManualSubmissionObservation;
    internal long NativeSubmissionGeneration => _openCodeStatus?.SubmissionGeneration ?? 0;
    public string TokenUsageLabel => _tokenUsage.Label;
    public event Action<string>? StatusChanged;
    public event Action<string>? UserWarning;

    private void ReportUserWarning(string message)
    {
        if (UserWarning is { } notify) notify(message);
        else StatusChanged?.Invoke(message);
    }
    internal event Action<string>? LifecycleStageChanged;
    internal event Action<string, string, string>? LifecycleFailureReported;
    public event Action? ActivityChanged;
    internal event Action<OpenCodeAutoResumeDiagnostic>? AutoResumeDiagnosticEmitted;
    public event Action? ActivatedByUser;
    public event Action? Ready;
    public event Action<string>? InputSubmitted;
    internal event Action<long, long>? NativeSubmissionConfirmed;
    public event Action? TokenUsageChanged;
    public event Action<string, bool>? Shortcut;
    public event Action<int>? ProcessExited;
    public event Action<bool>? ContextMenuRequested;

    public TerminalPane(
        string workingDirectory,
        bool launchCodex = false,
        string? startupCommand = null,
        bool startupWork = false,
        bool protectTuiViewport = false,
        string? planDocumentPath = null,
        string? projectName = null,
        string? paneKey = null,
        bool useProjectContext = true)
    {
        _headlessLifetimeToken = _headlessLifetimeCancellation.Token;
        WorkingDirectory = workingDirectory;
        _openCodeSessionTitle = ProjectSessionTitle(workingDirectory, projectName);
        LaunchesCodex = launchCodex;
        StartupCommand = startupCommand;
        _startupWork = startupWork;
        _protectTuiViewport = protectTuiViewport;
        _effectiveStartupCommand = startupCommand;
        _usesNativeOpenCodeStatus = protectTuiViewport && (startupCommand is "opencode" or "opencode --auto");
        _usesNativeOpenCodeTui = _usesNativeOpenCodeStatus &&
            !string.Equals(Environment.GetEnvironmentVariable("TERMINALNOTES_OPENCODE_RENDERER"), "headless", StringComparison.OrdinalIgnoreCase);
        _useNativeTerminalSurface = _usesNativeOpenCodeTui || !_usesNativeOpenCodeStatus &&
            !string.Equals(Environment.GetEnvironmentVariable("TERMINALNOTES_TERMINAL_RENDERER"), "webview", StringComparison.OrdinalIgnoreCase);
        if (_usesNativeOpenCodeStatus)
        {
            if (useProjectContext)
            {
                _openCodeProjectContext = new OpenCodeProjectContext(workingDirectory, planDocumentPath, projectName);
                try
                {
                    _openCodeConfigContent = _openCodeProjectContext.CreateProcessConfigContent(
                        Environment.GetEnvironmentVariable("OPENCODE_CONFIG_CONTENT"));
                }
                catch
                {
                    _openCodeProjectContext.Dispose();
                    throw;
                }

            }

            try
            {
                // A task terminal opened again takes back the server its agent kept working on after the app closed.
                var kept = useProjectContext && paneKey is { Length: > 0 } ? KeptServers.Take(workingDirectory, paneKey) : null;
                int port;
                if (kept is not null) port = kept.Port;
                else
                {
                    using var reservation = new TcpListener(IPAddress.Loopback, 0);
                    reservation.Start();
                    port = ((IPEndPoint)reservation.LocalEndpoint).Port;
                    reservation.Stop();
                }
                _effectiveStartupCommand = $"opencode --hostname 127.0.0.1 --port {port}" +
                    (startupCommand == "opencode --auto" ? " --auto" : "");
                Uri nativeEndpoint = new($"http://127.0.0.1:{port}/");
                NativeOpenCodeEndpoint = nativeEndpoint;
                _openCodeStatus = new OpenCodeStatusMonitor(nativeEndpoint, workingDirectory);
                InitializeHeadlessOpenCode(nativeEndpoint, paneKey);
                if (kept is not null)
                {
                    _headlessOpenCodeHost!.AdoptOnStart(kept.ProcessId);
                    _keptSessionId = kept.SessionId;
                    _keptProcessId = kept.ProcessId;
                }
                if (useProjectContext) InitializeAutoResume(nativeEndpoint, planDocumentPath);
                _openCodeSnapshot = _openCodeStatus.Snapshot;
                _openCodeStatus.StatusChanged += _ => ScheduleNativeActivityRefresh();
                _openCodeStatus.StatusChanged += _ => ScheduleSkillReload();
                _openCodeStatus.ActivityChanged += ScheduleNativeActivityRefresh;
                _openCodeStatus.SubmissionConfirmed += observation =>
                {
                    if (_disposed || Dispatcher.HasShutdownStarted) return;
                    void Raise()
                    {
                        if (!_disposed && observation.Generation == _openCodeStatus.SubmissionGeneration)
                            NativeSubmissionConfirmed?.Invoke(observation.ConfirmationVersion, observation.Generation);
                    }
                    if (Dispatcher.CheckAccess()) Raise();
                    else Dispatcher.BeginInvoke((Action)Raise);
                };
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException or InvalidOperationException)
            {
                // A missing status connection is explicitly unknown, never inferred from prose.
                System.Diagnostics.Trace.WriteLine("OpenCode status connection could not be initialized: " + ex.GetType().Name);
            }
        }
        _browser.AllowExternalDrop = false;
        if (_useNativeTerminalSurface)
        {
            _nativeTerminalEmulator = new TerminalEmulator(_usesNativeOpenCodeTui ? 40 : 80, _usesNativeOpenCodeTui ? 24 : 24, 5000);
            _nativeTerminalSurface = new NativeTerminalSurface(_nativeTerminalEmulator);
            _nativeTerminalEmulator.Response += response =>
            {
                Exception? error = HandleNativeTerminalResponse(response);
                if (error is not null) StatusChanged?.Invoke("터미널 응답: " + error.Message);
            };
            _nativeTerminalSurface.Input += HandleNativeSurfaceInput;
            _nativeTerminalSurface.InputFailed += ReportNativeInputFailure;
            // Stage codes only (no typed content) so a 문제 보내기 report shows when Hangul input stalled.
            _nativeTerminalSurface.ImeRecovered += code =>
            {
                ReportLifecycleStage(code);
                StatusChanged?.Invoke("한글 입력 연결을 복구했습니다 · 계속 입력하세요.");
            };
            _nativeTerminalSurface.ImeStage += ReportLifecycleStage;
            _nativeTerminalSurface.DimensionsChanged += HandleNativeSurfaceDimensionsChanged;
            _nativeTerminalSurface.Activated += () => ActivatedByUser?.Invoke();
            _nativeTerminalSurface.PasteRequested += PasteFromClipboard;
            _nativeTerminalSurface.CopyRequested += CopySelection;
            _nativeTerminalSurface.ContextMenuRequested += hasSelection => ContextMenuRequested?.Invoke(hasSelection);
            _nativeTerminalSurface.Shortcut += (key, shift) => Shortcut?.Invoke(key, shift);
            _nativeTerminalSurface.TextSendCompleted += HandleTerminalSendTextAck;
            if (_usesNativeOpenCodeTui)
                Children.Add(NativeTerminalSurface.CreateScrollableViewport(_nativeTerminalSurface));
            else
                Children.Add(_nativeTerminalSurface);
            _browser.Visibility = Visibility.Collapsed;
        }
        else Children.Add(_browser);
        Children.Add(_notice);
        InitializeServerStatus();
        ApplyActivityVisuals();
        // Native console output must not wait behind an indefinitely non-idle window message queue.
        // Each tick posts one output batch, with acknowledgement backpressure.
        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(32), DispatcherPriority.Loaded, (_, _) => FlushOutput(), Dispatcher);
        _activityTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            if (!_usesNativeOpenCodeStatus) _activityTracker.Tick();
            else
            {
                bool recent = _openCodeStatus?.HasRecentActivity == true;
                if (recent != _lastNativeRecentActivity)
                {
                    _lastNativeRecentActivity = recent;
                    ApplyActivityChange();
                }
            }
        }, Dispatcher);
        _activityTracker.ActivityChanged += OnActivityTrackerChanged;
        _inputCapture.Submitted += text =>
        {
            if (text.TrimStart().StartsWith('/')) _autoResume?.Suspend("세션 명령 입력 · 자동 진행 대기");
            if (!string.IsNullOrWhiteSpace(text)) ClearWorkFailed();
            _manualInputOverride = false;
            _lastManualSubmissionObservation = null;
            if (!string.IsNullOrWhiteSpace(text) && !text.TrimStart().StartsWith('/'))
                _lastManualSubmissionObservation = _openCodeStatus?.ObserveSubmission();
            if (_usesNativeOpenCodeTui)
                ObserveOpenCodeTuiManualSubmission(text);
            InputSubmitted?.Invoke(text);
        };
        _tokenUsage.Changed += OnTokenUsageChanged;
        _activityTimer.Start();
        WireTerminalSession(_session);
        Loaded += (_, _) =>
        {
            _loadedCompletion.TrySetResult(null);
            StartWebViewInitialization();
            TryStartTerminalWhenLaidOut();
        };
        SizeChanged += (_, _) => TryStartTerminalWhenLaidOut();
        IsVisibleChanged += (_, _) => TryStartTerminalWhenLaidOut();
    }

    private void WireTerminalSession(ConPtySession session)
    {
        session.LifecycleChanged += lifecycle => { if (ReferenceEquals(session, _session)) OnSessionLifecycleChanged(lifecycle); };
        session.InputFailed += failure => OnInputFailed(session, failure);
        session.Exited += code =>
        {
            if (_disposed || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(() => HandleTerminalSessionExit(session, code));
        };
        session.OutputReceived += text =>
        {
            if (!ReferenceEquals(session, _session) || _disposed) return;
            ObserveTuiOutput(session, text);
            _tokenUsage.Observe(text);
            if (_useNativeTerminalSurface)
            {
                if (!_disposed && !_failed) _nativeTerminalSurface?.Feed(text);
                return;
            }
            lock (_outputLock)
            {
                if (_disposed || _failed) return;
                if (_queuedCharacters > 8_000_000)
                {
                    _pending.Clear(); _queuedCharacters = 0;
                    _pending.Enqueue("\x1b[0m\r\n[화면 출력이 너무 많아 일부 출력을 생략했습니다]\r\n");
                }
                _pending.Enqueue(text); _queuedCharacters += text.Length;
            }
        };
    }

    private void HandleTerminalSessionExit(ConPtySession session, int code)
    {
            if (_disposed || !ReferenceEquals(session, _session)) return;
            ReportLifecycleStage("session_exited");
            if (_usesNativeOpenCodeTui)
                HandleOpenCodeTuiExit(code);
            else if (!_usesNativeOpenCodeStatus)
            {
                CaptureUnexpectedProcessRecovery();
                _autoResume?.Dispose();
                _activityTracker.MarkExited();
            }
            if (!_usesNativeOpenCodeTui) _openCodeStatus?.Dispose();
            FailPendingTextSends(new InvalidOperationException("The terminal process exited."));
            if (!_usesNativeOpenCodeTui) StatusChanged?.Invoke($"터미널 종료 · 코드 {code}");
            ProcessExited?.Invoke(code);
    }

    /// <summary>
    /// Waits until this pane has been loaded, laid out, and its ConPTY process
    /// has started. This is the launch handoff for workflows that create a pane
    /// in a lazy tab. Native OpenCode status readiness is checked separately.
    /// </summary>
    internal async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        if (Dispatcher.CheckAccess() && IsLoaded)
        {
            _loadedCompletion.TrySetResult(null);
            StartWebViewInitialization();
            TryStartTerminalWhenLaidOut();
        }

        Exception? loadFailure = await _loadedCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (loadFailure is not null)
            ExceptionDispatchInfo.Capture(loadFailure).Throw();

        Exception? startupFailure = await _terminalStartedCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (startupFailure is not null)
            ExceptionDispatchInfo.Capture(startupFailure).Throw();
    }

    private void StartWebViewInitialization()
    {
        if (_disposed || _failed || _initializeTask is not null) return;
        if (_useNativeTerminalSurface)
        {
            _browser.Visibility = Visibility.Collapsed;
            _nativeTerminalSurface!.Visibility = Visibility.Visible;
            _initializeTask = Task.CompletedTask;
            return;
        }
        _initializeTask = InitializeAsync();
    }

    private void TryStartTerminalWhenLaidOut()
    {
        if (_disposed || _failed || _ready ||
            (_usesNativeOpenCodeStatus && !_usesNativeOpenCodeTui && !_nativeViewReady) ||
            (_usesNativeOpenCodeTui || !_usesNativeOpenCodeStatus) && _pendingWebReadyDimensions is null ||
            !IsLoaded || (!IsVisible && !_allowBackgroundAutomationStartup) || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        if (_usesNativeOpenCodeStatus)
        {
            if (_usesNativeOpenCodeTui)
                _openCodeTuiInitialDimensions = _pendingWebReadyDimensions;
            _ = StartHeadlessOpenCodeAsync();
            _pendingWebReadyDimensions = null;
            return;
        }

        var dimensions = _pendingWebReadyDimensions!.Value;

        try
        {
            _activityTracker.MarkStarted(_startupWork);
            ReportLifecycleStage("session_start_requested");
            _session.Start(
                WorkingDirectory,
                dimensions.Columns,
                dimensions.Rows,
                LaunchesCodex,
                _effectiveStartupCommand,
                _openCodeConfigContent,
                // Native readiness must follow OpenCode's lifetime. The view stays
                // visible after exit, but an idle PowerShell host must not look live.
                exitOnStartupCommandCompletion: _usesNativeOpenCodeStatus);
            _openCodeStatus?.Start();
            _pendingWebReadyDimensions = null;
            _ready = true;
            FlushHeldStartupInput();
            _notice.Visibility = Visibility.Collapsed;
            ApplyActivityVisuals();
            _terminalStartedCompletion.TrySetResult(null);
            ReportLifecycleStage("session_started");
            Ready?.Invoke();
            StatusChanged?.Invoke("터미널 준비 완료 · " + WorkingDirectory);
        }
        catch (Exception exception)
        {
            _pendingWebReadyDimensions = null;
            _terminalStartedCompletion.TrySetResult(exception);
            ShowError("터미널 시작 실패: " + exception.Message);
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(OpenCodeProjectContext.DataDirectory, "WebView2"));
            if (_disposed) return;
            await _browser.EnsureCoreWebView2Async(env);
            if (_disposed) return;
            var core = _browser.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("terminalnotes.local", Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith(Origin, StringComparison.Ordinal)) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.WebMessageReceived += OnWebMessage;
            core.NavigationCompleted += (_, e) => { if (!e.IsSuccess) ShowError("터미널 화면을 열 수 없습니다: " + e.WebErrorStatus); };
            core.ProcessFailed += (_, _) =>
            {
                if (_disposed) return;
                _failed = true;
                _flushTimer.Stop();
                FailPendingTextSends(new InvalidOperationException("The terminal view process failed."));
                BeginSessionClose("webview_failed");
                ShowError("터미널 화면 오류로 이 세션을 종료했습니다. 새 터미널을 열어주세요.");
                ProcessExited?.Invoke(-1);
            };
            core.Navigate(Origin + (_usesNativeOpenCodeStatus ? "opencode.html" : "terminal.html" + (_protectTuiViewport ? "?tui=true" : "")));
        }
        catch (Exception ex) { if (!_disposed) ShowError("터미널 시작 실패. Microsoft Edge WebView2 Runtime 설치를 확인하세요.\n" + ex.Message); }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed || _failed || !e.Source.StartsWith(Origin, StringComparison.Ordinal)) return;
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            if (_usesNativeOpenCodeStatus)
            {
                HandleNativeWebMessage(root);
                return;
            }
            switch (root.GetProperty("type").GetString())
            {
                case "ready":
                    if (_ready || _pendingWebReadyDimensions is not null) return;
                    _pendingWebReadyDimensions = (Size(root, "cols"), Size(root, "rows"));
                    TryStartTerminalWhenLaidOut();
                    break;
                case "input":
                    if (_ready)
                    {
                        string input = root.GetProperty("data").GetString() ?? "";
                        bool isProgrammatic = root.TryGetProperty("programmatic", out var programmatic) && programmatic.ValueKind == JsonValueKind.True;
                        long inputSendId = 0;
                        bool hasSendId = root.TryGetProperty("sendId", out var sendElement) && sendElement.TryGetInt64(out inputSendId);
                        Exception? inputError = HandleTerminalInput(input, isProgrammatic, hasSendId ? inputSendId : null);
                        if (inputError is not null) throw inputError;
                    }
                    break;
                case "sendTextAck":
                    long ackSendId = root.GetProperty("id").GetInt64();
                    Exception? ackError = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                        ? new InvalidOperationException(error.GetString()) : null;
                    HandleTerminalSendTextAck(ackSendId, ackError);
                    break;
                case "resize":
                    short columns = Size(root, "cols");
                    short rows = Size(root, "rows");
                    if (_ready)
                    {
                        ReportLifecycleStage("resize_requested");
                        _session.RequestResize(columns, rows);
                    }
                    else if (_pendingWebReadyDimensions is not null)
                    {
                        _pendingWebReadyDimensions = (columns, rows);
                        TryStartTerminalWhenLaidOut();
                    }
                    break;
                case "drained": _writeInFlight = false; break;
                case "activityTail":
                    if (_usesNativeOpenCodeStatus) break;
                    _activityTracker.ObserveRenderedScreen(
                        root.GetProperty("data").GetString() ?? "",
                        root.GetProperty("screen").GetString() ?? "",
                        root.GetProperty("layout").GetString() ?? "",
                        root.TryGetProperty("promptAtCursor", out var promptAtCursor) && promptAtCursor.ValueKind == JsonValueKind.True);
                    break;
                case "activated": ActivatedByUser?.Invoke(); break;
                case "contextMenu": ContextMenuRequested?.Invoke(root.TryGetProperty("hasSelection", out var selection) && selection.ValueKind == JsonValueKind.True); break;
                case "copy": CopyText(root.GetProperty("data").GetString()); break;
                case "paste": PasteFromClipboard(); break;
                case "shortcut": Shortcut?.Invoke(root.GetProperty("key").GetString() ?? "", root.GetProperty("shift").GetBoolean()); break;
            }
        }
        catch (Exception ex) { StatusChanged?.Invoke("터미널: " + ex.Message); if (!_ready) ShowError(ex.Message); }
    }
    private static short Size(JsonElement root, string name) => (short)Math.Clamp(root.GetProperty(name).GetInt32(), 2, 500);
    private void ShowError(string text)
    {
        CaptureUnexpectedProcessRecovery();
        _openCodeStatus?.Dispose();
        _activityTracker.MarkError();
        var error = new InvalidOperationException(text);
        _loadedCompletion.TrySetResult(error);
        _terminalStartedCompletion.TrySetResult(error);
        _notice.Text = text;
        _notice.Visibility = Visibility.Visible;
        _browser.Visibility = Visibility.Collapsed;
        if (_nativeTerminalSurface is not null) _nativeTerminalSurface.Visibility = Visibility.Collapsed;
        StatusChanged?.Invoke(text);
        UserWarning?.Invoke(text);
    }
    private void FlushOutput()
    {
        if (!_ready || _disposed || _failed || _writeInFlight) return;
        var chunk = new StringBuilder(_retryOutput);
        lock (_outputLock)
        {
            while (_retryOutput is null && _pending.Count > 0 && chunk.Length < 65536) { var text = _pending.Dequeue(); _queuedCharacters -= text.Length; chunk.Append(text); }
        }
        if (chunk.Length == 0) return;
        _writeInFlight = true;
        var output = chunk.ToString();
        if (Post(new { type = "output", data = output })) { _retryOutput = null; _outputPostFailures = 0; }
        else
        {
            _writeInFlight = false;
            _retryOutput = output;
            if (++_outputPostFailures >= 3)
            {
                _failed = true; _flushTimer.Stop();
                BeginSessionClose("webview_post_failed");
                ShowError("터미널 화면과 연결이 끊겨 이 세션을 종료했습니다. 새 터미널을 열어주세요.");
            }
        }
    }
    private bool Post(object message)
    {
        if (_disposed || _browser.CoreWebView2 is null) return false;
        try { _browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message)); return true; }
        catch (Exception ex) { StatusChanged?.Invoke("터미널 화면: " + ex.Message); return false; }
    }
    private void OnActivityTrackerChanged()
    {
        if (_disposed) return;
        if (Dispatcher.CheckAccess()) ApplyActivityChange();
        else Dispatcher.BeginInvoke(() => { if (!_disposed) ApplyActivityChange(); });
    }
    private void OnSessionLifecycleChanged(ConPtyLifecycleEvent lifecycleEvent)
    {
        string stage = lifecycleEvent.Operation + "_" + lifecycleEvent.Phase;
        if (lifecycleEvent.ExceptionType is not null)
            stage += "_" + lifecycleEvent.ExceptionType;
        ReportLifecycleStage(stage);
        if (lifecycleEvent.Phase == "faulted")
        {
            string exceptionType = lifecycleEvent.ExceptionType ?? "UnknownException";
            try { LifecycleFailureReported?.Invoke(lifecycleEvent.Operation, exceptionType, stage); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Terminal lifecycle fault observer failed: " + ex.GetType().Name); }
        }
    }

    private void ReportLifecycleStage(string stage)
    {
        _lastLifecycleStage = stage;
        try { LifecycleStageChanged?.Invoke(stage); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Terminal lifecycle observer failed: " + ex.GetType().Name); }
    }

    private void BeginSessionClose(string reason)
    {
        ReportLifecycleStage(reason);
        if (Interlocked.Exchange(ref _sessionCloseStarted, 1) != 0) return;
        if (_usesNativeOpenCodeStatus)
        {
            try { _headlessLifetimeCancellation.Cancel(); } catch (ObjectDisposedException) { }
            try { _headlessManualDispatchCancellation?.Cancel(); } catch (ObjectDisposedException) { }
            _ = ObserveOpenCodeCloseAsync();
            return;
        }
        try { _ = ObserveSessionCloseAsync(_session.CloseAsync()); }
        catch (Exception ex)
        {
            ReportLifecycleStage("close_request_failed_" + ex.GetType().Name);
            System.Diagnostics.Trace.WriteLine("Terminal cleanup request failed: " + ex.GetType().Name);
        }
    }

    private async Task ObserveOpenCodeCloseAsync()
    {
        try
        {
            await _session.CloseAsync().ConfigureAwait(false);
            _headlessOpenCodeHost?.Dispose();
        }
        catch (Exception ex)
        {
            _headlessOpenCodeHost?.Dispose();
            ReportLifecycleStage("opencode_close_failed_" + ex.GetType().Name);
            System.Diagnostics.Trace.WriteLine("OpenCode cleanup failed: " + ex.GetType().Name);
        }
    }

    private async Task ObserveSessionCloseAsync(Task closeTask)
    {
        try { await closeTask.ConfigureAwait(false); }
        catch (Exception ex)
        {
            ReportLifecycleStage("close_failed_" + ex.GetType().Name);
            System.Diagnostics.Trace.WriteLine("Terminal cleanup failed: " + ex.GetType().Name);
        }
    }

    private void OnTokenUsageChanged()
    {
        if (_disposed) return;
        if (Dispatcher.CheckAccess()) TokenUsageChanged?.Invoke();
        else Dispatcher.BeginInvoke(() => { if (!_disposed) TokenUsageChanged?.Invoke(); });
    }
    private void FailPendingTextSends(Exception error)
    {
        foreach (SavedPlanDispatch dispatch in _savedPlanDispatches.Values.ToArray())
            CancelSavedPlanDispatch(dispatch, error);
        foreach (var completion in _pendingTextSends.Values) completion.TrySetException(error);
        _pendingTextSends.Clear();
    }
    private void OnInputFailed(ConPtySession session, Exception error)
    {
        void Handle()
        {
            if (_disposed || _failed || _sessionCloseStarted != 0 ||
                !ReferenceEquals(session, _session) || _tuiConnecting) return;
            _failed = true;
            _flushTimer.Stop();
            FailPendingTextSends(new IOException("Terminal input could not be written.", error));
            BeginSessionClose("input_failed");
            ShowError("터미널 입력 연결에 실패해 세션을 종료했습니다. 새 터미널을 열어주세요.\n" + error.Message);
        }

        if (Dispatcher.CheckAccess()) Handle();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke((Action)Handle);
    }

    private void ApplyActivityChange()
    {
        ApplyActivityVisuals();
        ActivityChanged?.Invoke();
    }
    private void ApplyActivityVisuals()
    {
        string color = TerminalActivityPalette.Background(Activity, HasCompletedWork);
        string foreground = TerminalActivityPalette.Foreground(Activity, HasCompletedWork);
        var mediaColor = System.Windows.Media.Color.FromRgb(
            Convert.ToByte(color[1..3], 16),
            Convert.ToByte(color[3..5], 16),
            Convert.ToByte(color[5..7], 16));
        Background = new SolidColorBrush(mediaColor);
        _browser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(
            mediaColor.R,
            mediaColor.G,
            mediaColor.B);
        if (_ready)
        {
            bool useDarkAnsi = string.Equals(color, "#FF8A00", StringComparison.OrdinalIgnoreCase);
            Post(new
            {
                type = "theme",
                background = color,
                foreground,
                cursor = foreground,
                selectionBackground = SelectionBackground(color),
                selectionForeground = foreground,
                useDarkAnsi,
                forceStateBackground = _protectTuiViewport
            });
            _nativeTerminalSurface?.SetTheme(color, foreground, SelectionBackground(color), _protectTuiViewport);
        }
    }
    private static string SelectionBackground(string background) => background.ToUpperInvariant() switch
    {
        "#FF8A00" => "#FFC47A",
        "#00853F" => "#00572D",
        _ => "#43536B"
    };
    public void FocusTerminal()
    {
        if (!_ready || _disposed || _failed) return;
        if (_useNativeTerminalSurface)
        {
            _nativeTerminalSurface?.FocusSurface();
            return;
        }
        _browser.Focus();
        Post(new { type = "focus" });
    }
    public bool CanPasteFromClipboard
    {
        get
        {
            if (IsUserInputBlocked) return false;
            try { return Clipboard.ContainsText(); }
            catch { return false; }
        }
    }
    public void CopySelection()
    {
        if (_useNativeTerminalSurface) { CopyText(_nativeTerminalSurface?.SelectedText); return; }
        PostTerminalCommand("copySelection");
    }
    public void SelectAll()
    {
        if (_useNativeTerminalSurface) { _nativeTerminalSurface?.SelectAllText(); return; }
        PostTerminalCommand("selectAll");
    }
    public void ClearSelection()
    {
        if (_useNativeTerminalSurface) { _nativeTerminalSurface?.ClearSelectionText(); return; }
        PostTerminalCommand("clearSelection");
    }
    public void ClearScreen()
    {
        if (_useNativeTerminalSurface) { _nativeTerminalSurface?.ClearTerminal(); return; }
        PostTerminalCommand("clear");
    }
    public void PasteFromClipboard()
    {
        try
        {
            if (IsUserInputBlocked) return;
            if (IgnoredBeforePlanning("paste", isProgrammatic: false, sendId: null)) return;
            if (!Clipboard.ContainsText()) return;
            string paste = Clipboard.GetText();
            if (paste.Length > 200_000)
            {
                ReportUserWarning("붙여넣기는 200,000자 이하로 나눠주세요.");
                return;
            }
            if (_usesNativeOpenCodeStatus && !_usesNativeOpenCodeTui)
            {
                PostNative(new { type = "paste", data = paste });
                return;
            }
            if ((paste.Contains('\n') || paste.Contains('\r')) &&
                MessageBox.Show(Window.GetWindow(this), "여러 줄을 터미널에 붙여넣으면 명령이 실행될 수 있습니다. 계속할까요?", "여러 줄 붙여넣기", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            if (_useNativeTerminalSurface)
            {
                _nativeTerminalSurface?.PasteText(paste);
                return;
            }
            Post(new { type = "paste", data = paste });
        }
        catch (Exception ex) { ReportUserWarning("클립보드: " + ex.Message); }
    }
    private void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch (Exception ex) { ReportUserWarning("클립보드: " + ex.Message); }
    }
    private void PostTerminalCommand(string type)
    {
        if (!Post(new { type })) ReportUserWarning("터미널 화면이 아직 준비되지 않았습니다.");
        else FocusTerminal();
    }
    public Task SendTextAsync(string text, bool submit = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!Dispatcher.CheckAccess()) return Dispatcher.InvokeAsync(() => SendTextAsync(text, submit)).Task.Unwrap();
        if (!IsReady) throw new InvalidOperationException("The terminal is not ready.");

        if (_usesNativeOpenCodeStatus && !_usesNativeOpenCodeTui)
        {
            if (submit) return SendNativeUiPromptAsync(Guid.NewGuid().ToString("N"), text, null, null);
            if (!PostNative(new { type = "nativeSetInput", text }))
                throw new InvalidOperationException("Could not send text to the OpenCode composer.");
            return Task.CompletedTask;
        }

        if (_useNativeTerminalSurface)
        {
            _inputCapture.PrepareForProgrammaticSubmission();
            long nativeId = ++_nextTextSendId;
            SavedPlanDispatch? nativeExpectedDispatch = _savedPlanDispatchForNextSend;
            SavedPlanDispatch? nativeAttachedDispatch = AttachSavedPlanDispatch(nativeId, text, submit);
            if (nativeExpectedDispatch is not null && !ReferenceEquals(nativeExpectedDispatch, nativeAttachedDispatch))
                throw new InvalidOperationException("Could not safely track the saved plan submission.");
            var nativeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingTextSends.Add(nativeId, nativeCompletion);
            ClearWorkFailed();
            _nativeTerminalSurface!.SendText(text, submit, nativeId);
            return AwaitTextSendAsync(nativeId, nativeCompletion.Task);
        }

        _inputCapture.PrepareForProgrammaticSubmission();
        long id = ++_nextTextSendId;
        SavedPlanDispatch? expectedDispatch = _savedPlanDispatchForNextSend;
        SavedPlanDispatch? attachedDispatch = AttachSavedPlanDispatch(id, text, submit);
        if (expectedDispatch is not null && !ReferenceEquals(expectedDispatch, attachedDispatch))
            throw new InvalidOperationException("Could not safely track the saved plan submission.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingTextSends.Add(id, completion);
        if (!Post(new { type = "sendText", id, data = text, submit }))
        {
            _pendingTextSends.Remove(id);
            if (_savedPlanDispatches.TryGetValue(id, out SavedPlanDispatch? dispatch))
                CancelSavedPlanDispatch(dispatch, new InvalidOperationException("Could not send text to the terminal view."));
            CompleteSavedPlanDispatch(id);
            throw new InvalidOperationException("Could not send text to the terminal view.");
        }
        ClearWorkFailed();
        return AwaitTextSendAsync(id, completion.Task);
    }
    private async Task AwaitTextSendAsync(long id, Task acknowledgement)
    {
        try { await acknowledgement.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException timeout)
        {
            if (Dispatcher.CheckAccess()) _pendingTextSends.Remove(id);
            else await Dispatcher.InvokeAsync(() => _pendingTextSends.Remove(id));
            throw new TimeoutException("Terminal input acknowledgement timed out; the text may have been submitted and was not sent again.", timeout);
        }
    }
    public void ObserveTokenUsage(string text) => _tokenUsage.Observe(text);
    public void MarkWorkCompleted()
    {
        _openCodeStatus?.ConfirmOwnedTurnCompleted();
        bool changed = _workFailed;
        _workFailed = false;
        _activityTracker.MarkWorkCompleted();
        if (changed) ApplyActivityChange();
    }
    internal void MarkWorkFailed()
    {
        if (_disposed || _workFailed) return;
        _workFailed = true;
        ApplyActivityChange();
    }
    internal void SetAutomationBusy(bool busy)
    {
        if (_disposed) return;
        if (!busy) SetNativeAutomationSupervisionNotice(null);
        bool changed = _automationBusy != busy || _nativeAutomationActive != busy;
        _nativeAutomationActive = busy;
        if (busy && _workFailed)
        {
            _workFailed = false;
            changed = true;
        }
        if (!changed) return;
        _automationBusy = busy;
        ApplyActivityChange();
    }
    private void ClearWorkFailed()
    {
        if (!_workFailed) return;
        _workFailed = false;
        ApplyActivityChange();
    }
    public void MarkNeedsInput() { _manualInputOverride = true; _activityTracker.MarkNeedsInput(); ApplyActivityChange(); }
    public void ClearActivityOverride() { _manualInputOverride = false; _activityTracker.ClearActivityOverride(); ApplyActivityChange(); }
    public void Interrupt()
    {
        CancelNativeAutomation();
        if (_usesNativeOpenCodeStatus)
        {
            try { _headlessManualDispatchCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        }
        _autoResume?.Suspend("사용자 중단 · 자동 진행 멈춤");
        CancelIncompleteSavedPlanDispatches();
        _inputCapture.Observe("\x03");
        ClearSavedPlanComposerDraftAfterInterrupt();
        if (_usesNativeOpenCodeTui) _openCodeTuiManualText = null;
        if (_usesNativeOpenCodeTui)
        {
            // The app stop action targets its verified task session. Keyboard
            // shortcuts inside OpenCode continue to be handled by the TUI.
            _ = RunNativeUiOperationAsync(AbortNativeSessionAsync);
            FocusTerminal();
            return;
        }
        if (_usesNativeOpenCodeStatus && !_usesNativeOpenCodeTui)
        {
            _ = RunNativeUiOperationAsync(AbortNativeSessionAsync);
            return;
        }
        if (IsRunning) _session.Write("\x03");
        FocusTerminal();
    }

    /// <summary>
    /// Updates the linked Markdown path exposed to this native OpenCode terminal.
    /// OpenCode rereads the app-owned instruction file before each model request.
    /// </summary>
    public void UpdatePlanDocumentContext(string? path, string? projectName = null)
    {
        // Stopped role tabs may remain visible until their replacement is created.
        // Project-wide name/plan updates must not reopen their disposed context.
        if (_disposed) return;
        _openCodeProjectContext?.UpdatePlanDocumentContext(path, projectName);
        if (!string.IsNullOrWhiteSpace(projectName))
            _openCodeSessionTitle = ProjectSessionTitle(WorkingDirectory, projectName);
        BindSavedPlanExecutionPath(path);
        _autoResume?.UpdatePlan(path);
    }

    private static string ProjectSessionTitle(string workingDirectory, string? projectName)
    {
        string? folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory)));
        string title = !string.IsNullOrWhiteSpace(projectName) ? projectName.Trim() :
            !string.IsNullOrWhiteSpace(folder) ? folder : Path.GetFullPath(workingDirectory);
        return title.Length <= 256 ? title : title[..256];
    }

    // ── Server status per terminal (2026-11-11 사용자: "서버문제인지 프로그램 문제인지 중간에 터미널창에서 아예 입력이 안되는 경우도
    // 많은데 서버의 상태를 터미널마다 표시할수있나"). Every OpenCode terminal has a dot in its top-right corner: green when its server
    // answers and input is open (the tooltip has the response time and states); otherwise the dot grows into a short label with the
    // reason: 서버 꺼짐, 서버 응답 없음, 화면 연결 끊김, 입력 막힘, 연결 중, 준비 중, 서버 느림. The server is asked every 5 seconds
    // (GET /session/status, 4 s limit) through this terminal's own connection; local states are read every second.
    private Border? _serverStatusChip;
    private TextBlock? _serverStatusText;
    private global::System.Windows.Shapes.Ellipse? _serverStatusDot;
    private DispatcherTimer? _serverStatusTimer;
    private int _serverStatusTicks;
    private bool _serverProbeRunning;
    private int _serverProbeFailures;
    private double? _serverLatencyMs;
    private DateTimeOffset? _serverLastAnswer;
    private static readonly Brush ServerOkBrush = Frozen(Color.FromRgb(0x3D, 0xD6, 0x8C));
    private static readonly Brush ServerWarnBrush = Frozen(Color.FromRgb(0xF0, 0xC8, 0x6E));
    private static readonly Brush ServerDownBrush = Frozen(Color.FromRgb(0xFF, 0x8A, 0x80));

    /// <summary>The label the corner shows (empty while healthy), for tests and accessibility</summary>
    internal string ServerStatusLabel => _serverStatusText?.Text ?? "";

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void InitializeServerStatus()
    {
        if (!_usesNativeOpenCodeStatus || _serverStatusChip is not null) return;
        _serverStatusDot = new global::System.Windows.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        _serverStatusText = new TextBlock { FontSize = 11.5, Margin = new Thickness(6, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_serverStatusDot);
        row.Children.Add(_serverStatusText);
        _serverStatusChip = new Border
        {
            Child = row, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 8, 0), Padding = new Thickness(5, 3, 5, 3), CornerRadius = new CornerRadius(9),
            Background = Frozen(Color.FromArgb(0xD8, 0x14, 0x14, 0x14)), Cursor = global::System.Windows.Input.Cursors.Help
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_serverStatusChip, "TerminalServerStatus");
        Panel.SetZIndex(_serverStatusChip, 50);
        Children.Add(_serverStatusChip);
        _serverStatusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, async (_, _) =>
        {
            if (_serverStatusTicks++ % 5 == 0) await ProbeServerAsync();
            else UpdateServerStatus();
        }, Dispatcher);
        _serverStatusTimer.Start();
        UpdateServerStatus();
    }

    private async Task ProbeServerAsync()
    {
        if (_disposed || _serverProbeRunning || !IsVisible || _headlessOpenCodeClient is not { } client || _headlessOpenCodeHost?.IsRunning != true)
        {
            UpdateServerStatus();
            return;
        }
        _serverProbeRunning = true;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(HeadlessLifetimeCancellation);
            limit.CancelAfter(TimeSpan.FromSeconds(4));
            var watch = global::System.Diagnostics.Stopwatch.StartNew();
            await client.ReadStatusAsync(limit.Token);
            _serverLatencyMs = watch.Elapsed.TotalMilliseconds;
            _serverProbeFailures = 0;
            _serverLastAnswer = DateTimeOffset.Now;
        }
        catch (Exception exception) when (exception is OperationCanceledException or global::System.Net.Http.HttpRequestException or IOException or
            InvalidDataException or ObjectDisposedException or JsonException or InvalidOperationException)
        {
            if (!_disposed) _serverProbeFailures++;
        }
        finally { _serverProbeRunning = false; }
        UpdateServerStatus();
    }

    private void UpdateServerStatus()
    {
        if (_serverStatusChip is null || _serverStatusText is null || _serverStatusDot is null || _disposed) return;
        bool serverUp = _headlessOpenCodeHost?.IsRunning == true;
        bool screenLost = _usesNativeOpenCodeTui && (HasTuiConnectionFailure || serverUp && IsReady && !_session.IsRunning);
        (string? label, Brush brush) = _failed ? ("터미널 오류", ServerDownBrush)
            : !serverUp && _headlessOpenCodeClient is not null ? ("서버 꺼짐", ServerDownBrush)
            : _serverProbeFailures >= 2 ? ("서버 응답 없음", ServerDownBrush)
            : screenLost ? ("화면 연결 끊김", ServerDownBrush)
            : _planMakerStartupHold ? ("연결 중 · 첫 질문 뒤 입력", ServerWarnBrush)
            : IsUserInputBlocked ? ("입력 막힘 · 응답 처리 중", ServerWarnBrush)
            : !IsReady || !serverUp ? ("준비 중", ServerWarnBrush)
            : _serverLatencyMs is > 2000 and var slow ? ($"서버 느림 {slow / 1000:0.0}초", ServerWarnBrush)
            : ((string?)null, ServerOkBrush);
        _serverStatusDot.Fill = brush;
        _serverStatusText.Text = label ?? "";
        _serverStatusText.Foreground = brush;
        _serverStatusText.Visibility = label is null ? Visibility.Collapsed : Visibility.Visible;
        string server = !serverUp ? (_headlessOpenCodeClient is null ? "시작 전" : "꺼짐")
            : _serverProbeFailures > 0 ? $"응답 없음 (연속 {_serverProbeFailures}번)"
            : _serverLatencyMs is { } latency ? $"정상 · 응답 {latency:0}ms" + (_serverLastAnswer is { } at ? $" ({at:HH:mm:ss})" : "") : "확인 중";
        string work = NativeOpenCodeStatus switch
        {
            OpenCodeStatusKind.Idle => "대기", OpenCodeStatusKind.Busy => "작업 중", OpenCodeStatusKind.NeedsInput => "답변·승인 대기",
            OpenCodeStatusKind.Pending => "전달 중", _ => "확인 중"
        };
        string input = label is "입력 막힘 · 응답 처리 중" or "연결 중 · 첫 질문 뒤 입력" or "준비 중" ? label
            : screenLost || label is "서버 꺼짐" or "서버 응답 없음" or "터미널 오류" ? "안 됨" : "가능";
        string tip = $"OpenCode 서버: {server}\n작업: {work}\n" + (_usesNativeOpenCodeTui ? $"화면: {(screenLost ? "끊김" : "연결됨")}\n" : "") + $"입력: {input}";
        _serverStatusChip.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(_serverStatusChip, "서버 상태 · " + (label ?? "정상") + " · " + tip.Replace('\n', ' '));
    }

    public void Dispose()
    {
        if (_disposed) return;
        var disposedError = new ObjectDisposedException(nameof(TerminalPane));
        _loadedCompletion.TrySetResult(disposedError);
        _terminalStartedCompletion.TrySetResult(disposedError);
        _automationBusy = false;
        _workFailed = false;
        CancelNativeAutomation();
        IsUserInputBlocked = true;
        _disposed = true; _flushTimer.Stop(); _activityTimer.Stop(); _nativeRefreshTimer?.Stop(); _nativeActivityRefreshTimer?.Stop(); _serverStatusTimer?.Stop();
        BeginSessionClose("pane_disposed");
        _autoResume?.Dispose();
        _openCodeStatus?.Dispose();
        foreach (var completion in _pendingTextSends.Values) completion.TrySetCanceled();
        _pendingTextSends.Clear();
        _activityTracker.MarkExited();
        _openCodeProjectContext?.Dispose();
        _headlessOpenCodeClient?.Dispose();
        _nativeTerminalSurface?.DisposeSurface();
        _browser.Dispose();
    }
}
}
#endregion
#endif

#if TN_WPF || TN_SOURCE_TERMINALPANE_ERRORRETRY_CS
#region TerminalPane.ErrorRetry.cs
namespace TerminalNotes
{
using global::TerminalNotes.Services;


public sealed partial class TerminalPane
{
    internal event Action<OpenCodeErrorRestart>? UnexpectedProcessRecovery;
    private OpenCodeErrorRestart? _processErrorSnapshot;
    private long _processErrorInputVersion;
    private long _processErrorSubmissionGeneration;
    internal bool CanRetryError => CanRetryOpenCodeTuiConnection || !IsPlanMakerActive && !_disposed && !_failed && !IsUserInputBlocked &&
        !HasPendingStartupSubmission && !HasLocalPendingInput() && !_manualInputOverride && _autoResume?.CanRetryError == true;
    internal DateTimeOffset? ErrorRetryAt => _tuiConnectionFailed ? _tuiReconnectAt : _autoResume?.ErrorRetryAt;

    internal Task<bool> RetryErrorAsync() => CanRetryOpenCodeTuiConnection ? RetryOpenCodeTuiConnectionAsync()
        : CanRetryError && _autoResume is { } recovery
        ? recovery.RetryErrorAsync() : Task.FromResult(false);

    private void CaptureUnexpectedProcessRecovery()
    {
        if (_processErrorSnapshot is not null && NativeSubmissionGeneration == _processErrorSubmissionGeneration || _disposed || IsPlanMakerActive || IsUserInputBlocked ||
            _manualInputOverride || HasPendingStartupSubmission || HasLocalPendingInput() || WasContinuedExternally ||
            NativeOpenCodeStatus == OpenCodeStatusKind.NeedsInput) return;
        if (_autoResume?.GetConfirmedRecoverySnapshot() is not { } snapshot) return;
        _processErrorSnapshot = snapshot;
        _processErrorInputVersion = _savedPlanComposerInterruptVersion;
        _processErrorSubmissionGeneration = NativeSubmissionGeneration;
        UnexpectedProcessRecovery?.Invoke(snapshot);
    }

    internal bool CanRecoverProcessError(OpenCodeErrorRestart request) => !_disposed &&
        ReferenceEquals(request, _processErrorSnapshot) && request.CanRestart() && !IsPlanMakerActive &&
        !IsUserInputBlocked && !_manualInputOverride && !HasPendingStartupSubmission && !HasLocalPendingInput() &&
        !WasContinuedExternally && _savedPlanComposerInterruptVersion == _processErrorInputVersion &&
        NativeSubmissionGeneration == _processErrorSubmissionGeneration;

    internal void AdoptProcessErrorSnapshot(OpenCodeErrorRestart request)
    {
        _processErrorSnapshot = request;
        _processErrorInputVersion = _savedPlanComposerInterruptVersion;
        _processErrorSubmissionGeneration = NativeSubmissionGeneration;
    }

    internal async Task<bool> StopErrorProcessAsync(OpenCodeErrorRestart request)
    {
        if (!Dispatcher.CheckAccess() || !CanRecoverProcessError(request)) return false;
        // The app owns both this server and its attach child. Finish their cleanup before creating a replacement.
        await StopOwnedOpenCodeProcessAsync();
        if (!CanRecoverProcessError(request)) return false;
        _failed = true;
        _ready = false;
        _openCodeStatus?.Dispose();
        return !IsRunning;
    }

    internal void PreserveRestartInstruction(OpenCodeErrorRestart request) =>
        _autoResume?.PreserveRestartInstruction(request.Prompt, request.OriginalInstruction);

    internal bool RestoreContextRecovery(OpenCodeErrorRestart request, bool previousOwnedHostStopped = false)
    {
        if (!request.ReconcileOnly || !CanRecoverProcessError(request) || !IsReady || !IsRunning ||
            _headlessSessionId != request.SessionId || _boundAutomationSession != request.SessionId) return false;
        return _autoResume?.RestoreContextRecovery(request, previousOwnedHostStopped) == true;
    }
}
}
#endregion
#endif

#if TN_WPF || TN_SOURCE_TERMINALPANE_ERRORRETRYUI_CS
#region TerminalPane.ErrorRetryUi.cs
namespace TerminalNotes
{
using global::System.Windows;
using global::System.Windows.Automation;
using global::System.Windows.Controls;
using global::System.Windows.Media;


public sealed partial class TerminalPane
{
    private Border? _errorRetryOverlay;
    private TextBlock? _errorRetryReason;

    internal void ShowErrorRetryWait(string? reason)
    {
        if (_disposed || Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => ShowErrorRetryWait(reason)); return; }
        if (string.IsNullOrWhiteSpace(reason) || IsPlanMakerActive || IsWaitingForResources)
        {
            if (_errorRetryOverlay is not null) _errorRetryOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        if (_errorRetryOverlay is null)
        {
            _errorRetryReason = new TextBlock { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center, FontSize = 15, Margin = new Thickness(0, 12, 0, 0) };
            var content = new StackPanel { MaxWidth = 460, Margin = new Thickness(16),
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new TextBlock { Text = "오류 복구 대기", FontSize = 22, FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(_errorRetryReason);
            content.Children.Add(new TextBlock { Text = "제목의 재시도를 누르면 바로 다시 확인합니다. 중단하면 자동 복구를 취소합니다.",
                Margin = new Thickness(0, 12, 0, 0), Foreground = Brushes.LightGray,
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
            _errorRetryOverlay = new Border { Background = new SolidColorBrush(Color.FromRgb(24, 29, 38)),
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false, Child = content };
            AutomationProperties.SetAutomationId(_errorRetryOverlay, "TerminalErrorRetryWait");
            Panel.SetZIndex(_errorRetryOverlay, int.MaxValue - 1);
            Children.Add(_errorRetryOverlay);
        }
        SetRowSpan(_errorRetryOverlay, Math.Max(1, RowDefinitions.Count));
        SetColumnSpan(_errorRetryOverlay, Math.Max(1, ColumnDefinitions.Count));
        _errorRetryReason!.Text = reason;
        AutomationProperties.SetName(_errorRetryOverlay, "오류 복구 대기 · " + reason);
        _errorRetryOverlay.Visibility = Visibility.Visible;
    }
}
}
#endregion
#endif

#if TN_WPF || TN_SOURCE_TERMINALPANE_EXTERNALTERMINAL_CS
#region TerminalPane.ExternalTerminal.cs
namespace TerminalNotes
{
using global::TerminalNotes.Services;


public sealed partial class TerminalPane
{
    private string? _boundAutomationSession;
    private string? _externalContinuationSession;
    private bool _retiredForExternalContinuation;
    private string? _externalContinuationNotice;
    internal bool WasContinuedExternally { get; private set; }
    internal bool HasContinuationSession => ContinuationCandidate is not null;
    private string? ContinuationCandidate => (_retiredForExternalContinuation ? _externalContinuationSession : null) ?? _boundAutomationSession ??
        _autoResume?.ContinuationSessionId ?? _openCodeStatus?.Snapshot.SessionId;

    internal async Task<string> CaptureContinuationSessionAsync()
    {
        // Only reuse an ID already verified while this pane's server was alive.
        // A failed external launch can retry even though that server has retired.
        if (_retiredForExternalContinuation && _externalContinuationSession is { } captured) return captured;
        if (ContinuationCandidate is not { } candidate)
            throw new InvalidOperationException("이어 열 OpenCode 대화가 없습니다. 먼저 이 터미널에서 작업을 시작하세요.");
        // The controller retained this exact root only after a successful directory-scoped
        // native read. A crashed server cannot answer again; the new CMD verifies it too.
        if (!IsRunning && (_ready || _failed) &&
            (candidate == _autoResume?.VerifiedContinuationSessionId || candidate == _externalContinuationSession))
            return _externalContinuationSession = candidate;
        if (NativeOpenCodeEndpoint is null)
            throw new InvalidOperationException("원래 OpenCode 세션의 연결을 확인하지 못했습니다.");
        return _externalContinuationSession = await ExternalTerminalLauncher.ResolveRootSessionAsync(NativeOpenCodeEndpoint, WorkingDirectory, candidate);
    }

    internal async Task StopForExternalContinuationAsync()
    {
        if (_retiredForExternalContinuation && !IsRunning) return;
        IsUserInputBlocked = true;
        CancelNativeAutomation();
        _autoResume?.Dispose();
        // Retire this pane's attach child and server together; the standalone CMD
        // opens the verified same session after the embedded process tree is gone.
        await StopOwnedOpenCodeProcessAsync();
        _openCodeStatus?.Dispose();
        _failed = true;
        _retiredForExternalContinuation = true;
        _externalContinuationNotice = "CMD 연결 확인 중";
        ApplyActivityChange();
    }

    internal void CompleteExternalContinuation(string session)
    {
        if (!_retiredForExternalContinuation || session != _externalContinuationSession)
            throw new InvalidOperationException("CMD에서 확인된 대화가 원래 세션과 일치하지 않습니다.");
        WasContinuedExternally = true;
        _externalContinuationNotice = null;
        ApplyActivityChange();
    }

    // The session a retired pane handed to CMD; the app terminal that takes it back opens this exact conversation.
    internal string? ExternalContinuationSession => WasContinuedExternally ? _externalContinuationSession : null;

    // A conversation brought back from CMD: this not-yet-started pane opens that exact session instead of
    // its slot's previous one. The pane's server starts only after it is shown, well after this call.
    internal bool ResumeSessionOnStart(string sessionId)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(sessionId, "\\Ases_[A-Za-z0-9_]{1,200}\\z") ||
            _nativePaneKey is null || _headlessSessionId is not null || IsRunning) return false;
        PersistNativeSession(sessionId);
        return true;
    }

    internal void MarkExternalContinuationFailed()
    {
        if (!_retiredForExternalContinuation || WasContinuedExternally) return;
        _externalContinuationNotice = "CMD 연결 실패 · CMD 버튼으로 재시도";
        ApplyActivityChange();
    }
}
}
#endregion
#endif

#if TN_WPF || TN_SOURCE_TERMINALPANE_LIFECYCLE_CS
#region TerminalPane.Lifecycle.cs
namespace TerminalNotes
{

public sealed partial class TerminalPane
{
    internal Task WaitForTerminalCloseAsync() => StopOwnedOpenCodeProcessAsync();

    /// <summary>Stops only this pane's owned terminal process tree and leaves its rendered buffer in place.</summary>
    internal async Task StopForTerminalLifecycleAsync()
    {
        if (_disposed) return;

        ReportLifecycleStage("user_terminal_stop_requested");
        Interlocked.Exchange(ref _sessionCloseStarted, 1);

        _ready = false;
        _failed = true;
        IsUserInputBlocked = true;
        _automationBusy = false;
        _workFailed = false;
        CancelNativeAutomation();
        try { _headlessManualDispatchCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        _autoResume?.Suspend("터미널 프로세스 중단 · 자동 진행 중지");
        CancelIncompleteSavedPlanDispatches();
        _flushTimer.Stop();
        _activityTimer.Stop();
        _nativeRefreshTimer?.Stop();

        try
        {
            await StopOwnedOpenCodeProcessAsync();
            _openCodeStatus?.Dispose();
            _activityTracker.MarkExited();
            ReportLifecycleStage("user_terminal_stopped");
            StatusChanged?.Invoke("터미널 프로세스를 중단했습니다.");
            ApplyActivityChange();
        }
        catch
        {
            ReportLifecycleStage("user_terminal_stop_failed");
            throw;
        }
    }
}
}
#endregion
#endif

#if TN_WPF || TN_SOURCE_TERMINALPANE_NATIVE_CS
#region TerminalPane.Native.cs
namespace TerminalNotes
{
using global::System.IO;
using global::System.Net.Http;
using global::System.Security.Cryptography;
using global::System.Text;
using global::System.Text.Json;
using global::System.Windows.Threading;
using global::TerminalNotes.Services;


public sealed partial class TerminalPane
{
    private OpenCodeHeadlessHost? _headlessOpenCodeHost;
    private OpenCodeConversationClient? _headlessOpenCodeClient;
    private readonly SemaphoreSlim _nativeOperationGate = new(1, 1);
    private string? _nativePaneKey;
    private DispatcherTimer? _nativeRefreshTimer;
    private string? _headlessSessionId;
    private string _nativeComposerDraft = string.Empty;
    private bool _nativeViewReady;
    private long _nativeViewGeneration;
    private OpenCodeAutomationDispatch? _headlessUnconfirmedDispatch;
    private string? _headlessUnconfirmedRequestId;
    private DateTimeOffset _headlessReceiptLastCheckedAt;
    private bool _headlessReceiptCheckInFlight;
    private DateTimeOffset _startupReceiptLastCheckedAt;
    private bool _startupReceiptCheckInFlight;
    private readonly CancellationTokenSource _headlessLifetimeCancellation = new();
    private readonly CancellationToken _headlessLifetimeToken;
    private CancellationToken HeadlessLifetimeCancellation => _headlessLifetimeToken;
    private CancellationTokenSource? _headlessManualDispatchCancellation;
    private bool _nativeRefreshInFlight;
    private DateTimeOffset _nativeCatalogExpiresAt;
    private JsonElement _nativeModelsCache;
    private JsonElement _nativeAgentsCache;
    private int _openCodeTuiStartAttempted;
    private string? _openCodeTuiManualText;
    private long _openCodeTuiManualGeneration;
    private DateTimeOffset _openCodeTuiManualSubmittedAt;
    private (short Columns, short Rows)? _openCodeTuiInitialDimensions;

    private void InitializeHeadlessOpenCode(Uri endpoint, string? paneKey)
    {
        _nativePaneKey = paneKey;
        _headlessOpenCodeHost = new OpenCodeHeadlessHost(endpoint);
        _headlessOpenCodeClient = new OpenCodeConversationClient(endpoint, WorkingDirectory);
        _nativeRefreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            async (_, _) =>
            {
                _nativeRefreshTimer?.Stop();
                await RefreshNativeStateAsync();
            }, Dispatcher);
        _headlessOpenCodeHost.Exited += code =>
        {
            if (_disposed || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(() => HandleHeadlessOpenCodeExit(code));
        };
        if (_openCodeStatus is not null)
            _openCodeStatus.RecoverySignalReceived += OnOpenCodeTuiRecoverySignal;
    }

    private void OnNativeViewReady()
    {
        if (_disposed || _failed || !_usesNativeOpenCodeStatus || _nativeViewReady) return;
        _nativeViewReady = true;
        TryStartTerminalWhenLaidOut();
    }

    private void ScheduleNativeStateRefresh()
    {
        if (_disposed || _nativeRefreshTimer is null || _nativeRefreshTimer.IsEnabled) return;
        _nativeRefreshTimer.Start();
    }

    private async Task StartHeadlessOpenCodeAsync()
    {
        if (Interlocked.Exchange(ref _openCodeTuiStartAttempted, 1) != 0) return;
        try
        {
            var host = _headlessOpenCodeHost ?? throw new InvalidOperationException("OpenCode server is unavailable.");
            var client = _headlessOpenCodeClient ?? throw new InvalidOperationException("OpenCode client is unavailable.");
            _resourceAdmissionRequested.TrySetResult();
            using var admission = await TerminalStartupAdmission.AcquireSharedAsync(UpdateResourceAdmissionWait, HeadlessLifetimeCancellation);
            HeadlessLifetimeCancellation.ThrowIfCancellationRequested();
            UpdateResourceAdmissionWait(null);
            _resourceAdmissionCompletion.TrySetResult(null);
            ReportLifecycleStage("native_server_start_requested");
            await host.StartAsync(WorkingDirectory, _openCodeConfigContent,
                autoMode: StartupCommand == "opencode --auto", cancellationToken: HeadlessLifetimeCancellation);
            if (_keptProcessId is int keptProcess)
            {
                KeptServers.Forget(keptProcess);
                _keptProcessId = null;
            }
            if (_disposed) { host.Dispose(); return; }

            _openCodeStatus?.Start();
            _headlessSessionId = await RestoreNativeSessionAsync(client, HeadlessLifetimeCancellation);
            if (_usesNativeOpenCodeTui)
            {
                _headlessSessionId ??= await EnsureHeadlessSessionAsync();
                await ConnectRetainedOpenCodeTuiAsync(replaceSession: false, HeadlessLifetimeCancellation);
            }
            if (_disposed) return;
            _ready = true;
            FlushHeldStartupInput();
            _notice.Visibility = System.Windows.Visibility.Collapsed;
            ApplyActivityVisuals();
            _terminalStartedCompletion.TrySetResult(null);
            ReportLifecycleStage("native_server_started");
            Ready?.Invoke();
            StatusChanged?.Invoke(_usesNativeOpenCodeTui ? "OpenCode TUI 화면 실행 중 · " + WorkingDirectory : "OpenCode 준비 완료 · " + WorkingDirectory);
            admission.Dispose();
            await RefreshNativeStateAsync();
        }
        catch (OperationCanceledException exception) when (HeadlessLifetimeCancellation.IsCancellationRequested || _disposed)
        {
            _resourceAdmissionCompletion.TrySetResult(exception);
            MarkCancelledResourceStartup(exception);
        }
        catch (Exception exception)
        {
            _resourceAdmissionCompletion.TrySetResult(exception);
            _failed = true;
            _terminalStartedCompletion.TrySetResult(exception);
            if (_usesNativeOpenCodeTui && _headlessOpenCodeHost?.IsRunning == true)
            {
                ShowOpenCodeTuiConnectionFailure();
                if (_tuiLastExitCode is not (null or 0)) ScheduleOpenCodeTuiReconnect();
            }
            else
            {
                _openCodeStatus?.Dispose();
                ShowError("OpenCode 서버를 시작하지 못했습니다: " + exception.Message);
            }
        }
        finally { UpdateResourceAdmissionWait(null); }
    }

    private void HandleHeadlessOpenCodeExit(int code)
    {
        if (_disposed || _failed && !_tuiConnectionFailed) return;
        _tuiConnectionFailed = false;
        CaptureUnexpectedProcessRecovery();
        _autoResume?.Dispose();
        try { _headlessLifetimeCancellation.Cancel(); } catch (ObjectDisposedException) { }
        _failed = true;
        _ready = false;
        _openCodeStatus?.Dispose();
        _activityTracker.MarkExited();
        FailPendingTextSends(new InvalidOperationException("The OpenCode server exited."));
        ReportLifecycleStage("native_server_exited");
        StatusChanged?.Invoke("OpenCode 서버 종료 · 코드 " + code);
        ProcessExited?.Invoke(code);
        ApplyActivityChange();
    }

    private async Task<string?> RestoreNativeSessionAsync(OpenCodeConversationClient client, CancellationToken cancellationToken)
    {
        if (_processErrorSnapshot is { ReconcileOnly: true } recovery)
        {
            // Compaction may already have mutated this native session. The retained
            // exact ID takes precedence over the general pane map; failure must never
            // fall through to creating a new session or replaying its original request.
            if (!CanRecoverProcessError(recovery))
                throw new InvalidOperationException("대화 정리 복구 요청의 소유권을 확인할 수 없습니다.");
            _ = await client.GetSessionAsync(recovery.SessionId, cancellationToken);
            await TryUpgradeLegacySessionTitleAsync(client, recovery.SessionId, cancellationToken);
            if (!CanRecoverProcessError(recovery))
                throw new InvalidOperationException("대화 정리 복구 요청이 변경되었습니다.");
            BindNativeAutomationSession(recovery.SessionId);
            return recovery.SessionId;
        }
        if (Interlocked.Exchange(ref _keptSessionId, null) is { } keptSession)
        {
            // The session the kept server went on working in (it may have been another terminal number before).
            try
            {
                _ = await client.GetSessionAsync(keptSession, cancellationToken);
                BindNativeAutomationSession(keptSession);
                try { await File.WriteAllTextAsync(NativeSessionMapPath(), JsonSerializer.Serialize(keptSession), cancellationToken); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                return keptSession;
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or JsonException) { }
        }
        string? saved = null;
        string path = NativeSessionMapPath();
        try
        {
            if (File.Exists(path))
            {
                string content = (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
                try { using var document = JsonDocument.Parse(content); saved = document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() : content; }
                catch (JsonException) { saved = content; }
            }
            if (string.IsNullOrWhiteSpace(saved)) return null;
            _ = await client.GetSessionAsync(saved, cancellationToken);
            await TryUpgradeLegacySessionTitleAsync(client, saved, cancellationToken);
            BindNativeAutomationSession(saved);
            return saved;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            PostNativeNotice("저장된 OpenCode 세션을 확인하지 못했습니다. 세션 목록에서 다시 선택할 수 있습니다.", "info");
            System.Diagnostics.Trace.WriteLine("Saved OpenCode session could not be restored: " + exception.GetType().Name);
            return null;
        }
    }

    private async Task TryUpgradeLegacySessionTitleAsync(OpenCodeConversationClient client, string sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.RenameDefaultSessionAsync(sessionId, _openCodeSessionTitle, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            // A title update must not prevent recovery of the exact saved session.
            System.Diagnostics.Trace.WriteLine("Saved OpenCode session title could not be updated: " + exception.GetType().Name);
        }
    }

    private string NativeSessionMapPath()
    {
        string identity = Path.GetFullPath(WorkingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant() + "\n" + (_nativePaneKey ?? "unpersisted");
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        string directory = Path.Combine(OpenCodeProjectContext.DataDirectory, "OpenCodeHeadlessSessions");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, hash + ".json");
    }

    private void PersistNativeSession(string sessionId)
    {
        if (_nativePaneKey is null) return;
        try
        {
            string path = NativeSessionMapPath();
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(sessionId), new UTF8Encoding(false));
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            PostNativeNotice("세션 연결을 저장하지 못했습니다. 현재 세션은 계속 사용할 수 있습니다.", "info");
        }
    }

    private void HandleNativeWebMessage(JsonElement root)
    {
        if (_disposed || _failed) return;
        string? type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        if (IsUserInputBlocked && type is ("nativeAbort" or "nativeNew" or "nativeSelect" or "nativePermission" or "nativeQuestion" or "nativeQuestionReject"))
        {
            PostNativeNotice("이 터미널의 사용자 입력이 일시 차단되었습니다.", "info");
            return;
        }
        switch (type)
        {
            case "nativeReady": OnNativeViewReady(); break;
            case "nativeSend":
                if (IsUserInputBlocked)
                {
                    PostNative(new { type = "nativeSendFailed", requestID = root.TryGetProperty("requestID", out var blockedId) ? blockedId.GetString() ?? "" : "", message = "이 터미널의 사용자 입력이 일시 차단되었습니다.", retryable = true });
                    break;
                }
                _ = RunNativeUiOperationAsync(() => SendNativeUiPromptAsync(
                    root.TryGetProperty("requestID", out var requestId) ? requestId.GetString() ?? "" : "",
                    root.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "",
                    root.TryGetProperty("model", out var model) ? model.GetString() : null,
                    root.TryGetProperty("agent", out var agent) ? agent.GetString() : null));
                break;
            case "nativeAbort": _ = RunNativeUiOperationAsync(AbortNativeSessionAsync); break;
            case "nativeNew": _ = RunNativeUiOperationAsync(CreateNativeSessionAsync); break;
            case "nativeSelect":
                _ = RunNativeUiOperationAsync(() => SelectNativeSessionAsync(
                    root.TryGetProperty("sessionID", out var id) ? id.GetString() ?? "" : ""));
                break;
            case "nativePermission":
                _ = RunNativeUiOperationAsync(() => AnswerNativePermissionAsync(
                    root.TryGetProperty("requestID", out var permissionId) ? permissionId.GetString() ?? "" : "",
                    root.TryGetProperty("reply", out var reply) ? reply.GetString() ?? "" : ""));
                break;
            case "nativeQuestion":
                JsonElement questionMessage = root.Clone();
                _ = RunNativeUiOperationAsync(() => ReplyNativeQuestionAsync(questionMessage));
                break;
            case "nativeQuestionReject":
                _ = RunNativeUiOperationAsync(() => RejectNativeQuestionAsync(
                    root.TryGetProperty("requestID", out var questionId) ? questionId.GetString() ?? "" : ""));
                break;
            case "nativeRefresh": _ = ReconcileNativeSendReceiptAsync(); _ = RefreshNativeStateAsync(); break;
            case "nativeSetInput":
                if (root.TryGetProperty("text", out var input) && input.ValueKind == JsonValueKind.String)
                    PostNative(new { type = "nativeSetInput", text = input.GetString() ?? "" });
                break;
            case "nativeDraft":
                _planMakerInputRevision++;
                _nativeComposerDraft = root.TryGetProperty("text", out var draft) && draft.ValueKind == JsonValueKind.String
                    ? draft.GetString() ?? "" : "";
                ApplyActivityChange();
                break;
            case "nativeQuestionDraft":
                if (IsPlanMakerActive && root.TryGetProperty("requestID", out var draftRequest) && draftRequest.ValueKind == JsonValueKind.String &&
                    draftRequest.GetString() is { Length: > 0 } draftRequestId)
                {
                    _planMakerInputRevision++;
                    if (root.TryGetProperty("hasPendingInput", out var pending) && pending.ValueKind == JsonValueKind.True)
                        _planMakerQuestionDrafts.Add(draftRequestId);
                    else _planMakerQuestionDrafts.Remove(draftRequestId);
                    ApplyActivityChange();
                }
                break;
            case "shortcut":
                Shortcut?.Invoke(root.TryGetProperty("key", out var key) ? key.GetString() ?? "" : "",
                    root.TryGetProperty("shift", out var shift) && shift.ValueKind == JsonValueKind.True);
                break;
            case "activated": ActivatedByUser?.Invoke(); break;
            case "copy": CopyText(root.TryGetProperty("data", out var copied) ? copied.GetString() : null); break;
            case "contextMenu": ContextMenuRequested?.Invoke(root.TryGetProperty("hasSelection", out var selection) && selection.ValueKind == JsonValueKind.True); break;
            case "paste": PasteFromClipboard(); break;
        }
    }

    private async Task RunNativeUiOperationAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) when (_disposed || HeadlessLifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            PostNativeNotice(exception.Message, "error");
            await RefreshNativeStateAsync();
        }
    }

    private async Task CreateNativeSessionAsync()
    {
        await _nativeOperationGate.WaitAsync(HeadlessLifetimeCancellation);
        try
        {
            EnsureNativeSessionSwitchAllowed();
            var client = RequireNativeClient();
            string id = ReadSessionId(await client.CreateSessionAsync(_openCodeSessionTitle, HeadlessLifetimeCancellation));
            _ = await client.GetSessionAsync(id, HeadlessLifetimeCancellation);
            if (_usesNativeOpenCodeTui)
                await SelectOpenCodeTuiSessionAsync(id, HeadlessLifetimeCancellation);
            SetHeadlessSession(id);
            PersistNativeSession(id);
        }
        finally { _nativeOperationGate.Release(); }
        await RefreshNativeStateAsync();
    }

    private async Task SelectNativeSessionAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await _nativeOperationGate.WaitAsync(HeadlessLifetimeCancellation);
        try
        {
            EnsureNativeSessionSwitchAllowed();
            _ = await RequireNativeClient().GetSessionAsync(sessionId, HeadlessLifetimeCancellation);
            if (_usesNativeOpenCodeTui)
                await SelectOpenCodeTuiSessionAsync(sessionId, HeadlessLifetimeCancellation);
            SetHeadlessSession(sessionId);
            PersistNativeSession(sessionId);
        }
        finally { _nativeOperationGate.Release(); }
        await RefreshNativeStateAsync();
    }

    private void EnsureNativeSessionSwitchAllowed()
    {
        if (_headlessUnconfirmedDispatch is not null)
            throw new InvalidOperationException("이전 요청의 접수를 확인 중입니다. 확인이 끝난 뒤 대화를 변경하세요.");
        if (_openCodeStatus?.Snapshot.Kind is not (OpenCodeStatusKind.Idle or OpenCodeStatusKind.Unknown) ||
            _nativeAutomationActive || _startupApiActive || HasPendingStartupSubmission || HasLocalPendingInput())
            throw new InvalidOperationException("먼저 진행 중인 요청을 마치고 입력 초안을 보낸 뒤 세션을 변경하세요.");
    }

    private void SetHeadlessSession(string sessionId)
    {
        BindNativeAutomationSession(sessionId);
    }

    private async Task SelectOpenCodeTuiSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var selector = new OpenCodeAutomationClient(NativeOpenCodeEndpoint!, WorkingDirectory, useTuiSelection: true);
        await selector.SelectStartupSessionAsync(sessionId, cancellationToken);
    }

    private async Task SendNativeUiPromptAsync(string requestId, string prompt, string? model, string? agent)
    {
        using var manualDispatch = BeginHeadlessManualDispatch();
        bool postStarted = false;
        string? sessionId = null;
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
            if (prompt.Length > 32 * 1024) throw new InvalidOperationException("요청은 32,768자를 넘을 수 없습니다.");
            if (string.IsNullOrWhiteSpace(requestId)) throw new InvalidOperationException("전송 식별자가 없습니다. 초안을 다시 보내세요.");
            await _nativeOperationGate.WaitAsync(manualDispatch.Token);
            try
            {
                if (_startupApiActive || HasPendingStartupSubmission)
                    throw new InvalidOperationException("플랜 요청의 접수를 확인 중입니다. 확인이 끝난 뒤 보내세요.");
                sessionId = await EnsureHeadlessSessionAsync();
                manualDispatch.Token.ThrowIfCancellationRequested();
                if (_usesNativeOpenCodeTui)
                    await SelectOpenCodeTuiSessionAsync(sessionId, manualDispatch.Token);
                if (_openCodeStatus?.Snapshot.Kind != OpenCodeStatusKind.Idle)
                    throw new InvalidOperationException("OpenCode가 응답 중이거나 세션 상태를 확인할 수 없습니다.");
                if (_headlessUnconfirmedDispatch is { } pending)
                {
                    postStarted = true;
                    if (_headlessUnconfirmedRequestId != requestId || pending.SessionId != sessionId || pending.Prompt != prompt)
                        throw new InvalidOperationException("이전 요청의 접수 결과가 불명확합니다. 같은 초안만 영수증을 다시 확인할 수 있습니다.");
                    if (!await HasHeadlessReceiptAsync(pending))
                        throw new InvalidOperationException("이전 요청의 접수를 확인하지 못했습니다. 중복 전송을 막기 위해 다시 보내지 않았습니다.");
                    _headlessUnconfirmedDispatch = null;
                    _headlessUnconfirmedRequestId = null;
                    if (_nativeComposerDraft == prompt) _nativeComposerDraft = "";
                    postStarted = true;
                    PostNative(new { type = "nativeSent", requestID = requestId, text = prompt, sessionID = sessionId });
                    return;
                }
                string messageId = OpenCodeRecoveryClient.CreateMessageId();
                var dispatch = new OpenCodeAutomationDispatch(sessionId, messageId, prompt, DateTimeOffset.UtcNow);
                _headlessUnconfirmedDispatch = dispatch;
                _headlessUnconfirmedRequestId = requestId;
                postStarted = true;
                await SubmitHeadlessPromptAsync(sessionId, prompt, model, agent, manualDispatch.Token, messageIdOverride: messageId);
                _headlessUnconfirmedDispatch = null;
                _headlessUnconfirmedRequestId = null;
                if (_nativeComposerDraft == prompt) _nativeComposerDraft = "";
            }
            finally { _nativeOperationGate.Release(); }
            PostNative(new { type = "nativeSent", requestID = requestId, text = prompt, sessionID = sessionId });
            await RefreshNativeStateAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !HeadlessLifetimeCancellation.IsCancellationRequested)
        {
            bool knownRejection = exception is HttpRequestException { StatusCode: { } code } && (int)code is >= 400 and < 500;
            bool accepted = false;
            if (postStarted && !knownRejection && _headlessUnconfirmedRequestId == requestId &&
                _headlessUnconfirmedDispatch is { } dispatch)
            {
                try { accepted = await HasHeadlessReceiptAsync(dispatch); }
                catch { }
                if (accepted)
                {
                    _headlessUnconfirmedDispatch = null;
                    _headlessUnconfirmedRequestId = null;
                    if (_nativeComposerDraft == prompt) _nativeComposerDraft = "";
                    PostNative(new { type = "nativeSent", requestID = requestId, text = prompt, sessionID = dispatch.SessionId });
                    await RefreshNativeStateAsync();
                    return;
                }
            }
            if (knownRejection)
            {
                _headlessUnconfirmedDispatch = null;
                _headlessUnconfirmedRequestId = null;
            }
            PostNative(new { type = "nativeSendFailed", requestID = requestId, message = exception.Message, retryable = !postStarted || knownRejection });
            PostNativeNotice(exception.Message, "error");
        }
    }

    private async Task<string> EnsureHeadlessSessionAsync()
    {
        if (_headlessSessionId is { Length: > 0 } current) return current;
        var client = RequireNativeClient();
        string created = ReadSessionId(await client.CreateSessionAsync(_openCodeSessionTitle, HeadlessLifetimeCancellation));
        _ = await client.GetSessionAsync(created, HeadlessLifetimeCancellation);
        SetHeadlessSession(created);
        PersistNativeSession(created);
        return created;
    }

    private async Task SubmitHeadlessPromptAsync(string sessionId, string prompt, string? model, string? agent,
        CancellationToken cancellationToken, bool observeForAutoResume = true, string? messageIdOverride = null)
    {
        var client = RequireNativeClient();
        string messageId = messageIdOverride ?? OpenCodeRecoveryClient.CreateMessageId();
        OpenCodeSubmissionObservation observation = _openCodeStatus?.ObserveSubmission() ?? default;
        observeForAutoResume &= !IsPlanMakerActive;
        if (observeForAutoResume) _autoResume?.ObserveSubmittedPrompt(prompt, sessionId, messageId);
        else _autoResume?.Suspend("질문 입력 · 자동 진행 대기");
        try
        {
            await client.SendAsync(sessionId, prompt, messageId, model, agent, cancellationToken);
        }
        catch
        {
            _autoResume?.Suspend("요청 전달 결과 확인 필요 · 자동 진행 중지");
            throw;
        }
        _openCodeStatus?.ConfirmApiSubmission(sessionId, messageId, observation.Generation);
        if (observeForAutoResume) _autoResume?.ConfirmApiSubmission(sessionId, messageId);
        ClearWorkFailed();
    }

    private async Task<bool> HasHeadlessReceiptAsync(OpenCodeAutomationDispatch dispatch)
    {
        using var client = new OpenCodeAutomationClient(NativeOpenCodeEndpoint!, WorkingDirectory, useTuiSelection: false);
        return await client.HasStartupMessageAsync(dispatch, HeadlessLifetimeCancellation);
    }

    private async Task SubmitHeadlessStartupPromptAsync(string prompt, bool isQuestion, Func<bool>? canSubmit,
        CancellationToken cancellationToken)
    {
        await _nativeOperationGate.WaitAsync(cancellationToken);
        try
        {
            if (canSubmit?.Invoke() == false) throw new OperationCanceledException(cancellationToken);
            string sessionId = await EnsureHeadlessSessionAsync();
            if (_openCodeStatus?.Snapshot.Kind is OpenCodeStatusKind.Busy or OpenCodeStatusKind.NeedsInput)
                throw new InvalidOperationException("OpenCode가 먼저 진행 중인 요청이나 질문을 처리해야 합니다.");
            await SubmitHeadlessPromptAsync(sessionId, prompt, null, null, cancellationToken, observeForAutoResume: !isQuestion);
            if (canSubmit?.Invoke() == false) throw new OperationCanceledException(cancellationToken);
        }
        finally { _nativeOperationGate.Release(); }
    }

    private async Task AbortNativeSessionAsync()
    {
        CancelNativeAutomation();
        try { _headlessManualDispatchCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        _autoResume?.Suspend("사용자 중단 · 자동 진행 중지");
        _inputCapture.Observe("\x03");
        if (HasPendingStartupSubmission)
            PostNativeNotice("중단 요청을 보냈습니다. 기존 입력의 접수 여부를 확인 중이며, 확인될 때까지 새 입력은 보내지 않습니다.", "info");
        if (_headlessSessionId is not { Length: > 0 } sessionId) return;
        await RequireNativeClient().AbortAsync(sessionId, HeadlessLifetimeCancellation);
        await RefreshNativeStateAsync();
    }

    private async Task AnswerNativePermissionAsync(string requestId, string reply)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (reply is not ("once" or "always" or "reject")) throw new ArgumentException("지원하지 않는 승인 응답입니다.");
        string sessionId = await EnsurePendingRequestBelongsToSelectedSessionAsync(requestId, permission: true);
        await RequireNativeClient().AnswerPermissionAsync(requestId, reply, HeadlessLifetimeCancellation);
        if (_headlessSessionId != sessionId) return;
        await RefreshNativeStateAsync();
    }

    private async Task ReplyNativeQuestionAsync(JsonElement root)
    {
        string requestId = root.TryGetProperty("requestID", out var id) ? id.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(requestId) || !root.TryGetProperty("answers", out var answersElement) ||
            answersElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("질문 답변 형식이 올바르지 않습니다.");
        bool planningAnswer = IsPlanMakerActive;
        PlanMakerInterviewControl? planningInterview = planningAnswer ? _planMakerInterview : null;
        if (planningAnswer)
        {
            _planMakerInputRevision++;
            planningInterview?.BeginAnswerSubmission();
            _planMakerReportPhase?.Invoke("플랜메이커 · 답변을 선택하고 전송 중입니다.");
        }
        bool gateHeld = false;
        try
        {
            if (planningAnswer)
            {
                await _nativeOperationGate.WaitAsync(HeadlessLifetimeCancellation);
                gateHeld = true;
            }
            string sessionId = await EnsurePendingRequestBelongsToSelectedSessionAsync(requestId, permission: false);
            string[][] answers = answersElement.EnumerateArray().Select(question => question.ValueKind == JsonValueKind.Array
                ? question.EnumerateArray().Select(answer => answer.GetString() ?? "").ToArray()
                : Array.Empty<string>()).ToArray();
            await RequireNativeClient().ReplyQuestionAsync(requestId, answers, HeadlessLifetimeCancellation);
            if (planningAnswer) _planMakerQuestionDrafts.Remove(requestId);
            if (_headlessSessionId != sessionId) return;
            await RefreshNativeStateAsync();
        }
        finally
        {
            if (gateHeld) _nativeOperationGate.Release();
            planningInterview?.EndAnswerSubmission();
            if (planningAnswer) _planMakerReportPhase?.Invoke("플랜메이커 · 답변을 반영하는 중입니다.");
        }
    }

    private async Task RejectNativeQuestionAsync(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (IsPlanMakerActive) _planMakerInputRevision++;
        string sessionId = await EnsurePendingRequestBelongsToSelectedSessionAsync(requestId, permission: false);
        await RequireNativeClient().RejectQuestionAsync(requestId, HeadlessLifetimeCancellation);
        if (IsPlanMakerActive) _planMakerQuestionDrafts.Remove(requestId);
        if (_headlessSessionId != sessionId) return;
        await RefreshNativeStateAsync();
    }

    private async Task RefreshNativeStateAsync()
    {
        if (_disposed || _headlessOpenCodeClient is null || !_browser.Dispatcher.CheckAccess())
        {
            if (!_disposed && !_browser.Dispatcher.HasShutdownStarted)
            await _browser.Dispatcher.InvokeAsync(RefreshNativeStateAsync).Task.Unwrap();
            return;
        }
        if (_nativeRefreshInFlight) return;
        _nativeRefreshInFlight = true;
        try
        {
            var client = _headlessOpenCodeClient;
            long generation = _nativeViewGeneration;
            string? sessionIdSnapshot = _headlessSessionId;
            JsonElement sessions = FilterNativeRootSessions(await client.ListSessionsAsync(HeadlessLifetimeCancellation));
            JsonElement permissions = await client.ReadPermissionsAsync(HeadlessLifetimeCancellation);
            JsonElement questions = await client.ReadQuestionsAsync(HeadlessLifetimeCancellation);
            if (DateTimeOffset.UtcNow >= _nativeCatalogExpiresAt)
            {
                _nativeModelsCache = await ReadOptionalAsync(client.ReadProvidersAsync);
                _nativeAgentsCache = await ReadOptionalAsync(client.ReadAgentsAsync);
                _nativeCatalogExpiresAt = DateTimeOffset.UtcNow.AddSeconds(60);
            }
            IReadOnlySet<string> sessionScope = sessionIdSnapshot is { Length: > 0 } rootSession
                ? await client.ReadSessionScopeAsync(rootSession, HeadlessLifetimeCancellation)
                : new HashSet<string>(StringComparer.Ordinal);
            JsonElement messages = sessionIdSnapshot is { Length: > 0 } sessionId
                ? await client.ReadMessagesAsync(sessionId, HeadlessLifetimeCancellation)
                : EmptyJsonArray();
            if (_nativeViewGeneration != generation || _headlessSessionId != sessionIdSnapshot) return;
            PostNative(new
            {
                type = "nativeState",
                ready = IsReady,
                sessionID = sessionIdSnapshot,
                sessions,
                messages,
                permissions = FilterRequests(permissions, "permissions", sessionScope),
                questions = FilterRequests(questions, "questions", sessionScope),
                status = (_openCodeStatus?.Snapshot.Kind ?? OpenCodeStatusKind.Unknown).ToString().ToLowerInvariant(),
                models = NormalizeModels(_nativeModelsCache),
                agents = NormalizeAgents(_nativeAgentsCache),
                error = (string?)null
            });
        }
        catch (OperationCanceledException) when (_disposed || HeadlessLifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            PostNative(new { type = "nativeState", ready = false, error = exception.Message });
        }
        finally
        {
            _nativeRefreshInFlight = false;
            _ = ReconcileNativeSendReceiptAsync();
            if (!_disposed && !_failed && _ready && _nativeRefreshTimer is not null && !_nativeRefreshTimer.IsEnabled)
                _nativeRefreshTimer.Start();
        }
    }

    private async Task<JsonElement> ReadOptionalAsync(Func<CancellationToken, Task<JsonElement>> read)
    {
        try { return await read(HeadlessLifetimeCancellation); }
        catch (OperationCanceledException) when (HeadlessLifetimeCancellation.IsCancellationRequested) { throw; }
        catch { return EmptyJsonArray(); }
    }

    private JsonElement FilterNativeRootSessions(JsonElement sessions)
    {
        if (sessions.ValueKind != JsonValueKind.Array) return EmptyJsonArray();
        var verified = new List<string>();
        foreach (JsonElement session in sessions.EnumerateArray().Take(100))
        {
            string? id = GetJsonString(session, "id");
            string? directory = GetJsonString(session, "directory");
            if (string.IsNullOrWhiteSpace(id) || !id.StartsWith("ses", StringComparison.Ordinal) ||
                GetJsonString(session, "parentID") is not null || string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                string expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(WorkingDirectory));
                string actual = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
                if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) verified.Add(session.GetRawText());
            }
            catch { }
        }
        return JsonDocument.Parse("[" + string.Join(',', verified) + "]").RootElement.Clone();
    }

    private async Task ReconcileNativeSendReceiptAsync()
    {
        await ReconcileInterruptedStartupReceiptAsync();
        if (_disposed || _headlessReceiptCheckInFlight || _headlessUnconfirmedDispatch is not { } dispatch ||
            _headlessUnconfirmedRequestId is not { } requestId ||
            DateTimeOffset.UtcNow - _headlessReceiptLastCheckedAt < TimeSpan.FromSeconds(2)) return;
        _headlessReceiptCheckInFlight = true;
        _headlessReceiptLastCheckedAt = DateTimeOffset.UtcNow;
        try
        {
            if (!await HasHeadlessReceiptAsync(dispatch) || _disposed ||
                !ReferenceEquals(_headlessUnconfirmedDispatch, dispatch) || _headlessUnconfirmedRequestId != requestId) return;
            _headlessUnconfirmedDispatch = null;
            _headlessUnconfirmedRequestId = null;
            if (_nativeComposerDraft == dispatch.Prompt) _nativeComposerDraft = "";
            PostNative(new { type = "nativeSent", requestID = requestId, text = dispatch.Prompt, sessionID = dispatch.SessionId });
            await RefreshNativeStateAsync();
        }
        catch (OperationCanceledException) when (HeadlessLifetimeCancellation.IsCancellationRequested) { }
        catch { }
        finally { _headlessReceiptCheckInFlight = false; }
    }

    private async Task ReconcileInterruptedStartupReceiptAsync()
    {
        if (_disposed || _startupApiActive || _startupReceiptCheckInFlight ||
            _startupApiUnconfirmed is not { } dispatch ||
            DateTimeOffset.UtcNow - _startupReceiptLastCheckedAt < TimeSpan.FromSeconds(2)) return;
        _startupReceiptCheckInFlight = true;
        _startupReceiptLastCheckedAt = DateTimeOffset.UtcNow;
        try
        {
            using var client = new OpenCodeAutomationClient(NativeOpenCodeEndpoint!, WorkingDirectory, useTuiSelection: false);
            if (!await client.HasStartupMessageAsync(dispatch, HeadlessLifetimeCancellation) || _disposed || _startupApiActive ||
                !ReferenceEquals(_startupApiUnconfirmed, dispatch)) return;
            _openCodeStatus?.ConfirmApiSubmission(dispatch.SessionId, dispatch.MessageId, _startupApiGeneration);
            _startupApiUnconfirmed = null;
            _openCodeStatus?.SetSessionScope(_boundAutomationSession);
            PostNativeNotice("The interrupted input was confirmed as accepted. No second request was sent.", "info");
        }
        catch (OperationCanceledException) when (HeadlessLifetimeCancellation.IsCancellationRequested) { }
        catch { }
        finally { _startupReceiptCheckInFlight = false; }
    }

    private static JsonElement EmptyJsonArray() => JsonDocument.Parse("[]").RootElement.Clone();

    private static string ReadSessionId(JsonElement response)
    {
        string? id = GetJsonString(response, "id");
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("OpenCode did not return a session ID.");
        return id;
    }

    private async Task<string> EnsurePendingRequestBelongsToSelectedSessionAsync(string requestId, bool permission)
    {
        string? sessionId = _headlessSessionId;
        if (sessionId is null) throw new InvalidOperationException("먼저 요청 대상 세션을 선택하세요.");
        JsonElement requests = permission
            ? await RequireNativeClient().ReadPermissionsAsync(HeadlessLifetimeCancellation)
            : await RequireNativeClient().ReadQuestionsAsync(HeadlessLifetimeCancellation);
        IReadOnlySet<string> scope = await RequireNativeClient().ReadSessionScopeAsync(sessionId, HeadlessLifetimeCancellation);
        string property = permission ? "permissions" : "questions";
        JsonElement list = requests.ValueKind == JsonValueKind.Object && requests.TryGetProperty(property, out var nested)
            ? nested : requests;
        bool exact = list.ValueKind == JsonValueKind.Array && list.EnumerateArray().Any(request =>
            GetJsonString(request, "id") == requestId && GetJsonString(request, "sessionID") is { } requestedSession && scope.Contains(requestedSession));
        if (!exact || _headlessSessionId != sessionId) throw new InvalidOperationException("이 승인 또는 질문은 현재 선택한 세션에 속하지 않습니다.");
        return sessionId;
    }

    private static JsonElement FilterRequests(JsonElement requests, string collection, IReadOnlySet<string> sessionScope)
    {
        if (sessionScope.Count == 0) return EmptyJsonArray();
        JsonElement list = requests.ValueKind == JsonValueKind.Object && requests.TryGetProperty(collection, out var nested)
            ? nested : requests;
        if (list.ValueKind != JsonValueKind.Array) return EmptyJsonArray();
        string[] filtered = list.EnumerateArray()
            .Where(request => GetJsonString(request, "sessionID") is { } session && sessionScope.Contains(session))
            .Select(request => request.GetRawText()).ToArray();
        return JsonDocument.Parse("[" + string.Join(',', filtered) + "]").RootElement.Clone();
    }

    private static object[] NormalizeModels(JsonElement providers)
    {
        JsonElement values = providers;
        if (providers.ValueKind == JsonValueKind.Object && providers.TryGetProperty("providers", out var nested)) values = nested;
        JsonElement connected = default;
        if (providers.ValueKind == JsonValueKind.Object && providers.TryGetProperty("all", out var all)) values = all;
        if (providers.ValueKind == JsonValueKind.Object && providers.TryGetProperty("connected", out var connectedValue)) connected = connectedValue;
        HashSet<string>? connectedIds = connected.ValueKind == JsonValueKind.Array
            ? connected.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal)
            : null;
        var result = new List<object>();
        IEnumerable<(string Key, JsonElement Value)> providerList = values.ValueKind switch
        {
            JsonValueKind.Array => values.EnumerateArray().Select((value, index) => (index.ToString(), value)).ToArray(),
            JsonValueKind.Object => values.EnumerateObject().Select(property => (property.Name, property.Value)).ToArray(),
            _ => Array.Empty<(string, JsonElement)>()
        };
        foreach (var (providerKey, provider) in providerList)
        {
            string providerId = GetJsonString(provider, "id") ?? GetJsonString(provider, "providerID") ?? providerKey;
            if (connectedIds is not null && !connectedIds.Contains(providerId)) continue;
            string providerName = GetJsonString(provider, "name") ?? providerId;
            if (!provider.TryGetProperty("models", out var modelValues)) continue;
            IEnumerable<(string Id, JsonElement Model)> list = modelValues.ValueKind switch
            {
                JsonValueKind.Array => modelValues.EnumerateArray().Select((value, index) => (index.ToString(), value)).ToArray(),
                JsonValueKind.Object => modelValues.EnumerateObject().Select(value => (value.Name, value.Value)).ToArray(),
                _ => Array.Empty<(string, JsonElement)>()
            };
            foreach (var (modelKey, model) in list)
            {
                string modelId = GetJsonString(model, "id") ?? modelKey;
                string modelName = GetJsonString(model, "name") ?? modelId;
                if (providerId.Length > 0 && modelId.Length > 0)
                    result.Add(new { id = providerId + "/" + modelId, name = providerName + " · " + modelName });
            }
        }
        return result.ToArray();
    }

    private static object[] NormalizeAgents(JsonElement agents)
    {
        JsonElement values = agents;
        if (agents.ValueKind == JsonValueKind.Object && agents.TryGetProperty("agents", out var nested)) values = nested;
        if (values.ValueKind != JsonValueKind.Array) return Array.Empty<object>();
        return values.EnumerateArray().Select(agent => new
        {
            name = GetJsonString(agent, "name") ?? GetJsonString(agent, "id") ?? "",
            description = GetJsonString(agent, "description")
        }).Where(agent => agent.name.Length > 0).Cast<object>().ToArray();
    }

    private static string? GetJsonString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private OpenCodeConversationClient RequireNativeClient() => _headlessOpenCodeClient ??
        throw new InvalidOperationException("OpenCode API 연결이 준비되지 않았습니다.");

    // Skills added, removed or changed while this terminal runs (2026-11-06 사용자: "중간에 스킬을 추가하거나 빼거나 되면 그것도
    // 바로 반영되는거지? 바로 되게해"). OpenCode reads its skill folders once per instance, so the instance is reset as soon as
    // this terminal's OpenCode is idle; the next request reads the skills again. Work in progress is never cut: a busy terminal
    // (or one in a PlanMaker interview or with input waiting) reloads the moment it becomes idle.
    private bool _skillReloadPending, _skillReloadRunning;
    internal bool SkillReloadPending => _skillReloadPending;
    internal event Action? SkillsReloaded;

    internal void RequestSkillReload()
    {
        if (_disposed || !_usesNativeOpenCodeStatus) return;
        _skillReloadPending = true;
        _ = TryReloadSkillsAsync();
    }

    private void ScheduleSkillReload()
    {
        if (!_skillReloadPending || _disposed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() => _ = TryReloadSkillsAsync());
    }

    private async Task TryReloadSkillsAsync()
    {
        if (!_skillReloadPending || _skillReloadRunning || _disposed || !CanStartNativeAutomation || _headlessOpenCodeClient is null ||
            NativeOpenCodeStatus != OpenCodeStatusKind.Idle || IsPlanMakerActive || HasLocalPendingInput()) return;
        _skillReloadRunning = true;
        bool entered = false;
        try
        {
            await _nativeOperationGate.WaitAsync(HeadlessLifetimeCancellation);
            entered = true;
            if (_disposed || NativeOpenCodeStatus != OpenCodeStatusKind.Idle || IsPlanMakerActive || HasLocalPendingInput()) return;
            _skillReloadPending = false;
            await RequireNativeClient().DisposeInstanceAsync(HeadlessLifetimeCancellation);
            SkillsReloaded?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or InvalidOperationException)
        {
            // Not applied: try again when the status next changes.
            _skillReloadPending = true;
            ReportLifecycleStage("skill_reload_failed");
        }
        finally
        {
            if (entered) _nativeOperationGate.Release();
            _skillReloadRunning = false;
        }
    }

    private async Task StopOwnedOpenCodeProcessAsync()
    {
        if (!_usesNativeOpenCodeStatus)
        {
            await _session.StopAndWaitAsync();
            return;
        }

        try { _headlessLifetimeCancellation.Cancel(); } catch (ObjectDisposedException) { }
        try { _headlessManualDispatchCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        // Stop the owned attach child before its server so it cannot leave a client
        // connected to a backend that is already being torn down.
        try { await _session.CloseAsync(); }
        finally { _headlessOpenCodeHost?.Dispose(); }
    }

    private CancellationTokenSource BeginHeadlessManualDispatch()
    {
        if (_headlessManualDispatchCancellation is null || _headlessManualDispatchCancellation.IsCancellationRequested)
        {
            _headlessManualDispatchCancellation?.Dispose();
            _headlessManualDispatchCancellation = new CancellationTokenSource();
        }
        return CancellationTokenSource.CreateLinkedTokenSource(HeadlessLifetimeCancellation, _headlessManualDispatchCancellation.Token);
    }

    private void PostNativeNotice(string message, string level) => PostNative(new { type = "nativeNotice", message, level });

    private bool PostNative(object message)
    {
        if (_disposed || _browser.CoreWebView2 is null) return false;
        try { _browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message)); return true; }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine("Native OpenCode view post failed: " + exception.GetType().Name);
            return false;
        }
    }
}



}
#endregion
#endif

#if TN_WPF || TN_SOURCE_TERMINALPANE_NATIVEACTIVITY_CS
#region TerminalPane.NativeActivity.cs
namespace TerminalNotes
{
using global::System.Windows.Threading;
using global::TerminalNotes.Services;


public sealed partial class TerminalPane
{
    private int _nativeActivityRefreshPending;
    private DispatcherTimer? _nativeActivityRefreshTimer;
    private OpenCodeStatusMonitorPhase _lastNativeActivityMonitorPhase;

    private void ScheduleNativeActivityRefresh()
    {
        if (_disposed || Dispatcher.HasShutdownStarted ||
            Interlocked.CompareExchange(ref _nativeActivityRefreshPending, 1, 0) != 0) return;
        // Native evidence remains immediate in the monitor. Only display updates are
        // coalesced: streamed deltas must not fill Normal priority ahead of user input.
        Dispatcher.BeginInvoke((Action)(() =>
        {
            if (_disposed || Dispatcher.HasShutdownStarted)
            {
                Interlocked.Exchange(ref _nativeActivityRefreshPending, 0);
                return;
            }
            if (_nativeActivityRefreshTimer is null)
            {
                _nativeActivityRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(100)
                };
                _nativeActivityRefreshTimer.Tick += (_, _) => RefreshNativeActivity();
            }
            _nativeActivityRefreshTimer.Start();
        }), DispatcherPriority.Background);
    }

    private void RefreshNativeActivity()
    {
        _nativeActivityRefreshTimer?.Stop();
        Interlocked.Exchange(ref _nativeActivityRefreshPending, 0);
        if (_disposed || _openCodeStatus is null) return;
        // Read the current snapshot rather than replaying queued, possibly older events.
        OpenCodeStatusSnapshot snapshot = _openCodeStatus.Snapshot;
        bool recent = _openCodeStatus.HasRecentActivity;
        OpenCodeStatusMonitorPhase phase = _openCodeStatus.MonitorPhase;
        bool changed = _openCodeSnapshot != snapshot || _lastNativeRecentActivity != recent ||
            _lastNativeActivityMonitorPhase != phase;
        _openCodeSnapshot = snapshot;
        _lastNativeRecentActivity = recent;
        _lastNativeActivityMonitorPhase = phase;
        if (changed) ApplyActivityChange();
        ScheduleNativeStateRefresh();
    }
}
}
#endregion
#endif

#if TN_WPF || TN_SOURCE_TERMINALPANE_PLANEXECUTION_CS
#region TerminalPane.PlanExecution.cs
namespace TerminalNotes
{
using global::System.IO;
using global::System.Text.Json;
using global::TerminalNotes.Services;


public sealed partial class TerminalPane
{
    private readonly Dictionary<long, SavedPlanDispatch> _savedPlanDispatches = [];
    private readonly HashSet<long> _blockedSavedPlanSendIds = [];
    private SavedPlanDispatch? _savedPlanDispatchForNextSend;
    private string? _planExecutionBoundPath;
    private bool _savedPlanComposerMayContainPendingInput;
    private long _savedPlanComposerInterruptVersion;

    public bool CanExecuteSavedPlan => string.IsNullOrEmpty(PlanExecutionBlockReason);

    public string PlanExecutionBlockReason => IsTypedAgent ? TypedAgentBlockReason : GetPlanExecutionBlockReason() ?? string.Empty;
    internal bool CanSubmitProjectInput => string.IsNullOrEmpty(ProjectInputBlockReason);
    internal string ProjectInputBlockReason => IsTypedAgent ? TypedAgentBlockReason
        : GetPlanExecutionBlockReason(allowNativeBusy: true, allowReceiptRetry: true) ?? string.Empty;

    /// <summary>Claude Code or Codex: plan and request input is typed into the CLI prompt and submitted with Enter.</summary>
    internal bool IsTypedAgent => AgentCli.IsTyped(StartupCommand);

    private string TypedAgentBlockReason =>
        _disposed || _failed || !IsRunning ? "터미널 세션을 사용할 수 없습니다."
        : !IsReady ? "터미널이 준비되지 않았습니다."
        : IsPlanMakerActive ? "플랜메이커 대화를 먼저 마치거나 중단하세요."
        : string.Empty;

    internal static string BuildTypedAgentPlanPrompt(string planPath) =>
        // Plain quotes: JSON escaping would turn Korean folder names into \uXXXX escapes for the agent.
        "Read and follow the saved implementation plan at \"" + planPath +
        "\". Treat it as the requested task. Also follow the user's global instructions at \"" +
        GlobalAgentInstructions.FilePath + "\" if that file exists. " +
        "First read the Markdown files, then make a todo list for the implementation and its tests. " +
        "Implement the plan, run an appropriate build and tests, and keep the todo status accurate. " +
        "Stop if the plan is missing, unreadable, or conflicts with these instructions.";

    /// <summary>
    /// Types one prompt into a Claude Code or Codex prompt and presses Enter. These CLIs have no
    /// receipt API, so the prompt goes in once the screen has stopped changing (the CLI has drawn its input).
    /// </summary>
    internal async Task<bool> SubmitTypedAgentInputAsync(string prompt, CancellationToken cancellationToken)
    {
        LastPlanExecutionFailure = null;
        if (!IsTypedAgent) return FailPlanExecution("Claude Code 또는 Codex 터미널이 아닙니다.");
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 32 * 1024) return FailPlanExecution("입력 내용이 비어 있거나 너무 깁니다.");
        await WaitForQuietScreenAsync(cancellationToken);
        // A first run in a new folder asks whether to trust it. That answer is the user's: wait for it
        // instead of typing the prompt into the question (Enter there picks "No, exit").
        bool asked = false;
        while (IsTypedAgentQuestionOnScreen() && !_disposed && IsRunning)
        {
            if (!asked) { asked = true; StatusChanged?.Invoke("에이전트가 폴더 신뢰 여부를 묻고 있습니다 · 터미널에서 답하면 플랜을 입력합니다."); }
            await Task.Delay(500, cancellationToken);
            if (!IsTypedAgentQuestionOnScreen()) await WaitForQuietScreenAsync(cancellationToken);
        }
        if (TypedAgentBlockReason is { Length: > 0 } block) return FailPlanExecution(block);
        // One line: a newline would submit early in these CLIs.
        string line = string.Join(" ", prompt.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(part => part.Trim()));
        // Paste first, then a separate Enter: Codex treats an Enter that arrives with a paste as part of it.
        await SendTextAsync(line, submit: false);
        // Long pastes are still being taken in for a moment; Enter before that is swallowed.
        await Task.Delay(1000, cancellationToken);
        await WaitForQuietScreenAsync(cancellationToken);
        await Task.Delay(1000, cancellationToken);
        if (_nativeTerminalSurface is { } surface) surface.SendEnter();
        else await SendTextAsync(string.Empty, submit: true);
        ClearWorkFailed();
        return true;
    }

    private static readonly string[] TypedAgentQuestionMarkers =
    [
        "trust this folder", "trust the files", "Do you trust", "Yes, I trust", "allow Codex to work", "Enter to confirm"
    ];

    private bool IsTypedAgentQuestionOnScreen()
    {
        if (_nativeTerminalEmulator?.GetSnapshot() is not { } snapshot) return false;
        var screen = new global::System.Text.StringBuilder();
        foreach (var line in snapshot.Lines)
        {
            foreach (var cell in line.Cells) screen.Append(cell.Text);
            screen.AppendLine();
        }
        string text = screen.ToString();
        return TypedAgentQuestionMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private async Task WaitForQuietScreenAsync(CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        long last = -1;
        DateTime stableSince = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            long version = _nativeTerminalEmulator?.GetSnapshot().Version ?? 0;
            if (version != last) { last = version; stableSince = DateTime.UtcNow; }
            else if (version > 0 && DateTime.UtcNow - stableSince >= TimeSpan.FromSeconds(1.5)) return;
            await Task.Delay(250, cancellationToken);
        }
    }
    internal string? LastPlanExecutionFailure { get; private set; }

    public Task<bool> ExecuteSavedPlanAsync(string planPath, Func<bool>? canDispatch = null)
    {
        if (!Dispatcher.CheckAccess())
            return Dispatcher.InvokeAsync(() => ExecuteSavedPlanAsync(planPath, canDispatch)).Task.Unwrap();
        return ExecuteSavedPlanOnDispatcherAsync(planPath, canDispatch);
    }

    /// <summary>
    /// Submits one user-authored plan instruction or question to the currently bound
    /// native OpenCode session. Explicit API input may be accepted while it is busy;
    /// the native server owns execution ordering. PTY writes retain their idle gate.
    /// </summary>
    public Task<bool> SubmitProjectInputAsync(string prompt, bool isQuestion, Func<bool>? canSubmit = null)
    {
        if (!Dispatcher.CheckAccess())
            return Dispatcher.InvokeAsync(() => SubmitProjectInputAsync(prompt, isQuestion, canSubmit)).Task.Unwrap();
        return SubmitProjectInputOnDispatcherAsync(prompt, isQuestion, canSubmit);
    }

    private async Task<bool> SubmitProjectInputOnDispatcherAsync(string prompt, bool isQuestion, Func<bool>? canSubmit)
    {
        if (IsTypedAgent) return canSubmit?.Invoke() != false && await SubmitTypedAgentInputAsync(prompt, CancellationToken.None);
        LastPlanExecutionFailure = null;
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 32 * 1024)
            return FailPlanExecution("입력 내용이 비어 있거나 너무 깁니다.");

        string? block = GetPlanExecutionBlockReason(canSubmit, allowNativeBusy: true, allowReceiptRetry: true);
        if (!string.IsNullOrEmpty(block)) return FailPlanExecution(block);

        // Questions are explicit user work. They invalidate queued recovery and never
        // arm its native auto-resume correlation window.
        if (isQuestion) _autoResume?.Suspend("질문 입력 · 자동 진행 대기");
        if (_usesNativeOpenCodeStatus)
        {
            using var manualDispatch = BeginHeadlessManualDispatch();
            bool accepted = await SubmitStartupInputAsync(prompt, isQuestion, canSubmit ?? (() => true), _ => { }, manualDispatch.Token, userInitiated: true, allowBusy: true);
            // API uncertainty is tracked by the exact message receipt, never a PTY draft.
            _savedPlanComposerMayContainPendingInput = false;
            return accepted;
        }
        var dispatch = new SavedPlanDispatch(prompt, canSubmit, observeForAutoResume: !isQuestion);
        _savedPlanDispatchForNextSend = dispatch;
        try
        {
            Task sendTask;
            try { sendTask = SendTextAsync(prompt); }
            finally
            {
                if (ReferenceEquals(_savedPlanDispatchForNextSend, dispatch))
                    _savedPlanDispatchForNextSend = null;
            }
            await sendTask;
            if (!dispatch.Submitted)
            {
                if (!isQuestion) _autoResume?.Suspend("입력 제출 확인 필요 · 자동 진행 멈춤");
                return FailPlanExecution("OpenCode가 입력 제출 확인을 받지 못했습니다.");
            }
            _savedPlanComposerMayContainPendingInput = true;
            if (!await WaitForNativeSubmissionConfirmationAsync(dispatch))
            {
                PreserveComposerAfterUnconfirmedSubmission(dispatch);
                if (!isQuestion) _autoResume?.Suspend("입력 전달 미확인 · 자동 진행 멈춤");
                return FailPlanExecution("OpenCode가 입력 전달을 확인하지 못했습니다. 입력이 남아 있을 수 있으니 확인한 뒤 중복 제출을 피해주세요.");
            }
            _savedPlanComposerMayContainPendingInput = false;
            return true;
        }
        catch (Exception ex)
        {
            CancelSavedPlanDispatch(dispatch, ex);
            if (!isQuestion) _autoResume?.Suspend("입력 전달 실패 · 자동 진행 멈춤");
            return FailPlanExecution("입력을 OpenCode에 전달하지 못했습니다.");
        }
        finally
        {
            if (ReferenceEquals(_savedPlanDispatchForNextSend, dispatch))
                _savedPlanDispatchForNextSend = null;
        }
    }

    private async Task<bool> ExecuteSavedPlanOnDispatcherAsync(string planPath, Func<bool>? canDispatch,
        Func<string, Task<bool>>? startupDispatch = null)
    {
        LastPlanExecutionFailure = null;
        SavedPlanDispatch? dispatch = null;
        try
        {
            string? initialBlock = startupDispatch is not null && _startupApiUnconfirmed is not null
                ? null : GetPlanExecutionBlockReason(canDispatch);
            if (!string.IsNullOrEmpty(initialBlock)) return FailPlanExecution(initialBlock);

            string fullPath;
            try
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(planPath);
                fullPath = Path.GetFullPath(planPath, WorkingDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                return FailPlanExecution("저장된 플랜 경로를 확인할 수 없습니다.");
            }

            if (!string.Equals(Path.GetExtension(fullPath), ".md", StringComparison.OrdinalIgnoreCase))
                return FailPlanExecution("저장된 플랜은 Markdown(.md) 파일이어야 합니다.");
            if (_planExecutionBoundPath is null || !string.Equals(
                    Path.GetFullPath(fullPath), Path.GetFullPath(_planExecutionBoundPath), StringComparison.OrdinalIgnoreCase))
                return FailPlanExecution("현재 터미널에 연결된 플랜이 변경되었습니다.");

            string planContents;
            try
            {
                var file = new FileInfo(fullPath);
                if (!file.Exists || file.Length == 0 || file.Length > MaximumSavedPlanBytes)
                    return FailPlanExecution("저장된 플랜이 없거나 비어 있거나 너무 큽니다.");
                planContents = await File.ReadAllTextAsync(fullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return FailPlanExecution("저장된 플랜을 읽을 수 없습니다.");
            }

            if (string.IsNullOrWhiteSpace(planContents) || planContents.Length > MaximumSavedPlanBytes)
                return FailPlanExecution("저장된 플랜이 비어 있거나 너무 큽니다.");

            // The selected pane, its project, and its bound plan may have changed
            // while the Markdown file was being read. Recheck after that await.
            string? preSendBlock = startupDispatch is not null && _startupApiUnconfirmed is not null
                ? null : GetPlanExecutionBlockReason(canDispatch);
            if (!string.IsNullOrEmpty(preSendBlock)) return FailPlanExecution(preSendBlock);
            if (!string.Equals(Path.GetFullPath(fullPath), Path.GetFullPath(_planExecutionBoundPath!), StringComparison.OrdinalIgnoreCase))
                return FailPlanExecution("현재 터미널에 연결된 플랜이 변경되었습니다.");

            string prompt = BuildSavedPlanPrompt(fullPath);
            if (startupDispatch is not null) return await startupDispatch(prompt);
            if (_usesNativeOpenCodeStatus)
            {
                using var manualDispatch = BeginHeadlessManualDispatch();
                return await SubmitStartupInputAsync(prompt, false, canDispatch ?? (() => true), _ => { }, manualDispatch.Token, userInitiated: true);
            }
            dispatch = new SavedPlanDispatch(prompt, canDispatch);
            _savedPlanDispatchForNextSend = dispatch;

            Task sendTask;
            try
            {
                // SendTextAsync posts one browser-side paste + Enter into this
                // same OpenCode TUI. The correlated host handler rechecks every
                // actual PTY write, including the gap between paste and Enter.
                sendTask = SendTextAsync(prompt);
            }
            finally
            {
                if (ReferenceEquals(_savedPlanDispatchForNextSend, dispatch))
                    _savedPlanDispatchForNextSend = null;
            }

            await sendTask;
            if (!dispatch.Submitted)
            {
                _autoResume?.Suspend("플랜 전달 결과 확인 필요 · 자동 진행 멈춤");
                return FailPlanExecution("터미널이 플랜 제출을 확인하지 못했습니다.");
            }
            _savedPlanComposerMayContainPendingInput = true;
            if (!await WaitForNativeSubmissionConfirmationAsync(dispatch))
            {
                PreserveComposerAfterUnconfirmedSubmission(dispatch);
                _autoResume?.Suspend("플랜 입력 전달 미확인 · 자동 진행 멈춤");
                return FailPlanExecution("OpenCode가 플랜 입력 전달을 확인하지 못했습니다. 입력이 남아 있을 수 있으니 확인한 뒤 중복 제출을 피해주세요.");
            }
            _savedPlanComposerMayContainPendingInput = false;
            return true;
        }
        catch (Exception ex)
        {
            if (startupDispatch is not null) throw;
            if (dispatch is not null) CancelSavedPlanDispatch(dispatch, ex);
            _autoResume?.Suspend("플랜 전달 실패 · 자동 진행 멈춤");
            return FailPlanExecution(ex is TimeoutException
                ? "플랜 전달 확인 시간이 초과되었습니다. 중복 제출은 하지 않았습니다."
                : "플랜을 OpenCode에 전달하지 못했습니다.");
        }
        finally
        {
            if (dispatch is not null && ReferenceEquals(_savedPlanDispatchForNextSend, dispatch))
                _savedPlanDispatchForNextSend = null;
        }
    }


    private async Task<bool> WaitForNativeSubmissionConfirmationAsync(SavedPlanDispatch dispatch)
    {
        OpenCodeStatusMonitor? monitor = _openCodeStatus;
        if (monitor is null || _disposed || !IsRunning || !CanDispatchCallback(dispatch.CanDispatch))
            return false;

        long baseline = dispatch.SubmissionConfirmationBaseline;
        if (monitor.SubmissionGeneration != dispatch.SubmissionGeneration)
            return false;
        if (monitor.SubmissionConfirmationVersion > baseline)
            return dispatch.ComposerInterruptVersion == _savedPlanComposerInterruptVersion &&
                   !_disposed && IsRunning && CanDispatchCallback(dispatch.CanDispatch);

        var confirmation = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSubmissionConfirmed(OpenCodeSubmissionObservation observation)
        {
            if (observation.ConfirmationVersion > baseline && observation.Generation == dispatch.SubmissionGeneration)
                confirmation.TrySetResult(observation.ConfirmationVersion);
        }

        monitor.SubmissionConfirmed += OnSubmissionConfirmed;
        try
        {
            if (monitor.SubmissionGeneration != dispatch.SubmissionGeneration)
                return false;
            if (monitor.SubmissionConfirmationVersion > baseline)
                return dispatch.ComposerInterruptVersion == _savedPlanComposerInterruptVersion &&
                       !_disposed && IsRunning && CanDispatchCallback(dispatch.CanDispatch);

            return await TerminalExecutionTiming.WaitForNativeSubmissionConfirmationAsync(
                confirmation.Task,
                () => monitor.SubmissionGeneration == dispatch.SubmissionGeneration &&
                      dispatch.ComposerInterruptVersion == _savedPlanComposerInterruptVersion &&
                      !_disposed && IsRunning && CanDispatchCallback(dispatch.CanDispatch));
        }
        finally
        {
            monitor.SubmissionConfirmed -= OnSubmissionConfirmed;
        }
    }
    private const int MaximumSavedPlanBytes = 2 * 1024 * 1024;

    private static string BuildSavedPlanPrompt(string planPath) => BuildSavedPlanPromptCore(planPath) +
        "\n\nWhen verifying an app you created, run it only as a bounded 60–120 second smoke check. Keep the exact child process handle or PID you started and terminate only that owned process tree in a finally/cleanup step, then verify it exited. Do not leave a foreground server running, use an unbounded wait, or kill by process name. If cleanup or exit cannot be verified, report that clearly and stop.";

    private static string BuildSavedPlanPromptCore(string planPath) =>
        "Read and follow the saved implementation plan at " + JsonSerializer.Serialize(planPath) +
        ". Treat it as the requested task. First read the Markdown file, then create or update OpenCode native todos for the implementation and its tests. " +
        "Implement the plan, run an appropriate build and tests, and keep todo status accurate. Stop if the plan is missing, unreadable, or conflicts with these instructions.";

    private string? GetPlanExecutionBlockReason(Func<bool>? canDispatch = null, bool allowTrackedDispatch = false,
        bool allowNativeBusy = false, bool allowReceiptRetry = false)
    {
        if (IsPlanMakerActive) return "플랜메이커 대화를 먼저 마치거나 중단하세요.";
        if (_startupApiActive || _startupApiUnconfirmed is not null && !allowReceiptRetry)
            return "OpenCode 시작 요청의 접수를 확인 중입니다.";
        if (!allowTrackedDispatch && (_savedPlanDispatchForNextSend is not null || _savedPlanDispatches.Count != 0))
            return "저장된 플랜을 이미 전달하고 있습니다.";

        string? reason = GetSavedPlanExecutionGateBlockReason(new SavedPlanExecutionState(
            _usesNativeOpenCodeStatus,
            IsReady,
            _disposed,
            _failed,
            IsRunning,
            IsUserInputBlocked,
            _manualInputOverride,
            _automationBusy,
            allowNativeBusy && _usesNativeOpenCodeStatus &&
                (_openCodeStatus?.Snapshot.Kind == OpenCodeStatusKind.Busy || allowReceiptRetry && _startupApiUnconfirmed is not null)
                ? OpenCodeStatusKind.Idle : _openCodeStatus?.Snapshot.Kind ?? OpenCodeStatusKind.Unknown));
        if (!string.IsNullOrEmpty(reason)) return reason;

        if (string.IsNullOrWhiteSpace(_planExecutionBoundPath))
            return "현재 터미널에 Markdown 플랜이 연결되어 있지 않습니다.";
        if (!string.Equals(Path.GetExtension(_planExecutionBoundPath), ".md", StringComparison.OrdinalIgnoreCase))
            return "현재 연결된 플랜은 Markdown(.md) 파일이 아닙니다.";

        if (!CanDispatchCallback(canDispatch))
            return "선택된 터미널 또는 플랜 연결이 변경되었습니다.";
        return null;
    }

    // What is typed in the composer is not a reason to stop: the plan reaches the session through the API, past the composer
    // (사용자: 있으면 또 어때 좀 가능하게 해).
    internal static string? GetSavedPlanExecutionGateBlockReason(SavedPlanExecutionState state)
    {
        if (!state.UsesNativeOpenCodeStatus) return "네이티브 OpenCode 대화형 세션에서만 실행할 수 있습니다.";
        if (state.IsDisposed || state.IsFailed || !state.ProcessRunning) return "터미널 세션을 사용할 수 없습니다.";
        if (!state.IsReady) return "터미널이 준비되지 않았습니다.";
        if (state.IsUserInputBlocked) return "터미널 입력이 차단되어 있습니다.";
        if (state.ManualOverride) return "수동 확인 상태를 먼저 해제해야 합니다.";
        if (state.AutomationBusy) return "자동화가 진행 중입니다.";

        return state.Status switch
        {
            OpenCodeStatusKind.Idle => null,
            OpenCodeStatusKind.Busy => "OpenCode가 작업 중입니다.",
            OpenCodeStatusKind.Pending => "OpenCode가 이전 요청을 확인 중입니다.",
            OpenCodeStatusKind.NeedsInput => "OpenCode의 질문이나 승인을 먼저 처리해야 합니다.",
            _ => "OpenCode 상태를 확인할 수 없습니다."
        };
    }

    internal readonly record struct SavedPlanExecutionState(
        bool UsesNativeOpenCodeStatus,
        bool IsReady,
        bool IsDisposed,
        bool IsFailed,
        bool ProcessRunning,
        bool IsUserInputBlocked,
        bool ManualOverride,
        bool AutomationBusy,
        OpenCodeStatusKind Status);

    private static bool CanDispatchCallback(Func<bool>? callback)
    {
        if (callback is null) return true;
        try { return callback(); }
        catch { return false; }
    }

    private bool FailPlanExecution(string reason)
    {
        LastPlanExecutionFailure = reason;
        StatusChanged?.Invoke(reason);
        return false;
    }

    private void BindSavedPlanExecutionPath(string? path)
    {
        string? nextPath = string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetFullPath(path, WorkingDirectory);
        if (!string.Equals(_planExecutionBoundPath, nextPath, StringComparison.OrdinalIgnoreCase))
            CancelIncompleteSavedPlanDispatches();
        _planExecutionBoundPath = nextPath;
    }

    private SavedPlanDispatch? AttachSavedPlanDispatch(long sendId, string text, bool submit)
    {
        SavedPlanDispatch? dispatch = _savedPlanDispatchForNextSend;
        if (dispatch is null) return null;
        _savedPlanDispatchForNextSend = null;
        if (!submit || !string.Equals(text, dispatch.Prompt, StringComparison.Ordinal))
        {
            dispatch.Cancelled = true;
            return null;
        }
        dispatch.SendId = sendId;
        dispatch.ComposerInterruptVersion = _savedPlanComposerInterruptVersion;
        _savedPlanDispatches[sendId] = dispatch;
        return dispatch;
    }

    private bool CanWriteSavedPlanDispatch(long sendId, string input)
    {
        if (_blockedSavedPlanSendIds.Contains(sendId))
        {
            return false;
        }

        if (!_savedPlanDispatches.TryGetValue(sendId, out SavedPlanDispatch? dispatch)) return true;
        string? blockReason = GetPlanExecutionBlockReason(dispatch.CanDispatch, allowTrackedDispatch: true);
        if (string.IsNullOrEmpty(blockReason)) return true;

        CancelSavedPlanDispatch(dispatch, new InvalidOperationException(blockReason));
        StatusChanged?.Invoke(blockReason);
        return false;
    }

    private void PrepareSavedPlanDispatchSubmit(long sendId, string input)
    {
        if (input != "\r") return;
        if (!_savedPlanDispatches.TryGetValue(sendId, out SavedPlanDispatch? dispatch)) return;

        // The native server can publish busy and even idle while the PTY write
        // is returning. Mark the local submission before Enter reaches it so a
        // fast native completion cannot be overwritten by a late optimistic
        // Pending state.
        if (_openCodeStatus is { } monitor)
        {
            OpenCodeSubmissionObservation observation = monitor.ObserveSubmission();
            dispatch.SubmissionConfirmationBaseline = observation.ConfirmationVersion;
            dispatch.SubmissionGeneration = observation.Generation;
        }

        // Arm the controller immediately before Enter reaches the PTY. The
        // native message event can arrive as soon as OpenCode reads that byte.
        // If the write fails, the caller cancels and suspends this observation.
        if (dispatch.ObserveForAutoResume)
        {
            _autoResume?.ObserveSubmittedPrompt(dispatch.Prompt);
            dispatch.LocalInputObserved = true;
        }
    }

    private void ObserveSavedPlanDispatchWrite(long sendId, string input)
    {
        if (!_savedPlanDispatches.TryGetValue(sendId, out SavedPlanDispatch? dispatch)) return;
        if (input != "\r")
        {
            if (input.Length != 0) dispatch.PromptWasWritten = true;
            return;
        }

        // Record the successful Enter write without resetting any native
        // evidence that may already have arrived. Submission observation was
        // armed before the write in PrepareSavedPlanDispatchSubmit.
        if (dispatch.ObserveForAutoResume && !dispatch.LocalInputObserved)
            _autoResume?.ObserveSubmittedPrompt(dispatch.Prompt);
        _savedPlanComposerMayContainPendingInput = false;
        dispatch.Submitted = true;
        _savedPlanDispatches.Remove(sendId);
    }

    private void CancelIncompleteSavedPlanDispatches()
    {
        foreach (SavedPlanDispatch dispatch in _savedPlanDispatches.Values.ToArray())
            CancelSavedPlanDispatch(dispatch, new InvalidOperationException("User input cancelled the pending saved plan dispatch."));
    }

    private void CancelSavedPlanDispatch(SavedPlanDispatch dispatch, Exception error)
    {
        if (dispatch.Cancelled) return;
        if (dispatch.PromptWasWritten && !dispatch.Submitted)
            _savedPlanComposerMayContainPendingInput = true;
        if (dispatch.SendId is long sendId)
        {
            if (_savedPlanDispatches.TryGetValue(sendId, out SavedPlanDispatch? tracked) && ReferenceEquals(tracked, dispatch))
                _savedPlanDispatches.Remove(sendId);
            _blockedSavedPlanSendIds.Add(sendId);
            if (_pendingTextSends.Remove(sendId, out TaskCompletionSource? completion))
                completion.TrySetException(error);
        }
        if (ReferenceEquals(_savedPlanDispatchForNextSend, dispatch))
            _savedPlanDispatchForNextSend = null;
        dispatch.Cancelled = true;
    }

    private void CompleteSavedPlanDispatch(long sendId)
    {
        _blockedSavedPlanSendIds.Remove(sendId);
        if (_savedPlanDispatches.TryGetValue(sendId, out SavedPlanDispatch? dispatch) && dispatch.Cancelled)
            _savedPlanDispatches.Remove(sendId);
    }

    private void PreserveComposerAfterUnconfirmedSubmission(SavedPlanDispatch dispatch)
    {
        if (dispatch.ComposerInterruptVersion == _savedPlanComposerInterruptVersion &&
            (_openCodeStatus is null || _openCodeStatus.SubmissionGeneration == dispatch.SubmissionGeneration))
            _savedPlanComposerMayContainPendingInput = true;
    }

    private void ClearSavedPlanComposerDraftAfterInterrupt()
    {
        _savedPlanComposerInterruptVersion++;
        _savedPlanComposerMayContainPendingInput = false;
    }

    private void ObserveUserWriteForSavedPlanDraft(string input)
    {
        if (input.Contains('\x03') || input.Contains('\r') || input.Contains('\n'))
            ClearSavedPlanComposerDraftAfterInterrupt();
    }

    private sealed class SavedPlanDispatch(string prompt, Func<bool>? canDispatch, bool observeForAutoResume = true)
    {
        public string Prompt { get; } = prompt;
        public Func<bool>? CanDispatch { get; } = canDispatch;
        public bool ObserveForAutoResume { get; } = observeForAutoResume;
        public long? SendId { get; set; }
        public bool PromptWasWritten { get; set; }
        public bool LocalInputObserved { get; set; }
        public long SubmissionConfirmationBaseline { get; set; }
        public long SubmissionGeneration { get; set; }
        public long ComposerInterruptVersion { get; set; }
        public bool Submitted { get; set; }
        public bool Cancelled { get; set; }
    }
}


}
#endregion
#endif

#if TN_WPF || TN_SOURCE_TERMINALPANE_PLANINPUTNOTICE_CS
#region TerminalPane.PlanInputNotice.cs
namespace TerminalNotes
{
using global::System.Windows;
using global::System.Windows.Controls;
using global::System.Windows.Media;


public sealed partial class TerminalPane
{
    private Border? _projectPlanInputNotice;

    internal void ShowProjectPlanInputNotice(bool visible)
    {
        if (_projectPlanInputNotice is null)
        {
            if (!visible) return;
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            SetRow(_browser, 1);
            SetRow(_notice, 1);
            _projectPlanInputNotice = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(35, 48, 64)),
                Padding = new Thickness(10, 6, 10, 6),
                Child = new TextBlock
                {
                    Text = "플랜이 없습니다. 아래 터미널에 작업 목표나 플랜을 입력해 주세요.",
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap
                }
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(_projectPlanInputNotice, "ProjectPlanInputNotice");
            Children.Add(_projectPlanInputNotice);
            InputSubmitted += _ => ShowProjectPlanInputNotice(false);
        }
        _projectPlanInputNotice.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
}
}
#endregion
#endif

#if TN_WPF || TN_SOURCE_TERMINALPANE_PLANMAKER_CS
#region TerminalPane.PlanMaker.cs
namespace TerminalNotes
{
using global::System.IO;
using global::System.Text.Json;
using global::TerminalNotes.Services;


public sealed partial class TerminalPane
{
    private string? _planMakerSessionId;
    private long _planMakerInputRevision;
    private PlanMakerInterviewControl? _planMakerInterview;
    private Action<string>? _planMakerReportPhase;
    private readonly HashSet<string> _planMakerQuestionDrafts = new(StringComparer.Ordinal);
    internal bool IsPlanMakerActive => _planMakerSessionId is not null;

    // Plan cleanup (2026-11-06 사용자: "정리도 opencode를 통해서 정리해야해 플랜메이커처럼"): one tool-less request on this
    // pane's own OpenCode (the same TUI and server PlanMaker uses), with the model PlanMaker would pick. The request runs
    // in a session of its own, so the terminal's session and any PlanMaker interview stay untouched.
    internal async Task<string> CompletePlanCleanupAsync(string prompt, Action<int>? progress, CancellationToken cancellationToken)
    {
        if (!_usesNativeOpenCodeStatus || !IsReady || !IsRunning || !CanStartNativeAutomation)
            throw new InvalidOperationException("플랜 정리는 준비된 OpenCode 터미널에서 시작할 수 있습니다.");
        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, HeadlessLifetimeCancellation);
        var client = RequireNativeClient();
        string? model = await client.ReadConfiguredPlanMakerModelAsync(await client.ReadAgentsAsync(cleanup.Token), cleanup.Token);
        if (_usesNativeOpenCodeTui && !_session.IsRunning)
            throw new IOException("플랜 정리용 OpenCode 터미널 연결이 끊겼습니다.");
        return await client.CompleteToolLessPromptAsync("Terminal Notes · 플랜 정리", prompt, model, cleanup.Token,
            timeout: TimeSpan.FromMinutes(30), idleLimit: TimeSpan.FromMinutes(3), progress: progress);
    }

    internal async Task<string> CreatePlanWithQuestionsAsync(string prompt, Func<bool> stillOwned,
        Action<string> reportPhase, CancellationToken cancellationToken, Action<string>? reportDraft = null,
        PlanMakerInterviewControl? interview = null, bool completeRecommendedCycle = false)
    {
        if (!_usesNativeOpenCodeStatus || !IsReady || !IsRunning)
            throw new InvalidOperationException("플랜 생성은 준비된 OpenCode 터미널에서 시작할 수 있습니다.");
        using var planning = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, HeadlessLifetimeCancellation);
        if (completeRecommendedCycle)
        {
            planning.CancelAfter(TimeSpan.FromMinutes(5));
            interview ??= new PlanMakerInterviewControl();
            interview.SetRecommendedAnswersEnabled(true);
        }
        CancellationToken token = planning.Token;
        long recommendedCycleInputRevision = _planMakerInputRevision;
        string? previousSession = null;
        string? planningSession = null;
        string? planningModel = null;
        string planningMessage = OpenCodeRecoveryClient.CreateMessageId();
        try
        {
            await _nativeOperationGate.WaitAsync(token);
            try
            {
                if (!stillOwned()) throw new OperationCanceledException(token);
                EnsureNativeSessionSwitchAllowed();
                if (_planMakerSessionId is not null) throw new InvalidOperationException("이미 플랜을 생성하고 있습니다.");
                var client = RequireNativeClient();
                _ = await client.ReadPlanMakerToolsAsync(token);
                JsonElement agents = await client.ReadAgentsAsync(token);
                if (agents.ValueKind != JsonValueKind.Array || !agents.EnumerateArray().Any(agent =>
                    agent.TryGetProperty("name", out var name) && name.GetString() == "plan"))
                    throw new InvalidDataException("이 OpenCode 서버에서 plan 에이전트를 확인하지 못했습니다.");
                previousSession = await EnsureHeadlessSessionAsync();
                planningModel = await client.ReadPlanMakerModelAsync(previousSession, agents, token);
                planningSession = ReadSessionId(await client.CreatePlanMakerSessionAsync(token));
                OpenCodeConversationClient.VerifyPlanMakerPermissions(await client.GetSessionAsync(planningSession, token));
                _autoResume?.Suspend("플랜 만들기 · 실행은 사용자 요청 대기");
                _planMakerSessionId = planningSession;
                _planMakerInterview = interview;
                _planMakerReportPhase = reportPhase;
                // Planning is temporary: the original session mapping remains persisted.
                SwitchPlanningSession(planningSession);
                if (_usesNativeOpenCodeTui) await PreparePlanMakerTuiAsync(planningSession, stillOwned, token);
                recommendedCycleInputRevision = _planMakerInputRevision;
                token.ThrowIfCancellationRequested();
                if (!stillOwned() || _headlessSessionId != planningSession || HasLocalPendingInput() ||
                    completeRecommendedCycle && (!CanAnswerRecommendedQuestion() || interview?.RecommendedAnswersEnabled != true || interview.IsFinishRequested))
                    throw new OperationCanceledException("The planning session or its input ownership changed before submission.", token);
                reportPhase("◌ 플랜 만들기 · OpenCode 질문 준비 중");
                await client.SendPlanMakerAsync(planningSession, prompt, planningMessage, token, planningModel);
                // The interview has started in this conversation: what the user types from now on belongs to it.
                _planMakerStartupHold = false;
            }
            finally { _nativeOperationGate.Release(); }
            await RefreshNativeStateAsync();
            string? lastPhase = null;
            string? lastDraft = null;
            string? lastNativeDraft = null;
            var previousMessages = new HashSet<string>(StringComparer.Ordinal);
            var attemptedRecommendedQuestions = new HashSet<string>(StringComparer.Ordinal);
            var repairedRecommendations = new Dictionary<(string Question, long Revision), (string? Label, string Reason)>();
            var beforeRecommendedAnswer = new HashSet<string>(StringComparer.Ordinal);
            bool recommendedCycleAnswered = false;
            bool recommendedCycleFinalRequested = false;
            string? recommendationNotice = null;
            string? recommendationQuestionId = null;
            bool roundHadQuestion = false;
            string? countedCompletion = null;
            int questionlessCompletions = 0;
            bool wasAwaitingContinuation = false;
            // Finishing asks once for the whole confirmed plan (BuildFinalPlanPrompt) and returns after that answer.
            bool finalRequested = false;
            const string FinalPhase = "플랜메이커 · 확인된 내용을 정리해 plan.md에 저장하는 중입니다.";
            void VerifyRecommendedCycleOwnership()
            {
                token.ThrowIfCancellationRequested();
                if (completeRecommendedCycle && (!stillOwned() || _headlessSessionId != planningSession ||
                    _planMakerSessionId != planningSession || _planMakerInputRevision != recommendedCycleInputRevision ||
                    !CanAnswerRecommendedQuestion() || interview?.RecommendedAnswersEnabled != true || interview.IsFinishRequested))
                    throw new OperationCanceledException("The automatic planning request or its input ownership changed.", token);
            }
            // A message the user types into the PlanMaker's terminal continues the interview instead of stopping it
            // (2026-11-11 사용자: "플랜메이커에서 중간에 내가 입력하면 꺼져버리는데"). Only Loop Full's automatic cycle still stops.
            string? UserFollowUp(JsonElement response) => completeRecommendedCycle || interview is null ? null :
                PlanMakerWorkflow.ReadUserFollowUp(response, planningSession!, planningMessage, previousMessages);
            bool UserTyped(JsonElement response, string stopReason)
            {
                if (UserFollowUp(response) is not null) return true;
                if (PlanMakerWorkflow.HasNewerUserRequest(response, planningSession!, planningMessage))
                    throw new OperationCanceledException(stopReason, token);
                return false;
            }
            string? LatestAssistantId(JsonElement response)
            {
                string? latest = null;
                foreach (JsonElement message in response.EnumerateArray())
                    if (message.TryGetProperty("info", out var info) &&
                        info.TryGetProperty("role", out var role) && role.GetString() == "assistant" &&
                        info.TryGetProperty("sessionID", out var session) && session.GetString() == planningSession &&
                        info.TryGetProperty("parentID", out var parent) && parent.GetString() == planningMessage)
                        latest = info.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
                return latest;
            }
            void ApplyDraft(JsonElement response)
            {
                // During the interview each answer returns only its new conditions; an answer that confirms
                // nothing new returns an empty block, which leaves the draft as it is (Auto Recommand does this often).
                string? updatedDraft = PlanMakerWorkflow.ReadPlanDraft(response, planningSession!, planningMessage,
                    previousMessageIds: interview is null ? null : previousMessages, allowEmpty: completeRecommendedCycle || interview is not null,
                    laterMessageId: UserFollowUp(response));
                if (interview is not null && updatedDraft is not null)
                {
                    string nativeDraft = updatedDraft;
                    updatedDraft = PlanMakerWorkflow.AccumulateDraft(lastDraft ?? "", nativeDraft, lastNativeDraft);
                    lastNativeDraft = nativeDraft;
                }
                if (updatedDraft is not null && updatedDraft != lastDraft)
                {
                    token.ThrowIfCancellationRequested();
                    if (!stillOwned() || _headlessSessionId != planningSession) throw new OperationCanceledException(token);
                    reportDraft?.Invoke(updatedDraft);
                    lastDraft = updatedDraft;
                    interview?.RecordDraftChanged();
                }
            }
            while (true)
            {
                token.ThrowIfCancellationRequested();
                VerifyRecommendedCycleOwnership();
                if (!stillOwned() || _headlessSessionId != planningSession)
                    throw new OperationCanceledException("플랜 생성 세션의 연결이 바뀌었습니다.", token);
                // The server can remain alive after its interactive TUI exits. A native
                // question cannot be answered through that disconnected attachment.
                if (_usesNativeOpenCodeTui && !_session.IsRunning)
                    throw new IOException("플랜메이커 터미널 연결이 끊겼습니다. 반영된 초안을 확인한 뒤 다시 시도하세요.");
                var client = RequireNativeClient();
                OpenCodeConversationClient.VerifyPlanMakerPermissions(await client.GetSessionAsync(planningSession!, token));
                JsonElement messages = await client.ReadMessagesAsync(planningSession!, token);
                planningModel ??= OpenCodeConversationClient.ReadPlanMakerRequestModel(messages, planningSession!, planningMessage);
                string? followUp = UserFollowUp(messages);
                if (interview is not null && followUp is null && PlanMakerWorkflow.HasNewerUserRequest(messages, planningSession!, planningMessage))
                    throw new OperationCanceledException("플랜 대화에 새 사용자 요청이 제출되어 자동 질문을 멈췄습니다.", token);
                // A final answer that failed (provider error, unreadable block) keeps what was already confirmed and saved.
                try { ApplyDraft(messages); }
                catch (Exception exception) when (finalRequested && lastDraft is { Length: > 0 } && exception is InvalidOperationException or InvalidDataException)
                { return lastDraft; }
                // Once OpenCode answers the typed message it becomes the current request, like a continuation;
                // the earlier request's last response was applied just above.
                if (followUp is not null && PlanMakerWorkflow.HasResponseTo(messages, planningSession!, followUp))
                {
                    previousMessages.Add(planningMessage);
                    planningMessage = followUp;
                    followUp = null;
                    roundHadQuestion = false;
                    questionlessCompletions = 0;
                    interview!.SetAwaitingContinuation(false);
                    ApplyDraft(messages);
                }
                string? plan = !completeRecommendedCycle && interview is null
                    ? PlanMakerWorkflow.ReadCompletedPlan(messages, planningSession!, planningMessage) : null;
                JsonElement statuses = await client.ReadStatusAsync(token);
                bool idle = statuses.ValueKind == JsonValueKind.Object &&
                    (!statuses.TryGetProperty(planningSession!, out var status) ||
                     status.TryGetProperty("type", out var kind) && kind.GetString() == "idle");
                JsonElement questions = await client.ReadQuestionsAsync(token);
                if (questions.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("OpenCode 질문 응답 형식이 올바르지 않습니다.");
                JsonElement[] currentQuestions = questions.EnumerateArray().Where(question =>
                    question.TryGetProperty("sessionID", out var id) && id.GetString() == planningSession).ToArray();
                if (currentQuestions.Length > 1 || currentQuestions.Any(question =>
                    !question.TryGetProperty("questions", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() != 1))
                    throw new InvalidDataException("OpenCode가 한 번에 여러 질문을 요청했습니다. 한 가지씩 확인하는 플랜 대화를 다시 시작하세요.");
                bool awaitingQuestion = currentQuestions.Length == 1;
                if (!awaitingQuestion || !currentQuestions[0].TryGetProperty("id", out var noticeId) ||
                    noticeId.ValueKind != JsonValueKind.String || noticeId.GetString() != recommendationQuestionId)
                    recommendationNotice = null;
                if (awaitingQuestion) { roundHadQuestion = true; questionlessCompletions = 0; }
                JsonElement permissions = await client.ReadPermissionsAsync(token);
                if (permissions.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("OpenCode 승인 응답 형식이 올바르지 않습니다.");
                bool awaitingPermission = permissions.EnumerateArray().Any(permission =>
                    permission.TryGetProperty("sessionID", out var id) && id.GetString() == planningSession);
                VerifyRecommendedCycleOwnership();
                if (completeRecommendedCycle && awaitingPermission)
                    throw new InvalidDataException("Loop Full planning requires a permission decision; automatic planning was stopped.");
                token.ThrowIfCancellationRequested();
                if (!stillOwned() || _headlessSessionId != planningSession)
                    throw new OperationCanceledException("플랜 생성 세션의 연결이 바뀌었습니다.", token);
                // Auto Recommand converged (answers stopped changing the plan) or reached its answer budget:
                // leave the pending question unanswered so the dialog finishes the interview there.
                if (awaitingQuestion && interview?.RecommendedAnswersEnabled == true && !interview.IsFinishRequested &&
                    !completeRecommendedCycle && interview.AutoStopReason is { } autoStop)
                {
                    reportPhase("플랜메이커 · " + autoStop + " 자동 추천을 마무리합니다.");
                }
                else if (awaitingQuestion && interview?.RecommendedAnswersEnabled == true && !interview.IsFinishRequested)
                {
                    JsonElement candidate = currentQuestions[0];
                    string? questionId = candidate.TryGetProperty("id", out var candidateId) && candidateId.ValueKind == JsonValueKind.String
                        ? candidateId.GetString() : null;
                    string? answer = PlanMakerWorkflow.ReadRecommendedAnswer(candidate);
                    if (completeRecommendedCycle)
                    {
                        if (attemptedRecommendedQuestions.Count != 0 && (questionId is null || !attemptedRecommendedQuestions.Contains(questionId)))
                            throw new InvalidDataException("Loop Full planning asked another question after its recommended decision.");
                        JsonElement question = candidate.GetProperty("questions")[0];
                        bool multiple = question.TryGetProperty("multiple", out var multipleValue) && multipleValue.ValueKind != JsonValueKind.False;
                        if (string.IsNullOrWhiteSpace(questionId) || multiple)
                            throw new InvalidDataException("Loop Full planning requires one exact single-select question.");
                    }
                    if (recommendationQuestionId != questionId)
                    {
                        recommendationQuestionId = questionId;
                        recommendationNotice = null;
                    }
                    if (string.IsNullOrWhiteSpace(questionId) || attemptedRecommendedQuestions.Contains(questionId))
                        recommendationNotice ??= "플랜메이커 · 전송한 추천 답변의 반영을 확인 중입니다. 필요하면 직접 답변해 주세요.";
                    else if (!awaitingPermission && followUp is null && CanAnswerRecommendedQuestion())
                    {
                        long modeRevision = interview.RecommendedAnswersRevision;
                        long inputRevision = _planMakerInputRevision;
                        var cacheKey = (candidate.GetRawText(), modeRevision);
                        if (answer is null && repairedRecommendations.TryGetValue(cacheKey, out var ranked))
                        {
                            // Already ranked for this question: a null label keeps waiting for the user's own answer.
                            answer = ranked.Label;
                            recommendationNotice ??= answer is null
                                ? "플랜메이커 · 명확한 추천 선택지가 없어 직접 답변을 기다립니다 · " + ranked.Reason
                                : "플랜메이커 · 추천 이유: " + ranked.Reason;
                        }
                        else if (answer is null)
                        {
                            interview.SetCanRequestFinish(false);
                            reportPhase("플랜메이커 · 현재 질문의 선택지와 추천 이유를 확인하는 중입니다.");
                            try
                            {
                                if (!repairedRecommendations.TryGetValue(cacheKey, out var repaired))
                                {
                                    async Task<bool> StillCurrentAsync(CancellationToken rankingToken)
                                    {
                                        if (!stillOwned() || _headlessSessionId != planningSession || _planMakerSessionId != planningSession ||
                                            _planMakerInputRevision != inputRevision || !CanAnswerRecommendedQuestion() ||
                                            !interview.RecommendedAnswersEnabled || interview.IsFinishRequested || interview.RecommendedAnswersRevision != modeRevision)
                                            return false;
                                        OpenCodeConversationClient.VerifyPlanMakerPermissions(await client.GetSessionAsync(planningSession!, rankingToken));
                                        JsonElement livePermissions = await client.ReadPermissionsAsync(rankingToken);
                                        JsonElement liveQuestions = await client.ReadQuestionsAsync(rankingToken);
                                        JsonElement liveMessages = await client.ReadMessagesAsync(planningSession!, rankingToken);
                                        JsonElement[] owned = liveQuestions.ValueKind == JsonValueKind.Array
                                            ? liveQuestions.EnumerateArray().Where(item => item.TryGetProperty("sessionID", out var id) && id.GetString() == planningSession).ToArray() : [];
                                        return owned.Length == 1 && owned[0].GetRawText() == candidate.GetRawText() &&
                                            livePermissions.ValueKind == JsonValueKind.Array && !livePermissions.EnumerateArray().Any(item =>
                                                item.TryGetProperty("sessionID", out var id) && id.GetString() == planningSession) &&
                                            !PlanMakerWorkflow.HasNewerUserRequest(liveMessages, planningSession!, planningMessage) &&
                                            stillOwned() && _headlessSessionId == planningSession && _planMakerSessionId == planningSession &&
                                            _planMakerInputRevision == inputRevision && CanAnswerRecommendedQuestion() && interview.RecommendedAnswersEnabled &&
                                            !interview.IsFinishRequested && interview.RecommendedAnswersRevision == modeRevision;
                                    }
                                    Task<bool> VerifyOnDispatcherAsync(CancellationToken rankingToken) => _browser.Dispatcher.CheckAccess()
                                        ? StillCurrentAsync(rankingToken)
                                        : _browser.Dispatcher.InvokeAsync(() => StillCurrentAsync(rankingToken)).Task.Unwrap();
                                    repaired = await client.RepairPlanMakerRecommendationAsync(candidate, planningModel, VerifyOnDispatcherAsync, token);
                                    repairedRecommendations.Add(cacheKey, repaired);
                                }
                                answer = repaired.Label;
                                if (answer is null && completeRecommendedCycle)
                                    throw new InvalidDataException("Loop Full planning found no recommended answer for its question.");
                                // Only the user can answer this one; Auto Recommand stays on for the next question.
                                recommendationNotice = answer is null
                                    ? "플랜메이커 · 명확한 추천 선택지가 없어 직접 답변을 기다립니다 · " + repaired.Reason
                                    : "플랜메이커 · 추천 이유: " + repaired.Reason;
                            }
                            catch (OperationCanceledException) when (!token.IsCancellationRequested && !completeRecommendedCycle)
                            { continue; }
                            catch (Exception exception) when (!token.IsCancellationRequested && !completeRecommendedCycle &&
                                exception is InvalidDataException or IOException or System.Net.Http.HttpRequestException)
                            {
                                interview.SetRecommendedAnswersEnabled(false);
                                recommendationNotice = "플랜메이커 · 추천을 확인하지 못했습니다. 직접 답변하거나 Auto Recommand를 다시 켜세요. " + exception.Message;
                                continue;
                            }
                            finally
                            {
                                interview.SetCanRequestFinish(true);
                                reportPhase(recommendationNotice ?? "플랜메이커 · 현재 질문을 다시 확인합니다.");
                            }
                        }
                        // A null answer waits for the user's own reply; the loop keeps polling (and can finish) meanwhile.
                        if (answer is not null)
                        {
                            interview.SetCanRequestFinish(false);
                            reportPhase("플랜메이커 · 추천 답변을 보내기 전에 현재 질문을 확인합니다.");
                            await _nativeOperationGate.WaitAsync(token);
                            try
                            {
                                OpenCodeConversationClient.VerifyPlanMakerPermissions(await client.GetSessionAsync(planningSession!, token));
                                JsonElement latestPermissions = await client.ReadPermissionsAsync(token);
                                JsonElement latestQuestions = await client.ReadQuestionsAsync(token);
                                JsonElement latestMessages = await client.ReadMessagesAsync(planningSession!, token);
                                JsonElement[] exactQuestions = latestQuestions.ValueKind == JsonValueKind.Array
                                    ? latestQuestions.EnumerateArray().Where(item => item.TryGetProperty("sessionID", out var id) && id.GetString() == planningSession).ToArray()
                                    : [];
                                bool sameQuestion = exactQuestions.Length == 1 && exactQuestions[0].GetRawText() == candidate.GetRawText();
                                bool permissionBlocked = latestPermissions.ValueKind != JsonValueKind.Array || latestPermissions.EnumerateArray().Any(item =>
                                    item.TryGetProperty("sessionID", out var id) && id.GetString() == planningSession);
                                token.ThrowIfCancellationRequested();
                                if (!stillOwned() || _headlessSessionId != planningSession || _planMakerSessionId != planningSession)
                                    throw new OperationCanceledException(token);
                                bool userTyped = UserTyped(latestMessages, "플랜 대화에 새 사용자 요청이 제출되어 추천 답변을 멈췄습니다.");
                                VerifyRecommendedCycleOwnership();
                                if (userTyped) recommendationNotice = "플랜메이커 · 입력 중에는 추천 답변을 보내지 않습니다.";
                                else if (sameQuestion && !permissionBlocked && interview.RecommendedAnswersEnabled && !interview.IsFinishRequested &&
                                    interview.RecommendedAnswersRevision == modeRevision && _planMakerInputRevision == inputRevision && CanAnswerRecommendedQuestion())
                                {
                                    // Record before POST: a lost transport acknowledgment must never resend this answer.
                                    attemptedRecommendedQuestions.Add(questionId);
                                    if (completeRecommendedCycle)
                                        foreach (JsonElement message in latestMessages.EnumerateArray())
                                            if (message.TryGetProperty("info", out var info) && info.TryGetProperty("role", out var role) &&
                                                role.GetString() == "assistant" && info.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                                                beforeRecommendedAnswer.Add(id.GetString()!);
                                    reportPhase("플랜메이커 · 추천 답변을 선택하고 전송 중입니다.");
                                    recommendationNotice = "플랜메이커 · 추천 답변 전송: " + answer;
                                    try
                                    {
                                        await client.ReplyQuestionAsync(questionId, [[answer]], token);
                                        if (completeRecommendedCycle) recommendedCycleAnswered = true;
                                        else interview.RecordRecommendedAnswer();
                                    }
                                    catch (Exception exception) when ((exception is System.Net.Http.HttpRequestException or IOException or TaskCanceledException) && !token.IsCancellationRequested)
                                    {
                                        interview.SetRecommendedAnswersEnabled(false);
                                        if (completeRecommendedCycle)
                                            throw new IOException("The recommended planning answer could not be confirmed.", exception);
                                        recommendationNotice = "플랜메이커 · 추천 답변 접수를 확인하지 못했습니다. 질문을 확인하고 직접 답변해 주세요.";
                                    }
                                    reportPhase(recommendationNotice);
                                    lastPhase = recommendationNotice;
                                    continue;
                                }
                            }
                            finally
                            {
                                _nativeOperationGate.Release();
                                interview.SetCanRequestFinish(true);
                                reportPhase(recommendationNotice ?? "플랜메이커 · 질문에 답해주세요.");
                            }
                        }
                    }
                    else recommendationNotice = awaitingPermission
                        ? "플랜메이커 · 승인을 기다리는 동안 추천 답변을 보내지 않습니다."
                        : "플랜메이커 · 입력 중에는 추천 답변을 보내지 않습니다.";
                }
                // An answer already submitted in the native TUI must drain before
                // finishing. A pending next question is a safe stopping boundary.
                // A message the user typed meanwhile is answered and applied before the interview finishes.
                if (!completeRecommendedCycle && interview?.IsFinishRequested == true && interview.CanRequestFinish && (idle || awaitingQuestion) &&
                    followUp is null)
                {
                    await _nativeOperationGate.WaitAsync(token);
                    try
                    {
                        long finishInputRevision = _planMakerInputRevision;
                        JsonElement finishStatuses = await client.ReadStatusAsync(token);
                        JsonElement finishQuestions = await client.ReadQuestionsAsync(token);
                        bool finishIdle = finishStatuses.ValueKind == JsonValueKind.Object &&
                            (!finishStatuses.TryGetProperty(planningSession!, out var finishStatus) ||
                             finishStatus.TryGetProperty("type", out var finishKind) && finishKind.GetString() == "idle");
                        bool finishAtQuestion = finishQuestions.ValueKind == JsonValueKind.Array && finishQuestions.EnumerateArray().Any(question =>
                            question.TryGetProperty("sessionID", out var id) && id.GetString() == planningSession);
                        string? finishQuestionId = finishAtQuestion ? finishQuestions.EnumerateArray().First(question =>
                            question.TryGetProperty("sessionID", out var id) && id.GetString() == planningSession).GetProperty("id").GetString() : null;
                        if (finishIdle || finishAtQuestion)
                        {
                            // Read after the safe boundary: an answer may have completed
                            // between the first draft read and its idle/question snapshot.
                            JsonElement finishMessages = await client.ReadMessagesAsync(planningSession!, token);
                            bool userTyped = UserTyped(finishMessages, "플랜 대화의 사용자 요청이 바뀌었습니다.");
                            ApplyDraft(finishMessages);
                            JsonElement afterStatuses = await client.ReadStatusAsync(token);
                            JsonElement afterQuestions = await client.ReadQuestionsAsync(token);
                            bool afterIdle = afterStatuses.ValueKind == JsonValueKind.Object &&
                                (!afterStatuses.TryGetProperty(planningSession!, out var afterStatus) ||
                                 afterStatus.TryGetProperty("type", out var afterKind) && afterKind.GetString() == "idle");
                            bool sameQuestion = finishQuestionId is not null && afterQuestions.ValueKind == JsonValueKind.Array &&
                                afterQuestions.EnumerateArray().Any(question => question.TryGetProperty("sessionID", out var id) && id.GetString() == planningSession &&
                                    question.TryGetProperty("id", out var questionId) && questionId.GetString() == finishQuestionId);
                            token.ThrowIfCancellationRequested();
                            if (!stillOwned() || _headlessSessionId != planningSession) throw new OperationCanceledException(token);
                            if (!userTyped && (afterIdle || sameQuestion) && !HasLocalPendingInput() && interview.CanRequestFinish)
                            {
                                // Re-read once more after the last safe-state check so a
                                // response finishing during those reads is included too.
                                JsonElement finalMessages = await client.ReadMessagesAsync(planningSession!, token);
                                userTyped = UserTyped(finalMessages, "플랜 대화의 사용자 요청이 바뀌었습니다.");
                                ApplyDraft(finalMessages);
                                if (!userTyped && (sameQuestion || PlanMakerWorkflow.HasCompletedAssistant(finalMessages, planningSession!, planningMessage)))
                                {
                                    token.ThrowIfCancellationRequested();
                                    if (!stillOwned() || _headlessSessionId != planningSession) throw new OperationCanceledException(token);
                                    // Text still in the prompt only delays finishing (the next poll checks again).
                                    if (!HasLocalPendingInput() && interview.CanRequestFinish && _planMakerInputRevision == finishInputRevision)
                                    {
                                        if (finalRequested) return lastDraft ?? "";
                                        // Before finishing, one request for the whole confirmed plan: plan.md gets the conversation even
                                        // when the model never marked it (2026-11-11 사용자: 플랜메이커가 끝나도 plan.md에 저장이 안 됨).
                                        // An open question is dismissed and the session stopped first so the request runs at once.
                                        if (sameQuestion && finishQuestionId is not null)
                                        {
                                            try { await client.RejectQuestionAsync(finishQuestionId, token); }
                                            catch (Exception exception) when (exception is System.Net.Http.HttpRequestException or IOException or InvalidDataException)
                                            { System.Diagnostics.Trace.WriteLine(exception); }
                                            await client.AbortAsync(planningSession!, token);
                                            for (int wait = 0; wait < 40; wait++)
                                            {
                                                JsonElement stopped = await client.ReadStatusAsync(token);
                                                if (stopped.ValueKind == JsonValueKind.Object && (!stopped.TryGetProperty(planningSession!, out var stoppedStatus) ||
                                                    stoppedStatus.TryGetProperty("type", out var stoppedKind) && stoppedKind.GetString() == "idle")) break;
                                                await Task.Delay(250, token);
                                            }
                                        }
                                        string finalMessage = OpenCodeRecoveryClient.CreateMessageId();
                                        await client.SendPlanMakerAsync(planningSession!, PlanMakerWorkflow.BuildFinalPlanPrompt(), finalMessage, token, planningModel);
                                        previousMessages.Add(planningMessage);
                                        planningMessage = finalMessage;
                                        finalRequested = true;
                                        roundHadQuestion = false;
                                        reportPhase(FinalPhase);
                                        lastPhase = FinalPhase;
                                    }
                                }
                            }
                        }
                    }
                    finally { _nativeOperationGate.Release(); }
                }
                bool completed = PlanMakerWorkflow.HasCompletedAssistant(messages, planningSession!, planningMessage);
                if (completeRecommendedCycle && idle && !awaitingQuestion && !awaitingPermission && completed)
                {
                    if (!recommendedCycleAnswered)
                        throw new InvalidDataException("Loop Full planning completed without a recommended native decision.");
                    // An assistant that existed before our answer cannot confirm that answer.
                    if (LatestAssistantId(messages) is { } assistantId && !beforeRecommendedAnswer.Contains(assistantId))
                    {
                        await _nativeOperationGate.WaitAsync(token);
                        try
                        {
                            OpenCodeConversationClient.VerifyPlanMakerPermissions(await client.GetSessionAsync(planningSession!, token));
                            JsonElement finalMessages = await client.ReadMessagesAsync(planningSession!, token);
                            JsonElement finalStatuses = await client.ReadStatusAsync(token);
                            JsonElement finalQuestions = await client.ReadQuestionsAsync(token);
                            JsonElement finalPermissions = await client.ReadPermissionsAsync(token);
                            bool finalIdle = finalStatuses.ValueKind == JsonValueKind.Object &&
                                (!finalStatuses.TryGetProperty(planningSession!, out var finalStatus) ||
                                 finalStatus.TryGetProperty("type", out var finalKind) && finalKind.GetString() == "idle");
                            bool finalBlocked = finalQuestions.ValueKind != JsonValueKind.Array || finalPermissions.ValueKind != JsonValueKind.Array ||
                                finalQuestions.EnumerateArray().Any(item => item.TryGetProperty("sessionID", out var id) && id.GetString() == planningSession) ||
                                finalPermissions.EnumerateArray().Any(item => item.TryGetProperty("sessionID", out var id) && id.GetString() == planningSession);
                            VerifyRecommendedCycleOwnership();
                            if (PlanMakerWorkflow.HasNewerUserRequest(finalMessages, planningSession!, planningMessage))
                                throw new OperationCanceledException("A newer user request replaced the recommended planning request.", token);
                            if (finalIdle && !finalBlocked && PlanMakerWorkflow.HasCompletedAssistant(finalMessages, planningSession!, planningMessage) &&
                                LatestAssistantId(finalMessages) is { } finalAssistantId && !beforeRecommendedAnswer.Contains(finalAssistantId))
                            {
                                string? finalPlan = PlanMakerWorkflow.ReadPlanDraft(finalMessages, planningSession!, planningMessage,
                                    requireCompleted: true, previousMessageIds: previousMessages, allowEmpty: true);
                                if (finalPlan is not null)
                                {
                                    VerifyRecommendedCycleOwnership();
                                    return finalPlan;
                                }
                                if (recommendedCycleFinalRequested)
                                    throw new InvalidDataException("Loop Full planning did not return a completed marked plan delta.");
                                string nextMessage = OpenCodeRecoveryClient.CreateMessageId();
                                VerifyRecommendedCycleOwnership();
                                await client.SendPlanMakerAsync(planningSession!, PlanMakerWorkflow.BuildRecommendedCycleCompletionPrompt(),
                                    nextMessage, token, planningModel);
                                previousMessages.Add(planningMessage);
                                planningMessage = nextMessage;
                                recommendedCycleFinalRequested = true;
                            }
                        }
                        finally { _nativeOperationGate.Release(); }
                    }
                }
                if (plan is not null && idle && !awaitingQuestion && !awaitingPermission) return plan;
                if (interview is null && idle && !awaitingQuestion && !awaitingPermission && completed)
                    throw new InvalidDataException("OpenCode가 완성된 플랜 형식을 반환하지 않았습니다. 질문 답변과 응답을 확인한 뒤 다시 시도하세요.");
                if (!completeRecommendedCycle && interview is not null && followUp is null && idle && !awaitingQuestion && !awaitingPermission && completed)
                {
                    if (countedCompletion != planningMessage)
                    {
                        countedCompletion = planningMessage;
                        questionlessCompletions = roundHadQuestion ? 0 : questionlessCompletions + 1;
                        if (questionlessCompletions >= 2) interview.SetAwaitingContinuation(true);
                    }
                    if (!interview.IsFinishRequested && (!interview.IsAwaitingContinuation || interview.HasContinuationRequest) &&
                        !HasLocalPendingInput() && !_manualInputOverride && !IsUserInputBlocked && IsReady && IsRunning && !_failed)
                    {
                        await _nativeOperationGate.WaitAsync(token);
                        try
                        {
                            OpenCodeConversationClient.VerifyPlanMakerPermissions(await client.GetSessionAsync(planningSession!, token));
                            // Native answers or user input may arrive during any read. Recheck
                            // the exact session immediately before the single next submission.
                            JsonElement latestStatuses = await client.ReadStatusAsync(token);
                            JsonElement latestQuestions = await client.ReadQuestionsAsync(token);
                            JsonElement latestPermissions = await client.ReadPermissionsAsync(token);
                            JsonElement latestMessages = await client.ReadMessagesAsync(planningSession!, token);
                            planningModel ??= OpenCodeConversationClient.ReadPlanMakerRequestModel(latestMessages, planningSession!, planningMessage);
                            bool latestIdle = latestStatuses.ValueKind == JsonValueKind.Object &&
                                (!latestStatuses.TryGetProperty(planningSession!, out var latestStatus) ||
                                 latestStatus.TryGetProperty("type", out var latestKind) && latestKind.GetString() == "idle");
                            bool latestBlocked = latestQuestions.ValueKind != JsonValueKind.Array || latestPermissions.ValueKind != JsonValueKind.Array ||
                                latestQuestions.EnumerateArray().Any(item => item.TryGetProperty("sessionID", out var id) && id.GetString() == planningSession) ||
                                latestPermissions.EnumerateArray().Any(item => item.TryGetProperty("sessionID", out var id) && id.GetString() == planningSession);
                            token.ThrowIfCancellationRequested();
                            if (!stillOwned() || _headlessSessionId != planningSession || _planMakerSessionId != planningSession)
                                throw new OperationCanceledException("플랜 생성 세션의 연결이 바뀌었습니다.", token);
                            // The user's own message already continues the interview: no continuation prompt on top of it.
                            bool userTyped = UserTyped(latestMessages, "플랜 대화에 새 사용자 요청이 제출되어 자동 질문을 멈췄습니다.");
                            if (!userTyped && !interview.IsFinishRequested && latestIdle && !latestBlocked &&
                                PlanMakerWorkflow.HasCompletedAssistant(latestMessages, planningSession!, planningMessage) &&
                                !HasLocalPendingInput() && !_manualInputOverride && !IsUserInputBlocked &&
                                IsReady && IsRunning && !_failed)
                            {
                                string nextMessage = OpenCodeRecoveryClient.CreateMessageId();
                                await client.SendPlanMakerAsync(planningSession!, PlanMakerWorkflow.BuildContinuationPrompt(), nextMessage, token, planningModel);
                                previousMessages.Add(planningMessage);
                                planningMessage = nextMessage;
                                roundHadQuestion = false;
                                if (interview.IsAwaitingContinuation) questionlessCompletions = 0;
                                interview.SetAwaitingContinuation(false);
                            }
                        }
                        finally { _nativeOperationGate.Release(); }
                    }
                }
                bool awaitingContinuation = interview?.IsAwaitingContinuation == true;
                string phase = finalRequested ? FinalPhase
                    : interview?.IsFinishRequested == true
                    ? "플랜메이커 · 현재 답변 반영을 기다린 뒤 질문을 마칩니다."
                    : recommendationNotice is not null && awaitingQuestion
                    ? recommendationNotice
                    : awaitingContinuation
                    ? "플랜메이커 · AI가 질문을 멈췄습니다. 질문 계속 또는 플랜으로 돌아가기를 선택하세요."
                    : awaitingQuestion || awaitingPermission || NativeOpenCodeStatus == OpenCodeStatusKind.NeedsInput
                    ? "◌ 플랜 만들기 · OpenCode 질문에 답해주세요"
                    : "◌ 플랜 만들기 · 답변을 반영하는 중";
                if (phase != lastPhase || awaitingContinuation != wasAwaitingContinuation)
                { reportPhase(phase); lastPhase = phase; wasAwaitingContinuation = awaitingContinuation; }
                await Task.Delay(500, token);
            }
        }
        finally
        {
            _planMakerStartupHold = false;
            interview?.SetAwaitingContinuation(false);
            interview?.SetCanRequestFinish(true);
            _autoResume?.Suspend("플랜 만들기 종료 · 실행은 사용자 요청 대기");
            if (planningSession is not null && !_disposed && !HeadlessLifetimeCancellation.IsCancellationRequested)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await RequireNativeClient().AbortAsync(planningSession, cleanup.Token); }
                catch (Exception exception) { System.Diagnostics.Trace.WriteLine(exception); }
                if (previousSession is not null)
                {
                    bool gateHeld = false;
                    try
                    {
                        await _nativeOperationGate.WaitAsync(cleanup.Token);
                        gateHeld = true;
                        // Restore only our own temporary selection, never a newer user selection.
                        if (_planMakerSessionId == planningSession && _headlessSessionId == planningSession)
                        {
                            if (_usesNativeOpenCodeTui && !HasLocalPendingInput() && !_manualInputOverride)
                            {
                                if (_tuiConnectionHooks is { } hooks) await hooks.SelectSession(previousSession, cleanup.Token);
                                else await SelectOpenCodeTuiSessionAsync(previousSession, cleanup.Token);
                            }
                            if (_planMakerSessionId == planningSession && _headlessSessionId == planningSession)
                                SwitchPlanningSession(previousSession);
                        }
                    }
                    catch (Exception exception)
                    {
                        if (gateHeld && _planMakerSessionId == planningSession && _headlessSessionId == planningSession)
                            SwitchPlanningSession(previousSession);
                        PostNativeNotice("기존 OpenCode 대화로 다시 연결하지 못했습니다 · " + exception.Message, "error");
                    }
                    finally { if (gateHeld) _nativeOperationGate.Release(); }
                }
            }
            _planMakerSessionId = null;
            _planMakerInterview = null;
            _planMakerReportPhase = null;
            _planMakerQuestionDrafts.Clear();
            _planMakerInputRevision++;
            if (!_disposed) await RefreshNativeStateAsync();
        }
    }

    private bool CanAnswerRecommendedQuestion() => _planMakerInterview?.IsAnswerSubmissionPending != true && !HasLocalPendingInput() && !_manualInputOverride && !IsUserInputBlocked &&
        IsReady && IsRunning && !_failed && !_disposed && !HeadlessLifetimeCancellation.IsCancellationRequested;

    private async Task PreparePlanMakerTuiAsync(string sessionId, Func<bool> stillOwned, CancellationToken token)
    {
        bool Owned() => stillOwned() && _planMakerSessionId == sessionId && _headlessSessionId == sessionId &&
            !HasLocalPendingInput() && !_manualInputOverride;
        token.ThrowIfCancellationRequested();
        if (!Owned())
            throw new OperationCanceledException("The planning TUI is no longer owned by this request.", token);

        // Selection acknowledges a broadcast, which can precede the TUI subscriber.
        // Attach the exact temporary session before submitting its sole prompt, while
        // no native question or answer exists. Never restart a TUI during its answers.
        try { await ConnectRetainedOpenCodeTuiAsync(replaceSession: true, token, stillOwned: Owned); }
        catch (Exception exception) when (exception is not OperationCanceledException &&
            !token.IsCancellationRequested && Owned() && TuiConnectionOwned)
        {
            ShowOpenCodeTuiConnectionFailure();
            throw;
        }
        token.ThrowIfCancellationRequested();
        if (!Owned())
            throw new OperationCanceledException("The planning TUI changed while connecting.", token);
        _failed = false;
        _ready = true;
        FlushHeldStartupInput();
        _notice.Visibility = System.Windows.Visibility.Collapsed;
        ApplyActivityChange();
    }

    private void SwitchPlanningSession(string sessionId)
    {
        _planMakerQuestionDrafts.Clear();
        _planMakerInputRevision++;
        _boundAutomationSession = sessionId;
        _externalContinuationSession = null;
        _headlessSessionId = sessionId;
        _nativeViewGeneration++;
        _openCodeStatus?.SetSessionScope(sessionId);
        ScheduleNativeStateRefresh();
    }
}
}
#endregion
#endif

#if TN_WPF || TN_SOURCE_TERMINALPANE_RECOVERY_CS
#region TerminalPane.Recovery.cs
namespace TerminalNotes
{
using global::System.IO;
using global::TerminalNotes.Services;


public sealed partial class TerminalPane
{
    private OpenCodeAutoResumeController? _autoResume;
    private string? _autoResumeStatus;
    private long _autoResumeAdmissionGeneration;
    private AutoResumeResourceWaitNotice? _autoResumeResourceWait;
    internal sealed record AutoResumeResourceWaitNotice(string EpisodeId, string Reason, Func<bool> StillRelevant);
    internal event Action<AutoResumeResourceWaitNotice>? AutoResumeResourceWaitStarted;
    internal bool IsAutoResumeResourceWaiting => Volatile.Read(ref _autoResumeResourceWait)?.StillRelevant() == true;
    internal Func<OpenCodeStalledRestart, Task<OpenCodeRestartOutcome>>? RestartStalledProcessAsync { get; set; }
    internal Func<OpenCodeRecommendedPlanRequest, CancellationToken, Task<OpenCodeRecommendedPlanResult?>>? RecommendPlanAsync { get; set; }
    internal bool HasUnfinishedAutoResumeWork => _autoResume?.HasUnfinishedWork == true;
    internal bool HasContinuousImprovementWork => _autoResume?.HasContinuousImprovementWork == true;
    internal bool IsContinuousImprovementEnabled => _autoResume?.IsContinuousImprovementEnabled == true;
    internal bool IsContinuousImprovementActive => _autoResume?.IsContinuousImprovementActive == true;
    internal bool IsAutoResumePaused => _autoResume?.IsRecoveryPaused == true;
    internal DateTimeOffset? AutoResumeLastProgressAt => _autoResume?.LastSubstantiveProgressAt;
    internal bool AutoResumeWaitsForPerson => _autoResume?.WaitsForPersonAfterError == true;

    internal const string LoopDecideYourselfAnswer =
        "지금 답할 사람이 없습니다. 되돌릴 수 있는 결정은 플랜에 맞고 가장 안전한 쪽으로 정해 진행하고, 정한 내용과 가정은 보고에 남기세요.";

    // Loop runs unattended (사용자: 중간에 멈추면 Loop가 무슨 의미가 있어): a question this terminal's OpenCode asked and nobody
    // answered gets its recommended option, or a reply to decide by itself. The OpenCode server is this terminal's own.
    // Returns what was done, or null when no question is pending (a permission request stays the user's).
    internal async Task<string?> AnswerLoopQuestionAsync(CancellationToken token)
    {
        if (_disposed || _headlessOpenCodeClient is not { } client || IsPlanMakerActive) return null;
        global::System.Text.Json.JsonElement pending = await client.ReadQuestionsAsync(token);
        if (pending.ValueKind != global::System.Text.Json.JsonValueKind.Array) return null;
        foreach (var request in pending.EnumerateArray())
        {
            if (request.ValueKind != global::System.Text.Json.JsonValueKind.Object ||
                !request.TryGetProperty("id", out var id) || id.GetString() is not { Length: > 0 } requestId) continue;
            if (PlanMakerWorkflow.LoopQuestionReply(request, LoopDecideYourselfAnswer) is not { } reply)
            {
                await client.RejectQuestionAsync(requestId, token);
                return "추천 선택지가 없어 질문을 넘겼습니다";
            }
            await client.ReplyQuestionAsync(requestId, reply, token);
            return reply[0][0] != LoopDecideYourselfAnswer ? "추천 답변으로 진행했습니다: " + reply[0][0] : "스스로 판단해 진행하도록 답했습니다";
        }
        return null;
    }
    internal bool HasVerifiedTaskCompletion => _autoResume?.HasVerifiedCompletion == true;
    internal bool HasVerifiedNotificationStall => _autoResume?.HasVerifiedNotificationStall == true;
    internal string? AutoResumeStatus => _autoResume?.Status;
    internal void EnableTaskCompletionMonitoring() => _autoResume?.EnableTaskCompletionMonitoring();

    private void InitializeAutoResume(Uri endpoint, string? planPath)
    {
        BindSavedPlanExecutionPath(planPath);
        var client = new OpenCodeRecoveryClient(endpoint, WorkingDirectory);
        _autoResume = new OpenCodeAutoResumeController(WorkingDirectory, planPath, client,
            () => !_disposed && !_failed && !IsUserInputBlocked && !_manualInputOverride && !_automationBusy &&
                  IsRunning && _openCodeStatus?.Snapshot.Kind is not (null or OpenCodeStatusKind.Unknown),
            acquireContinuationAdmission: (waiting, token) => AcquireAutoResumeAdmissionAsync(waiting, token));
        _autoResume.RestartStalledProcessWithOutcomeAsync = request => Dispatcher.InvokeAsync(async () =>
            RestartStalledProcessAsync is { } restart ? await restart(request) : OpenCodeRestartOutcome.Paused).Task.Unwrap();
        _autoResume.RecommendPlanAsync = (request, token) => Dispatcher.InvokeAsync(async () =>
        {
            token.ThrowIfCancellationRequested();
            return !_disposed && request.StillOwned() && RecommendPlanAsync is { } recommend
                ? await recommend(request, token) : null;
        }).Task.Unwrap();
        _openCodeStatus!.RecoverySignalReceived += _autoResume.ObserveNativeSignal;
        _autoResume.StatusChanged += status =>
        {
            if (_disposed || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;
                _autoResumeStatus = _autoResume?.Status;
                ApplyActivityChange();
            });
        };
        _autoResume.DiagnosticEmitted += diagnostic =>
        {
            foreach (Action<OpenCodeAutoResumeDiagnostic> handler in AutoResumeDiagnosticEmitted?.GetInvocationList() ?? [])
            {
                try { handler(diagnostic); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Auto-resume diagnostic observer failed: {ex.GetType().Name}"); }
            }
        };
        // The dispatcher exit handlers first retain the confirmed request for process recovery.
        // Disposing here on the process thread would lose that evidence before those handlers run.
        _autoResume.Start();
    }

    private async Task<IDisposable> AcquireAutoResumeAdmissionAsync(Action<string> waiting, CancellationToken token,
        Func<Action<StartupResourceWait>, CancellationToken, Task<IDisposable>>? acquire = null)
    {
        long generation = Interlocked.Increment(ref _autoResumeAdmissionGeneration);
        int pending = 1, notified = 0;
        AutoResumeResourceWaitNotice? notice = null;
        bool StillRelevant() => Volatile.Read(ref pending) != 0 && !token.IsCancellationRequested &&
            generation == Interlocked.Read(ref _autoResumeAdmissionGeneration) && !_disposed && !_failed &&
            !_manualInputOverride && !IsUserInputBlocked && !WasContinuedExternally;
        try
        {
            return await (acquire ?? TerminalStartupAdmission.AcquireSharedAsync)(wait =>
            {
                if (!StillRelevant()) return;
                string reason = wait.Reason.Replace("터미널 시작", "자동 진행", StringComparison.Ordinal);
                waiting(reason);
                // Recovery samples and reason/metric changes belong to this same pending admission.
                if (wait.Code is not ("cpu" or "memory") || Interlocked.Exchange(ref notified, 1) != 0 || !StillRelevant()) return;
                notice = new(Guid.NewGuid().ToString("N"), reason, StillRelevant);
                Volatile.Write(ref _autoResumeResourceWait, notice);
                foreach (Action<AutoResumeResourceWaitNotice> handler in AutoResumeResourceWaitStarted?.GetInvocationList() ?? [])
                {
                    try { handler(notice); }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Resource-wait notification observer failed: {ex.GetType().Name}"); }
                }
            }, token).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref pending, 0);
            if (notice is not null) Interlocked.CompareExchange(ref _autoResumeResourceWait, null, notice);
        }
    }

    internal async Task<bool> StopStalledProcessAsync(OpenCodeStalledRestart request)
    {
        if (!Dispatcher.CheckAccess()) throw new InvalidOperationException("Recovery requires the pane dispatcher.");
        if (_disposed || _failed || !IsReady || WasContinuedExternally || IsUserInputBlocked ||
            HasLocalPendingInput() || _manualInputOverride || !request.CanRestart()) return false;
        if (!await request.VerifyAsync() || !request.CanRestart() || HasLocalPendingInput() ||
            NativeOpenCodeStatus == OpenCodeStatusKind.NeedsInput) return false;
        IsUserInputBlocked = true;
        CancelNativeAutomation();
        // Wait for this pane's owned job to exit before another OpenCode can write the same project.
        await StopOwnedOpenCodeProcessAsync();
        _failed = true;
        _openCodeStatus?.Dispose();
        ApplyActivityChange();
        return !IsRunning;
    }
    internal void PreserveRestartInstruction(OpenCodeStalledRestart request) =>
        _autoResume?.PreserveRestartInstruction(request.Prompt, request.OriginalInstruction);

    public void SetAutoResumeEnabled(bool enabled) => _autoResume?.SetEnabled(enabled);

    public void ConfigureSingleContinuousImprovement(string project, bool enabled, DateTimeOffset? until = null, bool recommendPlan = false)
    {
        string expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(WorkingDirectory));
        string supplied = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));
        if (!string.Equals(expected, supplied, StringComparison.OrdinalIgnoreCase)) return;
        _autoResume?.SetContinuousImprovement(enabled, until, recommendPlan);
    }
}
}
#endregion
#endif

#if TN_WPF || TN_SOURCE_TERMINALPANE_RESOURCEADMISSION_CS
#region TerminalPane.ResourceAdmission.cs
namespace TerminalNotes
{
using global::System.Runtime.ExceptionServices;
using global::TerminalNotes.Services;


public sealed partial class TerminalPane
{
    private readonly TaskCompletionSource _resourceAdmissionRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception?> _resourceAdmissionCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _waitingForResources;
    private string? _resourceWaitReason;
    private string? _resourceWaitCode;
    private long _resourceWaitGeneration;
    private bool _allowResourceAdmissionRunOnce;
    private Func<bool>? _resourceRunOnceRequest;
    internal bool IsWaitingForResources => Volatile.Read(ref _waitingForResources) != 0;
    internal string? ResourceWaitReason => Volatile.Read(ref _resourceWaitReason);
    internal bool CanRunResourceAdmissionOnce => _allowResourceAdmissionRunOnce && !_disposed && !_failed && !_ready &&
        !HeadlessLifetimeCancellation.IsCancellationRequested && Volatile.Read(ref _resourceRunOnceRequest) is not null;

    internal void EnableResourceAdmissionRunOnce() => _allowResourceAdmissionRunOnce = true;

    internal bool TryRunResourceAdmissionOnce()
    {
        if (!CanRunResourceAdmissionOnce) return false;
        var request = Volatile.Read(ref _resourceRunOnceRequest);
        if (request is null || !request()) return false;
        Interlocked.CompareExchange(ref _resourceRunOnceRequest, null, request);
        ShowResourceAdmissionWait(ResourceWaitReason);
        return true;
    }

    /// <summary>Bounds load/layout initialization, then waits for resource admission without a startup deadline.</summary>
    internal async Task WaitForResourceAdmissionAsync(CancellationToken cancellationToken = default)
    {
        if (!_usesNativeOpenCodeStatus || _ready) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, HeadlessLifetimeCancellation);
        using var cancellation = cancellationToken.Register(() =>
        {
            if (!_resourceAdmissionCompletion.Task.IsCompleted)
            {
                MarkCancelledResourceStartup(new OperationCanceledException(cancellationToken));
                try { _headlessLifetimeCancellation.Cancel(); } catch (ObjectDisposedException) { }
            }
        });
        Task ready = EnsureReadyAsync(linked.Token);
        try
        {
            Task first = await Task.WhenAny(_resourceAdmissionRequested.Task, ready)
                .WaitAsync(TerminalExecutionTiming.StartupReadyTimeout, linked.Token).ConfigureAwait(false);
            if (first == ready) await ready.ConfigureAwait(false);
            Task admitted = await Task.WhenAny(_resourceAdmissionCompletion.Task, ready).WaitAsync(linked.Token).ConfigureAwait(false);
            if (admitted == ready) await ready.ConfigureAwait(false);
            Exception? failure = await _resourceAdmissionCompletion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        catch (TimeoutException)
        {
            MarkCancelledResourceStartup(new OperationCanceledException("Terminal initialization timed out."));
            try { _headlessLifetimeCancellation.Cancel(); } catch (ObjectDisposedException) { }
            throw new TimeoutException("터미널 화면 초기화가 제한 시간 안에 완료되지 않았습니다.");
        }
        finally
        {
            _ = ready.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void MarkCancelledResourceStartup(OperationCanceledException exception)
    {
        if (_disposed || _ready || _failed) return;
        // A cancelled lifetime cannot be reused by the next project start.
        _failed = true;
        _terminalStartedCompletion.TrySetResult(exception);
        _resourceAdmissionCompletion.TrySetResult(exception);
        UpdateResourceAdmissionWait(null);
        void Apply()
        {
            if (_disposed || _ready) return;
            ReportLifecycleStage("native_start_cancelled");
            StatusChanged?.Invoke("터미널 시작 취소");
            ApplyActivityChange();
        }
        if (Dispatcher.CheckAccess()) Apply();
        else if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished) Dispatcher.BeginInvoke((Action)Apply);
    }

    private void UpdateResourceAdmissionWait(StartupResourceWait? wait)
    {
        if (ResourceWaitReason == wait?.Reason && ReferenceEquals(Volatile.Read(ref _resourceRunOnceRequest), wait?.TryRunOnce)) return;
        Volatile.Write(ref _resourceRunOnceRequest, wait?.TryRunOnce);
        Volatile.Write(ref _waitingForResources, wait is null ? 0 : 1);
        Volatile.Write(ref _resourceWaitReason, wait?.Reason);
        long generation = Interlocked.Increment(ref _resourceWaitGeneration);
        void Apply()
        {
            if (_disposed || generation != Volatile.Read(ref _resourceWaitGeneration)) return;
            ShowResourceAdmissionWait(wait?.Reason);
            if (wait is not null)
            {
                if (_resourceWaitCode != wait.Code)
                { _resourceWaitCode = wait.Code; ReportLifecycleStage("native_resource_wait_" + wait.Code); }
                StatusChanged?.Invoke(wait.Reason);
            }
            ApplyActivityChange();
        }
        if (Dispatcher.CheckAccess()) Apply();
        else if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished) Dispatcher.BeginInvoke((Action)Apply);
    }
}
}
#endregion
#endif

#if TN_WPF || TN_SOURCE_TERMINALPANE_RESOURCEADMISSIONUI_CS
#region TerminalPane.ResourceAdmissionUi.cs
namespace TerminalNotes
{
using global::System.Windows;
using global::System.Windows.Automation;
using global::System.Windows.Controls;
using global::System.Windows.Media;


public sealed partial class TerminalPane
{
    private Border? _resourceAdmissionOverlay;
    private TextBlock? _resourceAdmissionReason;
    private Button? _resourceAdmissionRunOnce;

    internal void ShowResourceAdmissionWait(string? reason)
    {
        if (_disposed || Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowResourceAdmissionWait(reason));
            return;
        }

        bool visible = !string.IsNullOrWhiteSpace(reason) && !_ready && !_failed;
        if (!visible)
        {
            if (_resourceAdmissionOverlay is not null)
                _resourceAdmissionOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        if (_resourceAdmissionOverlay is null)
        {
            _resourceAdmissionReason = new TextBlock
            {
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0),
                FontSize = 14
            };
            var content = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 460,
                Margin = new Thickness(12)
            };
            content.Children.Add(new TextBlock
            {
                Text = "실행 대기 중",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(_resourceAdmissionReason);
            content.Children.Add(new TextBlock
            {
                Text = "환경이 안정되면 자동으로 실행합니다.",
                Foreground = new SolidColorBrush(Color.FromRgb(183, 192, 205)),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0)
            });
            _resourceAdmissionRunOnce = new Button
            {
                Content = "이번만 실행",
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(12, 6, 12, 6),
                ToolTip = "이 플랜메이커 시작 요청만 메모리/CPU 대기를 건너뜁니다. 동시 시작 방지와 시작 간격은 유지합니다."
            };
            AutomationProperties.SetAutomationId(_resourceAdmissionRunOnce, "TerminalResourceAdmissionRunOnce");
            _resourceAdmissionRunOnce.Click += (_, _) =>
            {
                if (TryRunResourceAdmissionOnce()) _resourceAdmissionRunOnce.IsEnabled = false;
            };
            content.Children.Add(_resourceAdmissionRunOnce);
            _resourceAdmissionOverlay = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(24, 29, 38)),
                Child = new ScrollViewer
                {
                    Content = content,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Center
                },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            AutomationProperties.SetAutomationId(_resourceAdmissionOverlay, "TerminalResourceAdmissionWait");
            Panel.SetZIndex(_resourceAdmissionOverlay, int.MaxValue);
            Children.Add(_resourceAdmissionOverlay);
        }

        SetRowSpan(_resourceAdmissionOverlay, Math.Max(1, RowDefinitions.Count));
        SetColumnSpan(_resourceAdmissionOverlay, Math.Max(1, ColumnDefinitions.Count));
        _resourceAdmissionReason!.Text = reason!;
        _resourceAdmissionRunOnce!.Visibility = _allowResourceAdmissionRunOnce ? Visibility.Visible : Visibility.Collapsed;
        _resourceAdmissionRunOnce.IsEnabled = CanRunResourceAdmissionOnce;
        AutomationProperties.SetName(_resourceAdmissionOverlay, "실행 대기 중 · " + reason);
        _resourceAdmissionOverlay.Visibility = Visibility.Visible;
    }
}
}
#endregion
#endif

#if TN_WPF || TN_SOURCE_TERMINALPANE_STARTUP_CS
#region TerminalPane.Startup.cs
namespace TerminalNotes
{
using global::System.IO;
using global::System.Net.Http;
using global::TerminalNotes.Services;


public sealed partial class TerminalPane
{
    private bool _startupApiActive;
    private OpenCodeAutomationDispatch? _startupApiUnconfirmed;
    private long _startupApiGeneration;

    internal bool HasPendingStartupSubmission => _startupApiUnconfirmed is not null;
    internal const string StartupOwnershipChangedPrefix = "시작 요청의 소유권 또는 입력 상태가 변경되었습니다";

    internal Task<bool> ExecuteStartupPlanAsync(string path, Func<bool> canDispatch,
        Action<string> report, CancellationToken cancellationToken) =>
        ExecuteSavedPlanOnDispatcherAsync(path, canDispatch,
            prompt => SubmitStartupInputAsync(prompt, false, canDispatch, report, cancellationToken));

    // Startup has no dependency on whether the TUI composer has finished mounting.
    // A POST is made once; a lost response changes this loop to exact-ID reads only.
    internal async Task<bool> SubmitStartupInputAsync(string prompt, bool isQuestion, Func<bool> canDispatch,
        Action<string> report, CancellationToken cancellationToken, bool userInitiated = false, bool allowBusy = false)
    {
        if (!Dispatcher.CheckAccess()) throw new InvalidOperationException("Startup must run on the pane dispatcher.");
        if (_startupApiActive || NativeOpenCodeEndpoint is null || _openCodeStatus is null)
            return FailPlanExecution("OpenCode 시작 요청을 준비할 수 없습니다.");
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 32 * 1024)
            return FailPlanExecution("입력 내용이 비어 있거나 너무 깁니다.");
        if (_startupApiUnconfirmed is { } pending && pending.Prompt != prompt)
            return FailPlanExecution("이전 시작 요청의 접수를 확인 중입니다.");
        if (_startupApiUnconfirmed is null && GetPlanExecutionBlockReason(canDispatch, allowNativeBusy: allowBusy) is { Length: > 0 } block)
            return FailPlanExecution(block);

        LastPlanExecutionFailure = null;
        // An interrupt or stop that happened before this start must not cancel it: the
        // token stays cancelled until replaced, which made every later Loop start fail.
        if (!userInitiated && _nativeAutomationCancellation.IsCancellationRequested)
        {
            _nativeAutomationCancellation.Dispose();
            _nativeAutomationCancellation = new CancellationTokenSource();
        }
        _startupApiActive = true;
        long inputVersion = _savedPlanComposerInterruptVersion;
        using var client = new OpenCodeAutomationClient(NativeOpenCodeEndpoint, WorkingDirectory, useTuiSelection: _usesNativeOpenCodeTui);
        using var lifetime = userInitiated
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, HeadlessLifetimeCancellation)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, NativeAutomationCancellation, HeadlessLifetimeCancellation);
        string? ownedHeadlessSession = _usesNativeOpenCodeStatus ? _headlessSessionId : null;
        long ownedNativeViewGeneration = _nativeViewGeneration;
        bool Owned() => !lifetime.IsCancellationRequested && !_disposed && !_failed && IsRunning &&
            !WasContinuedExternally && !IsUserInputBlocked && !_manualInputOverride &&
            (!_usesNativeOpenCodeStatus || (_headlessSessionId == ownedHeadlessSession && _nativeViewGeneration == ownedNativeViewGeneration)) &&
            inputVersion == _savedPlanComposerInterruptVersion && CanDispatchCallback(canDispatch);
        DateTimeOffset preparationStarted = DateTimeOffset.UtcNow;
        int failures = 0;
        try
        {
            while (Owned())
            {
                try
                {
                    if (_startupApiUnconfirmed is null)
                    {
                        if (NativeOpenCodeStatus == OpenCodeStatusKind.NeedsInput)
                            return FailPlanExecution("OpenCode의 질문이나 승인을 먼저 처리해야 합니다.");
                        string? session = allowBusy ? _headlessSessionId ?? _boundAutomationSession : _boundAutomationSession ?? _autoResume?.ContinuationSessionId;
                        if (allowBusy && NativeOpenCodeStatus == OpenCodeStatusKind.Busy && session is null)
                            return FailPlanExecution("현재 작업 중인 OpenCode 세션을 확인할 수 없습니다.");
                        if (session is null)
                        {
                            report("◌ OpenCode 시작 세션 준비 중");
                            session = await client.CreateStartupSessionAsync(_openCodeSessionTitle, lifetime.Token);
                            if (!Owned()) return false;
                            if (_usesNativeOpenCodeStatus)
                            {
                                SetHeadlessSession(session);
                                ownedHeadlessSession = _headlessSessionId;
                                ownedNativeViewGeneration = _nativeViewGeneration;
                            }
                            else BindNativeAutomationSession(session);
                        }
                        report("◌ OpenCode 시작 세션 연결 중");
                        if (!allowBusy) await client.SelectStartupSessionAsync(session, lifetime.Token);
                        if (!Owned()) return false;
                        var dispatch = new OpenCodeAutomationDispatch(session,
                            OpenCodeRecoveryClient.CreateMessageId(), prompt, DateTimeOffset.UtcNow);
                        await client.SubmitStartupPromptAsync(dispatch, () =>
                        {
                            // Client callbacks may run off the WPF thread.
                            Dispatcher.Invoke(() =>
                            {
                                if (!Owned() || NativeOpenCodeStatus == OpenCodeStatusKind.NeedsInput)
                                    throw new OperationCanceledException(lifetime.Token);
                                _startupApiUnconfirmed = dispatch;
                                _startupApiGeneration = _openCodeStatus.ObserveSubmission().Generation;
                                if (isQuestion) _autoResume?.Suspend("질문 입력 · 자동 진행 대기");
                                else _autoResume?.ObserveSubmittedPrompt(prompt, dispatch.SessionId, dispatch.MessageId);
                                report("◌ 입력 전달·수신 확인 중");
                            });
                        }, lifetime.Token, () => Dispatcher.Invoke(() =>
                        {
                            if (ReferenceEquals(_startupApiUnconfirmed, dispatch))
                            {
                                _startupApiUnconfirmed = null;
                                _openCodeStatus.SetSessionScope(_boundAutomationSession);
                                _autoResume?.Suspend("전송 전 중단 · 시작 요청 취소");
                            }
                        }), allowBusy: allowBusy);
                    }

                    if (!Owned()) return false;
                    var receipt = _startupApiUnconfirmed!;
                    if (await client.HasStartupMessageAsync(receipt, lifetime.Token))
                    {
                        if (!Owned()) return false;
                        _openCodeStatus.ConfirmApiSubmission(receipt.SessionId, receipt.MessageId, _startupApiGeneration);
                        if (!isQuestion) _autoResume?.ConfirmApiSubmission(receipt.SessionId, receipt.MessageId);
                        _startupApiUnconfirmed = null;
                        // TUI session selection can race its startup subscriber. Reattach the
                        // display independently; never repeat the accepted model request.
                        if (!allowBusy) _ = RestoreStartupSessionDisplayAsync(receipt, _startupApiGeneration, inputVersion, canDispatch);
                        return true;
                    }
                    report("◌ 요청 접수 자동 재확인 중");
                }
                catch (OpenCodeAutomationException ex) when (ex.Code == "prompt_rejected")
                {
                    _startupApiUnconfirmed = null;
                    _openCodeStatus.SetSessionScope(_boundAutomationSession);
                    _autoResume?.Suspend("시작 요청 거절 · 자동 재시도 준비");
                    report("◌ 요청 거절 · 연결 자동 재확인 중");
                    // The server explicitly rejected the request. A fresh readiness
                    // check is required before another attempt, with bounded backoff.
                }
                catch (Exception ex) when (ex is OpenCodeAutomationException or HttpRequestException or IOException ||
                                           ex is OperationCanceledException && !lifetime.IsCancellationRequested)
                {
                    report(_startupApiUnconfirmed is null
                        ? "◌ 시작 연결 실패 · 자동 재시도 중"
                        : "◌ 연결 복구·요청 접수 자동 재확인 중");
                }
                if (!Owned()) return false;
                if (_startupApiUnconfirmed is null && DateTimeOffset.UtcNow - preparationStarted >= TerminalExecutionTiming.StartupReadyTimeout)
                    throw new TimeoutException("OpenCode 시작 연결이 응답하지 않아 해당 터미널을 자동 복구합니다.");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 1 + failures++ * 2)), lifetime.Token);
            }
            return FailPlanExecution(StartupOwnershipChangedPrefix + " · " + (
                lifetime.IsCancellationRequested ? "시작 요청이 취소되었습니다." :
                _disposed || _failed || !IsRunning ? "터미널이 종료되었습니다." :
                WasContinuedExternally ? "CMD에서 이어 열었습니다." :
                IsUserInputBlocked ? "입력이 잠시 차단되어 있습니다." :
                _manualInputOverride ? "수동 확인 상태를 먼저 해제해야 합니다." :
                inputVersion != _savedPlanComposerInterruptVersion ? "터미널에서 Enter 또는 Ctrl+C 입력이 있었습니다." :
                !CanDispatchCallback(canDispatch) ? "작업 상태가 바뀌었습니다." : "OpenCode 대화 화면이 바뀌었습니다."));
        }
        finally { _startupApiActive = false; }
    }

    private async Task RestoreStartupSessionDisplayAsync(OpenCodeAutomationDispatch receipt, long generation,
        long inputVersion, Func<bool> canDispatch)
    {
        using var client = new OpenCodeAutomationClient(NativeOpenCodeEndpoint!, WorkingDirectory, useTuiSelection: _usesNativeOpenCodeTui);
        // These are display-only retries, covering delayed TUI subscription.
        foreach (int seconds in new[] { 1, 3, 10 })
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), NativeAutomationCancellation);
                if (_disposed || !IsRunning || _boundAutomationSession != receipt.SessionId || _manualInputOverride || IsUserInputBlocked ||
                    inputVersion != _savedPlanComposerInterruptVersion || !CanDispatchCallback(canDispatch)) return;
                if (!_usesNativeOpenCodeStatus || _usesNativeOpenCodeTui)
                    await client.SelectStartupSessionAsync(receipt.SessionId, NativeAutomationCancellation);
                if (_openCodeStatus is not null)
                    await _openCodeStatus.RefreshConfirmedApiStatusAsync(receipt.SessionId, receipt.MessageId, generation,
                        NativeAutomationCancellation);
            }
            catch (Exception ex) when (ex is OpenCodeAutomationException or HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    internal bool CanRecoverUnsubmittedStartup => !_disposed && !WasContinuedExternally &&
        !_nativeAutomationActive && _startupApiUnconfirmed is null && !IsUserInputBlocked &&
        !_manualInputOverride && !HasLocalPendingInput() &&
        _autoResume?.HasUnfinishedWork != true &&
        NativeOpenCodeStatus is not (OpenCodeStatusKind.Busy or OpenCodeStatusKind.Pending or OpenCodeStatusKind.NeedsInput);

    internal async Task<bool> StopUnsubmittedStartupAsync()
    {
        if (!Dispatcher.CheckAccess() || !CanRecoverUnsubmittedStartup) return false;
        await StopOwnedOpenCodeProcessAsync();
        _failed = true;
        _flushTimer.Stop();
        return true;
    }
}
}
#endregion
#endif

#if TN_WPF || TN_SOURCE_TERMINALPANE_SURFACE_CS
#region TerminalPane.Surface.cs
namespace TerminalNotes
{
using global::System.Text;
using global::System.Windows;
using global::System.Windows.Threading;
using global::TerminalNotes.Controls;
using global::TerminalNotes.Services;
using global::TerminalNotes.TerminalEngine;


public sealed partial class TerminalPane
{
    private NativeTerminalSurface? _nativeTerminalSurface;
    private TerminalEmulator? _nativeTerminalEmulator;

    private Exception? HandleNativeTerminalResponse(string response)
    {
        if (_disposed || _failed || !_session.IsRunning ||
            (!_ready && !(_tuiConnecting && TuiConnectionOwned && TerminalInputCapture.IsTerminalResponse(response)))) return null;
        try { _session.Write(response); return null; }
        catch (Exception exception) { return exception; }
    }

    private bool _inputFailureReported;

    // Every key press and mouse move reports here. Input refused because the terminal is busy,
    // blocked or still starting is an expected state (status line only); a broken input pipe is
    // shown once until input works again, instead of one popup per mouse movement.
    private void ReportNativeInputFailure(Exception exception)
    {
        if (exception is InvalidOperationException)
        {
            StatusChanged?.Invoke(IsUserInputBlocked ? "입력 대기 · 현재 응답 처리 중에는 입력을 받지 않습니다." : "터미널 준비 중 · 잠시 후 입력하세요.");
            return;
        }
        if (_inputFailureReported) return;
        _inputFailureReported = true;
        ReportUserWarning("터미널 입력: " + exception.Message);
    }

    // A PlanMaker's own terminal takes no keys until its interview has started (HoldInputUntilPlanningStarts).
    private volatile bool _planMakerStartupHold;
    private Action? _planMakerStartupHoldNotice;
    private long _planMakerStartupHoldNoticeAt;

    /// <summary>
    /// Until its first planning request is sent, a PlanMaker's terminal ignores typed keys: there is nothing to answer yet, and keys
    /// typed while it connects would land in another conversation or stop the PlanMaker (2026-11-11 사용자: "고쳐 문제가 있으면
    /// 고쳐야지"). <paramref name="notice"/> runs, at most once a second, when keys were ignored. Released once the interview starts
    /// or the attempt ends.
    /// </summary>
    internal void HoldInputUntilPlanningStarts(Action notice)
    {
        _planMakerStartupHoldNotice = notice;
        _planMakerStartupHold = true;
    }

    private bool IgnoredBeforePlanning(string input, bool isProgrammatic, long? sendId)
    {
        if (!_planMakerStartupHold || isProgrammatic || sendId is not null || TerminalInputCapture.IsTerminalResponse(input)) return false;
        if (IsMouseReport(input)) return true;
        long now = Environment.TickCount64;
        if (now - _planMakerStartupHoldNoticeAt > 1000)
        {
            _planMakerStartupHoldNoticeAt = now;
            _planMakerStartupHoldNotice?.Invoke();
        }
        return true;
    }

    private Exception? HandleNativeSurfaceInput(string input, bool isProgrammatic, long? sendId)
    {
        if (IgnoredBeforePlanning(input, isProgrammatic, sendId)) return null;
        if (!_disposed && !_failed && !_ready && CanHoldStartupInput(input, isProgrammatic, sendId))
        {
            // Typed while the terminal is still starting or reconnecting its view: keep it, in order,
            // and deliver it once the terminal is ready instead of silently dropping the keys.
            if (_heldStartupInput.Length == 0)
            {
                StatusChanged?.Invoke("터미널 준비 중 · 입력한 내용은 준비되면 바로 전달됩니다.");
                ReportLifecycleStage("input_held_until_ready");
            }
            _heldStartupInput.Append(input);
            return null;
        }
        if (_disposed || _failed || !_ready) return new InvalidOperationException("The terminal is not ready.");
        return HandleTerminalInput(input, isProgrammatic, sendId);
    }

    private readonly System.Text.StringBuilder _heldStartupInput = new();
    private const int HeldStartupInputLimit = 4096;

    private bool CanHoldStartupInput(string input, bool isProgrammatic, long? sendId) =>
        !isProgrammatic && sendId is null && !IsUserInputBlocked && !WasContinuedExternally && input.Length > 0 &&
        !TerminalInputCapture.IsTerminalResponse(input) && !IsMouseReport(input) &&
        _heldStartupInput.Length + input.Length <= HeldStartupInputLimit;

    private static bool IsMouseReport(string input) =>
        input.StartsWith("\u001b[<", StringComparison.Ordinal) || input.StartsWith("\u001b[M", StringComparison.Ordinal);

    // Called whenever the terminal becomes ready: sends the keys held during startup.
    private void FlushHeldStartupInput()
    {
        if (_heldStartupInput.Length == 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_heldStartupInput.Length == 0) return;
            if (_disposed || _failed || IsUserInputBlocked || WasContinuedExternally)
            {
                DiscardHeldStartupInput();
                return;
            }
            if (!_ready) return;
            string held = _heldStartupInput.ToString();
            _heldStartupInput.Clear();
            ReportLifecycleStage("input_held_delivered");
            if (HandleTerminalInput(held, isProgrammatic: false, sendId: null) is { } error) ReportNativeInputFailure(error);
            else StatusChanged?.Invoke("터미널 준비 완료 · 준비 중에 입력한 내용을 전달했습니다.");
        }, DispatcherPriority.Background);
    }

    private void DiscardHeldStartupInput()
    {
        if (_heldStartupInput.Length == 0) return;
        _heldStartupInput.Clear();
        ReportLifecycleStage("input_held_discarded");
        StatusChanged?.Invoke("터미널을 시작하지 못해 준비 중에 입력한 내용은 전달하지 않았습니다. 다시 연결한 뒤 입력하세요.");
    }

    private Exception? HandleTerminalInput(string input, bool isProgrammatic, long? sendId)
    {
        bool hasSendId = sendId.HasValue;
        long inputSendId = sendId.GetValueOrDefault();
        bool terminalResponse = TerminalInputCapture.IsTerminalResponse(input);
        if (!isProgrammatic && !terminalResponse) CancelIncompleteSavedPlanDispatches();
        if (hasSendId && !CanWriteSavedPlanDispatch(inputSendId, input))
            return new InvalidOperationException("The saved plan submission is no longer current.");
        if (IsUserInputBlocked && !isProgrammatic)
            return new InvalidOperationException("Terminal input is temporarily blocked.");
        if (!_usesNativeOpenCodeStatus) _activityTracker.ObserveUserInput(input);
        if (!isProgrammatic && !terminalResponse)
        {
            _planMakerInputRevision++;
            if (!IsPlanMakerActive) _autoResume?.ObserveLocalInput(input);
            _inputCapture.Observe(input);
        }
        try
        {
            if (hasSendId) PrepareSavedPlanDispatchSubmit(inputSendId, input);
            _session.Write(input);
            _inputFailureReported = false;
            if (hasSendId) ObserveSavedPlanDispatchWrite(inputSendId, input);
            else if (!isProgrammatic && !terminalResponse) ObserveUserWriteForSavedPlanDraft(input);
            return null;
        }
        catch (Exception writeError)
        {
            if (hasSendId)
            {
                if (_savedPlanDispatches.TryGetValue(inputSendId, out SavedPlanDispatch? dispatch))
                {
                    CancelSavedPlanDispatch(dispatch, writeError);
                    _autoResume?.Suspend("플랜 전달 실패 · 자동 진행 멈춤");
                }
                else if (_pendingTextSends.Remove(inputSendId, out var failedCompletion))
                    failedCompletion.TrySetException(writeError);
            }
            return writeError;
        }
    }

    private void HandleTerminalSendTextAck(long id, Exception? error)
    {
        if (_pendingTextSends.Remove(id, out var completion))
        {
            if (error is not null) completion.TrySetException(error);
            else completion.TrySetResult();
        }
        if (error is not null && _savedPlanDispatches.TryGetValue(id, out SavedPlanDispatch? dispatch))
            CancelSavedPlanDispatch(dispatch, error);
        CompleteSavedPlanDispatch(id);
    }

    private void HandleNativeSurfaceDimensionsChanged(short columns, short rows)
    {
        if (_disposed || _failed) return;
        if (_usesNativeOpenCodeTui)
        {
            columns = (short)Math.Clamp((int)columns, NativeTerminalSurface.MinimumColumns, 500);
            rows = (short)Math.Clamp((int)rows, NativeTerminalSurface.MinimumRows, 500);
        }
        _pendingWebReadyDimensions = (columns, rows);
        if (_usesNativeOpenCodeTui && !_ready)
            _openCodeTuiInitialDimensions = (columns, rows);
        try
        {
            _nativeTerminalEmulator?.Resize(columns, rows);
            if (_ready)
            {
                ReportLifecycleStage("resize_requested");
                _session.RequestResize(columns, rows);
            }
            else TryStartTerminalWhenLaidOut();
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke("터미널 크기 변경: " + exception.Message);
            if (!_ready) ShowError(exception.Message);
        }
    }
}
}
#endregion
#endif

#if TN_WPF || TN_SOURCE_TERMINALPANE_TUI_CS
#region TerminalPane.Tui.cs
namespace TerminalNotes
{
using global::System.IO;
using global::System.Text.Json;
using global::TerminalNotes.Services;


public sealed partial class TerminalPane
{
    private void ObserveOpenCodeTuiManualSubmission(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.TrimStart().StartsWith('/'))
        {
            _openCodeTuiManualText = null;
            return;
        }

        _openCodeTuiManualText = text;
        _openCodeTuiManualSubmittedAt = DateTimeOffset.UtcNow;
        _openCodeTuiManualGeneration = _openCodeStatus?.SubmissionGeneration ?? 0;
    }

    private void HandleOpenCodeTuiExit(int code)
    {
        if (_disposed || HeadlessLifetimeCancellation.IsCancellationRequested || _sessionCloseStarted != 0) return;
        _tuiLastExitCode = code;
        Volatile.Read(ref _tuiOutputProof)?.Completion.TrySetException(new InvalidOperationException("The owned TUI exited before producing a stable view."));
        FailPendingTextSends(new InvalidOperationException("The OpenCode TUI process exited."));
        ReportLifecycleStage("opencode_tui_exited");
        ReportLifecycleStage("opencode_tui_exit_code_" + code);
        ShowOpenCodeTuiConnectionFailure();
        if (!_tuiConnecting && _tuiConnectionStage == "running" && (code != 0 || KeepRetryingOpenCodeTuiConnection)) ScheduleOpenCodeTuiReconnect();
    }

    private void OnOpenCodeTuiRecoverySignal(OpenCodeRecoverySignal signal)
    {
        if (signal.Type == "message.updated") HideOpenCodeMark();
        if (!_usesNativeOpenCodeTui || _disposed || _failed || signal.Type != "message.updated" ||
            string.IsNullOrWhiteSpace(signal.MessageId) || signal.MessageCreatedAt is not { } created ||
            _openCodeTuiManualText is not { } expectedText || _headlessOpenCodeClient is not { } client ||
            created.ToUnixTimeMilliseconds() < _openCodeTuiManualSubmittedAt.ToUnixTimeMilliseconds())
            return;

        string messageId = signal.MessageId;
        long generation = _openCodeTuiManualGeneration;
        string sessionId = signal.SessionId;
        _ = VerifyOpenCodeTuiManualReceiptAsync(client, sessionId, messageId, expectedText, generation, created);
    }

    private async Task VerifyOpenCodeTuiManualReceiptAsync(OpenCodeConversationClient client, string sessionId,
        string messageId, string expectedText, long generation, DateTimeOffset messageCreatedAt)
    {
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(HeadlessLifetimeCancellation);
            JsonElement session = await client.GetSessionAsync(sessionId, lifetime.Token);
            if (!IsRootSessionInProject(session, sessionId)) return;
            bool receiptFound = false;
            foreach (int delay in new[] { 0, 125, 300, 600 })
            {
                if (delay > 0) await Task.Delay(delay, lifetime.Token);
                JsonElement messages = await client.ReadMessagesAsync(sessionId, lifetime.Token);
                if (ContainsExactUserMessage(messages, sessionId, messageId, expectedText, messageCreatedAt))
                {
                    receiptFound = true;
                    break;
                }
            }
            if (!receiptFound) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (_disposed || _failed || !_usesNativeOpenCodeTui ||
                    _openCodeTuiManualGeneration != generation || _openCodeTuiManualText != expectedText || _openCodeStatus is null)
                    return;
                if (!IsPlanMakerActive) BindNativeAutomationSession(sessionId);
                _openCodeStatus.ConfirmApiSubmission(sessionId, messageId, generation);
                if (!IsPlanMakerActive)
                {
                    _autoResume?.ObserveSubmittedPrompt(expectedText, sessionId, messageId);
                    _autoResume?.ConfirmApiSubmission(sessionId, messageId);
                }
                _openCodeTuiManualText = null;
                ClearWorkFailed();
            });
        }
        catch (OperationCanceledException) when (HeadlessLifetimeCancellation.IsCancellationRequested || _disposed) { }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine("OpenCode TUI message receipt could not be verified: " + exception.GetType().Name);
        }
    }

    private bool IsRootSessionInProject(JsonElement session, string sessionId) =>
        session.ValueKind == JsonValueKind.Object &&
        session.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String && id.GetString() == sessionId &&
        (!session.TryGetProperty("parentID", out JsonElement parent) || parent.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) &&
        session.TryGetProperty("directory", out JsonElement directory) && directory.ValueKind == JsonValueKind.String &&
        AreOpenCodeDirectoriesEqual(directory.GetString(), WorkingDirectory);

    private static bool AreOpenCodeDirectoriesEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        try
        {
            string canonicalLeft = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
            string canonicalRight = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
            return string.Equals(canonicalLeft, canonicalRight,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool ContainsExactUserMessage(JsonElement messages, string sessionId, string messageId,
        string expectedText, DateTimeOffset signalCreatedAt)
    {
        if (messages.ValueKind != JsonValueKind.Array) return false;
        foreach (JsonElement message in messages.EnumerateArray())
        {
            JsonElement info = message.TryGetProperty("info", out JsonElement nested) && nested.ValueKind == JsonValueKind.Object
                ? nested : message;
            if (!info.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String ||
                id.GetString() != messageId || !info.TryGetProperty("role", out JsonElement role) ||
                role.ValueKind != JsonValueKind.String || role.GetString() != "user" ||
                !info.TryGetProperty("sessionID", out JsonElement messageSession) || messageSession.ValueKind != JsonValueKind.String ||
                messageSession.GetString() != sessionId || !info.TryGetProperty("time", out JsonElement time) ||
                time.ValueKind != JsonValueKind.Object || !time.TryGetProperty("created", out JsonElement created) ||
                created.ValueKind != JsonValueKind.Number || !created.TryGetInt64(out long milliseconds))
                continue;

            DateTimeOffset messageTime;
            try { messageTime = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
            catch (ArgumentOutOfRangeException) { return false; }
            if (messageTime != signalCreatedAt) return false;

            JsonElement parts = message.TryGetProperty("parts", out JsonElement messageParts) ? messageParts : default;
            if (parts.ValueKind != JsonValueKind.Array) return false;
            var text = new System.Text.StringBuilder();
            foreach (JsonElement part in parts.EnumerateArray())
                if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("type", out JsonElement type) &&
                    type.ValueKind == JsonValueKind.String && type.GetString() == "text" &&
                    part.TryGetProperty("text", out JsonElement value) && value.ValueKind == JsonValueKind.String)
                    text.Append(value.GetString());
            return string.Equals(text.ToString(), expectedText, StringComparison.Ordinal);
        }
        return false;
    }
}
}
#endregion
#endif

#if TN_WPF || TN_SOURCE_TERMINALPANE_TUICONNECTION_CS
#region TerminalPane.TuiConnection.cs
namespace TerminalNotes
{
using global::System.IO;
using global::System.Windows;
using global::TerminalNotes.Services;
using Media = global::System.Windows.Media;


internal sealed record OpenCodeTuiConnectionHooks(
    Func<string, CancellationToken, Task> SelectSession,
    Func<string, CancellationToken, Task<string>> AttachCommand,
    TimeSpan FirstOutputTimeout, TimeSpan StableInterval, TimeSpan ReconnectDelay);

public sealed partial class TerminalPane
{
    // Instance-only seams for isolated fixtures; production always uses the exact native selector.
    private OpenCodeTuiConnectionHooks? _tuiConnectionHooks = null;
    private sealed record TuiOutputProof(ConPtySession Session, TaskCompletionSource Completion)
    {
        private enum OutputState { Text, Escape, EscapeIntermediate, Csi, Osc, ControlString }
        private OutputState _state;
        private bool _stringEscape, _highSurrogate;

        internal void Observe(string text)
        {
            if (Completion.Task.IsCompleted) return;
            // ConPTY can emit initialization/terminal-title sequences before the
            // startup command runs. Only rendered text proves that a view exists.
            // Keep parser state, never payloads, across arbitrarily split chunks.
            foreach (char value in text)
            {
                if (value is '\u0018' or '\u001a')
                {
                    _state = OutputState.Text;
                    _stringEscape = _highSurrogate = false;
                    continue;
                }
                if (_state is OutputState.Osc or OutputState.ControlString)
                {
                    if (value == '\u009c' || _state == OutputState.Osc && value == '\a' || _stringEscape && value == '\\')
                        _state = OutputState.Text;
                    _stringEscape = value == '\u001b';
                    continue;
                }
                if (value == '\u001b')
                {
                    _state = OutputState.Escape;
                    _highSurrogate = false;
                    continue;
                }
                switch (value)
                {
                    case '\u009b': _state = OutputState.Csi; _highSurrogate = false; continue;
                    case '\u009d': _state = OutputState.Osc; _stringEscape = _highSurrogate = false; continue;
                    case '\u0090': case '\u0098': case '\u009e': case '\u009f':
                        _state = OutputState.ControlString; _stringEscape = _highSurrogate = false; continue;
                    case '\u009c': _state = OutputState.Text; _highSurrogate = false; continue;
                }
                if (_state == OutputState.Escape)
                {
                    _state = value switch
                    {
                        '[' => OutputState.Csi,
                        ']' => OutputState.Osc,
                        'P' or 'X' or '^' or '_' => OutputState.ControlString,
                        >= '\u0020' and <= '\u002f' => OutputState.EscapeIntermediate,
                        >= '\u0030' and <= '\u007e' => OutputState.Text,
                        _ => OutputState.Escape
                    };
                    _stringEscape = false;
                    continue;
                }
                if (_state == OutputState.EscapeIntermediate)
                {
                    if (value is >= '\u0030' and <= '\u007e') _state = OutputState.Text;
                    continue;
                }
                if (_state == OutputState.Csi)
                {
                    if (value is >= '\u0040' and <= '\u007e') _state = OutputState.Text;
                    continue;
                }
                if (char.IsHighSurrogate(value)) { _highSurrogate = true; continue; }
                bool visible = char.IsLowSurrogate(value) ? _highSurrogate :
                    !char.IsControl(value) && !char.IsWhiteSpace(value) &&
                    char.GetUnicodeCategory(value) != global::System.Globalization.UnicodeCategory.Format;
                _highSurrogate = false;
                if (!visible) continue;
                Completion.TrySetResult();
                return;
            }
        }
    }
    private TuiOutputProof? _tuiOutputProof;
    private void ObserveTuiOutput(ConPtySession session, string text)
    {
        var proof = Volatile.Read(ref _tuiOutputProof);
        if (proof is not null && ReferenceEquals(session, proof.Session)) proof.Observe(text);
    }
    private bool _tuiConnecting, _tuiConnectionFailed, _tuiReconnectScheduled;
    private int _tuiReconnectAttempts;
    private int? _tuiLastExitCode;
    private DateTimeOffset? _tuiReconnectAt;
    private string _tuiConnectionStage = "not_started";
    internal bool HasTuiConnectionFailure => _usesNativeOpenCodeTui && _tuiConnectionFailed;
    internal string? TuiConnectionStatus => HasTuiConnectionFailure
        ? _tuiConnecting ? "TUI 재연결 중 · 서버와 세션 유지"
            : _tuiReconnectAt is { } at ? $"TUI 연결 종료 · {Math.Max(0, (int)Math.Ceiling((at - DateTimeOffset.UtcNow).TotalSeconds))}초 후 재연결"
            : "TUI 연결 실패 · 재연결로 같은 세션을 여세요" : null;
    private bool TuiConnectionOwned => _usesNativeOpenCodeTui && !_disposed &&
        !HeadlessLifetimeCancellation.IsCancellationRequested && _sessionCloseStarted == 0 &&
        !IsUserInputBlocked && !WasContinuedExternally && _headlessOpenCodeHost?.IsRunning == true;
    private bool CanRetryOpenCodeTuiConnection => TuiConnectionOwned && !_tuiConnecting && !IsPlanMakerActive &&
        _headlessSessionId is { Length: > 0 } && (_tuiConnectionFailed || _failed && !_session.IsRunning);

    private async Task ConnectRetainedOpenCodeTuiAsync(bool replaceSession, CancellationToken token, Func<bool>? stillOwned = null)
    {
        if (!TuiConnectionOwned || _headlessSessionId is not { Length: > 0 } sessionId)
            throw new InvalidOperationException("No verified retained TUI session is available.");
        _tuiConnecting = true;
        _tuiLastExitCode = null;
        _ready = false;
        _failed = false;
        HideOpenCodeMark();
        try
        {
            _tuiConnectionStage = "session_select";
            ReportLifecycleStage("opencode_tui_session_select_requested");
            if (_tuiConnectionHooks is { } hooks) await hooks.SelectSession(sessionId, token);
            else await SelectOpenCodeTuiSessionAsync(sessionId, token);
            token.ThrowIfCancellationRequested();
            if (!TuiConnectionOwned || _headlessSessionId != sessionId || stillOwned?.Invoke() == false)
                throw new OperationCanceledException(token);

            _tuiConnectionStage = "attach_command";
            string? command = null;
            string? program = null;
            string[] arguments = [];
            if (_tuiConnectionHooks is { } fixture) command = await fixture.AttachCommand(sessionId, token);
            else
            {
                // The TUI runs straight in the pseudoconsole: a PowerShell host around it cost ~50 MB a terminal and did nothing
                // else. Without the native opencode.exe there is nothing to run (no shell fallback): the reason is shown.
                var installed = await AutoInstall.CheckAsync(token);
                if (!installed.IsInstalled || string.IsNullOrWhiteSpace(installed.Path) || !File.Exists(installed.Path))
                    throw new FileNotFoundException(installed.IsInstalled ? AutoInstall.NativeMissing(installed.Path) : installed.Message);
                program = installed.Path;
                arguments = ["attach", NativeOpenCodeEndpoint!.GetLeftPart(UriPartial.Authority), "--dir", Path.GetFullPath(WorkingDirectory),
                    "--session", sessionId];
            }

            if (replaceSession)
            {
                token.ThrowIfCancellationRequested();
                if (!TuiConnectionOwned || _headlessSessionId != sessionId || stillOwned?.Invoke() == false)
                    throw new OperationCanceledException(token);
                _tuiConnectionStage = "old_attach_cleanup";
                await _session.StopAndWaitAsync();
                token.ThrowIfCancellationRequested();
                if (!TuiConnectionOwned || _headlessSessionId != sessionId || stillOwned?.Invoke() == false)
                    throw new OperationCanceledException(token);
                _session = new ConPtySession();
                WireTerminalSession(_session);
            }
            token.ThrowIfCancellationRequested();
            if (!TuiConnectionOwned || _headlessSessionId != sessionId || stillOwned?.Invoke() == false)
                throw new OperationCanceledException(token);
            _tuiConnectionStage = "pty_start";
            var outputProof = new TuiOutputProof(_session,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            Volatile.Write(ref _tuiOutputProof, outputProof);
            var dimensions = _openCodeTuiInitialDimensions ?? (Columns: (short)40, Rows: (short)24);
            short columns = (short)Math.Clamp((int)dimensions.Columns, 40, 500), rows = (short)Math.Clamp((int)dimensions.Rows, 24, 500);
            _nativeTerminalEmulator?.Resize(columns, rows);
            _session.Start(WorkingDirectory, columns, rows, startupCommand: command, openCodeConfigContent: _openCodeConfigContent,
                exitOnStartupCommandCompletion: true, executable: program, arguments: arguments);
            _tuiConnectionStage = "first_output";
            ReportLifecycleStage("opencode_tui_waiting_for_output");
            await outputProof.Completion.Task.WaitAsync(_tuiConnectionHooks?.FirstOutputTimeout ?? TimeSpan.FromSeconds(20), token);
            await Task.Delay(_tuiConnectionHooks?.StableInterval ?? TimeSpan.FromSeconds(1), token);
            if (stillOwned?.Invoke() == false) throw new OperationCanceledException(token);
            if (!TuiConnectionOwned || !_session.IsRunning || _headlessSessionId != sessionId)
                throw new InvalidOperationException("The owned TUI exited before producing a stable view.");
            _tuiConnectionFailed = false;
            _tuiReconnectAt = null;
            _tuiConnectionStage = "running";
            ReportLifecycleStage("opencode_tui_output_started");
            _ = ShowOpenCodeMarkWhileEmptyAsync(sessionId);
        }
        catch (Exception exception)
        {
            ReportLifecycleStage("opencode_tui_" + _tuiConnectionStage + "_failed_" + exception.GetType().Name);
            throw;
        }
        finally { _tuiConnecting = false; Volatile.Write(ref _tuiOutputProof, null); }
    }

    private void ShowOpenCodeTuiConnectionFailure()
    {
        HideOpenCodeMark();
        if (_disposed || HeadlessLifetimeCancellation.IsCancellationRequested || _sessionCloseStarted != 0) return;
        _tuiConnectionFailed = true;
        _failed = true;
        _ready = false;
        DiscardHeldStartupInput();
        string reason = _tuiLastExitCode is { } code ? "TUI 종료 코드 " + code : _tuiConnectionStage switch
        {
            "session_select" => "세션 연결을 확인하지 못했습니다",
            "pty_start" => "터미널 프로세스를 시작하지 못했습니다",
            "first_output" => "TUI 화면 응답을 확인하지 못했습니다",
            _ => "TUI 연결을 시작하지 못했습니다"
        };
        _notice.Text = reason + ". 서버와 세션은 유지됩니다.\n" +
            (_headlessSessionId is { Length: > 0 } ? "재연결을 눌러 같은 세션을 다시 여세요." : "세션을 확인한 뒤 터미널을 다시 시작하세요.");
        _notice.Visibility = Visibility.Visible;
        StatusChanged?.Invoke(TuiConnectionStatus ?? "TUI 연결 확인 필요");
        ApplyActivityChange();
    }

    private async Task<bool> RetryOpenCodeTuiConnectionAsync(bool automatic = false, Func<bool>? stillOwned = null)
    {
        if (!CanRetryOpenCodeTuiConnection || stillOwned?.Invoke() == false) return false;
        if (!automatic) _tuiReconnectAttempts = 0;
        _tuiReconnectAt = null;
        ReportLifecycleStage("opencode_tui_reconnect_attempt_" + (automatic ? _tuiReconnectAttempts : 0));
        try
        {
            await ConnectRetainedOpenCodeTuiAsync(replaceSession: true, HeadlessLifetimeCancellation, stillOwned);
            if (!TuiConnectionOwned || stillOwned?.Invoke() == false) return false;
            _failed = false;
            _ready = true;
            FlushHeldStartupInput();
            _notice.Visibility = Visibility.Collapsed;
            _terminalStartedCompletion = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _terminalStartedCompletion.TrySetResult(null);
            ApplyActivityChange();
            Ready?.Invoke();
            StatusChanged?.Invoke("OpenCode TUI 화면 실행 중 · 기존 세션 유지");
            return true;
        }
        catch (OperationCanceledException) when (HeadlessLifetimeCancellation.IsCancellationRequested || !TuiConnectionOwned || stillOwned?.Invoke() == false)
        {
            ReportLifecycleStage("opencode_tui_reconnect_cancelled");
            return false;
        }
        catch
        {
            ShowOpenCodeTuiConnectionFailure();
            return false;
        }
    }

    private bool KeepRetryingOpenCodeTuiConnection => !_manualInputOverride && !IsAutoResumePaused &&
        (_autoResume?.HasUnfinishedWork == true || _autoResume?.IsContinuousImprovementActive == true);

    private void ScheduleOpenCodeTuiReconnect()
    {
        if (!CanRetryOpenCodeTuiConnection || IsAutoResumePaused || _manualInputOverride ||
            _tuiReconnectScheduled || _tuiReconnectAttempts >= 2 && !KeepRetryingOpenCodeTuiConnection) return;
        _tuiReconnectScheduled = true;
        TimeSpan delay = _tuiConnectionHooks?.ReconnectDelay ?? TimeSpan.FromSeconds(
            _tuiReconnectAttempts switch { 0 => 1, 1 => 3, 2 => 10, 3 => 30, _ => 60 });
        _tuiReconnectAt = DateTimeOffset.UtcNow + delay;
        ApplyActivityChange();
        _ = ReconnectAfterDelayAsync(delay, KeepRetryingOpenCodeTuiConnection, _savedPlanComposerInterruptVersion);
    }

    private async Task ReconnectAfterDelayAsync(TimeSpan delay, bool requiresActiveWork, long inputVersion)
    {
        bool retryAgain = false;
        bool StillOwned() => inputVersion == _savedPlanComposerInterruptVersion &&
            !IsAutoResumePaused && !_manualInputOverride &&
            (!requiresActiveWork || KeepRetryingOpenCodeTuiConnection);
        try
        {
            await Task.Delay(delay, HeadlessLifetimeCancellation);
            // A task-origin retry must not fall back to the idle display retry
            // policy after Stop invalidates the controller's unfinished work.
            if (!CanRetryOpenCodeTuiConnection || !StillOwned()) return;
            _tuiReconnectAttempts = Math.Min(int.MaxValue - 1, _tuiReconnectAttempts + 1);
            bool recovered = await RetryOpenCodeTuiConnectionAsync(automatic: true, StillOwned);
            retryAgain = !recovered && StillOwned() &&
                (_tuiLastExitCode is not (null or 0) || KeepRetryingOpenCodeTuiConnection);
        }
        catch (OperationCanceledException) { ReportLifecycleStage("opencode_tui_reconnect_wait_cancelled"); }
        finally { _tuiReconnectScheduled = false; _tuiReconnectAt = null; }
        if (retryAgain) ScheduleOpenCodeTuiReconnect();
    }

    // OpenCode's own mark over an empty conversation, as its start screen shows it (사용자: "터미널 실행했을때 OPENCODE 심볼이 상단에
    // 떠야지 접속된줄 알텐데"). The app opens the TUI straight into the task's session, which skips that screen and shows an empty view
    // until something is asked. The mark comes once the TUI is up and the session has no message, and goes with the first message
    // (a plan the app sends or text entered in the TUI), a reconnect or a failure.
    private OpenCodeMark? _openCodeMark;
    private int _openCodeMarkVersion;

    internal bool ShowsOpenCodeMark => _openCodeMark is { Visibility: Visibility.Visible };

    private async Task ShowOpenCodeMarkWhileEmptyAsync(string sessionId)
    {
        int version = Volatile.Read(ref _openCodeMarkVersion);
        if (_headlessOpenCodeClient is not { } client) return;
        try
        {
            var messages = await client.ReadMessagesAsync(sessionId, HeadlessLifetimeCancellation);
            if (messages.ValueKind != global::System.Text.Json.JsonValueKind.Array || messages.GetArrayLength() > 0) return;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine("OpenCode mark: the session's messages could not be read: " + exception.GetType().Name);
            return;
        }
        // A message, a reconnect or a failure since the question was asked wins.
        if (_disposed || version != Volatile.Read(ref _openCodeMarkVersion) || _headlessSessionId != sessionId ||
            _tuiConnectionStage != "running" || _tuiConnectionFailed || _nativeTerminalSurface is not { } surface) return;
        if (_openCodeMark is null)
        {
            _openCodeMark = new OpenCodeMark(surface) { VerticalAlignment = VerticalAlignment.Top };
            // Right above the terminal, below the notices and overlays added after it.
            var viewport = Children.OfType<System.Windows.Controls.ScrollViewer>().FirstOrDefault(item => ReferenceEquals(item.Content, surface));
            UIElement below = viewport is null ? surface : viewport;
            SetRow(_openCodeMark, GetRow(below));
            SetRowSpan(_openCodeMark, GetRowSpan(below));
            Children.Insert(Children.IndexOf(below) + 1, _openCodeMark);
        }
        _openCodeMark.Visibility = Visibility.Visible;
    }

    private void HideOpenCodeMark()
    {
        Interlocked.Increment(ref _openCodeMarkVersion);
        if (_openCodeMark is null) return;
        if (Dispatcher.CheckAccess()) _openCodeMark.Visibility = Visibility.Collapsed;
        else Dispatcher.BeginInvoke(() => { if (_openCodeMark is { } mark) mark.Visibility = Visibility.Collapsed; });
    }
}

/// <summary>OpenCode's logo as its start screen draws it, read cell by cell from OpenCode 1.18.34: "open" grey and "code" bright, of
/// █ ▀ ▄ cells with a darker inside. Drawn as blocks on the terminal's own cells, two rows below the top and centred over the
/// conversation column, so it sits on the TUI's grid without seams.</summary>
internal sealed class OpenCodeMark : FrameworkElement
{
    private static readonly string[] Glyphs =
    [
        "                                 ▄     ",
        "█▀▀█ █▀▀█ █▀▀█ █▀▀▄ █▀▀▀ █▀▀█ █▀▀█ █▀▀█",
        "█  █ █  █ █▀▀▀ █  █ █    █  █ █  █ █▀▀▀",
        "▀▀▀▀ █▀▀▀ ▀▀▀▀ ▀▀▀▀ ▀▀▀▀ ▀▀▀▀ ▀▀▀▀ ▀▀▀▀",
    ];
    // o and c: "open" and "code"; O and C: the same on a letter's darker inside; d: the dim foot of the n.
    private static readonly string[] Styles =
    [
        "                                 c     ",
        "oooo oooo oooo oooo cccc cccc cccc cccc",
        "oOOo oOOo oOOO oOOo cCCC cCCc cCCc cCCC",
        "oooo oooo oooo oddo cccc cccc cccc cccc",
    ];
    private const int TopRows = 2;
    // OpenCode's conversation view keeps a 42-column panel on the right once the terminal is wider than 120 columns.
    private const int WideColumns = 120, SidePanelColumns = 42;
    private static readonly Media.Brush Open = Gray(0x80), Code = Gray(0xEE), OpenInside = Gray(0x28), CodeInside = Gray(0x43);
    private readonly Controls.NativeTerminalSurface _surface;

    internal OpenCodeMark(Controls.NativeTerminalSurface surface)
    {
        _surface = surface;
        IsHitTestVisible = false;
        Focusable = false;
        Media.RenderOptions.SetEdgeMode(this, Media.EdgeMode.Aliased);
        System.Windows.Automation.AutomationProperties.SetName(this, "OpenCode 연결됨");
    }

    private Size Cell
    {
        get
        {
            Size cell = _surface.CellSize;
            return _surface.LayoutTransform is Media.ScaleTransform scale ? new Size(cell.Width * scale.ScaleX, cell.Height * scale.ScaleY) : cell;
        }
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Cell.Width * Glyphs[0].Length, Cell.Height * (Glyphs.Length + TopRows));

    protected override void OnRender(Media.DrawingContext drawing)
    {
        Size cell = Cell;
        int columns = (int)Math.Floor(_surface.ActualWidth / Math.Max(1, _surface.CellSize.Width));
        int conversation = columns > WideColumns ? columns - SidePanelColumns : columns;
        double offset = Math.Max(0, (conversation - Glyphs[0].Length) / 2) * cell.Width;
        var dpi = Media.VisualTreeHelper.GetDpi(this);
        double SnapX(double value) => Math.Round(value * dpi.DpiScaleX) / dpi.DpiScaleX;
        double SnapY(double value) => Math.Round(value * dpi.DpiScaleY) / dpi.DpiScaleY;
        for (int row = 0; row < Glyphs.Length; row++)
            for (int column = 0; column < Glyphs[row].Length; column++)
            {
                char style = Styles[row][column];
                if (style == ' ') continue;
                double left = SnapX(offset + column * cell.Width), right = SnapX(offset + (column + 1) * cell.Width);
                double top = SnapY((row + TopRows) * cell.Height), bottom = SnapY((row + TopRows + 1) * cell.Height), middle = SnapY((row + TopRows + 0.5) * cell.Height);
                if (style is 'O' or 'C') drawing.DrawRectangle(style == 'O' ? OpenInside : CodeInside, null, new Rect(left, top, right - left, bottom - top));
                Media.Brush ink = style switch { 'c' or 'C' => Code, 'd' => OpenInside, _ => Open };
                switch (Glyphs[row][column])
                {
                    case '█': drawing.DrawRectangle(ink, null, new Rect(left, top, right - left, bottom - top)); break;
                    case '▀': drawing.DrawRectangle(ink, null, new Rect(left, top, right - left, middle - top)); break;
                    case '▄': drawing.DrawRectangle(ink, null, new Rect(left, middle, right - left, bottom - middle)); break;
                }
            }
    }

    private static Media.Brush Gray(byte level)
    {
        var brush = new Media.SolidColorBrush(Media.Color.FromRgb(level, level, level));
        brush.Freeze();
        return brush;
    }
}
}
#endregion
#endif
