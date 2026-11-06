using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TerminalNotes;
using TerminalNotes.Services;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes composer " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        object? previousDataDirectory = AppContext.GetData("TerminalNotes.DataDirectory");
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(fixture, "data"));
        string settingsPath = Path.Combine(fixture, "settings.json");
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new { Workspace = fixture, Document = (string?)null, AutoResumeEnabled = false, WorkMode = "Advanced" }));
        MainWindow? window = null;
        var panes = new List<TerminalPane>();
        var nativePanes = new List<TerminalPane>();
        try
        {
            // Never show or load the window: layout must not start OpenCode or any shell.
            window = new MainWindow(settingsPath, autoStartCodex: false, initialWorkspace: fixture);
            VerifyAutomaticProgressDefault(window, fixture, settingsPath, nativePanes);
            var workspace = (Grid)window.FindName("WorkspaceGrid");
            var notes = (Border)window.FindName("NotesPanel");
            var deck = Field<Grid>(window, "_taskDeck");
            var terminalArea = Field<Grid>(window, "_taskTerminalArea");
            var composer = Field<Border>(window, "_planComposerHost");
            var leftArea = Field<Grid>(window, "_leftPlanArea");
            var leftScroll = Field<ScrollViewer>(window, "_leftPlanScroll");
            var leftSidebar = Field<Grid>(window, "_taskSidebarSurface");
            var planSurface = Field<Grid>(window, "_tabDocumentSurface");
            var originalPlanHost = Field<Grid>(window, "_planDocumentOriginalHost");
            var taskList = Field<ScrollViewer>(window, "_taskListScroll");
            var editor = Field<TextBox>(window, "_projectInputEditor");
            var planEditor = Field<TextBox>(window, "_tabDocumentEditor");
            var planToggle = Field<Button>(window, "_planVisibilityButton");
            var planMaker = Field<Button>(window, "_planMakerButton");
            Require(planSurface.RowDefinitions.Count == 3 && !Descendants(planSurface).OfType<CheckBox>().Any(),
                "The removed automatic-progress checkbox left a control or empty row behind.");
            var sidebar = Field<ColumnDefinition>(window, "_taskSidebarColumn");
            Call(window, "LoadTaskSidebarSettings", false, 240d);
            Require(sidebar.Width.Value == 360, "A compact saved sidebar was not expanded for the plan.");
            var slots = new UniformGrid { Columns = 4, Rows = 2 };
            deck.Children.Add(slots);
            for (int index = 0; index < 8; index++)
            {
                var pane = new TerminalPane(fixture, useProjectContext: false);
                panes.Add(pane);
                slots.Children.Add(pane);
            }
            SetField(window, "_selectedProjectPath", fixture);
            Call(window, "UpdateProjectInputComposer");
            foreach (var input in new[] { editor, planEditor })
            {
                string original = input.Text;
                input.Clear();
                input.ApplyTemplate();
                var placeholder = (TextBlock)input.Template.FindName("InputPlaceholder", input);
                Require(placeholder is { Visibility: Visibility.Visible, IsHitTestVisible: false } && input.Text.Length == 0,
                    "An empty plan field must show guidance without turning it into draft text or intercepting input.");
                input.Text = "입력 ABC 123\n다음 줄";
                Require(placeholder.Visibility == Visibility.Collapsed && input.Text == "입력 ABC 123\n다음 줄",
                    "Guidance must disappear when multiline Korean/Latin text is entered without changing the draft.");
                input.Text = original;
            }
            editor.Text = "플랜메이커에 전달할 요구사항 초안";
            Layout(workspace, 1000, 600);
            Require(sidebar.ActualWidth >= 360 && planEditor.ActualHeight > 50 && editor.ActualHeight >= 48,
                "The wider left sidebar or editable plan areas were not applied.");
            // Same dark tone on purpose (user request); each section must carry its own visible frame instead.
            var sectionFrames = leftSidebar.Children.OfType<Border>().Where(frame => frame.BorderThickness.Left >= 1 && !frame.IsHitTestVisible).ToList();
            Require(sectionFrames.Any(frame => Grid.GetRow(frame) == Grid.GetRow(leftScroll)) &&
                sectionFrames.Any(frame => Grid.GetRow(frame) <= Grid.GetRow(taskList) && Grid.GetRow(frame) + Grid.GetRowSpan(frame) > Grid.GetRow(taskList)),
                "Task and plan sections are not framed separately.");
            Require(notes.Visibility == Visibility.Collapsed && Field<ColumnDefinition>(window, "_taskPlanColumn").ActualWidth == 0,
                "The default plan document still consumes terminal width.");
            Require(ReferenceEquals(composer.Parent, leftArea) && ReferenceEquals(planSurface.Parent, leftArea) &&
                ReferenceEquals(leftScroll.Parent, leftSidebar) && Grid.GetRow(leftScroll) > Grid.GetRow(taskList) &&
                composer.TranslatePoint(new Point(), workspace).X + composer.ActualWidth <= terminalArea.TranslatePoint(new Point(), workspace).X &&
                leftScroll.TranslatePoint(new Point(), leftSidebar).Y >= taskList.TranslatePoint(new Point(0, taskList.ActualHeight), leftSidebar).Y,
                "The composer and plan editor are not inside the LEFT sidebar below the project list.");
            Require(Math.Abs(deck.ActualWidth - terminalArea.ActualWidth) < 1 && terminalArea.RowDefinitions.Count == 2,
                "The terminal work area still reserves a plan column or footer.");
            RequireSidebarActions(window, leftSidebar);
            RequirePlanMakerHeader(planMaker, planSurface, planEditor, composer);
            Require(editor.MinLines == 3 && editor.MaxLines == 3 && double.IsNaN(editor.Height) &&
                editor.MinHeight == 0 && double.IsPositiveInfinity(editor.MaxHeight) &&
                leftArea.RowDefinitions[0].Height.IsStar && leftArea.RowDefinitions[1].Height.IsAuto,
                "The additional request must show three lines and give the remaining height to the plan document.");
            double compactInputHeight = editor.ActualHeight, compactDocumentHeight = planEditor.ActualHeight;
            double compactPlanAreaHeight = leftScroll.ActualHeight;
            double compactWidth = deck.ActualWidth;
            double compactHeight = deck.ActualHeight;
            GridLength sidebarWidth = sidebar.Width;
            Require(planSurface.Visibility == Visibility.Visible && planToggle.Visibility == Visibility.Collapsed &&
                Grid.GetRow(planSurface) < Grid.GetRow(composer) &&
                planSurface.TranslatePoint(new Point(0, planSurface.ActualHeight), leftArea).Y <= composer.TranslatePoint(new Point(), leftArea).Y + 1,
                "The task plan must always appear immediately above the composer with no document-collapse action.");
            planEditor.Text = "미저장 플랜 초안 보존";
            Layout(workspace, 1000, 800);
            Require(Math.Abs(editor.ActualHeight - compactInputHeight) < 1 &&
                Math.Abs((planEditor.ActualHeight - compactDocumentHeight) - (leftScroll.ActualHeight - compactPlanAreaHeight)) < 1,
                "Window resizing must allocate all additional plan area height to the document, keeping request input at three lines.");
            var splitter = Field<GridSplitter>(window, "_leftPlanSplitter");
            var execute = Field<Button>(window, "_tabDocumentExecute");
            double footerBottom = execute.TranslatePoint(new Point(0, execute.ActualHeight), workspace).Y;
            double inputHeight = editor.ActualHeight, documentHeight = planEditor.ActualHeight;
            double planAreaHeight = leftScroll.ActualHeight;
            Drag(splitter, -60);
            Layout(workspace, 1000, 800);
            Require(Math.Abs(editor.ActualHeight - inputHeight) < 1 && planEditor.ActualHeight > documentHeight + 10 &&
                Math.Abs((planEditor.ActualHeight - documentHeight) - (leftScroll.ActualHeight - planAreaHeight)) < 1,
                $"Dragging the divider must grow only the plan document by the available height: input {inputHeight}->{editor.ActualHeight}, document {documentHeight}->{planEditor.ActualHeight}, plan area {planAreaHeight}->{leftScroll.ActualHeight}.");
            Require(Math.Abs(execute.TranslatePoint(new Point(0, execute.ActualHeight), workspace).Y - footerBottom) < 1,
                "Dragging the divider moved the plan action footer away from the bottom.");
            RequirePlanMakerHeader(planMaker, planSurface, planEditor, composer);
            Drag(splitter, 60);
            SetField(window, "_notesHidden", true);
            Call(window, "SetTabDocumentModeVisible", false);
            Call(window, "ApplyLeftPlanVisibility");
            planToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Call(window, "SetPlanDocumentVisible", false);
            Layout(workspace, 1000, 600);
            Require(planSurface.Visibility == Visibility.Visible && notes.Visibility == Visibility.Collapsed &&
                Math.Abs(deck.ActualWidth - compactWidth) < 1 && Math.Abs(deck.ActualHeight - compactHeight) < 1 && sidebar.Width == sidebarWidth &&
                planEditor.TranslatePoint(new Point(), workspace).X + planEditor.ActualWidth <= terminalArea.TranslatePoint(new Point(), workspace).X,
                "A legacy hidden preference or document refresh hid the permanent left plan or changed terminal area.");
            Capture(workspace, fixture + "-wide.png");
            planToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(workspace, 1000, 600);
            Require(planSurface.Visibility == Visibility.Visible && Math.Abs(deck.ActualWidth - compactWidth) < 1 &&
                panes.SequenceEqual(slots.Children.OfType<TerminalPane>()) && panes.All(pane => !pane.IsRunning) &&
                ReferenceEquals(planEditor, Field<TextBox>(window, "_tabDocumentEditor")) &&
                editor.Text == "플랜메이커에 전달할 요구사항 초안" && planEditor.Text == "미저장 플랜 초안 보존",
                "A legacy plan toggle changed visibility, terminal identity, or document/composer drafts.");
            Call(window, "ToggleNotes");
            Require(leftScroll.Visibility == Visibility.Collapsed, "Sidebar collapse left its plan controls visible.");
            planToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(Field<bool>(window, "_taskSidebarCollapsed") && leftScroll.Visibility == Visibility.Collapsed,
                "The plan toggle changed the collapsed sidebar.");
            Call(window, "ToggleNotes");
            Require(leftScroll.Visibility == Visibility.Visible && planSurface.Visibility == Visibility.Visible, "Sidebar restoration changed plan visibility.");
            planToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SetField(window, "_selectedProjectPath", Path.Combine(fixture, "other"));
            Call(window, "UpdateProjectInputComposer");
            Require(editor.Text == "", "Project switching reused another project's composer draft.");
            SetField(window, "_selectedProjectPath", fixture);
            Call(window, "UpdateProjectInputComposer");
            Require(editor.Text == "플랜메이커에 전달할 요구사항 초안" && planSurface.Visibility == Visibility.Visible,
                "Project switching lost the composer draft or hid its plan.");
            Layout(workspace, 640, 440);
            sidebar.Width = new GridLength(200);
            Layout(workspace, 640, 440);
            Call(window, "CaptureTaskSidebarWidth");
            Require(Field<double>(window, "_taskSidebarWidth") == 200,
                "The enlarged default prevented manual compact sidebar resizing.");
            RequirePlanMakerHeader(planMaker, planSurface, planEditor, composer);
            RequireSidebarActions(window, leftSidebar);
            foreach (Button button in new[] { Field<Button>(window, "_tabDocumentSave"), Field<Button>(window, "_tabDocumentExecute") })
            {
                Point position = button.TranslatePoint(new Point(), composer);
                Require(button.ActualWidth > 0 && position.X >= 0 && position.Y >= 0 &&
                    position.X + button.ActualWidth <= composer.ActualWidth + 1 && position.Y + button.ActualHeight <= composer.ActualHeight + 1,
                    $"A compact composer action is clipped: {button.Content}, position={position}, action={button.ActualWidth}x{button.ActualHeight}, composer={composer.ActualWidth}x{composer.ActualHeight}, viewport={leftScroll.ActualHeight}, document={planSurface.ActualHeight}, sidebar={leftSidebar.ActualHeight}.");
            }
            Capture(workspace, fixture + "-narrow.png");
            planToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(workspace, 640, 440);
            Require(taskList.ActualHeight >= 70 && leftScroll.ScrollableHeight == 0 && editor.ActualHeight >= 48 &&
                Math.Abs(editor.ActualHeight - compactInputHeight) < 1 &&
                leftScroll.ComputedHorizontalScrollBarVisibility == Visibility.Collapsed,
                $"The narrow left plan hides the project list, text input, or pinned footer: list={taskList.ActualHeight}, input={editor.ActualHeight}, scroll={leftScroll.ScrollableHeight}.");
            Layout(workspace, 640, 440);
            Require(composer.TranslatePoint(new Point(0, composer.ActualHeight), leftScroll).Y <= leftScroll.ActualHeight + 1 &&
                execute.TranslatePoint(new Point(0, execute.ActualHeight), workspace).Y <= workspace.ActualHeight &&
                Grid.GetRow(planSurface) < Grid.GetRow(composer),
                "The permanent plan/composer order changed or bottom composer is outside the viewport.");
            double narrowFooterBottom = execute.TranslatePoint(new Point(0, execute.ActualHeight), workspace).Y;
            string inputDraft = editor.Text, planDraft = planEditor.Text;
            Color workspaceInputColor = ((SolidColorBrush)editor.Background).Color;
            editor.Text = planEditor.Text = string.Join("\n", Enumerable.Range(0, 80).Select(index => $"긴 편집 내용 {index}"));
            Field<TextBlock>(window, "_projectInputHint").Text = "저장하지 못했습니다. 플랜 파일을 확인한 뒤 다시 저장하세요.";
            Layout(workspace, 640, 440);
            editor.ScrollToEnd(); planEditor.ScrollToEnd();
            Layout(workspace, 640, 440);
            TextBlock feedback = Field<TextBlock>(window, "_projectInputHint");
            Require(feedback.TextTrimming == TextTrimming.CharacterEllipsis &&
                feedback.ToolTip is TextBlock feedbackTooltip && feedbackTooltip.Text.Contains(feedback.Text) &&
                feedbackTooltip.Text.Contains("요청을 입력한 뒤"),
                "Narrow feedback must expose the complete message and guidance in its tooltip.");
            Require(editor.VerticalOffset > 0 && planEditor.VerticalOffset > 0 && editor.LineCount >= 80 &&
                Math.Abs(editor.ActualHeight - compactInputHeight) < 1 &&
                Math.Abs(execute.TranslatePoint(new Point(0, execute.ActualHeight), workspace).Y - narrowFooterBottom) < 1,
                $"Long drafts or composer feedback moved the footer instead of scrolling inside the editors: input offset={editor.VerticalOffset}, document offset={planEditor.VerticalOffset}, footer={narrowFooterBottom}->{execute.TranslatePoint(new Point(0, execute.ActualHeight), workspace).Y}.");
            editor.Text = inputDraft; planEditor.Text = planDraft;
            Call(window, "SetTaskWorkspaceVisible", false);
            Require(ReferenceEquals(composer.Parent, originalPlanHost) && ReferenceEquals(planSurface.Parent, originalPlanHost) &&
                ReferenceEquals(notes.Parent, workspace) && Grid.GetRow(planSurface) == 1 && Grid.GetRow(composer) == 2 &&
                planToggle.Visibility == Visibility.Visible && editor.MinLines == 3 && editor.MaxLines == 3 &&
                Field<Grid>(window, "_projectInputComposer").RowDefinitions[1].Height.IsAuto &&
                ((SolidColorBrush)editor.Background).Color == workspaceInputColor,
                "Ordinary notes mode lost its original host/order/collapse action, input sizing or consistent editor surface.");
            Layout(workspace, 1000, 600);
            RequirePlanMakerHeader(planMaker, planSurface, planEditor, composer);
            Call(window, "SetPlanDocumentVisible", true);
            Require(!Field<bool>(window, "_notesHidden") && notes.Visibility == Visibility.Visible, "Ordinary notes cannot expand.");
            Call(window, "SetPlanDocumentVisible", false);
            Require(Field<bool>(window, "_notesHidden") && notes.Visibility == Visibility.Collapsed, "Ordinary notes cannot collapse.");
            Call(window, "SetTaskWorkspaceVisible", true);
            Require(ReferenceEquals(composer.Parent, leftArea) && ReferenceEquals(planSurface.Parent, leftArea) && editor.Text == "플랜메이커에 전달할 요구사항 초안",
                "Returning to task mode lost the composer identity or draft.");
            VerifyNewTerminalLeavesPlanMakerView(window);
            Console.WriteLine("PASS PlanMaker top-right, new terminal leaves the PlanMaker view, Save/Execute pinned, legacy-off automatic progress migration/native default/independent exception/Stop, distinct colors, three-line request/all remaining height to plan/narrow scrolling, permanent LEFT plan, eight-pane identity/drafts, ordinary notes host.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine("FAIL " + exception); return 1; }
        finally
        {
            foreach (TerminalPane pane in panes.Concat(nativePanes)) pane.Dispose();
            if (window is not null) Call(window, "DisposeResources");
            Directory.Delete(fixture, recursive: true);
            AppContext.SetData("TerminalNotes.DataDirectory", previousDataDirectory);
        }
    }

    private static void VerifyAutomaticProgressDefault(MainWindow window, string fixture, string settingsPath, List<TerminalPane> panes)
    {
        object settings = Field<object>(window, "_settings");
        bool legacyEnabled = (bool)settings.GetType().GetProperty("AutoResumeEnabled")!.GetValue(settings)!;
        Require(!legacyEnabled, "The legacy disabled-setting fixture was not read.");
        Call(window, "LoadAutoResumeSetting", legacyEnabled);
        Require(Field<bool>(window, "_autoResumeEnabled"), "A saved opt-out disabled built-in automatic progress.");
        Type launchType = typeof(MainWindow).GetNestedType("ResolvedTerminalLaunch", BindingFlags.NonPublic)!;
        object launch = Activator.CreateInstance(launchType, false, "opencode", "fixture", fixture, TerminalTool.OpenCode)!;
        TerminalPane Create(bool independent, bool suppress = false)
        {
            var tab = (TabItem)Call(window, "CreateTerminalTabCore", 99, launch, "fixture", false,
                Path.Combine(fixture, "plan.md"), suppress, independent, null, null)!;
            object state = tab.Tag;
            var pane = (TerminalPane)state.GetType().GetProperty("Pane")!.GetValue(state)!;
            panes.Add(pane);
            return pane;
        }
        var ordinary = Create(false);
        var controller = Field<OpenCodeAutoResumeController>(ordinary, "_autoResume");
        Require(Field<bool>(controller, "_enabled") && !controller.IsContinuousImprovementEnabled &&
            controller.ContinuationSessionId is null && !ordinary.IsRunning,
            "Ordinary native automatic progress must be enabled, Loop separate, and startup must not submit work.");
        File.WriteAllText(Path.Combine(fixture, "plan.md"), "# Fixture plan\nKeep user changes.");
        controller.ObserveSubmittedPrompt("user-owned unfinished request", "session-fixture", "message-fixture");
        controller.ConfirmApiSubmission("session-fixture", "message-fixture");
        Require(controller.ContinuationSessionId == "session-fixture", "The user request did not arm exact-session recovery.");
        // This is the same controller stop used by TerminalPane.Interrupt, without native HTTP.
        controller.Suspend("User stopped automatic recovery");
        ordinary.SetAutoResumeEnabled(true);
        Require(controller.ContinuationSessionId is null && !controller.HasUnfinishedWork,
            "The automatic default revived a manually stopped request.");
        Require(Field<object?>(Create(true), "_autoResume") is null,
            "Independent native terminals unexpectedly gained project automatic recovery.");
        Require(!Field<bool>(Field<OpenCodeAutoResumeController>(Create(false, true), "_autoResume"), "_enabled"),
            "An explicitly suppressed handoff/controlled pane ignored its automatic-recovery exception.");
        Call(window, "ApplyAutoResumeSetting", false);
        using var migrated = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Require(Field<bool>(window, "_autoResumeEnabled") && migrated.RootElement.GetProperty("AutoResumeEnabled").GetBoolean(),
            "The old opt-out survived the next settings save.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void RequireSidebarActions(MainWindow window, Grid sidebar)
    {
        var actions = Field<StackPanel>(window, "_taskSidebarActions");
        var buttons = actions.Children.OfType<Button>().Where(button => button.Visibility == Visibility.Visible).ToArray();
        Require(buttons.Length == 2 && Equals(buttons[1].Content, "＋ new"), "Refresh must immediately precede New.");
        Require(Grid.GetRow(actions) == (sidebar.ActualWidth >= 360 ? 0 : 1),
            "Wide headers must stay on one row and compact headers must wrap their action pair.");
        foreach (RadioButton tab in new[] { Field<RadioButton>(window, "_normalTasksTab"), Field<RadioButton>(window, "_archivedTasksTab") })
        {
            Point position = tab.TranslatePoint(new Point(), sidebar);
            var labels = Descendants(tab).OfType<ContentPresenter>().ToArray();
            Require(tab.Visibility == Visibility.Visible && tab.Opacity > 0 && tab.Content is string text && text.Length > 0 &&
                tab.ActualWidth > 0 && tab.ActualHeight > 0 && position.X >= 0 && position.Y >= 0 &&
                position.X + tab.ActualWidth <= sidebar.ActualWidth + 1 && labels.Any(label => label.ActualWidth > 0 && label.ActualHeight > 0),
                $"A sidebar tab/label is hidden or clipped: content={tab.Content}, visible={tab.IsVisible}, position={position}, size={tab.ActualWidth}x{tab.ActualHeight}, label sizes={string.Join(',', labels.Select(label => $"{label.ActualWidth}x{label.ActualHeight}"))}.");
        }
        foreach (Button button in buttons)
        {
            Point position = button.TranslatePoint(new Point(), sidebar);
            Require(position.X >= 0 && position.Y >= 0 && position.X + button.ActualWidth <= sidebar.ActualWidth + 1 &&
                position.Y + button.ActualHeight <= sidebar.ActualHeight + 1,
                $"A sidebar action is clipped: {button.Content}, position={position}, width={button.ActualWidth}, sidebar={sidebar.ActualWidth}.");
        }
    }

    private static void RequirePlanMakerHeader(Button button, Grid document, TextBox editor, Border composer)
    {
        // PlanMaker shares a small group with 플랜 정리 in the header's action column.
        Require(button.Parent is StackPanel group && group.Parent is Grid header && ReferenceEquals(header.Parent, document) && Grid.GetRow(header) == 0 &&
            Grid.GetColumn(group) == 1 && ReferenceEquals(button, Descendants(document).OfType<Button>().Single(item => System.Windows.Automation.AutomationProperties.GetAutomationId(item) == "PlanMaker")),
            "The original PlanMaker button must belong to the document header.");
        Point position = button.TranslatePoint(new Point(), document);
        Point composerPosition = button.TranslatePoint(new Point(), composer);
        Require(button.ActualWidth > 0 && position.X >= 0 && position.Y >= 0 &&
            Math.Abs(position.X + button.ActualWidth - document.ActualWidth) < 1 &&
            position.Y + button.ActualHeight <= editor.TranslatePoint(new Point(), document).Y && composerPosition.Y < 0,
            $"PlanMaker is not fully visible at the document's upper-right: position={position}, button={button.ActualWidth}x{button.ActualHeight}, document={document.ActualWidth}x{document.ActualHeight}.");
    }

    private static HashSet<Color> BackgroundColors(Brush brush) => brush switch
    {
        SolidColorBrush solid => [solid.Color],
        GradientBrush gradient => gradient.GradientStops.Select(stop => stop.Color).ToHashSet(),
        _ => throw new InvalidOperationException("Expected a solid or gradient section background.")
    };

    // "+ 터미널" while the PlanMaker view is shown opens the terminal there in the terminal view, and the side
    // terminal column (it shares the PlanMaker cell) stays hidden while the PlanMaker view is shown.
    private static void VerifyNewTerminalLeavesPlanMakerView(MainWindow window)
    {
        var planMakers = Field<Grid>(window, "_planMakerDeck");
        var terminalView = Field<RadioButton>(window, "_terminalViewTab");
        var side = (UIElement)window.FindName("SplitTerminals");
        var tabs = (TabControl)window.FindName("TerminalTabs");
        int before = tabs.Items.Count;
        Call(window, "ChangeTerminalLayout", 1, false);
        Call(window, "ShowPlanMakerView", true);
        Require(planMakers.Visibility == Visibility.Visible && terminalView.IsChecked != true, "The PlanMaker view did not open.");
        Call(window, "AddIndependentTerminal", null, null);
        Require(tabs.Items.Count == before + 1 && planMakers.Visibility == Visibility.Collapsed && terminalView.IsChecked == true,
            "A new terminal was created inside the PlanMaker view instead of the terminal view.");
        Call(window, "AddIndependentTerminal", null, null);
        Require(tabs.Items.Count == before + 2 && side.Visibility == Visibility.Visible, "The second terminal did not use the side column.");
        Call(window, "ShowPlanMakerView", true);
        SetField(window, "_appliedCapacity", 0);
        Call(window, "ApplyTerminalLayout");
        Require(planMakers.Visibility == Visibility.Visible && side.Visibility != Visibility.Visible,
            "Re-arranging the terminals showed the side column over the PlanMaker view.");
        Call(window, "ShowPlanMakerView", false);
        Require(side.Visibility == Visibility.Visible && planMakers.Visibility == Visibility.Collapsed, "The terminal view did not return.");
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void SetField(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] arguments) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Drag(GridSplitter splitter, double verticalChange)
    {
        splitter.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        splitter.RaiseEvent(new DragDeltaEventArgs(0, verticalChange) { RoutedEvent = Thumb.DragDeltaEvent });
        splitter.RaiseEvent(new DragCompletedEventArgs(0, verticalChange, false) { RoutedEvent = Thumb.DragCompletedEvent });
    }
    private static void Layout(FrameworkElement surface, double width, double height)
    {
        surface.Measure(new Size(width, height));
        surface.Arrange(new Rect(0, 0, width, height));
        surface.UpdateLayout();
        surface.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        surface.Measure(new Size(width, height));
        surface.Arrange(new Rect(0, 0, width, height));
        surface.UpdateLayout();
    }
    private static void Capture(FrameworkElement surface, string path)
    {
        // Never publish an empty offscreen render as visual evidence. This
        // unloaded fixture's layout assertions do not depend on composition.
        surface.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        if (!Enumerable.Range(0, pixels.Length / 4).Any(index => pixels[index * 4 + 3] > 0 &&
            (pixels[index * 4] > 0 || pixels[index * 4 + 1] > 0 || pixels[index * 4 + 2] > 0)))
        {
            Console.WriteLine("PREVIEW unavailable: unloaded offscreen drawing is empty; no PNG published.");
            return;
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
        Console.WriteLine("PREVIEW " + path);
    }
}
