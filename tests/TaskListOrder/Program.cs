using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using TerminalNotes;
using TerminalNotes.Services;

// The task list: drag-and-drop reordering inside the list (saved, new tasks after it by name, archive moves follow) and the
// per-task agent choice (OpenCode by default, Codex or Claude Code; the row's mark, its menu, launch command, Loop guard).
internal static class Program
{
    private const BindingFlags Members = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

    [STAThread]
    private static int Main()
    {
        // AGENT_SELECTION=yes for this test only: the copy of features.json next to the test, read once on first use.
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "features.json"), "{ \"AgentSelection\": true }");
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes TaskList " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(fixture, "data"));
        try
        {
            Require(AppFeatures.AgentSelection, "The test could not turn agent selection on.");
            Verify(fixture);
            Console.WriteLine("PASS task list: drag-and-drop order saved and restored, new tasks after it by name, archive moves keep the place, " +
                "drop line shown and hidden; per-task agent with mark, menu, launch command, Loop guard and saved choice.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL " + e); return 1; }
        finally { try { Directory.Delete(fixture, recursive: true); } catch (IOException) { } }
    }

    private static void Verify(string fixture)
    {
        string workspace = Path.Combine(fixture, "workspace");
        string alpha = Path.Combine(workspace, "alpha"), beta = Path.Combine(workspace, "beta"), gamma = Path.Combine(workspace, "gamma");
        foreach (string project in new[] { alpha, beta, gamma }) Directory.CreateDirectory(project);
        string settings = Path.Combine(fixture, "settings.json");
        var window = Open(settings, workspace, alpha, beta, gamma);
        try
        {
            Require(Order(window).SequenceEqual([alpha, beta, gamma]), "Without a saved order the list is not alphabetical: " + string.Join(", ", Order(window)));

            // Dropped on a row: above it, or below it.
            Call(window, "MoveTaskInOrder", gamma, alpha, false);
            Require(Order(window).SequenceEqual([gamma, alpha, beta]), "Dropping above a row did not move the task there: " + string.Join(", ", Order(window)));
            Call(window, "MoveTaskInOrder", gamma, beta, true);
            Require(Order(window).SequenceEqual([alpha, beta, gamma]), "Dropping below a row did not move the task there.");
            Call(window, "MoveTaskInOrder", beta, alpha, false);
            Require(Order(window).SequenceEqual([beta, alpha, gamma]), "The second move was lost.");
            Require(Saved(settings).GetProperty("TaskOrder").EnumerateArray().Select(item => item.GetString()!)
                    .Where(project => !project.EndsWith("workspace", StringComparison.OrdinalIgnoreCase)).SequenceEqual([beta, alpha, gamma]),
                "The order was not saved.");
            // An empty terminal cell's menu offers the tasks in the same order as the list.
            var offered = ((string[])Call(window, "GetTerminalPickerProjects")!).Where(project => !string.Equals(Path.GetFileName(project), "workspace", StringComparison.OrdinalIgnoreCase));
            Require(offered.SequenceEqual(Order(window), StringComparer.OrdinalIgnoreCase),
                "The terminal cell's task menu is not in the list's order: " + string.Join(", ", offered));

            // A row accepts drops and shows where the task lands.
            var surface = Property<Border>(Row(window, alpha), "Surface");
            Require(surface.AllowDrop, "A task row does not accept drops.");
            // Rows are realized by layout, as when the user drags over them.
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Call(window, "ShowTaskDropLine", surface, true);
            var layer = AdornerLayer.GetAdornerLayer(surface);
            Require(layer?.GetAdorners(surface)?.Length == 1, "The drop line was not shown.");
            Call(window, "HideTaskDropLine");
            Require(layer!.GetAdorners(surface) is null or { Length: 0 }, "The drop line stayed after the drag.");

            // A new task comes after the saved order; an archive move keeps the place under the new path.
            string delta = Path.Combine(workspace, "delta");
            Directory.CreateDirectory(delta);
            Call(window, "EnsureProjectTaskRow", delta);
            Call(window, "UpdateProjectTaskTree");
            Require(Order(window).SequenceEqual([beta, alpha, gamma, delta]), "A new task did not come after the saved order.");
            string archived = Path.Combine(workspace, "archive", "alpha");
            Call(window, "MoveTaskOrderEntry", alpha, archived);
            Require(Field<List<string>>(window, "_taskOrder").Where(project => !project.EndsWith("workspace", StringComparison.OrdinalIgnoreCase))
                    .SequenceEqual([beta, archived, gamma], StringComparer.OrdinalIgnoreCase),
                "An archive move lost the task's place.");
            Call(window, "MoveTaskOrderEntry", archived, alpha);

            // The agent: OpenCode by default, chosen per task from the row's menu, shown on its mark, used to launch it.
            Require(Agent(window, beta) == TerminalTool.OpenCode, "A task did not start out on OpenCode.");
            var mark = Field<IDictionary>(window, "_taskAgentButtons")[beta] as Button;
            Require(mark is { Visibility: Visibility.Visible } && (mark.ToolTip as string ?? "").Contains("OpenCode"), "The agent mark is missing.");
            var agentMenu = Property<Button>(Row(window, beta), "Select").ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "에이전트"));
            agentMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Codex")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(Agent(window, beta) == TerminalTool.Codex && (mark!.ToolTip as string ?? "").Contains("Codex"), "Choosing Codex from the menu did not take.");
            var launch = Call(window, "ResolveTaskLaunch", beta, "beta", 1)!;
            string command = (string)launch.GetType().GetProperty("Command")!.GetValue(launch)!;
            Require(command.StartsWith("codex", StringComparison.Ordinal), "The task did not launch with its agent: " + command);
            Require(Saved(settings).GetProperty("TaskAgents").GetProperty(beta).GetString() == "Codex", "The agent choice was not saved.");
            Call(window, "UpdateProjectTaskActivity");
            Require(!Property<System.Windows.Controls.Primitives.ToggleButton>(Row(window, beta), "Loop").IsEnabled,
                "Loop stayed available for a Codex task (it needs OpenCode).");
            Call(window, "SetTaskAgent", beta, TerminalTool.ClaudeCode);
            Require(Agent(window, beta) == TerminalTool.ClaudeCode, "Claude Code could not be chosen.");

            // Collapsed, the task panel leaves its expand button on screen: a click at that spot lands on it and opens the panel.
            var expand = (Button)window.FindName("FocusButton");
            Call(window, "SetTaskSidebarCollapsed", true);
            Call(window, "ApplyTaskSidebarState", false);
            window.UpdateLayout();
            Require(Field<ColumnDefinition>(window, "_taskSidebarColumn").ActualWidth == 0, "The collapsed panel kept its width.");
            // Beside its centre: a button clipped to nothing still answers on the zero-width line at its exact centre.
            Point spot = expand.TranslatePoint(new Point(expand.ActualWidth * 0.75, expand.ActualHeight / 2), window);
            Require(System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(expand).Width >= expand.ActualWidth &&
                window.InputHitTest(spot) is DependencyObject hit && (ReferenceEquals(hit, expand) || expand.IsAncestorOf(hit)),
                "The collapsed task panel has no expand button to click (at " + spot + ": " + window.InputHitTest(spot) + ").");
            SavePreview((FrameworkElement)window.Content, "task-panel-collapsed.png");
            expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Call(window, "ApplyTaskSidebarState", false);
            window.UpdateLayout();
            Require(!Field<bool>(window, "_taskSidebarCollapsed") && Field<ColumnDefinition>(window, "_taskSidebarColumn").ActualWidth > 0, "The expand button did not open the panel.");
        }
        finally { Close(window); }

        // Restarted: the order and the agent come back.
        window = Open(settings, workspace, alpha, beta, gamma);
        try
        {
            Require(Order(window).SequenceEqual([beta, alpha, gamma]), "The saved order did not come back: " + string.Join(", ", Order(window)));
            Require(Agent(window, beta) == TerminalTool.ClaudeCode, "The saved agent did not come back.");
            Call(window, "SetTaskAgent", beta, TerminalTool.OpenCode);
            Require(Agent(window, beta) == TerminalTool.OpenCode && !Saved(settings).GetProperty("TaskAgents").TryGetProperty(beta, out _),
                "Going back to OpenCode kept a saved choice.");
        }
        finally { Close(window); }
    }

    private static void SavePreview(FrameworkElement element, string name)
    {
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        string folder = Path.Combine(Path.GetTempPath(), "TerminalNotes.TaskList");
        Directory.CreateDirectory(folder);
        using var stream = File.Create(Path.Combine(folder, name));
        encoder.Save(stream);
    }

    private static MainWindow Open(string settings, string workspace, params string[] projects)
    {
        var window = new MainWindow(settings, autoStartCodex: false, initialWorkspace: workspace)
        { Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        Call(window, "SetWorkMode", false);
        window.Show();
        foreach (string project in projects)
        {
            Field<HashSet<string>>(window, "_loadedSingleImprovementPreferences").Add(project);
            Field<Dictionary<string, bool>>(window, "_projectTaskContinuousModes")[project] = false;
            Call(window, "EnsureProjectTaskRow", project);
        }
        Call(window, "UpdateProjectTaskTree");
        return window;
    }

    private static void Close(MainWindow window)
    {
        Call(window, "DisposeResources");
        window.Close();
    }

    // The tasks this test made (the window also lists the workspace folder itself).
    private static string[] Order(MainWindow window) =>
        Field<TreeViewItem>(window, "_taskOrderGroup").Items.Cast<TreeViewItem>().Select(node => (string)node.Tag)
            .Where(project => !string.Equals(Path.GetFileName(project), "workspace", StringComparison.OrdinalIgnoreCase)).ToArray();

    private static object Row(MainWindow window, string project) => Field<IDictionary>(window, "_projectTaskRows")[project]!;

    private static TerminalTool Agent(MainWindow window, string project) => (TerminalTool)Call(window, "AgentForTask", project)!;

    private static JsonElement Saved(string settings) => JsonDocument.Parse(File.ReadAllText(settings)).RootElement;

    private static T Property<T>(object target, string name) => (T)target.GetType().GetProperty(name)!.GetValue(target)!;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Members)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Members)!.Invoke(target, args);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
