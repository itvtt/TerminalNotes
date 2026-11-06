using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using TerminalNotes.Services;

namespace TerminalNotes;

// Loop keeps going until the user stops it or its end time passes (사용자: "멈추면 어떻게든 다시 실행시키고 다시 명령시키고
// 계속 이어가게", "중간에 끊기거나 멈추면 loop가 무슨 의미가 있어"). A started Loop task whose work stopped is rerun from the saved
// plan: its terminal closed or failed, OpenCode idle while the Loop's own recovery is paused, gone or not acting, busy with
// nobody recovering it, or its state unreadable (a hung server: the terminal is replaced). Reruns that bring no progress
// wait 1, 3, then 10 minutes apart, with no limit on their number. A question nobody answers gets its recommended answer,
// and Loops that were running when the app or Windows went down start again with the app.
public partial class MainWindow
{
    private static readonly bool LoopWatchdogInApp =
        string.Equals(Assembly.GetEntryAssembly()?.GetName().Name, "TerminalNotes", StringComparison.OrdinalIgnoreCase);
    private static readonly TimeSpan LoopIdleGrace = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LoopClosedGrace = TimeSpan.FromSeconds(30);
    // A busy session whose recovery paused may still be in a long tool run: give it much longer.
    private static readonly TimeSpan LoopBusyPausedGrace = TimeSpan.FromMinutes(10);
    // OpenCode's tools time out within 10 minutes, so a busy session nobody recovers that long without any progress is stuck.
    private static readonly TimeSpan LoopBusyUnownedGrace = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan LoopBusyOwnedGrace = TimeSpan.FromMinutes(45);
    // The Loop's own review rounds come at most 10 minutes apart; idle far longer means its recovery stopped acting.
    private static readonly TimeSpan LoopActiveIdleGrace = TimeSpan.FromMinutes(20);
    // OpenCode's state unreadable this long: its server hangs, so the terminal itself is replaced.
    private static readonly TimeSpan LoopUnknownGrace = TimeSpan.FromMinutes(5);
    // A question nobody answers this long during a Loop gets its recommended answer, or "decide yourself".
    internal static readonly TimeSpan LoopQuestionGrace = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan[] LoopRerunBackoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(10)];
    private readonly Dictionary<string, LoopWatch> _loopWatches = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loopResumes = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _loopWatchdogTimer;
    private string[] _loopResumeSaved = [];

    private enum LoopStall { Closed, Idle, ActiveIdle, BusyPaused, BusyUnowned, BusyOwned, Unknown }

    private sealed class LoopWatch
    {
        internal DateTimeOffset? StuckSince;
        internal LoopStall? Kind;
        internal string? Fingerprint;
        internal DateTimeOffset? NextRerunAt;
        internal int Reruns;
        internal bool Rerunning, LastRerunFailed;
        internal DateTimeOffset? QuestionSince, NextQuestionAnswerAt;
    }

    private void StartLoopWatchdog()
    {
        // Test processes drive CheckLoopWatchdog directly with their own clock.
        if (_loopWatchdogTimer is not null || !_autoStartCodex || !LoopWatchdogInApp) return;
        _loopWatchdogTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background,
            (_, _) => CheckLoopWatchdog(DateTimeOffset.Now), Dispatcher);
        _loopWatchdogTimer.Start();
    }

    private void CheckLoopWatchdog(DateTimeOffset now)
    {
        if (_closing || _closePending || _resourcesDisposed) return;
        foreach (string project in _automationRuntimes.Keys.ToArray())
        {
            if (!_automationRuntimes.TryGetValue(project, out var runtime) || !LoopShouldContinue(project, runtime, now))
            {
                if (!_loopWatches.TryGetValue(project, out var stale) || !stale.Rerunning) _loopWatches.Remove(project);
                continue;
            }
            if (!_loopWatches.TryGetValue(project, out var watch)) _loopWatches[project] = watch = new LoopWatch();
            if (watch.Rerunning) continue;
            var pane = runtime.SingleTerminal?.Tag is TerminalTabState { Pane: { } live } ? live : null;
            WatchLoopQuestion(project, pane, watch, now);
            var stall = LoopStuckState(project, runtime, pane);
            if (stall is not { } stuck)
            {
                watch.StuckSince = null;
                watch.Kind = null;
                // Real work again: the next stop starts from the shortest wait.
                if (pane is { NativeOpenCodeStatus: OpenCodeStatusKind.Busy, IsContinuousImprovementActive: true })
                { watch.Reruns = 0; watch.NextRerunAt = null; watch.LastRerunFailed = false; }
                continue;
            }
            // Any change of state or progress restarts the wait for this state.
            string fingerprint = LoopActivityFingerprint(pane);
            if (watch.Kind != stuck.Kind || watch.Fingerprint != fingerprint)
            {
                watch.Kind = stuck.Kind;
                watch.Fingerprint = fingerprint;
                watch.StuckSince = now;
            }
            watch.StuckSince ??= now;
            if (now - watch.StuckSince < stuck.Grace || watch.NextRerunAt is { } next && now < next) continue;
            watch.NextRerunAt = now + LoopRerunBackoff[Math.Min(watch.Reruns, LoopRerunBackoff.Length - 1)];
            watch.Reruns++;
            watch.StuckSince = null;
            // A hung OpenCode server, or a rerun that already failed once, gets a fresh terminal.
            bool replaceTerminal = stuck.Kind == LoopStall.Unknown || watch.LastRerunFailed;
            RecordAutomationDiagnostic("loop_watchdog_rerun", $"Loop stopped without progress ({stuck.Kind}); rerun {watch.Reruns}" +
                (replaceTerminal ? " in a new terminal" : ""), projectDirectory: project, deduplicate: false);
            SetProgressStatus($"Loop · {ProjectDisplayName(project)} · 멈춘 작업을 " + (replaceTerminal ? "새 터미널에서 " : "") + "다시 실행합니다");
            TrackJournalWork(LoopWatchdogRerunAsync(project, watch, replaceTerminal));
        }
        SaveLoopResume(now);
    }

    // Loop is on, allowed and before its end time, and this task was started in this session and not stopped by the user.
    private bool LoopShouldContinue(string project, AutomationProjectRuntime runtime, DateTimeOffset now) =>
        !_planWorkMode && !IsBasicWorkMode && LoopAutoRecommandUsers.Allowed &&
        _projectTaskContinuousModes.GetValueOrDefault(project) &&
        (_projectTaskImprovementUntil.GetValueOrDefault(project) is not { } until || until > now) &&
        runtime.SingleSubmitted && !runtime.SingleStopped &&
        // Loop needs OpenCode's session API: a task running (or set to run) Codex or Claude Code is never rerun.
        AgentForTask(project) == TerminalTool.OpenCode && runtime.SingleTerminal?.Tag is not TerminalTabState { Pane.IsTypedAgent: true };

    // The stopped state the task is in and how long it may stay there before a rerun, or null while it is working,
    // waiting for the user (a permission, typed input, an open PlanMaker) or already being started or recovered.
    private (LoopStall Kind, TimeSpan Grace)? LoopStuckState(string project, AutomationProjectRuntime runtime, TerminalPane? pane)
    {
        if (runtime.StartingSingle || runtime.SingleStartupCancellation is not null || _startingProjectTasks.Contains(project) ||
            _rerunningProjectTasks.Contains(project) || _stoppingProjectTasks.Contains(project) ||
            _projectTaskControlWrites.Contains(project) || _projectTaskOptionWrites.Contains(project) ||
            _recommendedPlanProjects.Contains(project) || _planMakers.ContainsKey(project) ||
            _pendingProjectInputs.Contains(project) || _tabDocumentSaving || HasExternalTerminal(project)) return null;
        // A scheduled process-error retry is already on its way.
        if (_processErrorRecoveries.TryGetValue(project, out var recovery) && (recovery.Running || recovery.RetryAt is not null)) return null;
        if (!HasLiveSingleTerminal(runtime) || pane is null || !pane.IsRunning) return (LoopStall.Closed, LoopClosedGrace);
        if (pane.IsPlanMakerActive || pane.IsWaitingForResources || pane.IsAutoResumeResourceWaiting) return null;
        var input = pane.NativeAutomationDiagnostic;
        if (input.HasPendingInput || input.ManualOverride) return null;
        // Credentials, permissions, billing or a malformed request: only a person can fix it (2026-10-20 지시).
        if (pane.AutoResumeWaitsForPerson) return null;
        bool owned = pane.IsContinuousImprovementActive, paused = pane.IsAutoResumePaused;
        return pane.NativeOpenCodeStatus switch
        {
            // A question is answered by WatchLoopQuestion; a permission request is the user's.
            OpenCodeStatusKind.NeedsInput => null,
            OpenCodeStatusKind.Busy or OpenCodeStatusKind.Pending =>
                paused ? (LoopStall.BusyPaused, LoopBusyPausedGrace) :
                owned ? (LoopStall.BusyOwned, LoopBusyOwnedGrace) : (LoopStall.BusyUnowned, LoopBusyUnownedGrace),
            OpenCodeStatusKind.Unknown => (LoopStall.Unknown, LoopUnknownGrace),
            _ => owned && !paused ? (LoopStall.ActiveIdle, LoopActiveIdleGrace) : (LoopStall.Idle, LoopIdleGrace)
        };
    }

    private static string LoopActivityFingerprint(TerminalPane? pane) => pane is null ? "" :
        $"{pane.NativeOpenCodeStatus}|{pane.AutoResumeLastProgressAt?.UtcTicks}|{pane.NativeSubmissionGeneration}|" +
        $"{pane.IsContinuousImprovementActive}|{pane.IsAutoResumePaused}";

    private async Task LoopWatchdogRerunAsync(string project, LoopWatch watch, bool replaceTerminal)
    {
        watch.Rerunning = true;
        var runtime = RuntimeFor(project);
        try
        {
            runtime.SingleError = null;
            if (replaceTerminal && runtime.SingleTerminal is { Tag: TerminalTabState state } tab)
            {
                // Keep the task in its split slot while the old terminal goes away.
                int slot = _terminalSlotLayout.IndexOf(project);
                DetachTaskTerminal(tab);
                TerminalTabs.Items.Remove(tab);
                try { await state.Pane.StopForTerminalLifecycleAsync(); }
                finally { DisposeSafely(state.Pane); }
                if (slot >= 0) AssignProjectToSlot(project, slot);
                UpdateTerminalCount();
            }
            await RerunProjectTaskCoreAsync(project, loopWatchdog: true);
            watch.LastRerunFailed = runtime.SingleError is not null;
        }
        catch (Exception ex)
        {
            watch.LastRerunFailed = true;
            RecordAutomationDiagnostic("loop_watchdog_rerun_failed", ex.GetType().Name, projectDirectory: project, severity: "warning");
        }
        finally { watch.Rerunning = false; }
    }

    // Loop runs unattended: a question OpenCode asks and nobody answers for LoopQuestionGrace gets its recommended option,
    // or "decide yourself". Permission requests stay the user's.
    private void WatchLoopQuestion(string project, TerminalPane? pane, LoopWatch watch, DateTimeOffset now)
    {
        if (pane is not { NativeOpenCodeStatus: OpenCodeStatusKind.NeedsInput, IsRunning: true } ||
            pane.NativeAutomationDiagnostic is { HasPendingInput: true } or { ManualOverride: true })
        {
            watch.QuestionSince = watch.NextQuestionAnswerAt = null;
            return;
        }
        watch.QuestionSince ??= now;
        if (now - watch.QuestionSince < LoopQuestionGrace || watch.NextQuestionAnswerAt is { } next && now < next) return;
        watch.NextQuestionAnswerAt = now + LoopQuestionGrace;
        TrackJournalWork(AnswerLoopQuestionAsync(project, pane));
    }

    private async Task AnswerLoopQuestionAsync(string project, TerminalPane pane)
    {
        try
        {
            if (await pane.AnswerLoopQuestionAsync(_startupCancellation.Token) is not { } answer) return;
            RecordAutomationDiagnostic("loop_question_answered", "Loop answered a question nobody answered", projectDirectory: project, deduplicate: false);
            SetProgressStatus($"Loop · {ProjectDisplayName(project)} · 질문에 {LoopQuestionGrace.TotalMinutes:0}분 동안 답이 없어 {answer}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        { RecordAutomationDiagnostic("loop_question_answer_failed", ex.GetType().Name, projectDirectory: project, severity: "warning"); }
    }

    // ── Resume after the app or Windows restarted ───────────────────────────────────────────────────────────────

    private static string LoopResumePath => Path.Combine(OpenCodeProjectContext.DataDirectory, "loop-resume.json");

    // The Loops that should be running now; written when that set changes, so a crash or a Windows restart keeps it.
    private void SaveLoopResume(DateTimeOffset now)
    {
        if (!_autoStartCodex || !LoopWatchdogInApp) return;
        string[] running = _automationRuntimes
            .Where(pair => LoopShouldContinue(pair.Key, pair.Value, now) || _loopWatches.TryGetValue(pair.Key, out var watch) && watch.Rerunning)
            .Select(pair => pair.Key).Concat(_loopResumes).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(project => project, StringComparer.OrdinalIgnoreCase).ToArray();
        if (running.SequenceEqual(_loopResumeSaved, StringComparer.OrdinalIgnoreCase)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LoopResumePath)!);
            string temporary = LoopResumePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(running));
            File.Move(temporary, LoopResumePath, overwrite: true);
            _loopResumeSaved = running;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { RecordAutomationDiagnostic("loop_resume_save_failed", ex.GetType().Name, severity: "warning"); }
    }

    private void ForgetLoopResume()
    {
        _loopResumes.Clear();
        _loopResumeSaved = [];
        try { File.Delete(LoopResumePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Loops that were running when the app last went down (not closed by the user) start again, one at a time.
    private void ResumeInterruptedLoops()
    {
        if (!_autoStartCodex || !LoopWatchdogInApp) return;
        string[] projects;
        try { projects = File.Exists(LoopResumePath) ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(LoopResumePath)) ?? [] : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { projects = []; }
        _loopResumeSaved = projects;
        if (projects.Length == 0) return;
        foreach (string project in projects) _loopResumes.Add(CanonicalProjectPath(project));
        TrackJournalWork(ResumeInterruptedLoopsAsync());
    }

    private async Task ResumeInterruptedLoopsAsync()
    {
        // Let the window, the task list and the OpenCode check settle first.
        await Task.Delay(TimeSpan.FromSeconds(20));
        foreach (string project in _loopResumes.ToArray())
        {
            if (_closing || _closePending) return;
            string name = ProjectDisplayName(project);
            try
            {
                LoadSingleProjectImprovementPreference(project);
                var until = _projectTaskImprovementUntil.GetValueOrDefault(project);
                string? blocked = _planWorkMode || IsBasicWorkMode || !LoopAutoRecommandUsers.Allowed ? "Loop를 쓸 수 없는 모드라" :
                    !_projectTaskContinuousModes.GetValueOrDefault(project) ? "Loop가 꺼져 있어" :
                    until is { } end && end <= DateTimeOffset.Now ? "Loop 종료 시간이 지나" :
                    ScheduledProjectStartBlockReason(project);
                if (blocked is not null)
                {
                    if (blocked != "이미 실행 중이라") SetAttentionStatus($"{name} · {blocked} 앱을 다시 시작한 뒤 Loop를 이어 가지 않았습니다.");
                    continue;
                }
                for (int waited = 0; waited < 120 && ScheduledProjectStartIsBusy(project); waited++)
                {
                    await Task.Delay(250);
                    if (_closing || _closePending) return;
                }
                RecordAutomationDiagnostic("loop_resumed_after_restart", "Loop restarted with the app", projectDirectory: project, deduplicate: false);
                SetProgressStatus($"{name} · 앱이 다시 시작되어 Loop를 이어 갑니다");
                await StartProjectTaskAsync(project);
                if (RuntimeFor(project).SingleError is { } error) SetAttentionStatus($"{name} · Loop 이어 가기 실패 · {error}");
                else if (ProjectTaskActionIsStop(project))
                    SetCompletionNotice($"{name} · 앱이 다시 시작되어 Loop를 이어 갑니다.");
                // Space the starts like the startup admission does.
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RecordAutomationDiagnostic("loop_resume_failed", ex.GetType().Name, projectDirectory: project, severity: "warning");
                SetAttentionStatus($"{name} · Loop 이어 가기 실패 · {ex.Message}");
            }
            finally { _loopResumes.Remove(project); }
        }
        SaveLoopResume(DateTimeOffset.Now);
    }
}
