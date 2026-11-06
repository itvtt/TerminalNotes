using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TerminalNotes;

// PlanMaker view: running PlanMakers are docked side by side in the right-hand area (고급·플랜 모드)
// instead of floating windows, so several interviews can run and be watched at once.
public partial class MainWindow
{
    private Grid? _planMakerDeck;
    private UniformGrid? _planMakerCells;
    private TextBlock? _planMakerDeckEmpty;
    private RadioButton? _terminalViewTab, _planMakerViewTab;
    private StackPanel? _rightAreaViewTabs;
    private readonly List<(PlanMakerDialog Dialog, Border Cell)> _dockedPlanMakers = [];

    // Only a shown main window can host it (the docked content starts on Loaded); otherwise keep the separate window.
    private bool CanDockPlanMakers => IsLoaded && IsVisible && _taskWorkspaceVisible && _planMakerDeck is not null;
    private bool PlanMakerViewShown => _planMakerDeck?.Visibility == Visibility.Visible;

    private void InitializePlanMakerDeck()
    {
        if (_taskTerminalArea is null || _planMakerDeck is not null) return;
        _planMakerCells = new UniformGrid { Columns = 1 };
        _planMakerDeckEmpty = new TextBlock
        {
            Text = "실행 중인 플랜메이커가 없습니다.\n플랜 영역의 ‘플랜메이커’로 시작하면 여기에 나란히 표시됩니다.",
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 21,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        _planMakerDeckEmpty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _planMakerDeck = new Grid { Visibility = Visibility.Collapsed };
        _planMakerDeck.Children.Add(_planMakerDeckEmpty);
        _planMakerDeck.Children.Add(_planMakerCells);
        Grid.SetRow(_planMakerDeck, 1);
        Grid.SetColumnSpan(_planMakerDeck, 2);
        _taskTerminalArea.Children.Add(_planMakerDeck);

        // The 터미널 | 플랜메이커 switch appears only while at least one PlanMaker is docked.
        var tabs = _rightAreaViewTabs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Visibility = Visibility.Collapsed };
        RadioButton Tab(string text, bool planMakers)
        {
            var tab = new RadioButton { Content = text, GroupName = "RightAreaView", Height = 30, Padding = new Thickness(10, 0, 10, 0),
                IsChecked = !planMakers, MinWidth = 56 };
            tab.SetResourceReference(StyleProperty, "ProjectArchiveTab");
            tab.Checked += (_, _) => ShowPlanMakerView(planMakers);
            System.Windows.Automation.AutomationProperties.SetName(tab, planMakers ? "플랜메이커 보기" : "터미널 보기");
            tabs.Children.Add(tab);
            return tab;
        }
        _terminalViewTab = Tab("터미널", false);
        _planMakerViewTab = Tab("플랜메이커", true);
        _planMakerViewTab.ToolTip = "실행 중인 플랜메이커를 나란히 봅니다 · 여러 태스크의 플랜메이커를 동시에 진행할 수 있습니다";
        if (NewTerminalButton.Parent is StackPanel headerActions) headerActions.Children.Insert(0, tabs);
        UpdatePlanMakerDeck();
    }

    private void ShowPlanMakerView(bool show)
    {
        if (_planMakerDeck is null || _taskDeck is null) return;
        _planMakerDeck.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // Hidden (not collapsed) keeps the terminals' size, so their TUIs are not resized while away.
        _taskDeck.Visibility = show ? Visibility.Hidden : Visibility.Visible;
        if (SplitTerminals.Parent == _taskTerminalArea && SplitTerminals.Visibility != Visibility.Collapsed)
            SplitTerminals.Visibility = show ? Visibility.Hidden : Visibility.Visible;
        var wanted = show ? _planMakerViewTab : _terminalViewTab;
        if (wanted is not null && wanted.IsChecked != true) wanted.IsChecked = true;
    }

    private void DockPlanMaker(PlanMakerDialog dialog)
    {
        if (_planMakerCells is null) return;
        FrameworkElement root = dialog.DockIntoHost();
        var header = new Grid { Margin = new Thickness(12, 8, 6, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { Text = "플랜메이커 · " + ProjectDisplayName(dialog.ProjectPath), FontSize = 14, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = dialog.ProjectPath };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        header.Children.Add(title);
        var close = new Button { Content = "×", Width = 28, Height = 26, Padding = new Thickness(0), FontSize = 15,
            ToolTip = "이 플랜메이커 닫기 · 반영된 초안은 유지" };
        close.SetResourceReference(StyleProperty, "QuietIconButton");
        close.Click += (_, _) => dialog.RequestClose();
        System.Windows.Automation.AutomationProperties.SetName(close, "플랜메이커 닫기 · " + ProjectDisplayName(dialog.ProjectPath));
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        var body = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);
        body.Children.Add(root);
        var cell = new Border { Child = body, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(3), ClipToBounds = true };
        cell.SetResourceReference(Border.BorderBrushProperty, "PanelEdge");
        cell.SetResourceReference(Border.BackgroundProperty, "Canvas");
        _dockedPlanMakers.Add((dialog, cell));
        _planMakerCells.Children.Add(cell);
        // Leaving (플랜으로 돌아가기 or ×): off the screen at once; the cell stays in the tree, collapsed, until its terminal has
        // closed, so the PlanMaker's OpenCode can still run the plan cleanup.
        void Leave()
        {
            cell.Visibility = Visibility.Collapsed;
            _dockedPlanMakers.RemoveAll(item => ReferenceEquals(item.Dialog, dialog));
            UpdatePlanMakerDeck();
            if (_dockedPlanMakers.Count == 0) ShowPlanMakerView(false);
        }
        dialog.Leaving += Leave;
        dialog.Closed += (_, _) =>
        {
            dialog.Leaving -= Leave;
            _dockedPlanMakers.RemoveAll(item => ReferenceEquals(item.Dialog, dialog));
            _planMakerCells.Children.Remove(cell);
            body.Children.Remove(root);
            UpdatePlanMakerDeck();
            if (_dockedPlanMakers.Count == 0) ShowPlanMakerView(false);
        };
        UpdatePlanMakerDeck();
        ShowPlanMakerView(true);
    }

    private void UpdatePlanMakerDeck()
    {
        if (_planMakerCells is null) return;
        int count = _dockedPlanMakers.Count;
        _planMakerCells.Columns = count <= 1 ? 1 : count <= 4 ? 2 : 3;
        if (_planMakerDeckEmpty is not null) _planMakerDeckEmpty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_planMakerViewTab is not null) _planMakerViewTab.Content = count == 0 ? "플랜메이커" : $"플랜메이커 {count}";
        if (_rightAreaViewTabs is not null) _rightAreaViewTabs.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
