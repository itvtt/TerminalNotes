using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using TerminalNotes.Services;

namespace TerminalNotes;

// 사용자 지시: 태스크마다 쓸 에이전트를 고른다(기본 OpenCode, Codex·Claude Code 선택). 고르면 태스크 행에 그 대표 아이콘이
// 보이고, 언제든 바꿀 수 있다: 아이콘을 누르면 에이전트 아이콘들이 떠서 고르고, 우클릭 메뉴에서도 고른다. .env의
// AGENT_SELECTION이 꺼져 있으면(사내 기본) 아이콘과 메뉴를 숨기고 모든 태스크가 OpenCode로 실행된다.
// A choice applies to the task's next terminal; a running one keeps its agent. Loop and PlanMaker need OpenCode.
public partial class MainWindow
{
    private static readonly TerminalTool[] SelectableTaskAgents = [TerminalTool.OpenCode, TerminalTool.Codex, TerminalTool.ClaudeCode];
    private readonly Dictionary<string, TerminalTool> _taskAgents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Button> _taskAgentButtons = new(StringComparer.OrdinalIgnoreCase);

    private TerminalTool AgentForTask(string project) => AppFeatures.AgentSelection
        ? _taskAgents.GetValueOrDefault(CanonicalProjectPath(project), TerminalTool.OpenCode) : TerminalTool.OpenCode;

    private void LoadTaskAgents(Dictionary<string, string>? saved)
    {
        _taskAgents.Clear();
        foreach (var (project, value) in saved ?? [])
            if (AgentCli.Parse(value) is var agent && agent != TerminalTool.OpenCode) _taskAgents[CanonicalProjectPath(project)] = agent;
    }

    private Dictionary<string, string> TaskAgentsForSettings() =>
        _taskAgents.Where(pair => pair.Value != TerminalTool.OpenCode).ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);

    private void MoveTaskAgent(string source, string destination)
    {
        if (_taskAgents.Remove(CanonicalProjectPath(source), out var agent)) _taskAgents[CanonicalProjectPath(destination)] = agent;
    }

    private void ForgetTaskAgent(string project)
    {
        _taskAgents.Remove(CanonicalProjectPath(project));
        _taskAgentButtons.Remove(CanonicalProjectPath(project));
    }

    // The agent's mark on the task row; clicking it shows the agents to choose from.
    private Button CreateTaskAgentButton(string project)
    {
        var button = new Button { Width = 22, Height = 22, Padding = new Thickness(2), Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand, Focusable = false,
            Visibility = AppFeatures.AgentSelection ? Visibility.Visible : Visibility.Collapsed };
        button.SetResourceReference(StyleProperty, "TaskActionButton");
        ToolTipService.SetShowOnDisabled(button, true);
        button.Click += (_, e) => { e.Handled = true; ShowTaskAgentPicker(project, button); };
        _taskAgentButtons[CanonicalProjectPath(project)] = button;
        UpdateTaskAgentButton(project);
        return button;
    }

    private void UpdateTaskAgentButton(string project)
    {
        if (!_taskAgentButtons.TryGetValue(CanonicalProjectPath(project), out var button)) return;
        var agent = AgentForTask(project);
        button.Content = AgentGlyph(agent, 16, StateColorBrush("#F2F2F2"));
        string name = AgentCli.DisplayName(agent);
        button.ToolTip = $"에이전트 · {name}\n눌러서 바꾸기 (다음 실행부터 적용)";
        System.Windows.Automation.AutomationProperties.SetName(button, "에이전트 " + name + " · 바꾸기");
    }

    private void ShowTaskAgentPicker(string project, Button anchor)
    {
        var current = AgentForTask(project);
        var choices = new StackPanel { Orientation = Orientation.Horizontal };
        var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade };
        foreach (var agent in SelectableTaskAgents)
        {
            bool selected = agent == current;
            var label = new TextBlock { Text = AgentCli.DisplayName(agent), FontSize = 12, Margin = new Thickness(0, 5, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, selected ? "Accent" : "TextPrimary");
            var stack = new StackPanel { Width = 84 };
            stack.Children.Add(AgentGlyph(agent, 26, selected ? StateColorBrush("#E5C07B") : StateColorBrush("#F2F2F2")));
            stack.Children.Add(label);
            var choice = new Button { Content = stack, Padding = new Thickness(6, 8, 6, 6), Margin = new Thickness(2), Tag = agent,
                Background = Brushes.Transparent, BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = selected ? "지금 쓰는 에이전트" : AgentCli.DisplayName(agent) + "로 바꾸기" + (agent == TerminalTool.OpenCode ? "" : " · Loop·플랜메이커는 OpenCode에서만 동작합니다") };
            choice.SetResourceReference(Control.BorderBrushProperty, selected ? "Accent" : "Line");
            System.Windows.Automation.AutomationProperties.SetName(choice, AgentCli.DisplayName(agent));
            System.Windows.Automation.AutomationProperties.SetAutomationId(choice, "TaskAgentChoice" + agent);
            choice.Click += (_, e) =>
            {
                e.Handled = true;
                popup.IsOpen = false;
                SetTaskAgent(project, agent);
            };
            choices.Children.Add(choice);
        }
        var frame = new Border { Child = choices, Padding = new Thickness(6), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BackgroundProperty, "SurfaceElevated");
        frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        popup.Child = frame;
        popup.IsOpen = true;
    }

    // The task's right-click "에이전트" submenu.
    private MenuItem CreateTaskAgentMenu(string project)
    {
        var menu = new MenuItem { Header = "에이전트", Visibility = AppFeatures.AgentSelection ? Visibility.Visible : Visibility.Collapsed,
            ToolTip = "이 태스크를 실행할 에이전트 · 다음 실행부터 적용" };
        foreach (var agent in SelectableTaskAgents)
        {
            var item = new MenuItem { Header = AgentCli.DisplayName(agent), IsCheckable = true, Tag = agent,
                Icon = AgentGlyph(agent, 14, StateColorBrush("#F2F2F2")) };
            item.Click += (_, e) => { e.Handled = true; SetTaskAgent(project, agent); };
            menu.Items.Add(item);
        }
        menu.SubmenuOpened += (_, _) =>
        {
            var current = AgentForTask(project);
            foreach (var item in menu.Items.OfType<MenuItem>()) item.IsChecked = item.Tag is TerminalTool agent && agent == current;
        };
        return menu;
    }

    internal void SetTaskAgent(string project, TerminalTool agent)
    {
        project = CanonicalProjectPath(project);
        if (!AppFeatures.AgentSelection || AgentForTask(project) == agent) return;
        if (agent == TerminalTool.OpenCode) _taskAgents.Remove(project);
        else _taskAgents[project] = agent;
        SaveSettings();
        UpdateTaskAgentButton(project);
        UpdateProjectTaskActivity();
        string name = AgentCli.DisplayName(agent), task = ProjectDisplayName(project);
        bool running = RuntimeFor(project).SingleTerminal is { Tag: TerminalTabState { Pane.IsRunning: true } };
        if (agent != TerminalTool.OpenCode && !AgentCli.IsInstalled(agent))
            SetAttentionStatus($"{task} · {name}을(를) 골랐지만 이 PC에서 {AgentCli.Executable(agent)} 명령을 찾지 못했습니다. 설치한 뒤 실행하세요.");
        else
            SetStatus($"{task} · " + (running ? $"다음 실행부터 {name}로 실행합니다. 지금 터미널은 그대로 둡니다." : $"{name}로 실행합니다.") +
                (agent != TerminalTool.OpenCode && _projectTaskContinuousModes.GetValueOrDefault(project) ? " Loop는 OpenCode에서만 동작해 쉬게 됩니다." : ""));
    }

    // Monochrome marks (색은 하나: 아이보리, 고른 것은 금색): OpenCode a square ring, Codex a hexagon knot, Claude Code a starburst.
    internal static FrameworkElement AgentGlyph(TerminalTool agent, double size, Brush ink)
    {
        Geometry geometry = Geometry.Parse(agent switch
        {
            TerminalTool.Codex => "M8,1.2 L13.9,4.6 L13.9,11.4 L8,14.8 L2.1,11.4 L2.1,4.6 Z M8,4.6 L10.9,6.3 L10.9,9.7 L8,11.4 L5.1,9.7 L5.1,6.3 Z",
            TerminalTool.ClaudeCode => "M8,1 L8,15 M1,8 L15,8 M3.05,3.05 L12.95,12.95 M12.95,3.05 L3.05,12.95",
            _ => "M2,2 L14,2 L14,14 L2,14 Z M5.5,5.5 L5.5,10.5 L10.5,10.5 L10.5,5.5 Z"
        });
        var path = new Path { Data = geometry, Stretch = Stretch.None, SnapsToDevicePixels = true };
        // The path language fills even-odd, so OpenCode's inner square is a hole.
        if (agent == TerminalTool.OpenCode) path.Fill = ink;
        else
        {
            path.Stroke = ink;
            path.StrokeThickness = agent == TerminalTool.ClaudeCode ? 2.1 : 1.5;
            path.StrokeStartLineCap = path.StrokeEndLineCap = PenLineCap.Round;
            path.StrokeLineJoin = PenLineJoin.Round;
        }
        return new Viewbox { Width = size, Height = size, Child = new Canvas { Width = 16, Height = 16, Children = { path } } };
    }
}
