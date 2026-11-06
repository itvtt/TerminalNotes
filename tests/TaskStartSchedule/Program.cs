using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using TerminalNotes;
using TerminalNotes.Services;

// Timed task starts: store, card badge/menu, the schedule dialog (Loop choice, editing, cancel) and the start itself.
internal static class Program
{
    private const BindingFlags Members = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

    [STAThread]
    private static int Main()
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes Schedule " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(fixture, "data"));
        try
        {
            VerifyStore(fixture);
            VerifyWindow(fixture);
            Console.WriteLine("PASS task start schedules: store, card badge and menu, dialog with Loop choice/edit/cancel, timed start, skip and missed cases.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL " + e); return 1; }
        finally { try { Directory.Delete(fixture, recursive: true); } catch (IOException) { } }
    }

    private static void VerifyStore(string fixture)
    {
        string project = Path.Combine(fixture, "store");
        var schedule = new ProjectStartSchedule(DateTimeOffset.Now.AddHours(2), true, DateTimeOffset.Now.AddHours(5));
        ProjectStartScheduleStore.Write(project + Path.DirectorySeparatorChar, schedule);
        Require(ProjectStartScheduleStore.ReadAll().TryGetValue(project, out var read) && read == schedule, "Schedule did not round-trip.");
        ProjectStartScheduleStore.Write(project, null);
        Require(ProjectStartScheduleStore.ReadAll().Count == 0, "Schedule was not removed.");
        string path = Path.Combine(fixture, "data", "project-start-schedules.json");
        // Loop 1 or Loop 2 is saved with the schedule; one saved before that choice keeps following the task's own Loop.
        ProjectStartScheduleStore.Write(project, schedule with { LoopFull = false });
        Require(ProjectStartScheduleStore.ReadAll()[project].LoopFull == false, "The Loop 1 choice did not round-trip.");
        File.WriteAllText(path, "{" + System.Text.Json.JsonSerializer.Serialize(project) + ":{\"StartAt\":\"2030-01-01T09:00:00+09:00\",\"Loop\":true,\"LoopUntil\":null}}");
        Require(ProjectStartScheduleStore.ReadAll()[project] is { Loop: true, LoopFull: null }, "An older schedule did not read without a Loop choice.");
        ProjectStartScheduleStore.Write(project, null);
        File.WriteAllText(path, "{broken");
        bool rejected = false;
        try { ProjectStartScheduleStore.ReadAll(); } catch (InvalidDataException) { rejected = true; }
        Require(rejected && File.ReadAllText(path) == "{broken", "A malformed schedule file was not preserved.");
        File.Delete(path);
    }

    private static void VerifyWindow(string fixture)
    {
        string workspace = Path.Combine(fixture, "workspace");
        string project = Path.Combine(workspace, "nightly");
        Directory.CreateDirectory(project);
        var window = new MainWindow(Path.Combine(fixture, "settings.json"), autoStartCodex: false, initialWorkspace: workspace)
        { Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        var started = new List<string>();
        Set(window, "ScheduledProjectStartOverride", (Func<string, Task>)(started_project => { started.Add(started_project); return Task.CompletedTask; }));
        try
        {
            Call(window, "SetWorkMode", false);
            window.Show();
            Field<HashSet<string>>(window, "_loadedSingleImprovementPreferences").Add(project);
            Field<Dictionary<string, bool>>(window, "_projectTaskContinuousModes")[project] = false;
            Call(window, "EnsureProjectTaskRow", project);
            object row = Field<IDictionary>(window, "_projectTaskRows")[project]!;
            var badge = Property<Button>(row, "ScheduleBadge");
            var menu = Property<MenuItem>(row, "Schedule");
            var cancelMenu = Property<MenuItem>(row, "ScheduleCancel");
            Call(window, "UpdateProjectTaskActivity");
            Require(badge.Visibility == Visibility.Collapsed && cancelMenu.Visibility == Visibility.Collapsed && Equals(menu.Header, "예약 실행…") &&
                Property<Button>(row, "Select").ContextMenu.Items.Contains(menu), "An unscheduled task showed a schedule badge or lacked the menu item.");

            // New schedule from the dialog: preset, Loop with an end time, validation.
            DateTime tomorrow = DateTime.Today.AddDays(1);
            RunDialog(window, project, dialog =>
            {
                Click(Find<Button>(dialog, b => Equals(b.Content, "내일 09:00")));
                Require(Find<TextBox>(dialog, "ProjectStartDate").Text == tomorrow.ToString("yyyy-MM-dd") && Find<TextBox>(dialog, "ProjectStartTime").Text == "09:00",
                    "The tomorrow preset did not fill the date and time.");
                var loop = Find<CheckBox>(dialog, "ProjectStartLoop");
                Require(loop.IsChecked == false && !Find<CheckBox>(dialog, "ProjectStartLoopLimited").IsEnabled, "Loop did not start from the task's current setting.");
                // Loop 1 and Loop 2 are offered once Loop is on, starting from the task's own (Loop 2 until Loop 1 is chosen).
                Require(!Find<RadioButton>(dialog, "ProjectStartLoop1").IsEnabled && !Find<RadioButton>(dialog, "ProjectStartLoop2").IsEnabled, "Loop 1 · Loop 2 were open with Loop off.");
                loop.IsChecked = true;
                Require(Find<RadioButton>(dialog, "ProjectStartLoop2").IsChecked == true && Find<RadioButton>(dialog, "ProjectStartLoop1").IsEnabled, "Loop 2 was not the starting choice.");
                Find<RadioButton>(dialog, "ProjectStartLoop1").IsChecked = true;
                Require(Find<TextBlock>(dialog, t => t.Text.Contains("플랜에 없는 기능은 더하지 않습니다")) is not null, "The chosen Loop's description was not shown.");
                SavePreview(dialog, "schedule-loop.png");
                Find<CheckBox>(dialog, "ProjectStartLoopLimited").IsChecked = true;
                Find<TextBox>(dialog, "ProjectStartLoopEndDate").Text = tomorrow.ToString("yyyy-MM-dd");
                Find<TextBox>(dialog, "ProjectStartLoopEndTime").Text = "08:00";
                Click(Apply(dialog));
                Require(dialog.IsVisible && Find<TextBlock>(dialog, t => t.Text.Contains("시작 시간 이후")) is not null, "A Loop end before the start was accepted.");
                Find<TextBox>(dialog, "ProjectStartLoopEndTime").Text = "18:00";
                Click(Apply(dialog));
            });
            var schedules = Field<Dictionary<string, ProjectStartSchedule>>(window, "_projectStartSchedules");
            Require(schedules.TryGetValue(project, out var saved) && saved.StartAt.LocalDateTime == tomorrow.AddHours(9) && saved.Loop &&
                saved.LoopUntil?.LocalDateTime == tomorrow.AddHours(18) && saved.LoopFull == false && ProjectStartScheduleStore.ReadAll().ContainsKey(project),
                "The dialog did not save the schedule with Loop and its end time.");
            Require(badge.Visibility == Visibility.Visible && cancelMenu.Visibility == Visibility.Visible &&
                Find<TextBlock>(badge, t => t.Text == tomorrow.ToString("MM/dd") + " 09:00 · Loop 1") is not null &&
                ((string)menu.Header).StartsWith("예약 실행 변경 · ") && badge.ToolTip!.ToString()!.Contains("Loop 1") && badge.ToolTip!.ToString()!.Contains("18:00까지"),
                "The card did not show the scheduled time and Loop.");
            Require((bool)Call(window, "HasWorkRequiringSystemAwake")!, "A pending schedule let the PC sleep.");

            // Editing: the dialog opens with the saved values; changing the time keeps one schedule.
            RunDialog(window, project, dialog =>
            {
                Require(Find<TextBox>(dialog, "ProjectStartTime").Text == "09:00" && Find<CheckBox>(dialog, "ProjectStartLoop").IsChecked == true &&
                    Find<RadioButton>(dialog, "ProjectStartLoop1").IsChecked == true &&
                    Find<TextBox>(dialog, "ProjectStartLoopEndTime").Text == "18:00" && Apply(dialog).Content as string == "변경" &&
                    Find<Button>(dialog, b => Equals(b.Content, "예약 취소"))!.Visibility == Visibility.Visible, "Editing did not reopen the saved schedule.");
                Find<TextBox>(dialog, "ProjectStartTime").Text = "7:30";
                Find<CheckBox>(dialog, "ProjectStartLoop").IsChecked = false;
                Click(Apply(dialog));
            });
            Require(schedules[project] is { Loop: false, LoopUntil: null, LoopFull: null } edited && edited.StartAt.LocalDateTime == tomorrow.AddHours(7.5) && schedules.Count == 1,
                "Editing did not replace the schedule.");
            RunDialog(window, project, dialog => Click(Find<Button>(dialog, b => Equals(b.Content, "예약 취소"))));
            Require(!schedules.ContainsKey(project) && badge.Visibility == Visibility.Collapsed && !ProjectStartScheduleStore.ReadAll().ContainsKey(project),
                "Cancel from the dialog left the schedule.");

            // Accounts without Loop and plan mode keep the current Loop setting and show no Loop choice.
            FieldInfo accountOverride = typeof(MainWindow).Assembly.GetType("TerminalNotes.Services.LoopAutoRecommandUsers")!
                .GetField("AccountOverride", BindingFlags.Static | BindingFlags.NonPublic)!;
            accountOverride.SetValue(null, (Func<string>)(() => @"CORP\someone"));
            try
            {
                RunDialog(window, project, dialog =>
                {
                    Require(FindOptional<CheckBox>(dialog, "ProjectStartLoop") is null && FindOptional<RadioButton>(dialog, "ProjectStartLoop1") is null &&
                        FindOptional<RadioButton>(dialog, "ProjectStartLoop2") is null, "An account without Loop saw the Loop choice.");
                    Click(Apply(dialog));
                });
                Require(schedules[project] is { Loop: false } && Find<TextBlock>(badge, t => t.Text.Contains("Loop")) is null, "The schedule showed Loop to an account without Loop.");
            }
            finally { accountOverride.SetValue(null, null); }
            CancelMenu(window, cancelMenu);
            Require(!schedules.ContainsKey(project), "The card's cancel menu left the schedule.");

            // Due schedule: Loop is applied, then the task starts once and the schedule is gone.
            Set(window, "_projectStartSchedulesLoadedAt", DateTimeOffset.Now.AddMinutes(-30));
            var loopEnd = DateTimeOffset.Now.AddHours(3);
            Require((bool)Call(window, "SetProjectStartSchedule", project, new ProjectStartSchedule(DateTimeOffset.Now.AddSeconds(-1), true, loopEnd))!, "Setting a schedule failed.");
            Call(window, "RunDueProjectStartSchedules");
            WaitOnDispatcher(() => started.Count == 1 && Field<HashSet<string>>(window, "_scheduledProjectStarts").Count == 0, "Due schedule did not start.");
            Require(started[0] == project && !schedules.ContainsKey(project) && !ProjectStartScheduleStore.ReadAll().ContainsKey(project) &&
                Field<Dictionary<string, bool>>(window, "_projectTaskContinuousModes")[project] &&
                SingleProjectContinuousImprovementStore.Read(project) is { Enabled: true } loopSaved && loopSaved.Until == loopEnd,
                "Due schedule did not apply Loop before starting, or stayed scheduled.");
            Call(window, "RunDueProjectStartSchedules");
            Require(started.Count == 1, "A schedule started twice.");

            // The schedule's Loop 1 or Loop 2 is the one that runs, even when the task was set to the other.
            Require((bool)Call(window, "SetProjectStartSchedule", project, new ProjectStartSchedule(DateTimeOffset.Now.AddSeconds(-1), true, loopEnd, false))!, "Setting a Loop 1 schedule failed.");
            Call(window, "RunDueProjectStartSchedules");
            WaitOnDispatcher(() => started.Count == 2 && Field<HashSet<string>>(window, "_scheduledProjectStarts").Count == 0, "The Loop 1 schedule did not start.");
            Require(!Field<Dictionary<string, bool>>(window, "_projectTaskRecommendPlans")[project] && SingleProjectContinuousImprovementStore.Read(project) is { Enabled: true, Loop: 1 },
                "The schedule's Loop 1 was not applied to a task set to Loop 2.");
            started.RemoveAt(1);

            // A Loop end time shows its own grey hourglass badge; the start schedule keeps the gold clock.
            Call(window, "UpdateProjectTaskActivity");
            var loopEndBadge = Property<Button>(row, "LoopEndBadge");
            static string GlyphKind(Button button) { var glyph = ((StackPanel)button.Content).Children[0]; return glyph.GetType().GetProperty("Kind")!.GetValue(glyph)!.ToString()!; }
            Require(loopEndBadge.Visibility == Visibility.Visible && GlyphKind(loopEndBadge) == "Hourglass" && GlyphKind(badge) == "Clock" &&
                ((TextBlock)((StackPanel)loopEndBadge.Content).Children[1]).Text.Contains(loopEnd.LocalDateTime.ToString("HH:mm")),
                "A Loop end time did not show its hourglass badge, or it looked like the start schedule.");

            // Due without Loop turns Loop off for this run.
            Call(window, "SetProjectStartSchedule", project, new ProjectStartSchedule(DateTimeOffset.Now.AddSeconds(-1), false));
            Call(window, "RunDueProjectStartSchedules");
            WaitOnDispatcher(() => started.Count == 2 && Field<HashSet<string>>(window, "_scheduledProjectStarts").Count == 0, "Schedule without Loop did not start.");
            Require(!Field<Dictionary<string, bool>>(window, "_projectTaskContinuousModes")[project], "Schedule without Loop kept Loop on.");
            Call(window, "UpdateProjectTaskActivity");
            Require(loopEndBadge.Visibility == Visibility.Collapsed, "The Loop end badge stayed after Loop was turned off.");

            // A task already running is not started again.
            var starting = Field<HashSet<string>>(window, "_startingProjectTasks");
            starting.Add(project);
            Call(window, "SetProjectStartSchedule", project, new ProjectStartSchedule(DateTimeOffset.Now.AddSeconds(-1), false));
            Call(window, "RunDueProjectStartSchedules");
            WaitOnDispatcher(() => Field<HashSet<string>>(window, "_scheduledProjectStarts").Count == 0 && !schedules.ContainsKey(project), "Running-task schedule was not handled.");
            Require(started.Count == 2 && Status(window).Contains("이미 실행 중이라"), "A running task was started again by its schedule.");
            starting.Remove(project);

            // Missed while the app was closed: not started, reported. Just-due at startup still starts.
            Set(window, "_projectStartSchedulesLoadedAt", DateTimeOffset.Now);
            Call(window, "SetProjectStartSchedule", project, new ProjectStartSchedule(DateTimeOffset.Now.AddMinutes(-10), false));
            Call(window, "RunDueProjectStartSchedules");
            Require(started.Count == 2 && !schedules.ContainsKey(project) && Status(window).Contains("앱이 꺼져 있어"), "A missed schedule was started or kept.");
            Call(window, "SetProjectStartSchedule", project, new ProjectStartSchedule(DateTimeOffset.Now.AddSeconds(-30), false));
            Call(window, "RunDueProjectStartSchedules");
            WaitOnDispatcher(() => started.Count == 3, "A schedule due during the restart did not start.");

            // Excluding the task cancels its schedule; moving keeps it on the new folder.
            Call(window, "SetProjectStartSchedule", project, new ProjectStartSchedule(DateTimeOffset.Now.AddHours(1), false));
            Call(window, "SetTaskExcluded", project, true);
            Require(!schedules.ContainsKey(project), "An excluded task kept its schedule.");
            Call(window, "SetTaskExcluded", project, false);
            string moved = Path.Combine(workspace, "archive", "nightly");
            Call(window, "SetProjectStartSchedule", project, new ProjectStartSchedule(DateTimeOffset.Now.AddHours(1), false));
            Call(window, "MoveProjectStartSchedule", project, moved);
            Require(!schedules.ContainsKey(project) && schedules.ContainsKey(moved) && ProjectStartScheduleStore.ReadAll().ContainsKey(moved), "Moving a task lost its schedule.");
            Call(window, "SetProjectStartSchedule", moved, null);
            Require(!(bool)Call(window, "HasWorkRequiringSystemAwake")!, "No schedule still kept the PC awake.");
        }
        finally
        {
            Call(window, "DisposeResources");
            window.Close();
        }
    }

    private static string Status(MainWindow window) => ((TextBlock)window.FindName("StatusLabel")).Text;

    private static void CancelMenu(MainWindow window, MenuItem cancel) => cancel.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    // Opens the modal dialog and drives it from inside its message loop.
    private static void RunDialog(MainWindow window, string project, Action<Window> drive)
    {
        Exception? failure = null;
        var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.Title == "예약 실행" && w.IsVisible);
            if (dialog is null) { if (elapsed.Elapsed > TimeSpan.FromSeconds(10)) timer.Stop(); return; }
            timer.Stop();
            dialog.UpdateLayout();
            try { drive(dialog); }
            catch (Exception e) { failure = e; }
            if (dialog.IsVisible) dialog.Close();
        };
        timer.Start();
        Call(window, "ShowProjectStartSchedule", project);
        timer.Stop();
        if (failure is not null) throw new InvalidOperationException("Dialog check failed.", failure);
        Require(elapsed.Elapsed < TimeSpan.FromSeconds(10), "The schedule dialog did not open.");
    }

    private static void WaitOnDispatcher(Func<bool> condition, string message)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            Require(elapsed.Elapsed < TimeSpan.FromSeconds(10), message);
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }

    private static Button Apply(Window dialog) =>
        Find<Button>(dialog, b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "ProjectStartScheduleApply") ?? throw new InvalidOperationException("Apply not found.");

    private static void Click(Button? button) => (button ?? throw new InvalidOperationException("Button not found.")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    // Missing controls fail with their name instead of a null dereference.
    private static void SavePreview(Window dialog, string name)
    {
        dialog.UpdateLayout();
        var content = (FrameworkElement)dialog.Content;
        var size = new Size(content.ActualWidth + content.Margin.Left + content.Margin.Right, content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(size.Width * 2), (int)Math.Ceiling(size.Height * 2), 192, 192,
            System.Windows.Media.PixelFormats.Pbgra32);
        var background = new System.Windows.Media.DrawingVisual();
        using (var context = background.RenderOpen())
            context.DrawRectangle(dialog.Background, null, new Rect(size));
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        string folder = Path.Combine(Path.GetTempPath(), "TerminalNotes.TaskStartSchedule");
        Directory.CreateDirectory(folder);
        using var stream = File.Create(Path.Combine(folder, name));
        encoder.Save(stream);
    }

    private static T Find<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Find<T>(root, element => element.Name == name) ?? throw new InvalidOperationException(name + " not found.");
    private static T? FindOptional<T>(DependencyObject root, string name) where T : FrameworkElement => Find<T>(root, element => element.Name == name);

    private static T? Find<T>(DependencyObject root, Func<T, bool> match) where T : FrameworkElement
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is T element && match(element)) return element;
            if (child is DependencyObject node && Find(node, match) is { } found) return found;
        }
        return null;
    }

    private static T Property<T>(object target, string name) => (T)target.GetType().GetProperty(name)!.GetValue(target)!;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Members)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Members)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] args) => (target as Type ?? target.GetType()).GetMethod(name, Members)!.Invoke(target is Type ? null : target, args);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
