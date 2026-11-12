using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using TerminalNotes.Services;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TerminalNotes.AgentBoardTests")]

namespace TerminalNotes;

// Every agent at a glance, like herdr (2026-11-11 사용자: "herdr 기능도 추가해" → 에이전트 현황판, 막힘·완료 알림): a summary beside
// the terminal tools ("막힘 1 · 작업 중 2 · 완료 1") that opens the board, every terminal with its agent and state, blocked first; a
// click goes to that terminal. A terminal that gets blocked (an answer or approval, a rate limit, an error) or finishes its work
// raises a notification, under the same 작업 알림 setting (on, off, snoozed) as the task notifications.
public partial class MainWindow
{
    internal enum AgentState { Blocked, Working, Done, Waiting, Ended }

    internal enum AgentNotice { None, Blocked, Done, FinishedQuietly }

    internal sealed class AgentWatch
    {
        public AgentState? State;
        public DateTimeOffset Since;
        public bool WasWorking;
        public AgentState? Reported;
    }

    /// <summary>What a terminal's new reading tells: blocked or finished once it held a few seconds (not a blink), once each time;
    /// what a terminal already is when first seen is not news, and a finish is only told after work (a task's own completion is told
    /// by the task notifications).</summary>
    internal static AgentNotice Observe(AgentWatch watch, AgentState current, DateTimeOffset now, bool taskOwned)
    {
        if (watch.State is null)
        {
            watch.State = watch.Reported = current;
            watch.Since = now;
            watch.WasWorking = current == AgentState.Working;
            return AgentNotice.None;
        }
        if (watch.State != current)
        {
            watch.State = current;
            watch.Since = now;
            watch.Reported = null;
            if (current == AgentState.Working) watch.WasWorking = true;
        }
        if (watch.Reported == current || now - watch.Since < AgentStateHold) return AgentNotice.None;
        watch.Reported = current;
        if (current == AgentState.Blocked) return AgentNotice.Blocked;
        if (current != AgentState.Done || !watch.WasWorking) return AgentNotice.None;
        watch.WasWorking = false;
        return taskOwned ? AgentNotice.FinishedQuietly : AgentNotice.Done;
    }

    private static readonly TimeSpan AgentStateHold = TimeSpan.FromSeconds(3);
    private Button? _agentBoardButton;
    private TextBlock? _agentBoardSummary;
    private Popup? _agentBoardPopup;
    private StackPanel? _agentBoardRows;
    private DispatcherTimer? _agentBoardTimer;
    private bool _leftoversStopped;
    private readonly Dictionary<TerminalPane, AgentWatch> _agentWatch = new();

    /// <summary>The summary the button shows, for tests and accessibility</summary>
    internal string AgentBoardSummary => _agentBoardSummary is null ? "" : new TextRange(_agentBoardSummary.ContentStart, _agentBoardSummary.ContentEnd).Text;

    /// <summary>Replaces the notification (tests)</summary>
    internal Action<string, string, bool>? AgentNotificationOverride { get; set; }

    private void InitializeAgentBoard()
    {
        if (_agentBoardButton is not null || NewTerminalButton.Parent is not StackPanel actions) return;
        _agentBoardSummary = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        _agentBoardButton = new Button
        {
            Content = _agentBoardSummary, Height = 30, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "에이전트 현황 · 터미널마다 막힘 · 작업 중 · 완료"
        };
        _agentBoardButton.SetResourceReference(StyleProperty, "QuietIconButton");
        AutomationProperties.SetName(_agentBoardButton, "에이전트 현황");
        AutomationProperties.SetAutomationId(_agentBoardButton, "AgentBoardButton");
        _agentBoardRows = new StackPanel();
        var content = new ScrollViewer { MaxHeight = 440, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _agentBoardRows };
        var frame = new Border
        {
            Width = 380, Margin = new Thickness(0, 8, 0, 0), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(8),
            Child = content
        };
        frame.SetResourceReference(Border.BackgroundProperty, "PanelGradient");
        frame.SetResourceReference(Border.BorderBrushProperty, "MetalEdge");
        _agentBoardPopup = new Popup
        {
            PlacementTarget = _agentBoardButton, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade, Child = frame
        };
        _agentBoardButton.Click += (_, _) =>
        {
            RenderAgentBoard();
            _agentBoardPopup.IsOpen = true;
        };
        actions.Children.Insert(0, _agentBoardButton);
        actions.Children.Insert(0, _agentBoardPopup);
        InitializeHerdrExtras(actions);
        _agentBoardTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => ObserveAgents(), Dispatcher);
        _agentBoardTimer.Start();
        Closed += (_, _) => _agentBoardTimer.Stop();
        Activated += (_, _) => BringBackFromBackgroundWork();

        ObserveAgents();
    }

    /// <summary>A terminal's agent state: a Claude Code or Codex question on the screen is blocked whatever its output looks like.</summary>
    internal static AgentState StateOf(TerminalPane pane)
    {
        if (!pane.IsTypedAgent) return AgentStateOf(pane.Activity, pane.HasCompletedWork);
        // Claude Code and Codex: from their screen (a question, "esc to interrupt"), not from how much they print.
        if (pane.IsAskingUser) return AgentState.Blocked;
        if (pane.Activity is TerminalActivity.Exited) return AgentState.Ended;
        if (pane.Activity is TerminalActivity.Starting || !pane.IsReady) return AgentState.Working;
        if (pane.IsTypedAgentWorking) return AgentState.Working;
        if (pane.Activity is TerminalActivity.RateLimited or TerminalActivity.Error) return AgentState.Blocked;
        return pane.TypedAgentWorked ? AgentState.Done : AgentState.Waiting;
    }

    /// <summary>What a terminal waits for, told on the board and in its notification.</summary>
    internal static string WaitingFor(TerminalPane pane) => pane.IsAskingUser ? "답이나 승인을 기다립니다 · 터미널에서 답하세요" : pane.ActivityLabel;

    internal static AgentState AgentStateOf(TerminalActivity activity, bool completedWork) => activity switch
    {
        TerminalActivity.NeedsInput or TerminalActivity.RateLimited or TerminalActivity.Error => AgentState.Blocked,
        TerminalActivity.Active or TerminalActivity.Starting => AgentState.Working,
        TerminalActivity.Exited => AgentState.Ended,
        TerminalActivity.Idle when completedWork => AgentState.Done,
        _ => AgentState.Waiting
    };

    private static string AgentStateLabel(AgentState state) => state switch
    {
        AgentState.Blocked => "막힘", AgentState.Working => "작업 중", AgentState.Done => "완료", AgentState.Waiting => "대기", _ => "종료"
    };

    private Brush AgentStateBrush(AgentState state) => (Brush)FindResource(state switch
    {
        AgentState.Blocked => "Accent", AgentState.Working => "TextPrimary", AgentState.Done => "TextSecondary", _ => "Muted"
    });

    private void ObserveAgents()
    {
        if (_closing || _closePending || _agentBoardSummary is null) return;
        CheckBackgroundWork();
        // Servers an earlier version left running after it closed are stopped once, at start.
        if (!_leftoversStopped)
        {
            _leftoversStopped = true;
            StopLeftoverServers();
            // Once per boot: why the PC went down last time, when it was not the user's own shutdown.
            ReportPreviousShutdown();
        }
        var tabs = TerminalTabs.Items.Cast<TabItem>().ToArray();
        var panes = tabs.Select(tab => ((TerminalTabState)tab.Tag).Pane).ToHashSet();
        foreach (var gone in _agentWatch.Keys.Where(pane => !panes.Contains(pane)).ToArray()) _agentWatch.Remove(gone);
        var now = DateTimeOffset.UtcNow;
        var counts = new Dictionary<AgentState, int>();
        foreach (var tab in tabs)
        {
            var state = (TerminalTabState)tab.Tag;
            AgentState current = StateOf(state.Pane);
            counts[current] = counts.GetValueOrDefault(current) + 1;
            if (!_agentWatch.TryGetValue(state.Pane, out var watch)) _agentWatch[state.Pane] = watch = new AgentWatch();
            bool taskOwned = _taskNotificationBindings.Values.Any(binding => ReferenceEquals(binding.Pane, state.Pane));
            switch (Observe(watch, current, now, taskOwned))
            {
                case AgentNotice.Blocked: NotifyAgent(tab, "확인 필요", WaitingFor(state.Pane)); break;
                case AgentNotice.Done:
                    NotifyAgent(tab, "작업 완료", "작업을 마쳤습니다.");
                    LearnFrom(state.Pane);
                    break;
                case AgentNotice.FinishedQuietly: LearnFrom(state.Pane); break;
            }
        }
        // Agents in outside terminals count too (working or waiting: their state is read from CPU use).
        ScanExternalAgents();
        foreach (var agent in _externalAgents)
        {
            var state = agent.Working ? AgentState.Working : AgentState.Waiting;
            counts[state] = counts.GetValueOrDefault(state) + 1;
        }
        _agentBoardSummary.Inlines.Clear();
        foreach (var state in new[] { AgentState.Blocked, AgentState.Working, AgentState.Done })
        {
            if (counts.GetValueOrDefault(state) == 0) continue;
            if (_agentBoardSummary.Inlines.Count > 0) _agentBoardSummary.Inlines.Add(new Run("  ·  ") { Foreground = AgentStateBrush(AgentState.Waiting) });
            _agentBoardSummary.Inlines.Add(new Run($"{AgentStateLabel(state)} {counts[state]}")
                { Foreground = AgentStateBrush(state), FontWeight = state == AgentState.Blocked ? FontWeights.SemiBold : FontWeights.Normal });
        }
        if (_agentBoardSummary.Inlines.Count == 0) _agentBoardSummary.Inlines.Add(new Run(tabs.Length == 0 ? "에이전트 없음" : "에이전트 대기") { Foreground = AgentStateBrush(AgentState.Waiting) });
        _agentBoardButton!.Visibility = tabs.Length == 0 && _externalAgents.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetHelpText(_agentBoardButton, AgentBoardSummary);
        if (_agentBoardPopup?.IsOpen == true) RenderAgentBoard();
    }

    private void NotifyAgent(TabItem tab, string title, string detail)
    {
        var state = (TerminalTabState)tab.Tag;
        // Not about the terminal the user is looking at right now.
        if (IsActive && WindowState != WindowState.Minimized && ReferenceEquals(TerminalTabs.SelectedItem, tab)) return;
        if (!_taskNotificationPreferences.CanNotify(DateTimeOffset.UtcNow)) return;
        string body = $"{state.Sequence:00} {state.Title} · {ProjectDisplayName(state.Pane.WorkingDirectory)}\n{detail}";
        if (AgentNotificationOverride is { } sink) { sink(title, body, title == "확인 필요"); return; }
        AgentState expected = title == "확인 필요" ? AgentState.Blocked : AgentState.Done;
        _tray?.ShowTaskNotification(title, body, title == "확인 필요",
            () => !_closing && TerminalTabs.Items.Contains(tab) && StateOf(state.Pane) == expected);
    }

    // The board: every terminal, blocked first, then working, done, waiting and ended; a click goes to it.
    private void RenderAgentBoard()
    {
        if (_agentBoardRows is null) return;
        _agentBoardRows.Children.Clear();
        var rows = TerminalTabs.Items.Cast<TabItem>()
            .Select(tab => (Tab: tab, State: (TerminalTabState)tab.Tag))
            .Select(row => (row.Tab, row.State, Agent: StateOf(row.State.Pane)))
            .OrderBy(row => row.Agent).ThenBy(row => row.State.Sequence).ToList();
        foreach (var (tab, state, agent) in rows)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = AgentStateBrush(agent), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0) };
            var name = new TextBlock { Text = $"{state.Sequence:00}  {state.Title}", FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            var where = new TextBlock { Text = ProjectDisplayName(state.Pane.WorkingDirectory), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
            where.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var text = new StackPanel();
            text.Children.Add(name);
            text.Children.Add(where);
            var label = new TextBlock { Text = AgentStateLabel(agent), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
                Foreground = AgentStateBrush(agent), FontWeight = agent == AgentState.Blocked ? FontWeights.SemiBold : FontWeights.Normal };
            var line = new DockPanel();
            DockPanel.SetDock(dot, Dock.Left);
            DockPanel.SetDock(label, Dock.Right);
            line.Children.Add(dot);
            line.Children.Add(label);
            line.Children.Add(text);
            var row = new Button { Content = line, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 2), ToolTip = WaitingFor(state.Pane) };
            row.SetResourceReference(StyleProperty, "QuietIconButton");
            AutomationProperties.SetName(row, $"{state.Sequence:00} {state.Title} · {AgentStateLabel(agent)}");
            row.Click += (_, _) =>
            {
                if (_agentBoardPopup is not null) _agentBoardPopup.IsOpen = false;
                if (TerminalTabs.Items.Contains(tab)) SelectTerminal(tab);
            };
            _agentBoardRows.Children.Add(row);
        }
        if (_externalAgents.Count > 0)
        {
            var heading = new TextBlock { Text = "다른 터미널", FontSize = 12, Margin = new Thickness(8, rows.Count > 0 ? 10 : 2, 8, 4) };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            _agentBoardRows.Children.Add(heading);
        }
        foreach (var external in _externalAgents.OrderByDescending(agent => agent.Working))
        {
            var agent = external.Working ? AgentState.Working : AgentState.Waiting;
            var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = AgentStateBrush(agent), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0) };
            var name = new TextBlock { Text = external.Name, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            var where = new TextBlock { Text = external.Host + " 창", FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
            where.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var text = new StackPanel();
            text.Children.Add(name);
            text.Children.Add(where);
            var label = new TextBlock { Text = AgentStateLabel(agent), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Foreground = AgentStateBrush(agent) };
            var line = new DockPanel();
            DockPanel.SetDock(dot, Dock.Left);
            DockPanel.SetDock(label, Dock.Right);
            line.Children.Add(dot);
            line.Children.Add(label);
            line.Children.Add(text);
            var row = new Button { Content = line, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 2),
                ToolTip = "그 창으로 가기 · 다른 터미널의 상태는 CPU 사용으로 봅니다" };
            row.SetResourceReference(StyleProperty, "QuietIconButton");
            AutomationProperties.SetName(row, $"{external.Name} · {external.Host} · {AgentStateLabel(agent)}");
            IntPtr window = external.Window;
            row.Click += (_, _) =>
            {
                if (_agentBoardPopup is not null) _agentBoardPopup.IsOpen = false;
                ExternalAgents.Focus(window);
            };
            _agentBoardRows.Children.Add(row);
        }
        if (rows.Count == 0 && _externalAgents.Count == 0)
        {
            var empty = new TextBlock { Text = "열린 터미널이 없습니다.", Margin = new Thickness(8), FontSize = 13 };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            _agentBoardRows.Children.Add(empty);
        }
    }
}
