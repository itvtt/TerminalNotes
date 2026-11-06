using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using TerminalNotes;

internal static class DispatcherRefreshSmoke
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Hidden)!.GetValue(value)!;
    private static object? Call(object value, string name, params object?[] args) => value.GetType().GetMethod(name, Hidden)!.Invoke(value, args);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }

    public static void Run(string fixture)
    {
        bool baseline = Environment.GetEnvironmentVariable("TERMINALNOTES_UI_DISPATCHER_REFRESH_BASELINE") == "1";
        string workspace = Path.Combine(fixture, "picker");
        Directory.CreateDirectory(workspace);
        string first = Path.Combine(workspace, "alpha"), second = Path.Combine(workspace, "beta"), unopened = Path.Combine(workspace, "gamma");
        foreach (string project in new[] { first, second, unopened }) Directory.CreateDirectory(project);
        string settings = Path.Combine(fixture, "settings.json");
        File.WriteAllText(settings, JsonSerializer.Serialize(new { Workspace = workspace, SetupCompleted = true, WorkMode = "Advanced" }));
        var window = new MainWindow(settings, autoStartCodex: false, initialWorkspace: workspace);
        try
        {
            // Do not show/load this window: panes cannot create browsers, terminals, or model requests.
            var tabs = Field<TabControl>(window, "TerminalTabs");
            TabItem Add(string project, string title, bool independent = false)
            {
                Call(window, "EnsureProjectTaskRow", project);
                var pane = new TerminalPane(project);
                Type stateType = typeof(MainWindow).GetNestedType("TerminalTabState", BindingFlags.NonPublic)!;
                object state = Activator.CreateInstance(stateType)!;
                stateType.GetProperty("Pane")!.SetValue(state, pane);
                stateType.GetProperty("Title")!.SetValue(state, title);
                stateType.GetProperty("Sequence")!.SetValue(state, tabs.Items.Count + 1);
                stateType.GetProperty("IsIndependent")!.SetValue(state, independent);
                stateType.GetProperty("PlanDocumentPath")!.SetValue(state, Path.Combine(project, "terminal-notes", "plan.md"));
                var header = new StackPanel();
                header.Children.Add(new TextBlock { Text = title });
                var tab = new TabItem { Tag = state, Header = header };
                tabs.Items.Add(tab);
                return tab;
            }
            TabItem a = Add(first, "Alpha one"), b = Add(first, "Alpha two"), c = Add(second, "Beta"), d = Add(second, "Independent", true);
            Call(window, "EnsureProjectTaskRow", unopened);
            void Refresh() => Call(window, "UpdateDispatcher");
            Refresh();
            var buttons = Field<IDictionary>(window, "_sessionButtons");
            var unopenedButtons = Field<IDictionary>(window, "_unopenedProjectButtons");
            var button = (Button)buttons[a]!;
            var unopenedButton = (Button)unopenedButtons[unopened]!;
            var content = (StackPanel)button.Content;
            var children = content.Children.Cast<object>().ToArray();
            var secondContent = (StackPanel)((Button)buttons[b]!).Content;
            var secondChildren = secondContent.Children.Cast<object>().ToArray();
            var unopenedContent = (StackPanel)unopenedButton.Content;
            var unopenedChildren = unopenedContent.Children.Cast<object>().ToArray();
            var panel = Field<Panel>(window, "DispatcherSessions");
            Require(panel.Children.Cast<object>().SequenceEqual(new object[] { buttons[a]!, buttons[b]!, buttons[c]!, unopenedButton, buttons[d]! }),
                "project membership/order and independent terminal order match the picker");
            Require(!Field<Popup>(window, "SessionPickerPopup").IsOpen, "allocation measurement uses the closed picker");
            for (int i = 0; i < 30; i++) Refresh();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 300; i++) Refresh();
            watch.Stop();
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Console.WriteLine($"MEASURE picker-four-panes/300-refreshes baseline={baseline}: Allocated={allocated / 1048576d:F2}MiB Elapsed={watch.Elapsed.TotalMilliseconds:F0}ms");
            if (!baseline)
            {
                Require(ReferenceEquals(button.Content, content) && content.Children.Cast<object>().SequenceEqual(children), "unchanged session refresh preserves every picker child identity");
                Require(ReferenceEquals(unopenedButton.Content, unopenedContent) && unopenedContent.Children.Cast<object>().SequenceEqual(unopenedChildren), "unchanged unopened project refresh preserves every picker child identity");
                var method = typeof(MainWindow).GetMethod("StateColorBrush", BindingFlags.Static | BindingFlags.NonPublic)!;
                var brush = (Brush)method.Invoke(null, ["#F4EFE4"])!;
                Require(brush.IsFrozen && ReferenceEquals(brush, method.Invoke(null, ["#F4EFE4"])), "fixed palette brushes are frozen and reused");
            }
            a.Tag.GetType().GetProperty("Title")!.SetValue(a.Tag, "Renamed live title");
            var paneA = (TerminalPane)a.Tag.GetType().GetProperty("Pane")!.GetValue(a.Tag)!;
            paneA.MarkNeedsInput();
            paneA.ObserveTokenUsage("total tokens: 1979402\n");
            tabs.SelectedItem = b;
            Refresh();
            var labels = ((StackPanel)button.Content).Children.OfType<TextBlock>().ToArray();
            Require(labels[0].Text.Contains("2개 세션") && labels[1].Text == "Renamed live title" && labels[2].Text.Contains(paneA.ActivityLabel), "group/title/activity update immediately");
            Require(paneA.TokenUsageLabel != "토큰 정보 없음" && labels[2].Text.Contains(paneA.TokenUsageLabel), "formatted token usage updates immediately");
            Require(button.BorderBrush.ToString() == "#FF5C5C5C" && ((Button)buttons[b]!).BorderBrush.ToString() == "#FFE5C07B", "selected session borders update immediately");
            Call(window, "RememberProjectName", unopened, "A renamed project");
            Refresh();
            Require(((StackPanel)unopenedButton.Content).Children.OfType<TextBlock>().First().Text == "A renamed project" &&
                ReferenceEquals(panel.Children[0], unopenedButton), "project name changes update existing picker labels and ordering");
            Field<HashSet<string>>(window, "_projectTaskControlWrites").Add(unopened);
            Refresh();
            Require(!unopenedButton.IsEnabled && AutomationProperties.GetName(unopenedButton).Contains("A renamed project"), "authoritative unopened project enable checks and accessible names remain current");
            tabs.Items.Remove(a);
            paneA.Dispose();
            Refresh();
            Require(!buttons.Contains(a) && ((StackPanel)((Button)buttons[b]!).Content).Children.OfType<TextBlock>().First().Text.Contains("1개 세션"), "removed sessions release picker rows and promote the next group header");
            if (!baseline) Require(ReferenceEquals(((Button)buttons[b]!).Content, secondContent) &&
                secondContent.Children.Cast<object>().Skip(1).SequenceEqual(secondChildren), "picker membership change preserves retained session title/status identity");
            Call(window, "UpdateProjectTaskActivity");
            object taskRow = Field<IDictionary>(window, "_projectTaskRows")[unopened]!;
            var start = (Button)taskRow.GetType().GetProperty("Start")!.GetValue(taskRow)!;
            var taskState = (TextBlock)taskRow.GetType().GetProperty("State")!.GetValue(taskRow)!;
            object styleReference = start.ReadLocalValue(System.Windows.FrameworkElement.StyleProperty);
            object stateToolTip = taskState.ToolTip;
            Call(window, "UpdateProjectTaskActivity");
            if (!baseline) Require(ReferenceEquals(styleReference, start.ReadLocalValue(System.Windows.FrameworkElement.StyleProperty)) &&
                ReferenceEquals(stateToolTip, taskState.ToolTip), "unchanged task refresh retains the style resource expression and final diagnostic tooltip");
            Console.WriteLine("Fixture uses only unloaded panes; no model, PTY, native server, or live application interaction.");
        }
        finally { Call(window, "DisposeResources"); window.Close(); }
    }
}
