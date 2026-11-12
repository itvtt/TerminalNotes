using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using TerminalNotes.Services;

namespace TerminalNotes;

/// <summary>Plan cleanups set for a time (one per task): %LOCALAPPDATA%\TerminalNotes\plan-cleanup-schedules.json, task → when.</summary>
internal static class PlanCleanupScheduleStore
{
    private static readonly object Gate = new();

    // Read each time: hosts and tests point the data folder elsewhere before the first use.
    private static string StorePath => Path.Combine(OpenCodeProjectContext.DataDirectory, "plan-cleanup-schedules.json");

    internal static Dictionary<string, DateTimeOffset> ReadAll()
    {
        lock (Gate) return ReadCore();
    }

    /// <summary>Sets or (null) removes one task's cleanup time.</summary>
    internal static void Write(string project, DateTimeOffset? at)
    {
        lock (Gate)
        {
            var records = ReadCore();
            if (at is { } value) records[project] = value;
            else if (!records.Remove(project)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            string temporary = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(records));
                File.Move(temporary, StorePath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static Dictionary<string, DateTimeOffset> ReadCore()
    {
        if (!File.Exists(StorePath)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(StorePath)) is { } records
                ? new(records, StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException exception) { throw new InvalidDataException("플랜 정리 예약 파일이 손상되어 그대로 두었습니다.", exception); }
    }
}

// Plan cleanup at a set time (2026-11-12 사용자: "플랜정리도 시간 예약을 걸어두게 할 수 있나?"; the model answers faster at
// night). Like 예약 실행: one time per task, kept across restarts, checked every 15 seconds, the PC kept awake while one
// is pending, and one missed while the app was closed is reported instead of run late. Nobody is there to press 저장, so the result
// is saved to plan.md the way the cleanup after PlanMaker is, with the plan as it was kept in terminal-notes\plan.before-cleanup.md.
public partial class MainWindow
{
    private static readonly TimeSpan ScheduledCleanupBusyWait = TimeSpan.FromMinutes(30);
    private readonly Dictionary<string, DateTimeOffset> _planCleanupSchedules = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _scheduledPlanCleanups = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _planCleanupScheduleTimer;
    private DateTimeOffset _planCleanupSchedulesLoadedAt = DateTimeOffset.Now;

    private void InitializePlanCleanupSchedules()
    {
        _planCleanupSchedulesLoadedAt = DateTimeOffset.Now;
        try
        {
            foreach (var (project, at) in PlanCleanupScheduleStore.ReadAll())
                _planCleanupSchedules[CanonicalProjectPath(project)] = at;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        { SetStatus("플랜 정리 예약을 읽지 못했습니다 · " + exception.Message); }
        UpdatePlanCleanupScheduleTimer();
    }

    private void UpdatePlanCleanupScheduleTimer()
    {
        if (_closing || _resourcesDisposed || _planCleanupSchedules.Count == 0) _planCleanupScheduleTimer?.Stop();
        else
        {
            if (_planCleanupScheduleTimer is null)
            {
                _planCleanupScheduleTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(15) };
                _planCleanupScheduleTimer.Tick += (_, _) => RunDuePlanCleanupSchedules();
            }
            if (!_planCleanupScheduleTimer.IsEnabled) _planCleanupScheduleTimer.Start();
        }
        UpdateSleepPrevention();
    }

    // Within a day the time alone says when (it is the next one); later ones carry the date.
    private static string PlanCleanupTimeLabel(DateTimeOffset at) =>
        at.LocalDateTime.ToString(at - DateTimeOffset.Now < TimeSpan.FromDays(1) ? "HH:mm" : "MM/dd HH:mm", CultureInfo.InvariantCulture);

    private DateTimeOffset? PlanCleanupScheduleFor(string project) =>
        _planCleanupSchedules.TryGetValue(CanonicalProjectPath(project), out var at) ? at : null;

    /// <summary>Sets or (null) clears one task's cleanup time. A new time that cannot be saved is refused.</summary>
    private bool SetPlanCleanupSchedule(string project, DateTimeOffset? at)
    {
        project = CanonicalProjectPath(project);
        try { PlanCleanupScheduleStore.Write(project, at); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus("플랜 정리 예약을 저장하지 못했습니다 · " + exception.Message);
            if (at is not null) return false;
        }
        if (at is { } value) _planCleanupSchedules[project] = value;
        else _planCleanupSchedules.Remove(project);
        UpdatePlanCleanupScheduleTimer();
        if (!_resourcesDisposed) RefreshOrganizePlanControl();
        return true;
    }

    private void MovePlanCleanupSchedule(string source, string destination)
    {
        if (PlanCleanupScheduleFor(source) is not { } at) return;
        SetPlanCleanupSchedule(source, null);
        if (!SetPlanCleanupSchedule(destination, at))
            SetAttentionStatus($"{ProjectDisplayName(destination)} · 이동한 태스크의 플랜 정리 예약을 저장하지 못했습니다. 다시 예약하세요.");
    }

    private void RunDuePlanCleanupSchedules()
    {
        if (_closing || _closePending || _resourcesDisposed) return;
        var now = DateTimeOffset.Now;
        foreach (var (project, at) in _planCleanupSchedules.Where(pair => pair.Value <= now).ToArray())
        {
            if (at < _planCleanupSchedulesLoadedAt - MissedProjectStartGrace)
            {
                SetPlanCleanupSchedule(project, null);
                SetAttentionStatus($"{ProjectDisplayName(project)} · 앱이 꺼져 있어 {at.LocalDateTime:MM/dd HH:mm} 예약한 플랜 정리를 하지 못했습니다. 다시 예약하세요.");
                continue;
            }
            _ = RunScheduledPlanCleanupAsync(project);
        }
    }

    // Work on the same plan that ends by itself: the cleanup waits for it (up to ScheduledCleanupBusyWait) instead of skipping.
    private bool ScheduledPlanCleanupWaits(string project) =>
        _tabDocumentSaving || _planMakers.ContainsKey(project) || _pendingProjectInputs.Contains(project) || _organizingPlans.Contains(project);

    internal async Task RunScheduledPlanCleanupAsync(string project)
    {
        project = CanonicalProjectPath(project);
        if (!_scheduledPlanCleanups.Add(project)) return;
        string name = ProjectDisplayName(project);
        try
        {
            // One-shot: cleared first, so a failure or a restart never repeats it.
            SetPlanCleanupSchedule(project, null);
            string? blocked = !Directory.Exists(project) ? "폴더를 찾을 수 없어" :
                IsBasicWorkMode ? "일반 모드라" :
                IsExcludedTaskProject(project) ? "목록에서 제외된 태스크라" : null;
            var waitSince = DateTimeOffset.Now;
            while (blocked is null && ScheduledPlanCleanupWaits(project))
            {
                if (DateTimeOffset.Now - waitSince > ScheduledCleanupBusyWait) { blocked = "플랜메이커나 다른 정리·저장이 30분 넘게 이어져"; break; }
                await Task.Delay(1000);
                if (_closing || _closePending) return;
            }
            AutomationPlanEditSession? session = null;
            if (blocked is null)
            {
                session = await LoadAutomationPlanAsync(null, project, 1, DefaultProjectPlanPath(project), refreshCleanSnapshot: true);
                if (_closing || _closePending) return;
                blocked = !string.Equals(session.Edit.Draft, session.Snapshot.Text, StringComparison.Ordinal) ? "저장하지 않은 플랜 초안이 있어" :
                    !session.Snapshot.Exists || string.IsNullOrWhiteSpace(session.Snapshot.Text) ? "플랜이 비어 있어" : null;
            }
            if (blocked is not null) { SetAttentionStatus($"{name} · {blocked} 예약한 플랜 정리를 건너뛰었습니다."); return; }
            SetProgressStatus($"{name} · 예약한 플랜 정리 시작");
            await OrganizePlanDraftAsync(project, session!.Path, "예약 플랜 정리 · " + name, save: true);
        }
        catch (Exception exception)
        {
            if (_closing || _closePending) return;
            RecordAutomationDiagnostic("scheduled_plan_cleanup_failed", "예약 플랜 정리 실패 · " + exception.Message, severity: "error", exception: exception);
            SetAttentionStatus($"{name} · 예약한 플랜 정리 실패 · {exception.Message}");
        }
        finally { _scheduledPlanCleanups.Remove(project); }
    }

    // The next time of day at `hour` (today if still ahead, otherwise tomorrow).
    private static DateTime NextAt(int hour)
    {
        DateTime today = DateTime.Today.AddHours(hour);
        return today > DateTime.Now.AddMinutes(1) ? today : today.AddDays(1);
    }

    private void ShowPlanCleanupSchedule(string project)
    {
        project = CanonicalProjectPath(project);
        if (_closing || _closePending) return;
        if (_projectTaskRows.TryGetValue(project, out var row)) row.Select.ContextMenu.IsOpen = false;
        var existing = PlanCleanupScheduleFor(project);
        var window = new Window
        {
            Owner = this, Title = "플랜 정리 예약", Width = 440, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = StateColorBrush("#1A1A1A"), Foreground = StateColorBrush("#F2F2F2"),
            FontFamily = FontFamily, FontSize = 14
        };
        window.Resources.MergedDictionaries.Add(Resources);
        DateTime start = existing?.LocalDateTime ?? NextAt(22);
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = ProjectDisplayName(project), FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "날짜 (연-월-일)", Margin = new Thickness(0, 16, 0, 5) });
        var date = new TextBox { Text = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Name = "PlanCleanupDate" };
        panel.Children.Add(date);
        panel.Children.Add(new TextBlock { Text = "시간", ToolTip = "24시간 · 이 컴퓨터의 현지 시간", Margin = new Thickness(0, 12, 0, 5) });
        var time = new TextBox { Text = start.ToString("HH:mm", CultureInfo.InvariantCulture), Name = "PlanCleanupTime" };
        panel.Children.Add(time);
        var presets = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (text, hour) in new[] { ("22:00", 22), ("자정", 0), ("새벽 2시", 2), ("새벽 4시", 4) })
        {
            var preset = new Button { Content = text, Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 6, 6), FontSize = 12 };
            preset.Click += (_, _) =>
            {
                DateTime value = NextAt(hour);
                date.Text = value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                time.Text = value.ToString("HH:mm", CultureInfo.InvariantCulture);
            };
            presets.Children.Add(preset);
        }
        panel.Children.Add(presets);
        var note = new TextBlock { Text = "정리 결과는 plan.md에 저장하고, 정리 전 플랜은 terminal-notes\\plan.before-cleanup.md에 남깁니다. 그 시간에 앱이 켜져 있어야 합니다(트레이 가능).",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        panel.Children.Add(note);
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = StateColorBrush("#FFCA80"), Margin = new Thickness(0, 10, 0, 0) };
        date.TextChanged += (_, _) => feedback.Text = "";
        time.TextChanged += (_, _) => feedback.Text = "";
        panel.Children.Add(feedback);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancelSchedule = new Button { Content = "예약 취소", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0),
            Visibility = existing is null ? Visibility.Collapsed : Visibility.Visible };
        cancelSchedule.Click += (_, _) =>
        {
            SetPlanCleanupSchedule(project, null);
            SetStatus($"{ProjectDisplayName(project)} · 플랜 정리 예약을 취소했습니다.");
            window.Close();
        };
        var close = new Button { Content = "닫기", IsCancel = true, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        var apply = new Button { Content = existing is null ? "예약" : "변경", IsDefault = true, Padding = new Thickness(18, 6, 18, 6) };
        apply.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetAutomationId(apply, "PlanCleanupScheduleApply");
        apply.Click += (_, _) =>
        {
            if (!TryParseLocalTime(date.Text, time.Text, out var at)) { feedback.Text = "날짜와 시간을 확인하세요. 예: 2026-09-23 / 02:00"; return; }
            if (at <= DateTimeOffset.Now) { feedback.Text = "현재보다 이후 시간을 지정하세요."; return; }
            if (!SetPlanCleanupSchedule(project, at)) { feedback.Text = "예약을 저장하지 못했습니다. 잠시 후 다시 시도하세요."; return; }
            SetStatus($"{ProjectDisplayName(project)} · 플랜 정리 예약 · {at.LocalDateTime:yyyy-MM-dd HH:mm}");
            window.Close();
        };
        buttons.Children.Add(cancelSchedule);
        buttons.Children.Add(close);
        buttons.Children.Add(apply);
        panel.Children.Add(buttons);
        window.Content = panel;
        window.ShowDialog();
    }
}
