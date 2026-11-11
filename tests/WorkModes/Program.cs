using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using TerminalNotes;
using TerminalNotes.Services;

internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int result = 1;
        app.Startup += async (_, _) =>
        {
            try
            {
                if (args.Contains("--startup-status-only")) await VerifyStartupStatusAsync();
                else await RunAsync();
                result = 0;
            }
            catch (Exception exception) { Console.Error.WriteLine("FAIL " + exception); }
            finally { app.Shutdown(); }
        };
        app.Run();
        return result;
    }

    private static async Task RunAsync()
    {
        VerifySettingsAndLayout();
        VerifyRefusalPreservesSessions();
        await VerifySessionAdmissionAsync();
        VerifyPendingAndExternalAdmission();
        VerifyBlockedFactories();
        VerifyRetiringCleanupAdmission();
        await VerifyBasicVisibleSessionAsync();
        await VerifyPlanFreeBasicStartAsync();
        await VerifyBasicWorkspaceChangeAsync();
        await VerifyBasicTaskManagementAsync();
        await VerifyBasicTaskDeletionAsync();
        await VerifyBasicTaskDeletionOwnershipAsync();
        await VerifyBasicSetupAsync();
        await VerifyFirstRunModeSelectionAsync();
        VerifyWorkspacePrecedence();
        await VerifyStartupModeChangeAsync();
        await VerifyStartupStatusAsync();
        await VerifyBlockedStartAsync();
        Console.WriteLine($"PASS {_checks} checks: simple Basic folder/native-pane flow, plan-free startup, retained Advanced state, asynchronous mode switching, session limits and blocked side effects; no shell/PTY/model/WSL.");
    }

    private static void VerifySettingsAndLayout()
    {
        using (var fixture = new Fixture())
        {
            Check(Basic(fixture.Window), "A fresh settings path must default to Basic.");
            var basic = (RadioButton)fixture.Window.FindName("BasicWorkMode");
            var advanced = (RadioButton)fixture.Window.FindName("AdvancedWorkMode");
            foreach (string name in new[] { "BasicWorkMode", "AdvancedWorkMode" })
                Check(WindowChrome.GetIsHitTestVisibleInChrome((FrameworkElement)fixture.Window.FindName(name)),
                    name + " cannot receive mouse input in the native caption area.");
            Check(((FrameworkElement)fixture.Window.FindName("BasicWorkspace")).IsAncestorOf(
                    (FrameworkElement)fixture.Window.FindName("BasicCloseTerminalButton")),
                "Basic terminal close must be reachable in its workspace rather than the native caption area.");
            Check(basic.IsChecked == true && advanced.IsChecked == false &&
                ((FrameworkElement)fixture.Window.FindName("TerminalLayoutSelector")).Visibility == Visibility.Collapsed &&
                ((Button)fixture.Window.FindName("NewTerminalButton")).Visibility == Visibility.Collapsed,
                "Basic UI must show the selected mode and suppress extra-session/split controls.");
            AssertCaptionFits(fixture.Window);
            Check((int)Field(fixture.Window, "_splitCapacity")! == 1, "Basic must use one slot.");
            Call(fixture.Window, "ChangeTerminalLayout", 8, false);
            Check((int)Field(fixture.Window, "_splitCapacity")! == 1, "A direct layout call bypassed Basic mode.");
            advanced.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!Basic(fixture.Window) && basic.IsChecked == false && advanced.IsChecked == true &&
                ((FrameworkElement)fixture.Window.FindName("TerminalLayoutSelector")).Visibility == Visibility.Visible,
                "The Advanced mode button did not update the actual mode/UI.");
            AssertCaptionFits(fixture.Window);
            Call(fixture.Window, "ChangeTerminalLayout", 6, false);
            var layout = (TerminalSlotLayout)Field(fixture.Window, "_terminalSlotLayout")!;
            layout.SetCapacity(6);
            string hidden = fixture.Project("hidden-slot");
            layout.Assign(hidden, 5);
            Check((bool)Call(fixture.Window, "SetWorkMode", true)!, "Switching an empty app to Basic failed.");
            Check((int)Field(fixture.Window, "_splitCapacity")! == 6 && layout.Slots[5] == hidden &&
                ((Grid)fixture.Window.FindName("BasicWorkspace")).Visibility == Visibility.Visible &&
                ((Grid)fixture.Window.FindName("WorkspaceGrid")).Visibility == Visibility.Hidden,
                "Basic must render one column while preserving advanced capacity and hidden slot identities.");
            Check((bool)Call(fixture.Window, "SetWorkMode", false)!, "Returning to Advanced failed.");
            Check((int)Field(fixture.Window, "_splitCapacity")! == 6 && layout.Slots[5] == hidden,
                "Advanced must restore the previous split count and hidden slots.");
            // 분할 is one button; its window offers the seven splits (1 to 6 and 8) as drawings (no numbers), five is three over
            // two wider cells and eight is 4×2.
            // The skill store is only for the master accounts (MASTER_USERS): another account does not see its button.
            var storeUsers = typeof(MainWindow).Assembly.GetType("TerminalNotes.SkillStoreUsers")!;
            var storeOverride = storeUsers.GetField("AccountOverride", BindingFlags.Static | BindingFlags.NonPublic)!;
            var listed = (string[]?)storeUsers.GetField("AllowedUsers", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
            bool restricted = listed is { Length: > 0 } && !listed.Contains("*");
            var storeButton = (Button)fixture.Window.FindName("SkillStoreButton");
            var pluginButton = (Button)fixture.Window.FindName("PluginStoreButton");
            storeOverride.SetValue(null, (Func<string>)(() => @"DOMAIN\not-a-store-user"));
            Call(fixture.Window, "UpdateWorkModeControls");
            Check(storeButton.Visibility == (restricted ? Visibility.Collapsed : Visibility.Visible) && pluginButton.Visibility == Visibility.Collapsed,
                "The skill store button did not follow MASTER_USERS, or the hidden plugin button showed.");
            if (restricted)
            {
                storeOverride.SetValue(null, (Func<string>)(() => @"DOMAIN\" + listed![0]));
                Call(fixture.Window, "UpdateWorkModeControls");
                Check(storeButton.Visibility == Visibility.Visible && pluginButton.Visibility == Visibility.Collapsed, "A listed account did not see the skill store button, or saw the hidden plugin button.");
            }
            storeOverride.SetValue(null, null);
            Call(fixture.Window, "UpdateWorkModeControls");
            Check(fixture.Window.FindName("Layout1") is null && fixture.Window.FindName("TerminalLayoutButton") is Button,
                "The split must be one 분할 button.");
            var pickerType = typeof(MainWindow).Assembly.GetType("TerminalNotes.TerminalLayoutPicker")!;
            var picker = (Window)Activator.CreateInstance(pickerType, BindingFlags.Instance | BindingFlags.NonPublic, null, [6], null)!;
            var choices = ((IEnumerable<Button>)pickerType.GetProperty("Choices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker)!).ToList();
            int[] offered = [1, 2, 3, 4, 5, 6, 8];
            Check(choices.Count == offered.Length && choices.Select((choice, index) => choice.Content is Grid drawing &&
                    drawing.Children.OfType<Border>().Count() == offered[index] && !drawing.Children.OfType<TextBlock>().Any()).All(drawn => drawn),
                "The split window must draw the seven layouts, one to six and eight cells, without numbers.");
            picker.ShowActivated = false;
            picker.WindowStartupLocation = WindowStartupLocation.Manual;
            picker.Left = picker.Top = -20000;
            picker.Dispatcher.BeginInvoke(() => choices[4].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            Check(picker.ShowDialog() == true && (int?)pickerType.GetProperty("Chosen", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker) == 5,
                "Clicking a drawing did not choose that split.");
            Call(fixture.Window, "ChangeTerminalLayout", 5, false);
            var deck = (Grid)Field(fixture.Window, "_taskDeck")!;
            var cells = deck.Children.OfType<Border>().Where(cell => cell.AllowDrop && cell.Visibility == Visibility.Visible).ToList();
            Check((int)Field(fixture.Window, "_splitCapacity")! == 5 && deck.ColumnDefinitions.Count == 6 && deck.RowDefinitions.Count == 2 &&
                cells.Count == 5 && cells.Take(3).All(cell => Grid.GetRow(cell) == 0 && Grid.GetColumnSpan(cell) == 2) &&
                cells.Skip(3).All(cell => Grid.GetRow(cell) == 1 && Grid.GetColumnSpan(cell) == 3) && Grid.GetColumn(cells[4]) == 3 &&
                ((Border)fixture.Window.FindName("TerminalLayoutIcon")).Child is Grid icon && icon.Children.Count == 5,
                "Five must be three cells on top of two wider ones, and the 분할 button must draw it.");
            var eightPicker = (Window)Activator.CreateInstance(pickerType, BindingFlags.Instance | BindingFlags.NonPublic, null, [6], null)!;
            var eightChoices = ((IEnumerable<Button>)pickerType.GetProperty("Choices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(eightPicker)!).ToList();
            eightPicker.ShowActivated = false;
            eightPicker.WindowStartupLocation = WindowStartupLocation.Manual;
            eightPicker.Left = eightPicker.Top = -20000;
            eightPicker.Dispatcher.BeginInvoke(() => eightChoices[6].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            Check(eightPicker.ShowDialog() == true && (int?)pickerType.GetProperty("Chosen", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(eightPicker) == 8,
                "Clicking the last drawing did not choose eight.");
            Call(fixture.Window, "ChangeTerminalLayout", 8, false);
            cells = deck.Children.OfType<Border>().Where(cell => cell.AllowDrop && cell.Visibility == Visibility.Visible).ToList();
            Check((int)Field(fixture.Window, "_splitCapacity")! == 8 && deck.ColumnDefinitions.Count == 4 && deck.RowDefinitions.Count == 2 &&
                cells.Count == 8 && cells.Select((cell, slot) => Grid.GetRow(cell) == slot / 4 && Grid.GetColumn(cell) == slot % 4 &&
                    Grid.GetColumnSpan(cell) == 1).All(placed => placed) &&
                ((Border)fixture.Window.FindName("TerminalLayoutIcon")).Child is Grid eightIcon && eightIcon.Children.Count == 8,
                "Eight must be four cells over four, and the 분할 button must draw it.");
            Call(fixture.Window, "ChangeTerminalLayout", 7, false);
            Check((int)Field(fixture.Window, "_splitCapacity")! == 8, "Seven cells must use the eight split.");
            Call(fixture.Window, "ChangeTerminalLayout", 5, false);
            // Previews: the window of drawings (8 chosen) and the 분할 button.
            var shown = (Window)Activator.CreateInstance(pickerType, BindingFlags.Instance | BindingFlags.NonPublic, null, [8], null)!;
            SavePreview((FrameworkElement)shown.Content, "split-picker.png");
            SavePreview((FrameworkElement)fixture.Window.FindName("TerminalLayoutSelector"), "split-button.png", 5);
            SavePreview((FrameworkElement)fixture.Window.FindName("TerminalLayoutIcon"), "split-icon.png", 12);
            // 분할 · 플러그인 · 스킬스토어 share one bordered look, apart from the window's caption buttons.
            var layoutButton = (Button)fixture.Window.FindName("TerminalLayoutButton");
            Check(pluginButton.Style == layoutButton.Style && storeButton.Style == layoutButton.Style && layoutButton.BorderThickness.Left >= 1 &&
                layoutButton.BorderBrush is System.Windows.Media.SolidColorBrush { Color.A: > 0 } &&
                ((Button)fixture.Window.FindName("WorkSettingsButton")).Style != layoutButton.Style,
                "분할, 플러그인 and 스킬 must share the bordered tool button look.");
            if (restricted) storeOverride.SetValue(null, (Func<string>)(() => @"DOMAIN\" + listed![0]));
            Call(fixture.Window, "UpdateWorkModeControls");
            SavePreview((FrameworkElement)((FrameworkElement)fixture.Window.FindName("CommandBar")).Parent, "title-tools.png", 2);
            // 공지 is a bordered button like 분할; beside it, as plain text, this PC's CPU · 메모리 · GPU, read every 2 seconds while the
            // window shows (this test window is never shown, so it is let through).
            var announcement = (Button)fixture.Window.FindName("AnnouncementButton");
            Check(announcement.Style == layoutButton.Style, "공지 must have the bordered tool button look.");
            SetField(fixture.Window, "_systemMeterShown", true);
            var meter = (FrameworkElement)fixture.Window.FindName("SystemMeter");
            for (var deadline = DateTime.UtcNow.AddSeconds(15); Field(fixture.Window, "_systemMeterRead") is not true && DateTime.UtcNow < deadline;)
            {
                // This part runs synchronously: let the dispatcher take the meter's readings for a moment.
                var frame = new System.Windows.Threading.DispatcherFrame();
                var pause = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(200), System.Windows.Threading.DispatcherPriority.Background,
                    (sender, _) => { ((System.Windows.Threading.DispatcherTimer)sender!).Stop(); frame.Continue = false; }, fixture.Window.Dispatcher);
                pause.Start();
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
            string meterText = (string)typeof(MainWindow).GetProperty("SystemMeterText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Window)!;
            Check(System.Text.RegularExpressions.Regex.IsMatch(meterText, @"^CPU \d{1,3}%( · GPU \d{1,3}%)? · 메모리 \d{1,3}%$"), "The PC meter shows: " + meterText);
            Console.WriteLine("PC meter: " + meterText);
            // The title bar laid out at a width by hand (this window is never shown): wide, the meter is beside 공지; narrow, it gives
            // way rather than cutting 공지.
            var titleBar = (FrameworkElement)fixture.Window.FindName("WindowTitleBar");
            bool MeterShownAt(double width)
            {
                for (int pass = 0; pass < 3; pass++)
                {
                    titleBar.Measure(new Size(width, 36));
                    titleBar.Arrange(new Rect(0, 0, width, 36));
                    if (pass < 2) Call(fixture.Window, "FitSystemMeter");
                }
                return meter.Visibility == Visibility.Visible;
            }
            Check(MeterShownAt(1500), "The PC meter did not show in a wide window.");
            SavePreview(titleBar, "title-bar.png", 1.5, arrange: false);
            Check(!MeterShownAt(900), "The PC meter did not give way in a narrow window.");
            SavePreview(titleBar, "title-bar-narrow.png", 1.5, arrange: false);
            storeOverride.SetValue(null, null);
            Call(fixture.Window, "UpdateWorkModeControls");
            Call(fixture.Window, "ChangeTerminalLayout", 6, false);
            Call(fixture.Window, "SaveSettings");
            using var json = JsonDocument.Parse(File.ReadAllText(fixture.SettingsPath));
            Check(json.RootElement.GetProperty("WorkMode").GetString() == "Advanced" &&
                json.RootElement.GetProperty("AdvancedTerminalCapacity").GetInt32() == 6, "Mode/layout choice was not persisted.");
            using var reopened = new Fixture(File.ReadAllText(fixture.SettingsPath));
            Check(!Basic(reopened.Window), "An explicit saved Advanced choice was lost on restart.");
        }
        using (var legacy = new Fixture("{\"Workspace\":\"\",\"Document\":null}"))
        {
            Check(!Basic(legacy.Window), "Existing settings without WorkMode must retain Advanced behavior.");
            Call(legacy.Window, "SetWorkMode", true);
            using var reopened = new Fixture(File.ReadAllText(legacy.SettingsPath));
            Check(Basic(reopened.Window), "An explicit saved Basic choice was lost on restart.");
        }
        // The app always opens on one cell, whatever split was saved (an earlier app's 8 too).
        foreach (int saved in new[] { 4, 8 })
            using (var reopened = new Fixture("{\"Workspace\":\"\",\"Document\":null,\"WorkMode\":\"Advanced\",\"AdvancedTerminalCapacity\":" + saved + "}"))
                Check((int)Field(reopened.Window, "_splitCapacity")! == 1, $"A saved {saved}-way split did not open as one cell.");
    }

    private static void VerifyRefusalPreservesSessions()
    {
        using var fixture = new Fixture();
        Call(fixture.Window, "SetWorkMode", false);
        Call(fixture.Window, "ChangeTerminalLayout", 4, false);
        string project = fixture.Project("same-project");
        var first = fixture.ExistingTerminal(project, task: true);
        var hidden = fixture.ExistingTerminal(project, hidden: true);
        object firstState = first.Tag, hiddenState = hidden.Tag;
        string draft = Property<string>(hiddenState, "InputDraft");
        fixture.Tabs.SelectedItem = first;
        Call(fixture.Window, "SaveSettings");
        byte[] settings = File.ReadAllBytes(fixture.SettingsPath);
        using var popup = new ModalDriver();
        Check(!(bool)Call(fixture.Window, "SetWorkMode", true)!, "Two actual sessions in the same project must refuse Basic.");
        popup.AssertShown();
        Check(!Basic(fixture.Window) && (int)Field(fixture.Window, "_splitCapacity")! == 4,
            "A refused switch changed the mode/layout.");
        Check(fixture.Tabs.Items.Count == 2 && ReferenceEquals(first.Tag, firstState) && ReferenceEquals(hidden.Tag, hiddenState) &&
            Property<string>(hiddenState, "InputDraft") == draft && hidden.Visibility == Visibility.Collapsed,
            "A refused switch closed/replaced a session or changed its draft/visibility.");
        Check(settings.SequenceEqual(File.ReadAllBytes(fixture.SettingsPath)), "A refused switch rewrote persisted settings.");
    }

    private static async Task VerifySessionAdmissionAsync()
    {
        foreach (string ownership in new[] { "manual", "task", "hidden", "detached" })
        {
            using var fixture = new Fixture();
            string project = fixture.Project(ownership), other = fixture.Project("other");
            var tab = fixture.ExistingTerminal(project, task: ownership is "task" or "detached", hidden: ownership == "hidden");
            if (ownership == "detached") fixture.Tabs.Items.Remove(tab);
            Check(!Admit(fixture.Window, other), ownership + " session did not occupy the single Basic work slot.");
            Check(!Admit(fixture.Window, project, reuse: false), ownership + " session allowed a second session in its project.");
            if (ownership is "task" or "detached")
                Check(Admit(fixture.Window, project), ownership + " session could not be reused.");
            Call(fixture.Window, "SetWorkMode", false);
            Check(Admit(fixture.Window, other, reuse: false), "Advanced unexpectedly applied the Basic session limit.");
        }
        foreach (string ownership in new[] { "manual", "task" })
        {
            // Plan mode: advanced UI, one work terminal; switching back restores the advanced split.
            using var fixture = new Fixture();
            Call(fixture.Window, "SetWorkMode", false);
            fixture.Window.GetType().GetField("_splitCapacity", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fixture.Window, 4);
            string project = fixture.Project(ownership), other = fixture.Project("other");
            fixture.ExistingTerminal(project, task: ownership == "task");
            var planMode = Enum.Parse(typeof(MainWindow).GetNestedType("WorkMode", BindingFlags.NonPublic)!, "Plan");
            Check((bool)Call(fixture.Window, "ApplyWorkMode", planMode)!, "Switching to Plan with one work terminal failed.");
            Check(!Basic(fixture.Window) && (int)Field(fixture.Window, "_splitCapacity")! == 1,
                "Plan mode must keep the advanced UI and show a single terminal slot.");
            Check(!Admit(fixture.Window, other), ownership + " session did not occupy the single Plan work slot.");
            if (Field(fixture.Window, "_archivedTasksTab") is RadioButton archiveTab)
                Check(archiveTab.Visibility == Visibility.Collapsed, "Plan mode must hide the archive tab.");
            if (ownership == "task") Check(Admit(fixture.Window, project), "Plan mode could not reuse its own task session.");
            Check((bool)Call(fixture.Window, "SetWorkMode", false)!, "Returning from Plan to Advanced failed.");
            Check((int)Field(fixture.Window, "_splitCapacity")! == 4 && Admit(fixture.Window, other, reuse: false),
                "Leaving Plan mode did not restore the advanced split or lift the session limit.");
        }
        using (var fixture = new Fixture())
        {
            string project = fixture.Project("work");
            var planning = new CancellationTokenSource();
            string plannerProject = fixture.Project("planner");
            var dialog = (Window)Activator.CreateInstance(typeof(MainWindow).Assembly.GetType("TerminalNotes.PlanMakerDialog")!,
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { fixture.Window, plannerProject, "# Unsaved planning draft", planning.Token }, null)!;
            var planningPane = Property<TerminalPane>(dialog, "Pane");
            ((IDictionary)Field(fixture.Window, "_planMakers")!).Add(plannerProject, planning);
            ((IDictionary)Field(fixture.Window, "_planMakerDialogs")!).Add(plannerProject, dialog);
            try
            {
                Check(Admit(fixture.Window, project, reuse: false), "PlanMaker incorrectly occupied the Basic work slot.");
                fixture.ExistingTerminal(project, task: true);
                Check((bool)Call(fixture.Window, "SetWorkMode", true)!, "PlanMaker and one work session must coexist in Basic.");
                Check((int)Call(fixture.Window, "WorkSessionCount")! == 1 && !planningPane.IsRunning,
                    "A separate real PlanMaker pane incorrectly counted as a work terminal.");
            }
            finally
            {
                ((IDictionary)Field(fixture.Window, "_planMakers")!).Clear();
                ((IDictionary)Field(fixture.Window, "_planMakerDialogs")!).Clear();
                await (Task)Call(dialog, "CleanUpAsync")!;
                dialog.Close(); planning.Dispose();
            }
        }
    }

    private static void VerifyPendingAndExternalAdmission()
    {
        foreach (string pending in new[] { "_startingProjectTasks", "_pendingProjectInputs", "_externalTerminalWrites" })
        {
            using var fixture = new Fixture();
            string project = fixture.Project("pending"), other = fixture.Project("other");
            var set = (ISet<string>)Field(fixture.Window, pending)!;
            set.Add(project);
            Check(!Admit(fixture.Window, other), pending + " did not reserve the Basic work slot.");
            set.Clear();
            Check(Admit(fixture.Window, other), "A completed pending operation failed to release its slot.");
        }
        using (var fixture = new Fixture())
        {
            string project = fixture.Project("reserved"), other = fixture.Project("other");
            using (var admission = (IDisposable)Call(fixture.Window, "AcquireWorkSessionAdmission", project)!)
            {
                Check(!Admit(fixture.Window, other), "An in-flight admission failed to reserve the work slot.");
                using var nested = (IDisposable)Call(fixture.Window, "AcquireWorkSessionAdmission", project)!;
                Check(!Admit(fixture.Window, other), "A nested same-project admission released its reservation.");
            }
            Check(Admit(fixture.Window, other), "Disposing the final admission failed to release its reservation.");
            string leasePath = (string)typeof(ExternalTerminalLauncher).GetMethod("LeasePath", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { project, "ses_work_mode_fixture" })!;
            Directory.CreateDirectory(Path.GetDirectoryName(leasePath)!);
            File.WriteAllText(leasePath, "stale fixture lease");
            Check(Admit(fixture.Window, other), "A stale unlocked CMD lease incorrectly occupied the work slot.");
            using (var lease = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Check(!Admit(fixture.Window, other), "An active external CMD session was not counted globally.");
                Check(!Admit(fixture.Window, project), "Basic must not reopen an externally owned session in-app.");
            }
            Check(Admit(fixture.Window, other), "A released external CMD lease kept blocking Basic.");
        }
        using (var fixture = new Fixture())
        {
            string project = fixture.Project("same-project-new-session");
            Call(fixture.Window, "SetWorkMode", false);
            fixture.ExistingTerminal(project, task: true);
            using (var newSession = (IDisposable)Call(fixture.Window, "AcquireNewWorkSessionAdmission", project)!)
            {
                using var popup = new ModalDriver();
                Check(!(bool)Call(fixture.Window, "SetWorkMode", true)!, "A pending explicit new session in the same project was ignored.");
                popup.AssertShown();
            }
            Check((bool)Call(fixture.Window, "SetWorkMode", true)!, "Releasing a pending explicit new session did not allow Basic.");
            string saveOnly = fixture.Project("save-only");
            ((ISet<string>)Field(fixture.Window, "_pendingProjectInputs")!).Add(saveOnly);
            ((ISet<string>)Field(fixture.Window, "_savingOnlyProjectInputs")!).Add(saveOnly);
            Check(Admit(fixture.Window, project), "A plan-only save incorrectly reserved another work-session slot.");
        }
    }

    private static void VerifyBlockedFactories()
    {
        using var fixture = new Fixture();
        string existing = fixture.Project("existing"), blocked = fixture.Project("blocked");
        fixture.ExistingTerminal(existing, task: true);
        var launchType = typeof(MainWindow).GetNestedType("ResolvedTerminalLaunch", BindingFlags.NonPublic)!;
        object launch = Activator.CreateInstance(launchType, new object?[] { false, null, "Blocked", blocked, TerminalTool.PowerShell })!;
        Exception? failure = Capture(() => Call(fixture.Window, "CreateTerminalTab", 2, launch, "Blocked"));
        Check(failure is InvalidOperationException && fixture.Tabs.Items.Count == 1, "The direct factory bypassed the Basic work-session limit.");
        Check(!Directory.Exists(Path.Combine(blocked, "terminal-notes")), "A blocked factory wrote a plan before admission.");
        Check(!((IDictionary)Field(fixture.Window, "_automationRuntimes")!).Contains(blocked),
            "A blocked factory created task runtime state before admission.");
    }

    private static void VerifyRetiringCleanupAdmission()
    {
        using var fixture = new Fixture();
        string project = fixture.Project("retiring"), other = fixture.Project("next-project");
        var tab = fixture.ExistingTerminal(project);
        var pane = Property<TerminalPane>(tab.Tag, "Pane");
        fixture.Tabs.Items.Remove(tab);
        pane.Dispose();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retiring = (IDictionary)Field(fixture.Window, "_retiringWorkPanes")!;
        retiring.Add(pane, cleanup.Task);
        Check(!Admit(fixture.Window, other) && (int)Call(fixture.Window, "WorkSessionCount")! == 1,
            "Disposing/removing a pane released Basic admission before its exact process cleanup completed.");
        Check(retiring.Contains(pane), "Pending cleanup tracking was discarded prematurely.");
        cleanup.SetResult();
        Check(Admit(fixture.Window, other) && (int)Call(fixture.Window, "WorkSessionCount")! == 0 && !retiring.Contains(pane),
            "Successful process cleanup failed to release and remove the retired pane.");

        var failed = Task.FromException(new IOException("Synthetic owned-process cleanup failure"));
        retiring.Add(pane, failed);
        Check(!Admit(fixture.Window, other) && retiring.Contains(pane),
            "Failed process cleanup incorrectly released the Basic work-session slot.");
        retiring.Clear();
    }

    private static async Task VerifyBasicVisibleSessionAsync()
    {
        foreach (bool task in new[] { true, false })
        {
            using var fixture = new Fixture();
            Call(fixture.Window, "SetWorkMode", false);
            Call(fixture.Window, "ChangeTerminalLayout", 6, false);
            string current = fixture.Project(task ? "task-session" : "independent-session");
            string selected = fixture.Project("selected-other");
            var tab = fixture.ExistingTerminal(current, task: task);
            var pane = Property<TerminalPane>(tab.Tag, "Pane");
            string draft = Property<string>(tab.Tag, "InputDraft");
            ContentControl? taskHost = null;
            if (task)
            {
                taskHost = new ContentControl { Content = pane };
                ((IDictionary)Field(fixture.Window, "_taskTerminalHosts")!).Add(tab, taskHost);
            }
            else SetProperty(tab.Tag, "IsIndependent", true);
            var layout = (TerminalSlotLayout)Field(fixture.Window, "_terminalSlotLayout")!;
            layout.SetCapacity(6); layout.Assign(current, 5);
            SetField(fixture.Window, "_selectedProjectPath", selected);
            Call(fixture.Window, "LoadTaskSidebarSettings", true, 300d);
            Call(fixture.Window, "LoadTaskPlanWidthSetting", (double?)0.61);
            Call(fixture.Window, "SaveSettings");
            using var before = JsonDocument.Parse(File.ReadAllText(fixture.SettingsPath));
            Check((bool)Call(fixture.Window, "SetWorkMode", true)!, "A single existing session could not enter Basic.");
            var basicHost = (ContentControl)fixture.Window.FindName("BasicTerminalContent");
            Check(ReferenceEquals(basicHost.Content, pane) && pane.Parent is not null &&
                ((Grid)fixture.Window.FindName("WorkspaceGrid")).Visibility == Visibility.Hidden &&
                ((FrameworkElement)fixture.Window.FindName("BasicEmptyState")).Visibility == Visibility.Collapsed,
                "Basic must display the exact existing native pane in its full-width host.");
            Check(taskHost is null || taskHost.Content is null, "The Advanced task host retained the Basic pane.");
            AssertBasicWorkspaceFolderOpening(fixture.Window, current);
            Check(ReferenceEquals(fixture.Tabs.SelectedItem, tab) && Property<string>(tab.Tag, "InputDraft") == draft &&
                (string?)Field(fixture.Window, "_selectedProjectPath") == selected && layout.Slots[5] == current,
                "Entering Basic changed the selection, input draft or Advanced slot identity.");
            Call(fixture.Window, "SelectTerminal", tab, false);
            Call(fixture.Window, "UpdateDispatcher");
            Check(ReferenceEquals(basicHost.Content, pane) && (string?)Field(fixture.Window, "_selectedProjectPath") == selected,
                "Terminal activity/activation changed Basic ownership or the Advanced project selection.");
            await VerifyBasicReconnectAsync(fixture, tab, pane);
            Call(fixture.Window, "SaveSettings");
            using var basic = JsonDocument.Parse(File.ReadAllText(fixture.SettingsPath));
            foreach (string setting in new[] { "TaskSidebarCollapsed", "TaskSidebarWidth", "TaskPlanWidth" })
                Check(before.RootElement.GetProperty(setting).GetRawText() == basic.RootElement.GetProperty(setting).GetRawText(),
                    "Saving Basic overwrote the Advanced " + setting + " preference.");
            Check((bool)Call(fixture.Window, "SetWorkMode", false)!, "Returning from Basic failed.");
            Check(basicHost.Content is null && (taskHost is null || ReferenceEquals(taskHost.Content, pane)) &&
                Property<string>(tab.Tag, "InputDraft") == draft && layout.Slots[5] == current &&
                (int)Field(fixture.Window, "_splitCapacity")! == 6 && !pane.IsRunning,
                "The Basic/Advanced roundtrip replaced, started or lost the existing pane/draft/layout.");
            if (task) Check(ReferenceEquals(Field(Call(fixture.Window, "RuntimeFor", current)!, "SingleTerminal"), tab),
                "The Basic roundtrip changed the task's session ownership.");
        }
    }

    private static async Task VerifyBasicReconnectAsync(Fixture fixture, TabItem tab, TerminalPane pane)
    {
        var window = fixture.Window;
        var host = (ContentControl)window.FindName("BasicTerminalContent");
        var reconnect = (Button)window.FindName("BasicReconnectTerminalButton");
        string draft = Property<string>(tab.Tag, "InputDraft");
        Check(reconnect.Visibility == Visibility.Collapsed && !reconnect.IsEnabled,
            "Healthy Basic panes must preserve the original three actions without a reconnect gap.");
        // Only borrow a read-only current-process reference for IsRunning; detach it before
        // disposing the fake host. No process, ConPTY, server or native request is launched.
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var backend = new OpenCodeHeadlessHost(new Uri("http://127.0.0.1:43129/"));
        bool nativeTui = (bool)Field(pane, "_usesNativeOpenCodeTui")!;
        object? previousBackend = Field(pane, "_headlessOpenCodeHost");
        SetField(pane, "_usesNativeOpenCodeTui", true);
        SetField(pane, "_tuiConnectionFailed", true);
        SetField(pane, "_headlessSessionId", "ses_basic_existing");
        SetField(pane, "_headlessOpenCodeHost", backend);
        int retries = 0;
        Func<TerminalPane, Task<bool>> retry = target =>
        {
            Check(ReferenceEquals(target, pane), "Basic retried a pane other than its exact visible session.");
            retries++;
            return Task.FromResult(true);
        };
        async Task Reject(string reason)
        {
            int before = retries;
            Call(window, "UpdateBasicTerminalPresentation");
            Check(!reconnect.IsEnabled, "Basic enabled a blocked reconnect: " + reason);
            await (Task)Call(window, "RetryBasicTerminalAsync", retry)!;
            Check(retries == before, "A stale reconnect handler reached the retry operation: " + reason);
        }
        try
        {
            await Reject("retained backend unavailable");
            SetField(backend, "_process", process);
            Call(window, "UpdateDispatcher");
            Check(Property<bool>(pane, "CanRetryError") && reconnect.Visibility == Visibility.Visible && reconnect.IsEnabled &&
                (string?)reconnect.ToolTip == "재연결", "The hosted failed TUI must expose the same-pane reconnect action.");
            AssertCaptionFits(window);
            SetField(pane, "_tuiConnecting", true);
            await Reject("already connecting");
            Check(reconnect.Visibility == Visibility.Visible && ((string)reconnect.ToolTip).Contains("재연결 중"),
                "An in-progress reconnect must retain its icon and explain why it is disabled.");
            SetField(pane, "_tuiConnecting", false);
            SetField(pane, "<IsUserInputBlocked>k__BackingField", true);
            await Reject("pane input blocked");
            SetField(pane, "<IsUserInputBlocked>k__BackingField", false);
            foreach (string flag in new[] { "_closing", "_closePending" })
            {
                SetField(window, flag, true);
                await Reject(flag);
                SetField(window, flag, false);
            }
            foreach (string name in new[] { "_terminalCloseOperations", "_terminalLifecycleOperations" })
            {
                var set = (ISet<TabItem>)Field(window, name)!;
                set.Add(tab);
                await Reject(name);
                set.Remove(tab);
            }
            var retiring = (IDictionary)Field(window, "_retiringWorkPanes")!;
            retiring.Add(pane, new TaskCompletionSource().Task);
            await Reject("pane cleanup");
            retiring.Remove(pane);
            foreach (string name in new[] { "_externalTerminalWrites", "_projectTaskControlWrites", "_projectTaskOptionWrites", "_stoppingProjectTasks" })
            {
                var set = (ISet<string>)Field(window, name)!;
                set.Add(pane.WorkingDirectory);
                await Reject(name);
                set.Remove(pane.WorkingDirectory);
            }
            // Exercise click-time validation without first allowing presentation to repair stale ownership.
            host.Content = null;
            await (Task)Call(window, "RetryBasicTerminalAsync", retry)!;
            Check(retries == 0, "A detached visible pane was retried by a stale reconnect handler.");
            SetField(window, "_basicHostedTab", null);
            await (Task)Call(window, "RetryBasicTerminalAsync", retry)!;
            Check(retries == 0, "A stale hosted-tab identity was retried.");
            fixture.Tabs.SelectedItem = null;
            await (Task)Call(window, "RetryBasicTerminalAsync", retry)!;
            Check(retries == 0, "A stale selected-tab identity was retried.");
            fixture.Tabs.SelectedItem = tab;
            Call(window, "UpdateBasicTerminalPresentation");
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<TerminalPane, Task<bool>> pendingRetry = target =>
            {
                Check(ReferenceEquals(target, pane), "An asynchronous retry changed the pane identity.");
                retries++;
                return completion.Task;
            };
            Task pending = (Task)Call(window, "RetryBasicTerminalAsync", pendingRetry)!;
            Check(!reconnect.IsEnabled && reconnect.Visibility == Visibility.Visible,
                "The reconnect button remained enabled while its operation was pending.");
            await (Task)Call(window, "RetryBasicTerminalAsync", retry)!;
            Check(retries == 1, "A double click dispatched two reconnect operations.");
            completion.SetResult(true);
            await pending;
            Check(reconnect.IsEnabled && ReferenceEquals(host.Content, pane) && ReferenceEquals(fixture.Tabs.SelectedItem, tab) &&
                fixture.Tabs.Items.Count == 1 && Property<string>(tab.Tag, "InputDraft") == draft &&
                (string?)Field(pane, "_headlessSessionId") == "ses_basic_existing" && fixture.StartupChecks == 0,
                "Reconnect replaced the pane/session, lost a draft, created another terminal or reached startup.");
            Check((bool)Call(window, "SetWorkMode", false)!, "Reconnect fixture could not leave Basic.");
            await (Task)Call(window, "RetryBasicTerminalAsync", retry)!;
            Check(retries == 1 && reconnect.Visibility == Visibility.Collapsed && !reconnect.IsEnabled,
                "A handler from Basic retried after the mode changed.");
            Check((bool)Call(window, "SetWorkMode", true)!, "Reconnect fixture could not re-enter Basic.");
            SetField(pane, "_tuiConnectionFailed", false);
            Call(window, "UpdateDispatcher");
            Check(reconnect.Visibility == Visibility.Collapsed && !reconnect.IsEnabled,
                "A recovered Basic pane did not collapse the reconnect action.");
        }
        finally
        {
            SetField(backend, "_process", null);
            SetField(pane, "_headlessOpenCodeHost", previousBackend);
            SetField(pane, "_usesNativeOpenCodeTui", nativeTui);
            SetField(pane, "_tuiConnectionFailed", false);
            SetField(pane, "_tuiConnecting", false);
            SetField(pane, "_headlessSessionId", null);
        }
    }

    private static async Task VerifyPlanFreeBasicStartAsync()
    {
        using var fixture = new Fixture();
        string first = fixture.Project("simple-folder"), next = fixture.Project("next-folder");
        string existingPlan = Path.Combine(first, "plan.md");
        byte[] original = System.Text.Encoding.UTF8.GetBytes("# Existing user plan\nNever auto-run or rewrite.\n");
        File.WriteAllBytes(existingPlan, original);
        string[] files = Directory.GetFiles(first, "*", SearchOption.AllDirectories);
        var bindings = ((IList)Field(fixture.Window, "_documentBindings")!).Count;
        string? selected = (string?)Field(fixture.Window, "_selectedProjectPath");
        Check((bool)Call(fixture.Window, "SelectBasicFolder", first)!, "Basic folder selection failed.");
        Check(files.SequenceEqual(Directory.GetFiles(first, "*", SearchOption.AllDirectories)) &&
            ((IList)Field(fixture.Window, "_documentBindings")!).Count == bindings &&
            (string?)Field(fixture.Window, "_selectedProjectPath") == selected && fixture.StartupChecks == 0,
            "Basic folder selection created a plan/binding, changed Advanced selection or started OpenCode.");
        AssertBasicWorkspaceFolderOpening(fixture.Window, fixture.Root);
        Check(fixture.Tabs.Items.Count == 0 && fixture.StartupChecks == 0,
            "Opening the idle workspace folder must not create a terminal or contact OpenCode.");
        SetField(fixture.Window, "_autoStartCodex", true);
        SetField(fixture.Window, "_openCodeReady", true);
        Call(fixture.Window, "StartBasicTerminal");
        var tab = fixture.Tabs.Items.Cast<TabItem>().Single();
        var pane = Property<TerminalPane>(tab.Tag, "Pane");
        fixture.TrackPane(pane);
        Check(Property<bool>(tab.Tag, "IsIndependent") && Property<string>(tab.Tag, "PlanDocumentPath") == "" &&
            pane.WorkingDirectory == fixture.Root && fixture.Tabs.Items.Count == 1 &&
            Field(pane, "_autoResume") is null && Field(pane, "_openCodeProjectContext") is null &&
            pane.StartupCommand is "opencode" or "opencode --auto" && !pane.IsRunning,
            "Basic must create one native OpenCode pane in the configured workspace despite a legacy task selection, without a plan, project context or auto-resume workflow.");
        Check(ReferenceEquals(((ContentControl)fixture.Window.FindName("BasicTerminalContent")).Content, pane) &&
            files.SequenceEqual(Directory.GetFiles(first, "*", SearchOption.AllDirectories)) && original.SequenceEqual(File.ReadAllBytes(existingPlan)) &&
            !Directory.Exists(Path.Combine(first, "terminal-notes")) &&
            !File.Exists(Path.Combine(fixture.Root, "plan.md")) && !Directory.Exists(Path.Combine(fixture.Root, "terminal-notes")),
            "Basic start hid the pane or wrote a project plan/workflow file.");
        string draft = Property<string>(tab.Tag, "InputDraft");
        Check((bool)Call(fixture.Window, "SelectBasicFolder", next)! &&
            (string?)Field(fixture.Window, "_basicSelectedFolder") == next &&
            ReferenceEquals(((ContentControl)fixture.Window.FindName("BasicTerminalContent")).Content, pane) &&
            Property<string>(tab.Tag, "InputDraft") == draft &&
            pane.WorkingDirectory == fixture.Root && fixture.Tabs.Items.Count == 1 &&
            ((TextBlock)fixture.Window.FindName("BasicFolderLabel")).Text == fixture.Root &&
            (string?)((TextBlock)fixture.Window.FindName("BasicFolderLabel")).ToolTip == fixture.Root &&
            !((Button)fixture.Window.FindName("BasicStartTerminalButton")).IsEnabled,
            "A dormant task selection changed the active workspace pane, folder label or draft, or allowed a second terminal.");
        bool notesHidden = (bool)Field(fixture.Window, "_notesHidden")!;
        Call(fixture.Window, "HandleShortcut", "m", true, pane);
        Check((bool)Field(fixture.Window, "_notesHidden")! == notesHidden &&
            ((Grid)fixture.Window.FindName("WorkspaceGrid")).Visibility == Visibility.Hidden,
            "The notes keyboard shortcut restored Advanced controls in Basic.");
        await (Task)Call(fixture.Window, "CloseTerminalAsync", tab, pane)!;
        Check(fixture.Tabs.Items.Count == 0 && (bool)Call(fixture.Window, "SelectBasicFolder", next)! &&
            ((Button)fixture.Window.FindName("BasicStartTerminalButton")).IsEnabled,
            "Closing the sole Basic terminal did not release folder selection/start.");
        Check(original.SequenceEqual(File.ReadAllBytes(existingPlan)), "Closing Basic changed an existing plan.");
        string escapedWorkspace = fixture.Project("workspace & 한글 'quoted' $draft [1]");
        SetField(fixture.Window, "_workspace", escapedWorkspace);
        Call(fixture.Window, "UpdateBasicTerminalPresentation");
        AssertBasicWorkspaceFolderOpening(fixture.Window, escapedWorkspace);
        foreach (string closingField in new[] { "_closing", "_closePending" })
        {
            bool launchedWhileClosing = false;
            SetField(fixture.Window, closingField, true);
            try
            {
                Call(fixture.Window, "OpenBasicWorkspaceFolder",
                    (Action<System.Diagnostics.ProcessStartInfo>)(_ => launchedWhileClosing = true));
                Check(!launchedWhileClosing, "A closing window must not launch the workspace folder: " + closingField);
            }
            finally { SetField(fixture.Window, closingField, false); }
        }
    }

    private static async Task VerifyBasicWorkspaceChangeAsync()
    {
        using var fixture = new Fixture();
        string idle = fixture.Project("changed workspace & 한글 'quoted' $draft [1]");
        string next = fixture.Project("next launch workspace");
        string plan = Path.Combine(idle, "plan.md");
        File.WriteAllText(plan, "# Existing plan\nKeep this saved plan unchanged.\n");
        byte[] planBytes = File.ReadAllBytes(plan);
        int scanGeneration = (int)Field(fixture.Window, "_basicTaskScanGeneration")!;
        int bindings = ((IList)Field(fixture.Window, "_documentBindings")!).Count;
        string? selected = (string?)Field(fixture.Window, "_selectedProjectPath");
        Check((bool)Call(fixture.Window, "ChangeBasicWorkspace", Path.Combine(idle, "..", Path.GetFileName(idle)))! &&
            (string?)Field(fixture.Window, "_workspace") == idle &&
            (string?)Field(fixture.Window, "_basicSelectedFolder") == idle &&
            ((TextBlock)fixture.Window.FindName("WorkspaceLabel")).Text == idle,
            "Changing the idle Basic workspace must normalize and display the configured full path.");
        AssertBasicWorkspaceFolderOpening(fixture.Window, idle);
        Check(fixture.Tabs.Items.Count == 0 && fixture.StartupChecks == 0 &&
            ((Button)fixture.Window.FindName("BasicStartTerminalButton")).IsEnabled &&
            ((TextBlock)fixture.Window.FindName("BasicActiveFolderLabel")).Text == "" &&
            (int)Field(fixture.Window, "_basicTaskScanGeneration")! == scanGeneration &&
            ((IList)Field(fixture.Window, "_documentBindings")!).Count == bindings &&
            (string?)Field(fixture.Window, "_selectedProjectPath") == selected,
            "An idle workspace change must not create a terminal, scan tasks or alter Advanced selection/bindings.");
        using (var settings = JsonDocument.Parse(File.ReadAllText(fixture.SettingsPath)))
            Check(settings.RootElement.GetProperty("Workspace").GetString() == idle &&
                settings.RootElement.GetProperty("CodexWorkspace").GetString() == idle &&
                settings.RootElement.GetProperty("BasicSelectedFolder").GetString() == idle,
                "The changed Basic workspace must persist as the next launch folder.");

        // No attention windows are allowed in this isolated invalid-input test.
        if (Field(fixture.Window, "_statusNotifications") is IDisposable notifications) notifications.Dispose();
        byte[] saved = File.ReadAllBytes(fixture.SettingsPath);
        using (var lockedSettings = new FileStream(fixture.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
            Check(!(bool)Call(fixture.Window, "ChangeBasicWorkspace", next)! &&
                (string?)Field(fixture.Window, "_workspace") == idle &&
                (string?)Field(fixture.Window, "_basicSelectedFolder") == idle &&
                ((TextBlock)fixture.Window.FindName("WorkspaceLabel")).Text == idle &&
                ((TextBlock)fixture.Window.FindName("BasicFolderLabel")).Text == idle,
                "A failed workspace settings save must retain the previous workspace, selection and labels.");
        Check(saved.SequenceEqual(File.ReadAllBytes(fixture.SettingsPath)),
            "A failed workspace settings save must preserve the previous settings file.");
        foreach (string invalid in new[] { "", "\0invalid", Path.Combine(fixture.Root, "missing-workspace") })
            Check(!(bool)Call(fixture.Window, "ChangeBasicWorkspace", invalid)! &&
                (string?)Field(fixture.Window, "_workspace") == idle &&
                (string?)Field(fixture.Window, "_basicSelectedFolder") == idle &&
                saved.SequenceEqual(File.ReadAllBytes(fixture.SettingsPath)) &&
                ((TextBlock)fixture.Window.FindName("BasicFolderLabel")).Text == idle,
                "Invalid or missing workspace input must preserve the folder, header and saved settings.");
        foreach (string guard in new[] { "_closing", "_closePending", "_openCodeStartupBusy" })
        {
            SetField(fixture.Window, guard, true);
            try
            {
                Call(fixture.Window, "UpdateBasicTerminalPresentation");
                Check(!((Button)fixture.Window.FindName("BasicChangeFolderButton")).IsEnabled &&
                    !(bool)Call(fixture.Window, "ChangeBasicWorkspace", next)! &&
                    (string?)Field(fixture.Window, "_workspace") == idle && saved.SequenceEqual(File.ReadAllBytes(fixture.SettingsPath)),
                    "Closing or startup preparation must reject workspace changes: " + guard);
            }
            finally { SetField(fixture.Window, guard, false); }
        }
        Call(fixture.Window, "SetWorkMode", false);
        Check(!(bool)Call(fixture.Window, "ChangeBasicWorkspace", next)! &&
            (string?)Field(fixture.Window, "_workspace") == idle,
            "The Basic workspace action must reject direct calls in Advanced mode.");
        Call(fixture.Window, "SetWorkMode", true);
        SetField(fixture.Window, "_autoStartCodex", true);
        SetField(fixture.Window, "_openCodeReady", true);
        Call(fixture.Window, "StartBasicTerminal");
        var tab = fixture.Tabs.Items.Cast<TabItem>().Single();
        var pane = Property<TerminalPane>(tab.Tag, "Pane");
        fixture.TrackPane(pane);
        SetProperty(tab.Tag, "InputDraft", "Keep this unsent draft across folder changes.");
        string draft = Property<string>(tab.Tag, "InputDraft");
        Check((bool)Call(fixture.Window, "ChangeBasicWorkspace", next)! &&
            (string?)Field(fixture.Window, "_workspace") == next && pane.WorkingDirectory == idle &&
            fixture.Tabs.Items.Count == 1 && ReferenceEquals(fixture.Tabs.SelectedItem, tab) &&
            ReferenceEquals(((ContentControl)fixture.Window.FindName("BasicTerminalContent")).Content, pane) &&
            Property<string>(tab.Tag, "InputDraft") == draft && !pane.IsRunning &&
            !((Button)fixture.Window.FindName("BasicStartTerminalButton")).IsEnabled,
            "Changing folders with an existing pane must preserve the pane, draft and single-session limit.");
        AssertBasicWorkspaceFolderOpening(fixture.Window, idle);
        var pendingLabel = (TextBlock)fixture.Window.FindName("BasicActiveFolderLabel");
        Check(pendingLabel.Text == "다음 실행 · " + next && (string?)pendingLabel.ToolTip == next &&
            ((TextBlock)fixture.Window.FindName("WorkspaceLabel")).Text == next,
            "An existing pane must keep its current path while the next launch folder is explicitly displayed.");
        using (var settings = JsonDocument.Parse(File.ReadAllText(fixture.SettingsPath)))
            Check(settings.RootElement.GetProperty("CodexWorkspace").GetString() == next,
                "A pending workspace change must persist without waiting for the current terminal to close.");
        await (Task)Call(fixture.Window, "CloseTerminalAsync", tab, pane)!;
        AssertBasicWorkspaceFolderOpening(fixture.Window, next);
        Check(pendingLabel.Text == "" && fixture.Tabs.Items.Count == 0,
            "Closing the existing terminal must promote the configured next folder to the idle header.");
        Call(fixture.Window, "StartBasicTerminal");
        var nextTab = fixture.Tabs.Items.Cast<TabItem>().Single();
        var nextPane = Property<TerminalPane>(nextTab.Tag, "Pane");
        fixture.TrackPane(nextPane);
        Check(nextPane.WorkingDirectory == next && !ReferenceEquals(nextPane, pane) && !nextPane.IsRunning &&
            Property<string>(nextTab.Tag, "PlanDocumentPath") == "" && fixture.StartupChecks == 0 &&
            planBytes.SequenceEqual(File.ReadAllBytes(plan)) && Directory.GetFileSystemEntries(idle).SequenceEqual(new[] { plan }) &&
            Directory.GetFileSystemEntries(next).Length == 0 &&
            (int)Field(fixture.Window, "_basicTaskScanGeneration")! == scanGeneration &&
            ((IList)Field(fixture.Window, "_documentBindings")!).Count == bindings,
            "The next explicit launch must use the changed folder without starting a process or creating/modifying a plan.");
    }

    private static void AssertBasicWorkspaceFolderOpening(MainWindow window, string expectedFolder)
    {
        var label = (TextBlock)window.FindName("BasicFolderLabel");
        Check(label.Text == expectedFolder && (string?)label.ToolTip == expectedFolder,
            "Basic must display the full configured or hosted terminal path, independent of dormant task selection.");
        System.Diagnostics.ProcessStartInfo? launch = null;
        Call(window, "OpenBasicWorkspaceFolder", (Action<System.Diagnostics.ProcessStartInfo>)(start => launch = start));
        Check(launch is not null && string.Equals(launch.FileName, "explorer.exe", StringComparison.OrdinalIgnoreCase) &&
            launch.Arguments.Length == 0 && launch.ArgumentList.SequenceEqual(new[] { expectedFolder }),
            "The folder action must pass the displayed folder to Explorer as one argument without shell interpolation.");
    }

    private static async Task VerifyBasicTaskManagementAsync()
    {
        using var fixture = new Fixture();
        string first = fixture.Project("first-task"), second = fixture.Project("second-task");
        string plan = Path.Combine(first, "plan.md");
        File.WriteAllText(plan, "# Existing plan\nPreserve exactly.\n");
        byte[] bytes = File.ReadAllBytes(plan);
        int scanGeneration = (int)Field(fixture.Window, "_basicTaskScanGeneration")!;
        Call(fixture.Window, "InitializeBasicWorkspace", false);
        Check((string?)Field(fixture.Window, "_basicSelectedFolder") == fixture.Root &&
            ((Button)fixture.Window.FindName("BasicStartTerminalButton")).IsEnabled && fixture.Tabs.Items.Count == 0,
            "Configured workspace must be immediately ready without automatically opening a terminal.");
        Check((int)Field(fixture.Window, "_basicTaskScanGeneration")! == scanGeneration &&
            ((Border)fixture.Window.FindName("BasicTaskSidebar")).Visibility == Visibility.Collapsed &&
            ((Grid)fixture.Window.FindName("BasicWorkspace")).ColumnDefinitions[0].Width.Value == 0,
            "Basic initialization must keep task controls hidden with no sidebar width or automatic task scan.");
        await (Task)Call(fixture.Window, "RefreshBasicTasksAsync")!;
        Check(((StackPanel)fixture.Window.FindName("BasicTaskRows")).Children.Count == 2 &&
            ((Border)fixture.Window.FindName("BasicTaskSidebar")).Visibility == Visibility.Collapsed,
            "An explicit legacy refresh must retain dormant task rows without exposing task controls.");
        string? created = (string?)Call(fixture.Window, "CreateBasicTask", "new-task");
        await (Task)Call(fixture.Window, "RefreshBasicTasksAsync")!;
        Check(created == Path.Combine(fixture.Root, "new-task") && Directory.Exists(created) &&
            Directory.GetFileSystemEntries(created).Length == 0 && fixture.Tabs.Items.Count == 0 &&
            (string?)Field(fixture.Window, "_basicSelectedFolder") == created,
            "Creating a Basic task must make/select only an empty child folder.");
        Call(fixture.Window, "SelectBasicFolder", first);
        Call(fixture.Window, "SelectBasicFolder", second);
        await (Task)Call(fixture.Window, "RefreshBasicTasksAsync")!;
        Check(bytes.SequenceEqual(File.ReadAllBytes(plan)) && !Directory.Exists(Path.Combine(second, "terminal-notes")) &&
            ((IList)Field(fixture.Window, "_documentBindings")!).Count == 0 && fixture.StartupChecks == 0,
            "Basic create/select/refresh wrote a plan/binding or contacted OpenCode.");
        string imported = Path.Combine(fixture.Project("imports"), "external-task");
        Directory.CreateDirectory(imported);
        var racingRefresh = (Task)Call(fixture.Window, "RefreshBasicTasksAsync")!;
        Call(fixture.Window, "SelectBasicFolder", imported);
        await racingRefresh;
        Check(((string[])Field(fixture.Window, "_basicTaskProjects")!).Contains(imported),
            "An in-flight refresh hid a folder imported after its snapshot.");
        Call(fixture.Window, "SelectBasicFolder", second);
        Call(fixture.Window, "SaveSettings");
        using var restored = new Fixture(File.ReadAllText(fixture.SettingsPath));
        SetField(restored.Window, "_workspace", fixture.Root);
        Call(restored.Window, "InitializeBasicWorkspace", false);
        Check((string?)Field(restored.Window, "_basicSelectedFolder") == second,
            "The saved Basic task selection was not restored.");
        using var advanced = new Fixture(File.ReadAllText(fixture.SettingsPath).Replace("\"WorkMode\":\"Basic\"", "\"WorkMode\":\"Advanced\""));
        advanced.LoadConfiguration();
        Call(advanced.Window, "SaveSettings");
        using (var savedAdvanced = JsonDocument.Parse(File.ReadAllText(advanced.SettingsPath)))
            Check(savedAdvanced.RootElement.GetProperty("BasicSelectedFolder").GetString() == second &&
                savedAdvanced.RootElement.GetProperty("BasicProjects").EnumerateArray().Any(item => item.GetString() == imported),
                "Advanced startup/save erased the dormant Basic selection/imports.");
        Call(advanced.Window, "SetWorkMode", true);
        await (Task)Call(advanced.Window, "RefreshBasicTasksAsync")!;
        Check((string?)Field(advanced.Window, "_basicSelectedFolder") == second &&
            ((string[])Field(advanced.Window, "_basicTaskProjects")!).Contains(imported),
            "Advanced-to-Basic did not restore persisted Basic selection/imports.");
        string otherWorkspace = fixture.Project("changed-workspace");
        var tab = fixture.ExistingTerminal(first);
        var pane = Property<TerminalPane>(tab.Tag, "Pane");
        string draft = Property<string>(tab.Tag, "InputDraft");
        SetField(fixture.Window, "_workspace", otherWorkspace);
        Call(fixture.Window, "InitializeBasicWorkspace", true);
        Check((string?)Field(fixture.Window, "_basicSelectedFolder") == otherWorkspace &&
            ReferenceEquals(((ContentControl)fixture.Window.FindName("BasicTerminalContent")).Content, pane) &&
            Property<string>(tab.Tag, "InputDraft") == draft && !pane.IsRunning,
            "Changing configured workspace must update the next target while retaining the active pane/draft.");
    }

    private static async Task VerifyBasicTaskDeletionAsync()
    {
        using var fixture = new Fixture();
        string project = fixture.Project("remove-from-list"), other = fixture.Project("keep-listed");
        string plan = Path.Combine(project, "plan.md");
        File.WriteAllText(plan, "# Keep the saved plan\r\n");
        string history = Path.Combine(project, "terminal-notes", "history.json");
        Directory.CreateDirectory(Path.GetDirectoryName(history)!);
        File.WriteAllText(history, "{\"draft\":\"keep unsent input and session history\"}");
        string source = Path.Combine(project, "user-code.txt");
        File.WriteAllText(source, "User files stay exactly here.");
        var original = Directory.EnumerateFiles(project, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        Call(fixture.Window, "InitializeBasicWorkspace", false);
        await (Task)Call(fixture.Window, "RefreshBasicTasksAsync")!;
        Call(fixture.Window, "SelectBasicFolder", project);
        using (var cancel = new ModalDriver(accept: false))
        {
            Call(fixture.Window, "ConfirmDeleteBasicTask", project);
            cancel.AssertShown();
            Check(((string[])Field(fixture.Window, "_basicTaskProjects")!).Contains(project),
                "Cancelling Basic task deletion changed membership.");
        }
        var scan = (Task)Call(fixture.Window, "RefreshBasicTasksAsync")!;
        Check((bool)Call(fixture.Window, "DeleteBasicTaskFromList", project)!, "Idle Basic task deletion failed.");
        await scan;
        await (Task)Call(fixture.Window, "RefreshBasicTasksAsync")!;
        Check(!((string[])Field(fixture.Window, "_basicTaskProjects")!).Contains(project) &&
            ((string[])Field(fixture.Window, "_basicTaskProjects")!).Contains(other) &&
            (string?)Field(fixture.Window, "_basicSelectedFolder") == fixture.Root,
            "Delete/refresh resurrected a task or failed to select the configured workspace.");
        Check(Directory.Exists(project) && original.Keys.Order().SequenceEqual(Directory.EnumerateFiles(project, "*", SearchOption.AllDirectories).Order()) &&
            original.All(item => item.Value.SequenceEqual(File.ReadAllBytes(item.Key))) &&
            !File.Exists(Path.Combine(fixture.Root, "plan.md")) && fixture.Tabs.Items.Count == 0 && fixture.StartupChecks == 0,
            "List deletion changed project files/history or created a default plan/terminal.");
        string saved = File.ReadAllText(fixture.SettingsPath);
        using (var restored = new Fixture(saved))
        {
            SetField(restored.Window, "_workspace", fixture.Root);
            Call(restored.Window, "InitializeBasicWorkspace", false);
            await (Task)Call(restored.Window, "RefreshBasicTasksAsync")!;
            Check(!((string[])Field(restored.Window, "_basicTaskProjects")!).Contains(project) &&
                (string?)Field(restored.Window, "_basicSelectedFolder") == fixture.Root,
                "Restart resurrected a removed Basic task or restored its selection.");
        }
        using (var advanced = new Fixture(saved.Replace("\"WorkMode\":\"Basic\"", "\"WorkMode\":\"Advanced\"")))
        {
            Call(advanced.Window, "SaveSettings");
            using var json = JsonDocument.Parse(File.ReadAllText(advanced.SettingsPath));
            Check(json.RootElement.GetProperty("BasicExcludedProjects").EnumerateArray().Any(item => item.GetString() == project),
                "Advanced saving before Basic initialization erased persisted exclusions.");
            SetField(advanced.Window, "_workspace", fixture.Root);
            Call(advanced.Window, "SetWorkMode", true);
            await (Task)Call(advanced.Window, "RefreshBasicTasksAsync")!;
            Check(!((string[])Field(advanced.Window, "_basicTaskProjects")!).Contains(project),
                "Advanced-to-Basic resurrected a removed task.");
        }
        Check((bool)Call(fixture.Window, "SelectBasicFolder", project)!, "Reimporting a removed folder failed.");
        await (Task)Call(fixture.Window, "RefreshBasicTasksAsync")!;
        using (var json = JsonDocument.Parse(File.ReadAllText(fixture.SettingsPath)))
            Check(((string[])Field(fixture.Window, "_basicTaskProjects")!).Contains(project) &&
                !json.RootElement.GetProperty("BasicExcludedProjects").EnumerateArray().Any(item => item.GetString() == project),
                "Reimporting did not restore membership and remove the persisted exclusion.");
        using (var confirm = new ModalDriver())
        {
            Call(fixture.Window, "ConfirmDeleteBasicTask", project);
            confirm.AssertShown();
            Check(confirm.Text.Contains("파일은 그대로 유지") &&
                !((string[])Field(fixture.Window, "_basicTaskProjects")!).Contains(project),
                "The confirmation did not explain preserved files or delete the chosen list entry.");
        }
        Call(fixture.Window, "SelectBasicFolder", project);
        byte[] persisted = File.ReadAllBytes(fixture.SettingsPath);
        using (var lockedSettings = new FileStream(fixture.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Check(!(bool)Call(fixture.Window, "DeleteBasicTaskFromList", project)! &&
                ((string[])Field(fixture.Window, "_basicTaskProjects")!).Contains(project) &&
                ((ISet<string>)Field(fixture.Window, "_basicImportedProjects")!).Contains(project) &&
                !((ISet<string>)Field(fixture.Window, "_basicExcludedProjects")!).Contains(project) &&
                (string?)Field(fixture.Window, "_basicSelectedFolder") == project,
                "A failed settings save removed the task or failed to restore Basic membership/selection.");
        }
        Check(persisted.SequenceEqual(File.ReadAllBytes(fixture.SettingsPath)),
            "A failed deletion save changed the previous persisted settings.");
    }

    private static async Task VerifyBasicTaskDeletionOwnershipAsync()
    {
        foreach (string ownership in new[] { "manual", "hidden", "detached", "retiring", "pending", "stopping", "rerunning", "new-admission", "external" })
        {
            using var fixture = new Fixture();
            string project = fixture.Project("owned-" + ownership), other = fixture.Project("idle-other");
            Call(fixture.Window, "InitializeBasicWorkspace", false);
            await (Task)Call(fixture.Window, "RefreshBasicTasksAsync")!;
            IDisposable? reservation = null;
            try
            {
                if (ownership == "pending") ((ISet<string>)Field(fixture.Window, "_startingProjectTasks")!).Add(project);
                else if (ownership == "stopping") ((ISet<string>)Field(fixture.Window, "_stoppingProjectTasks")!).Add(project);
                else if (ownership == "rerunning") ((ISet<string>)Field(fixture.Window, "_rerunningProjectTasks")!).Add(project);
                else if (ownership == "new-admission") reservation = (IDisposable)Call(fixture.Window, "AcquireNewWorkSessionAdmission", project)!;
                else if (ownership == "external")
                {
                    string lease = (string)typeof(ExternalTerminalLauncher).GetMethod("LeasePath", BindingFlags.Static | BindingFlags.NonPublic)!
                        .Invoke(null, new object[] { project, "ses_delete_fixture" })!;
                    Directory.CreateDirectory(Path.GetDirectoryName(lease)!);
                    reservation = new FileStream(lease, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                }
                else
                {
                    var tab = fixture.ExistingTerminal(project, task: ownership == "detached", hidden: ownership == "hidden");
                    if (ownership is "detached" or "retiring") fixture.Tabs.Items.Remove(tab);
                    if (ownership == "retiring")
                    {
                        var pane = Property<TerminalPane>(tab.Tag, "Pane");
                        pane.Dispose();
                        ((IDictionary)Field(fixture.Window, "_retiringWorkPanes")!).Add(pane, new TaskCompletionSource().Task);
                    }
                }
                Check(!(bool)Call(fixture.Window, "DeleteBasicTaskFromList", project)! &&
                    ((string[])Field(fixture.Window, "_basicTaskProjects")!).Contains(project),
                    ownership + " ownership allowed its Basic task to be deleted.");
                Check((bool)Call(fixture.Window, "DeleteBasicTaskFromList", other)!,
                    ownership + " ownership incorrectly blocked deletion of an unrelated idle task.");
            }
            finally { reservation?.Dispose(); }
        }
        using var race = new Fixture();
        string racing = race.Project("starts-during-confirmation");
        Call(race.Window, "InitializeBasicWorkspace", false);
        await (Task)Call(race.Window, "RefreshBasicTasksAsync")!;
        using var modal = new ModalDriver(beforeResponse: () =>
            ((ISet<string>)Field(race.Window, "_startingProjectTasks")!).Add(racing));
        Call(race.Window, "ConfirmDeleteBasicTask", racing);
        modal.AssertShown();
        Check(((string[])Field(race.Window, "_basicTaskProjects")!).Contains(racing) &&
            !((ISet<string>)Field(race.Window, "_basicExcludedProjects")!).Contains(racing),
            "A start admitted during the modal confirmation was deleted after confirmation.");
    }

    private static async Task VerifyBasicSetupAsync()
    {
        foreach (string outcome in new[] { "accept", "cancel", "mode-change", "mode-roundtrip" })
        {
            using var fixture = new Fixture();
            string chosen = Path.Combine(fixture.Root, "chosen-workspace");
            int choices = 0;
            var pending = new TaskCompletionSource<WorkspaceSetupResult?>();
            var hooks = new OpenCodeStartupHooks(_ => Task.FromResult(true), () => false,
                _ => throw new InvalidOperationException("Setup tests must never install or launch processes."),
                (_, _) => { choices++; return outcome.StartsWith("mode-", StringComparison.Ordinal) ? pending.Task :
                    Task.FromResult<WorkspaceSetupResult?>(outcome == "accept" ? new(chosen, true) : null); });
            SetField(fixture.Window, "_startupHooks", hooks);
            SetField(fixture.Window, "_autoStartCodex", true);
            Call(fixture.Window, "InitializeBasicWorkspace", false);
            var startup = (Task)Call(fixture.Window, "PrepareOpenCodeAndStartAsync", null, false, true, null)!;
            if (outcome.StartsWith("mode-", StringComparison.Ordinal))
            {
                Check(Call(fixture.Window, "CreateBasicTask", "during-setup") is null &&
                    !(bool)Call(fixture.Window, "SelectBasicFolder", fixture.Root)! &&
                    !Directory.Exists(Path.Combine(fixture.Root, "during-setup")),
                    "Pending first-run setup allowed task creation/target changes in the old workspace.");
                Call(fixture.Window, "SetWorkMode", false);
                if (outcome == "mode-roundtrip") Call(fixture.Window, "SetWorkMode", true);
                pending.SetResult(new(chosen, true));
            }
            await startup;
            Check(choices == 1 && fixture.Tabs.Items.Count == 0,
                "Basic first run skipped setup or opened a terminal automatically.");
            if (outcome == "accept")
            {
                Check((bool)Field(fixture.Window, "_setupCompleted")! && Directory.Exists(chosen) &&
                    (string?)Field(fixture.Window, "_basicSelectedFolder") == chosen && Directory.GetFileSystemEntries(chosen).Length == 0,
                    "Accepted Basic setup must select the final configured folder without creating a plan.");
                using var settings = JsonDocument.Parse(File.ReadAllText(fixture.SettingsPath));
                Check(settings.RootElement.GetProperty("CodexWorkspace").GetString() == chosen &&
                    settings.RootElement.GetProperty("SetupCompleted").GetBoolean() &&
                    settings.RootElement.GetProperty("BasicSelectedFolder").GetString() == chosen,
                    "Basic setup/final target was not persisted.");
                await (Task)Call(fixture.Window, "PrepareOpenCodeAndStartAsync", null, false, true, null)!;
                Check(choices == 1, "Completed Basic setup was requested again.");
            }
            else Check(!(bool)Field(fixture.Window, "_setupCompleted")! && !Directory.Exists(chosen),
                "Cancelled or obsolete Basic setup created/accepted a workspace.");
        }
        using var retry = new Fixture();
        string retryWorkspace = Path.Combine(retry.Root, "accepted-on-retry");
        int attempts = 0;
        SetField(retry.Window, "_startupHooks", new OpenCodeStartupHooks(_ => Task.FromResult(true), () => false,
            _ => throw new InvalidOperationException("Retry test must never install."),
            (_, _) => Task.FromResult<WorkspaceSetupResult?>(++attempts == 1 ? null : new(retryWorkspace, false))));
        Call(retry.Window, "InitializeBasicWorkspace", false);
        await (Task)Call(retry.Window, "PrepareOpenCodeAndStartAsync", null, false, true, null)!;
        await (Task)Call(retry.Window, "PrepareOpenCodeAndStartAsync", null, true, true, retry.Root)!;
        var retryTab = retry.Tabs.Items.Cast<TabItem>().Single();
        var retryPane = Property<TerminalPane>(retryTab.Tag, "Pane");
        retry.TrackPane(retryPane);
        Check(retryPane.WorkingDirectory == retryWorkspace && Property<string>(retryTab.Tag, "PlanDocumentPath") == "" &&
            Directory.GetFileSystemEntries(retryWorkspace).Length == 0 && !retryPane.IsRunning,
            "Retry after cancelled setup opened the obsolete default folder or created a plan/process.");
    }

    private static async Task VerifyFirstRunModeSelectionAsync()
    {
        foreach (bool chosenBasic in new[] { true, false })
        {
            using var fixture = new Fixture();
            Call(fixture.Window, "SetWorkMode", !chosenBasic);
            string chosen = Path.Combine(fixture.Root, chosenBasic ? "first-run-basic" : "first-run-advanced");
            int checks = 0, installs = 0, choices = 0;
            var events = new List<string>();
            SetField(fixture.Window, "_startupHooks", new OpenCodeStartupHooks(
                _ => { events.Add("check"); return Task.FromResult(++checks > 1); },
                () => { events.Add("confirm"); return true; },
                _ => { events.Add("install"); installs++; return Task.FromResult(new AutoInstallResult(AutoInstallStatus.Completed, "isolated installation complete")); },
                (_, _) =>
                {
                    choices++;
                    events.Add("choose");
                    Check(checks == 2 && installs == 1 && !(bool)Field(fixture.Window, "_setupCompleted")!,
                        "First-run mode selection must occur only after installation is verified.");
                    return Task.FromResult<WorkspaceSetupResult?>(new(chosen, true, chosenBasic));
                }));
            await (Task)Call(fixture.Window, "PrepareOpenCodeAndStartAsync", null, false, true, null)!;
            Check(events.SequenceEqual(new[] { "check", "confirm", "install", "check", "choose" }) &&
                Basic(fixture.Window) == chosenBasic && (bool)Field(fixture.Window, "_setupCompleted")! &&
                (string?)Field(fixture.Window, "_workspace") == chosen && fixture.Tabs.Items.Count == 0 &&
                Directory.Exists(chosen) && !Directory.EnumerateFiles(chosen, "plan.md", SearchOption.AllDirectories).Any(),
                "Accepting first-run mode selection must apply the chosen mode and workspace without opening a terminal or plan.");
            using (var settings = JsonDocument.Parse(File.ReadAllText(fixture.SettingsPath)))
                Check(settings.RootElement.GetProperty("WorkMode").GetString() == (chosenBasic ? "Basic" : "Advanced") &&
                    settings.RootElement.GetProperty("SetupCompleted").GetBoolean() &&
                    settings.RootElement.GetProperty("CodexWorkspace").GetString() == chosen,
                    "First-run mode and workspace selection must be saved together.");
            await (Task)Call(fixture.Window, "PrepareOpenCodeAndStartAsync", null, false, true, null)!;
            Check(choices == 1 && installs == 1 && Basic(fixture.Window) == chosenBasic && fixture.Tabs.Items.Count == 0,
                "Completed setup must retain the chosen mode and skip first-run selection on the next explicit preparation.");
        }
    }

    private static void VerifyWorkspacePrecedence()
    {
        using var owner = new Fixture();
        string saved = owner.Project("saved"), compatible = owner.Project("compatible"), explicitPath = owner.Project("explicit");
        string settings = JsonSerializer.Serialize(new { Workspace = compatible, CodexWorkspace = saved,
            WorkspaceDefaultsVersion = 1, SetupCompleted = true, WorkMode = "Basic" });
        using var configured = new Fixture(settings);
        configured.LoadConfiguration();
        Check((string?)Field(configured.Window, "_basicSelectedFolder") == saved && configured.Tabs.Items.Count == 0,
            "Saved CodexWorkspace did not take precedence over the compatibility workspace.");
        using var explicitFixture = new Fixture(settings, explicitPath);
        explicitFixture.LoadConfiguration();
        Check((string?)Field(explicitFixture.Window, "_basicSelectedFolder") == explicitPath && explicitFixture.Tabs.Items.Count == 0,
            "Explicit initial workspace did not override saved configuration without opening a terminal.");
    }

    private static async Task VerifyStartupModeChangeAsync()
    {
        using var fixture = new Fixture();
        Call(fixture.Window, "SetWorkMode", false);
        string project = fixture.Project("startup-mode-change");
        var detection = new TaskCompletionSource<bool>();
        var hooks = new OpenCodeStartupHooks(_ => detection.Task, () => false,
            _ => throw new InvalidOperationException("Mode-change test must never invoke installation."));
        SetField(fixture.Window, "_startupHooks", hooks);
        SetField(fixture.Window, "_workspace", project);
        SetField(fixture.Window, "_startInWorkspaceProject", true);
        SetField(fixture.Window, "_setupCompleted", true);
        var startup = (Task)Call(fixture.Window, "PrepareOpenCodeAndStartAsync", null, true, false, project)!;
        Check((bool)Call(fixture.Window, "SetWorkMode", true)!, "One pending startup could not enter Basic.");
        detection.SetResult(true);
        await startup;
        Check(fixture.Tabs.Items.Count == 0 && !Directory.Exists(Path.Combine(project, "terminal-notes")) &&
            !File.Exists(Path.Combine(project, "plan.md")) && (string?)Field(fixture.Window, "_selectedProjectPath") is null,
            "A pending Advanced startup resumed after entering Basic and created/attached a plan.");
        Check(((Button)fixture.Window.FindName("BasicFolderButton")).IsEnabled,
            "Cancelled startup retained its pending session admission or disabled Basic folder selection.");
    }

    private static async Task VerifyStartupStatusAsync()
    {
        foreach (string outcome in new[] { "completed-basic", "completed-basic-nonexplicit", "first-run-basic",
            "completed-advanced", "installed-basic", "mode-change", "close-pending", "newer-status", "failed-check", "cancelled-check",
            "declined-install", "failed-install", "cancelled-setup" })
        {
            using var fixture = new Fixture();
            bool firstRun = outcome is "first-run-basic" or "cancelled-setup";
            bool installing = outcome is "installed-basic" or "declined-install" or "failed-install";
            bool delayed = outcome is "mode-change" or "close-pending" or "newer-status";
            int checks = 0, installs = 0, choices = 0;
            var detection = new TaskCompletionSource<bool>();
            SetField(fixture.Window, "_setupCompleted", !firstRun);
            if (outcome == "completed-advanced") Call(fixture.Window, "SetWorkMode", false);
            SetField(fixture.Window, "_startupHooks", new OpenCodeStartupHooks(
                _ =>
                {
                    checks++;
                    if (delayed) return detection.Task;
                    if (outcome == "failed-check") return Task.FromException<bool>(new IOException("isolated check failure"));
                    if (outcome == "cancelled-check") return Task.FromCanceled<bool>(new CancellationToken(true));
                    return Task.FromResult(!installing || (outcome == "installed-basic" && checks > 1));
                },
                () => outcome != "declined-install",
                _ =>
                {
                    installs++;
                    return Task.FromResult(new AutoInstallResult(
                        outcome == "failed-install" ? AutoInstallStatus.Failed : AutoInstallStatus.Completed,
                        outcome == "failed-install" ? "isolated installer failure" : "isolated installation complete"));
                },
                (_, _) =>
                {
                    choices++;
                    return Task.FromResult<WorkspaceSetupResult?>(outcome == "cancelled-setup" ? null : new(fixture.Root, false));
                }));
            var startup = (Task)Call(fixture.Window, "PrepareOpenCodeAndStartAsync", null, false,
                outcome != "completed-basic-nonexplicit", null)!;
            if (delayed)
            {
                Check(((TextBlock)fixture.Window.FindName("StatusLabel")).Text == "OpenCode 설치 여부를 확인하고 있습니다…",
                    "Pending detection did not display its installation progress.");
                if (outcome == "mode-change") Call(fixture.Window, "SetWorkMode", false);
                else if (outcome == "close-pending") SetField(fixture.Window, "_closePending", true);
                else Call(fixture.Window, "SetInlineStatus", "A newer unrelated status");
                detection.SetResult(true);
            }
            await startup;
            string status = ((TextBlock)fixture.Window.FindName("StatusLabel")).Text;
            string expected = outcome switch
            {
                "newer-status" => "A newer unrelated status",
                "failed-check" => "OpenCode 시작 준비 실패 · isolated check failure",
                "cancelled-check" => "OpenCode 시작 준비를 취소했습니다. ‘+ 터미널’에서 다시 확인할 수 있습니다.",
                "declined-install" => "OpenCode 실행 불가 · OpenCode 실행 확인 실패 · ‘+ 터미널’에서 다시 확인할 수 있습니다.",
                "failed-install" => "isolated installer failure · OpenCode 실행 확인 실패 · ‘+ 터미널’에서 다시 확인할 수 있습니다.",
                "cancelled-setup" => "작업 시작을 취소했습니다.",
                _ => "준비 완료"
            };
            Check(status == expected, outcome + " left a stale progress message or overwrote another status: " + status);
            Check(fixture.Tabs.Items.Count == 0 && !(bool)Field(fixture.Window, "_openCodeStartupBusy")! &&
                !Directory.Exists(Path.Combine(fixture.Root, "terminal-notes")),
                outcome + " created a terminal/plan or retained the startup busy flag.");
            Check(installs == (outcome is "installed-basic" or "failed-install" ? 1 : 0) &&
                choices == (firstRun ? 1 : 0), outcome + " changed installation/setup branching.");
        }
        Console.WriteLine($"PASS {_checks} startup-status checks; injected hooks only, no terminal/installer/model processes.");
    }

    private static async Task VerifyBlockedStartAsync()
    {
        using var fixture = new Fixture();
        string existing = fixture.Project("existing"), blocked = fixture.Project("blocked-start");
        fixture.ExistingTerminal(existing, task: true);
        byte[] original = System.Text.Encoding.UTF8.GetBytes("# Saved plan\r\nKeep this exact disk content.\r\n");
        string plan = Path.Combine(blocked, "terminal-notes", "plan.md");
        Directory.CreateDirectory(Path.GetDirectoryName(plan)!); File.WriteAllBytes(plan, original);
        SetField(fixture.Window, "_selectedProjectPath", blocked);
        using var popup = new ModalDriver();
        await (Task)Call(fixture.Window, "StartProjectTaskAsync", blocked)!;
        popup.AssertShown();
        Check(fixture.StartupChecks == 0, "A blocked start reached installation/OpenCode readiness checks.");
        Check(original.SequenceEqual(File.ReadAllBytes(plan)) && !((IDictionary)Field(fixture.Window, "_automationRuntimes")!).Contains(blocked),
            "A blocked task start changed the saved plan or created runtime state.");
        Check(!((ISet<string>)Field(fixture.Window, "_startingProjectTasks")!).Contains(blocked) &&
            !((ISet<string>)Field(fixture.Window, "_projectTaskControlWrites")!).Contains(blocked),
            "A blocked task start reserved task control state.");
        using var newTerminalPopup = new ModalDriver();
        Call(fixture.Window, "CreateProfileTerminal", (object?)null);
        newTerminalPopup.AssertShown();
        Check(fixture.Tabs.Items.Count == 1 && fixture.StartupChecks == 0,
            "The new-terminal keyboard/menu target bypassed Basic admission.");

        var load = (Task)Call(fixture.Window, "LoadAutomationPlanAsync", null, blocked, 1, plan, false)!;
        await load;
        object session = load.GetType().GetProperty("Result")!.GetValue(load)!;
        object edit = Property<object>(session, "Edit");
        string draft = "# User's unsaved draft\nPreserve this before rejecting execution.";
        SetProperty(edit, "Draft", draft);
        SetField(fixture.Window, "_composerState", null);
        SetField(fixture.Window, "_selectedProjectPath", blocked);
        SetField(fixture.Window, "_activeTabDocumentPath", plan);
        Call(fixture.Window, "ShowTabDocumentDraft", edit);
        using (var executePopup = new ModalDriver())
        {
            await (Task)Call(fixture.Window, "SaveTabDocumentAsync", true)!;
            executePopup.AssertShown();
        }
        Check(original.SequenceEqual(File.ReadAllBytes(plan)) && Property<string>(edit, "Draft") == draft &&
            ((TextBox)Field(fixture.Window, "_tabDocumentEditor")!).Text == draft,
            "Rejected plan execution saved/changed the user's disk plan or unsaved editor draft.");
        Check(!((ISet<string>)Field(fixture.Window, "_pendingPlanExecutions")!).Contains(blocked) && fixture.StartupChecks == 0,
            "Rejected plan execution reserved dispatch state or reached startup.");
        await (Task)Call(fixture.Window, "SaveTabDocumentAsync", false)!;
        Check(File.ReadAllText(plan) == draft && fixture.StartupChecks == 0 && fixture.Tabs.Items.Count == 1,
            "A plan-only save must remain available while another project's Basic session exists.");
    }

    private static bool Basic(MainWindow window) => Property<bool>(window, "IsBasicWorkMode");
    private static bool Admit(MainWindow window, string project, bool reuse = true) => (bool)Call(window, "CanAdmitWorkSession", project, reuse)!;

    private static void AssertCaptionFits(MainWindow window)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(820, 480));
        root.Arrange(new Rect(0, 0, 820, 480));
        root.UpdateLayout();
        foreach (string name in new[] { "BasicWorkMode", "AdvancedWorkMode", "TerminalLayoutSelector" })
        {
            var control = (FrameworkElement)window.FindName(name);
            if (control.Visibility != Visibility.Visible) continue;
            Rect bounds = control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
            Check(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= 0 && bounds.Right <= 820.5 && bounds.Top >= 0 && bounds.Bottom <= 36.5,
                name + " is clipped or unreachable at the supported minimum window size: " + bounds);
        }
        if (Basic(window))
            foreach (string name in new[] { "BasicChangeFolderButton", "BasicOpenFolderButton", "BasicStartTerminalButton", "BasicReconnectTerminalButton", "BasicCloseTerminalButton" })
            {
                var control = (FrameworkElement)window.FindName(name);
                if (name == "BasicChangeFolderButton")
                {
                    Check(control.Visibility == Visibility.Collapsed,
                        "The Basic workspace-change action must stay hidden without reserving layout space.");
                    continue;
                }
                if (name == "BasicReconnectTerminalButton" && control.Visibility == Visibility.Collapsed) continue;
                Rect bounds = control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
                Check(control.Visibility == Visibility.Visible && bounds.Width > 0 && bounds.Height > 0 &&
                    bounds.Left >= 0 && bounds.Right <= 820.5 && bounds.Top >= 36 && bounds.Bottom <= 480.5,
                    name + " is clipped or unreachable in the Basic workspace: " + bounds);
                var button = (Button)control;
                Check(button.Content is System.Windows.Shapes.Path && !string.IsNullOrWhiteSpace(button.ToolTip as string) &&
                    !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)) && ToolTipService.GetShowOnDisabled(button),
                    name + " must provide an icon, accessible name and tooltip even while disabled.");
            }
    }

    // MainWindow and pane Loaded events deliberately never run. The tests model existing
    // session ownership, while exercising the actual UI, admission and settings methods.
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "TerminalNotes WorkModes " + Guid.NewGuid().ToString("N"));
        internal string SettingsPath => Path.Combine(Root, "settings.json");
        internal MainWindow Window { get; }
        internal TabControl Tabs => (TabControl)Window.FindName("TerminalTabs");
        private readonly List<TerminalPane> _panes = [];
        private readonly object? _previousData;
        private bool _configurationLoaded;
        internal int StartupChecks { get; private set; }

        internal Fixture(string? settings = null, string? initialWorkspace = null)
        {
            Directory.CreateDirectory(Root);
            _previousData = AppContext.GetData("TerminalNotes.DataDirectory");
            AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(Root, "data"));
            if (settings is not null) File.WriteAllText(SettingsPath, settings);
            var hooks = new OpenCodeStartupHooks(_ => { StartupChecks++; return Task.FromResult(false); }, () => false,
                _ => throw new InvalidOperationException("An isolated test must never invoke an installer."));
            Window = new MainWindow(SettingsPath, autoStartCodex: false, initialWorkspace: initialWorkspace, startupHooks: hooks);
            SetField(Window, "_workspace", Root);
            new WindowInteropHelper(Window).EnsureHandle();
        }

        internal void TrackPane(TerminalPane pane) => _panes.Add(pane);

        internal void LoadConfiguration()
        {
            _configurationLoaded = true;
            Call(Window, "Window_Loaded", Window, new RoutedEventArgs());
        }

        internal string Project(string name)
        {
            string path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        internal TabItem ExistingTerminal(string project, bool task = false, bool hidden = false)
        {
            var pane = new TerminalPane(project, useProjectContext: false);
            _panes.Add(pane);
            object state = Activator.CreateInstance(typeof(MainWindow).GetNestedType("TerminalTabState", BindingFlags.NonPublic)!, true)!;
            SetProperty(state, "Pane", pane);
            SetProperty(state, "Sequence", _panes.Count);
            SetProperty(state, "Title", "Existing " + _panes.Count);
            SetProperty(state, "PlanDocumentPath", Path.Combine(project, "terminal-notes", "plan.md"));
            SetProperty(state, "InputDraft", "Unsent session draft " + _panes.Count);
            var header = new StackPanel();
            header.Children.Add(new TextBlock { Text = "Existing " + _panes.Count });
            var tab = new TabItem { Tag = state, Header = header, Visibility = hidden ? Visibility.Collapsed : Visibility.Visible };
            Tabs.Items.Add(tab);
            if (task) FieldInfo(Call(Window, "RuntimeFor", project)!, "SingleTerminal").SetValue(Call(Window, "RuntimeFor", project), tab);
            return tab;
        }

        public void Dispose()
        {
            Check((_configurationLoaded || !(bool)Field(Window, "_windowInitialized")!) && _panes.All(pane => !pane.IsRunning),
                "An isolated test unexpectedly loaded the app or started a real terminal.");
            Tabs.Items.Clear();
            ((IDictionary)Field(Window, "_automationRuntimes")!).Clear();
            ((ISet<string>)Field(Window, "_externalTerminalWrites")!).Clear();
            foreach (var pane in _panes) pane.Dispose();
            Call(Window, "DisposeResources");
            SetField(Window, "_closing", true);
            Window.Close();
            AppContext.SetData("TerminalNotes.DataDirectory", _previousData);
        }
    }

    private sealed class ModalDriver : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private Exception? _failure;
        internal int Count { get; private set; }
        internal string Text { get; private set; } = "";
        internal ModalDriver(bool accept = true, Action? beforeResponse = null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(20) };
            _timer.Tick += (_, _) =>
            {
                Window? dialog = Application.Current.Windows.Cast<Window>().SingleOrDefault(item => AutomationProperties.GetAutomationId(item) == "AppMessageDialog");
                if (dialog is null) return;
                try
                {
                    _timer.Stop();
                    Text = string.Join(" ", Descendants(dialog).OfType<TextBlock>().Select(item => item.Text));
                    Count++;
                    beforeResponse?.Invoke();
                    Descendants(dialog).OfType<Button>().Single(item => AutomationProperties.GetAutomationId(item) == (accept ? "AppMessageConfirm" : "AppMessageCancel"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception exception) { _failure = exception; _timer.Stop(); dialog.Close(); }
            };
            _timer.Start();
        }
        internal void AssertShown()
        {
            if (_failure is not null) ExceptionDispatchInfo.Capture(_failure).Throw();
            Check(Count == 1, "The refusal must show exactly one own-app popup.");
        }
        public void Dispose() => _timer.Stop();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static FieldInfo FieldInfo(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(instance.GetType().Name, name);
    private static object? Field(object instance, string name) => FieldInfo(instance, name).GetValue(instance);
    private static void SetField(object instance, string name, object? value) => FieldInfo(instance, name).SetValue(instance, value);
    private static T Property<T>(object instance, string name) => (T)instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void SetProperty(object instance, string name, object? value) => instance.GetType().GetProperty(name)!.SetValue(instance, value);
    private static object? Call(object instance, string name, params object?[] args)
    {
        try { return instance.GetType().GetMethod(name, PrivateInstance)!.Invoke(instance, args); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        { ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
    }
    private static Exception? Capture(Action action) { try { action(); return null; } catch (Exception exception) { return exception; } }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); _checks++; }

    private static void SavePreview(FrameworkElement element, string name, double scale = 2, bool arrange = true)
    {
        if (arrange)
        {
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            element.Arrange(new Rect(element.DesiredSize));
            element.UpdateLayout();
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale), (int)Math.Ceiling(element.ActualHeight * scale), 96 * scale, 96 * scale,
            System.Windows.Media.PixelFormats.Pbgra32);
        var background = new System.Windows.Media.DrawingVisual();
        using (var context = background.RenderOpen())
            context.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x12, 0x12, 0x12)), null, new Rect(element.RenderSize));
        bitmap.Render(background);
        bitmap.Render(element);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        string folder = Path.Combine(Path.GetTempPath(), "TerminalNotes.WorkModes");
        Directory.CreateDirectory(folder);
        using var stream = File.Create(Path.Combine(folder, name));
        encoder.Save(stream);
        Console.WriteLine("PREVIEW " + Path.Combine(folder, name));
    }
}
