using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace TerminalNotes;

// The rest of the herdr and Hermes ideas the user chose (2026-11-11): 지난 대화 검색 and 예약 작업 buttons beside the agent board, the
// scheduled jobs running every half minute, and agents in outside terminals on the board.
public partial class MainWindow
{
    private DispatcherTimer? _jobTimer;
    private readonly Dictionary<string, DateTimeOffset> _jobWaitingSince = new(StringComparer.Ordinal);
    private readonly HashSet<string> _jobsSending = new(StringComparer.Ordinal);
    private IReadOnlyList<ExternalAgents.Agent> _externalAgents = [];
    private int _externalScanTick = -1; // the first scan comes with the first tick, not while the window is built

    /// <summary>Only the app itself runs scheduled jobs (a test window does not, unless it points the list elsewhere).</summary>
    private bool RunsJobs => AppContext.GetData("TerminalNotes.ScheduledJobsPath") is string || IsTheApp;

    private void InitializeHerdrExtras(StackPanel actions)
    {
        actions.Children.Insert(0, IconButton("", "예약 작업 · 정한 요일·시각에 태스크 터미널로 지시를 보냅니다", "ScheduledJobsButton", OpenScheduledJobs));
        actions.Children.Insert(0, IconButton("", "지난 대화 검색 · 태스크의 OpenCode·Claude Code 대화", "ConversationSearchButton", OpenConversationSearch));
        _jobTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => RunDueJobs(), Dispatcher);
        _jobTimer.Start();
        Closed += (_, _) => _jobTimer.Stop();
    }

    private Button IconButton(string glyph, string tip, string id, Action click)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14 },
            Width = 34, Height = 30, Margin = new Thickness(0, 0, 4, 0), ToolTip = tip
        };
        button.SetResourceReference(StyleProperty, "QuietIconButton");
        AutomationProperties.SetName(button, tip.Split(" · ")[0]);
        AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) => click();
        return button;
    }

    private IEnumerable<TerminalPane> Panes => TerminalTabs.Items.Cast<TabItem>().Select(tab => ((TerminalTabState)tab.Tag).Pane);

    private void OpenConversationSearch()
    {
        var projects = GetTerminalPickerProjects().Concat(Panes.Select(pane => pane.WorkingDirectory))
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        new ConversationSearchWindow(() => Panes.Select(pane => pane.SearchEndpoint).FirstOrDefault(endpoint => endpoint is not null), projects) { Owner = this }.Show();
    }

    private void OpenScheduledJobs()
    {
        var projects = GetTerminalPickerProjects().Where(Directory.Exists).ToList();
        new ScheduledJobsWindow(projects, ProjectDisplayName) { Owner = this }.ShowDialog();
    }

    // Every half minute: a due job opens its task's terminal and, once the agent is free, gets its instruction.
    private void RunDueJobs()
    {
        if (_closing || _closePending || !RunsJobs || !_openCodeReady) return;
        var now = DateTimeOffset.Now;
        foreach (var job in ScheduledJobs.List())
        {
            if (_jobsSending.Contains(job.Id)) continue;
            var (due, at) = ScheduledJobs.Check(job, now.LocalDateTime);
            if (due == ScheduledJobs.Due.NotYet) { _jobWaitingSince.Remove(job.Id); continue; }
            if (due == ScheduledJobs.Due.Missed)
            {
                ScheduledJobs.MarkRun(job.Id, now, $"건너뜀 · {at:MM-dd HH:mm}에 앱이 꺼져 있었습니다");
                continue;
            }
            var waiting = _jobWaitingSince.TryGetValue(job.Id, out var since) ? since : _jobWaitingSince[job.Id] = now;
            if (now - waiting > ScheduledJobs.BusyWait)
            {
                _jobWaitingSince.Remove(job.Id);
                ScheduledJobs.MarkRun(job.Id, now, "건너뜀 · 30분 동안 에이전트가 바빴습니다");
                continue;
            }
            if (!Directory.Exists(job.Project)) { ScheduledJobs.MarkRun(job.Id, now, "건너뜀 · 태스크 폴더가 없습니다"); continue; }
            TerminalPane pane;
            try { pane = OpenTaskTerminal(job.Project); }
            catch (Exception exception) when (exception is InvalidOperationException or IOException) { continue; }
            if (!pane.IsReady || StateOf(pane) is AgentState.Working or AgentState.Blocked) continue;
            _ = SendJobAsync(pane, job);
        }
    }

    private async Task SendJobAsync(TerminalPane pane, ScheduledJobs.Job job)
    {
        _jobsSending.Add(job.Id);
        try
        {
            string text = $"[예약 작업 · {ScheduledJobs.When(job)}]\n{job.Instruction}";
            // OpenCode takes it through its server; Claude Code and Codex as typed input.
            bool sent = pane.IsTypedAgent
                ? await pane.SubmitTypedAgentInputAsync(text.Replace('\n', ' '), CancellationToken.None)
                : await pane.SubmitStartupInputAsync(text, false, () => !_closing, _ => { }, CancellationToken.None);
            _jobWaitingSince.Remove(job.Id);
            ScheduledJobs.MarkRun(job.Id, DateTimeOffset.Now, sent ? "보냄" : "보내지 못함 · " + (pane.LastPlanExecutionFailure ?? "터미널이 받지 않았습니다"));
            if (!sent && _taskNotificationPreferences.CanNotify(DateTimeOffset.UtcNow))
                _tray?.ShowTaskNotification("예약 작업", $"{ProjectDisplayName(job.Project)}\n지시를 보내지 못했습니다.", true, () => !_closing);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ScheduledJobs.MarkRun(job.Id, DateTimeOffset.Now, "보내지 못함 · " + exception.Message);
        }
        finally { _jobsSending.Remove(job.Id); }
    }

    /// <summary>Replaces the scan of outside terminals (tests).</summary>
    internal Func<IReadOnlySet<int>, IReadOnlyList<ExternalAgents.Agent>>? ExternalScanOverride { get; set; }

    /// <summary>Every third second: the agents in outside terminals (not this app's servers or TUIs).</summary>
    private void ScanExternalAgents()
    {
        if (++_externalScanTick % 3 != 1) return;
        var ours = Panes.Select(pane => pane.ServerProcessId).OfType<int>().ToHashSet();
        try { _externalAgents = ExternalScanOverride is { } scan ? scan(ours) : ExternalAgents.Scan(ours); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { _externalAgents = []; }
    }
}

/// <summary>예약 작업: the jobs (when · task, the instruction, the last result) with on/off, 수정 and 삭제, and a form to add one.</summary>
internal sealed class ScheduledJobsWindow : Window
{
    private readonly IReadOnlyList<string> _projects;
    private readonly Func<string, string> _name;
    private readonly StackPanel _list = new() { Margin = new Thickness(18, 0, 18, 8) };
    private readonly Button _project = new() { MinWidth = 220, Height = 32, HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 0, 12, 0) };
    private string? _projectPath;
    private readonly TextBox _instruction = new() { Tag = "보낼 지시 (예: 빌드하고 테스트해서 실패하면 고쳐)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 76,
        Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 10, 0, 0), MaxLength = 4000 };
    private readonly TextBox _time = new() { Text = "09:00", Width = 70, MaxLength = 5, TextAlignment = TextAlignment.Center, Height = 32, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ToggleButton[] _days = ScheduledJobs.DayNames.Select(day => new ToggleButton { Content = day, Width = 34, Height = 30, Margin = new Thickness(0, 0, 4, 0) }).ToArray();
    private readonly TextBlock _error = new();
    private readonly Button _save = new() { Content = "추가", MinWidth = 88 };
    private string? _editing;

    internal ScheduledJobsWindow(IReadOnlyList<string> projects, Func<string, string> name)
    {
        _projects = projects;
        _name = name;
        Title = "예약 작업";
        Width = 640; MinWidth = 520; SizeToContent = SizeToContent.Height; MaxHeight = SystemParameters.WorkArea.Height - 40;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        // The task: a button with the store's menu (a Windows combo box would not take the dark look).
        PickProject(projects.FirstOrDefault());
        _project.Click += (_, _) =>
        {
            var menu = StoreLook.Menu();
            foreach (string project in _projects) StoreLook.Item(menu, _name(project), () => PickProject(project));
            StoreLook.Open(menu, _project);
        };
        AutomationProperties.SetName(_project, "태스크");
        AutomationProperties.SetName(_instruction, "보낼 지시");
        AutomationProperties.SetName(_time, "시각");
        foreach (var day in _days) { day.IsChecked = true; day.SetResourceReference(StyleProperty, "StoreChip"); }
        var form = new StackPanel { Margin = new Thickness(24, 6, 24, 0) };
        var when = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var day in _days) when.Children.Add(day);
        when.Children.Add(Quick("매일", ScheduledJobs.EveryDay));
        when.Children.Add(Quick("평일", ScheduledJobs.Weekdays));
        _time.Margin = new Thickness(10, 0, 0, 0);
        when.Children.Add(_time);
        var target = new DockPanel();
        target.Children.Add(_project);
        form.Children.Add(target);
        form.Children.Add(_instruction);
        form.Children.Add(when);
        var heading = new TextBlock { Text = "예약된 작업", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(24, 14, 24, 6) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        var cancel = new Button { Content = "닫기", IsCancel = true, MinWidth = 88 };
        _save.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetAutomationId(_save, "ScheduledJobSave");
        _save.Click += (_, _) => Save();
        var footer = StoreLook.Footer(_error, cancel, _save);
        var body = new StackPanel();
        body.Children.Add(heading);
        body.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var line = new Border { Height = 1, Margin = new Thickness(24, 6, 24, 6) };
        line.SetResourceReference(Border.BackgroundProperty, "Line");
        body.Children.Add(line);
        body.Children.Add(form);
        body.Children.Add(footer);
        StoreLook.Dress(this, Title, body, projects.Count == 0 ? "태스크가 없습니다" : null);
        Render();
    }

    private void PickProject(string? project)
    {
        _projectPath = project;
        _project.Content = project is null ? "태스크 없음" : _name(project) + "  ⌄";
    }

    private Button Quick(string text, int days)
    {
        var button = new Button { Content = text, Height = 30, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(4, 0, 0, 0) };
        button.SetResourceReference(StyleProperty, "QuietButton");
        button.Click += (_, _) => { for (int index = 0; index < _days.Length; index++) _days[index].IsChecked = (days & (1 << index)) != 0; };
        return button;
    }

    private void Render()
    {
        _list.Children.Clear();
        var jobs = ScheduledJobs.List();
        foreach (var job in jobs) _list.Children.Add(Row(job));
        if (jobs.Count == 0)
        {
            var none = new TextBlock { Text = "아직 없습니다. 아래에서 태스크와 지시, 요일과 시각을 정해 추가하세요.", FontSize = 13, Margin = new Thickness(6, 2, 6, 6), TextWrapping = TextWrapping.Wrap };
            none.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            _list.Children.Add(none);
        }
    }

    private Border Row(ScheduledJobs.Job job)
    {
        var title = new TextBlock { Text = $"{ScheduledJobs.When(job)} · {_name(job.Project)}", FontSize = 13.5, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var what = new TextBlock { Text = job.Instruction.ReplaceLineEndings(" "), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) };
        what.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        var text = new StackPanel();
        text.Children.Add(title);
        text.Children.Add(what);
        if (job.LastRun is { } last)
        {
            var result = new TextBlock { Text = $"{last.LocalDateTime:MM-dd HH:mm} {job.LastResult}", FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            result.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            text.Children.Add(result);
        }
        var toggle = new ToggleButton { IsChecked = job.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        toggle.SetResourceReference(StyleProperty, "StoreSwitch");
        AutomationProperties.SetName(toggle, "켜기 · " + ScheduledJobs.When(job));
        toggle.Click += (_, _) => { ScheduledJobs.SetEnabled(job.Id, toggle.IsChecked == true); Render(); };
        var edit = new Button { Content = "수정", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var delete = new Button { Content = "삭제", Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        edit.SetResourceReference(StyleProperty, "QuietButton");
        delete.SetResourceReference(StyleProperty, "QuietButton");
        edit.Click += (_, _) => Edit(job);
        delete.Click += (_, _) =>
        {
            if (!AppMessageDialog.Confirm(this, "예약 작업", $"'{ScheduledJobs.When(job)} · {_name(job.Project)}'을(를) 지울까요?", "삭제")) return;
            ScheduledJobs.Delete(job.Id);
            if (_editing == job.Id) Edit(null);
            Render();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(edit);
        buttons.Children.Add(delete);
        buttons.Children.Add(toggle);
        var row = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(text);
        var frame = new Border { Child = row, CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 9, 10, 9), Margin = new Thickness(6, 0, 6, 6), BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BackgroundProperty, "Surface");
        frame.SetResourceReference(Border.BorderBrushProperty, _editing == job.Id ? "Accent" : "ButtonLine");
        AutomationProperties.SetAutomationId(frame, "ScheduledJobRow");
        return frame;
    }

    private void Edit(ScheduledJobs.Job? job)
    {
        _editing = job?.Id;
        _save.Content = job is null ? "추가" : "저장";
        _instruction.Text = job?.Instruction ?? "";
        _time.Text = job?.Time ?? "09:00";
        for (int index = 0; index < _days.Length; index++) _days[index].IsChecked = ((job?.Days ?? ScheduledJobs.EveryDay) & (1 << index)) != 0;
        if (job is not null) PickProject(job.Project);
        _error.Text = "";
        Render();
    }

    private void Save()
    {
        try
        {
            if (_projectPath is not { } project) throw new ArgumentException("태스크를 고르세요.");
            int days = _days.Select((day, index) => day.IsChecked == true ? 1 << index : 0).Sum();
            var existing = _editing is null ? null : ScheduledJobs.List().FirstOrDefault(job => job.Id == _editing);
            var job = existing is null
                ? new ScheduledJobs.Job { Project = project, Instruction = _instruction.Text, Days = days, Time = _time.Text }
                : existing with { Project = project, Instruction = _instruction.Text, Days = days, Time = _time.Text, Created = DateTimeOffset.Now, LastRun = null, LastResult = null };
            ScheduledJobs.Save(job);
            Edit(null);
        }
        catch (ArgumentException exception) { _error.Text = exception.Message; }
        catch (IOException exception) { _error.Text = "저장하지 못했습니다 · " + exception.Message; }
    }
}
