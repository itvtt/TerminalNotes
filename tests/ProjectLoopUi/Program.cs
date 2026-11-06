using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TerminalNotes;
using TerminalNotes.Services;

internal static class Program
{
    private const BindingFlags Members = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    [STAThread]
    private static int Main()
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes Loop " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        object? previousDataDirectory = AppContext.GetData("TerminalNotes.DataDirectory");
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(fixture, "data"));
        try
        {
            VerifyInputAndVisuals(fixture);
            VerifyRuntimeState(fixture);
            VerifyRecommendedPlanOwnership(fixture);
            VerifyOwnerAccountOnly(fixture);
            Console.WriteLine("PASS Loop keyboard/UIA, the Loop 1 · Loop 2 · 끄기 choice on click, expiry, safe plan save, cancellation, draft ownership and external conflicts.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL " + e); return 1; }
        finally
        {
            AppContext.SetData("TerminalNotes.DataDirectory", previousDataDirectory);
            Directory.Delete(fixture, recursive: true);
        }
    }

    private static void VerifyInputAndVisuals(string fixture)
    {
        var loop = (ToggleButton)Call(typeof(MainWindow), "CreateProjectLoopButton")!;
        var holder = new Window { Width = 180, Height = 100, Content = loop, ShowInTaskbar = false };
        try
        {
            holder.Show(); holder.UpdateLayout(); loop.Focus();
            var source = PresentationSource.FromVisual(loop)!;
            loop.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
            loop.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyUpEvent });
            Require(loop.IsChecked == true, "Space did not toggle Loop.");
            var enter = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            loop.RaiseEvent(enter);
            Require(enter.Handled && loop.IsChecked == false, "Enter did not toggle exactly once and stop card activation.");
            var peer = new ToggleButtonAutomationPeer(loop);
            var toggle = (IToggleProvider)peer.GetPattern(PatternInterface.Toggle)!;
            toggle.Toggle();
            Require(toggle.ToggleState == ToggleState.On && loop.Content is FrameworkElement && AutomationProperties.GetName(loop).Contains("Loop"), "Loop has no accessible toggle state or visible label.");
            int cardClicks = 0;
            holder.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => cardClicks++));
            loop.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, loop));
            Require(cardClicks == 0, "Loop click propagated to its project card.");
            Call(typeof(MainWindow), "ApplyProjectLoopVisual", loop, "fixture", true, null, "Loop · 개선 검토 대기 · 20초 후");
            Require(ColorOf(loop) == Color.FromRgb(0x3D, 0x3D, 0x3D) && AutomationProperties.GetName(loop).Contains("반복 실행 중"), "Active Loop was not visibly/accessibly active.");
            Capture(loop, fixture + "-active.png");
            Call(typeof(MainWindow), "ApplyProjectLoopVisual", loop, "fixture", false, null, null);
            Require(ColorOf(loop).A == 0 && loop.IsChecked == true && loop.ToolTip.ToString()!.Contains("실행 대기"), "Stopped Loop preference was colored as running or lost.");
            Capture(loop, fixture + "-stopped.png");
            Call(typeof(MainWindow), "ApplyProjectLoopVisual", loop, "fixture", true, DateTimeOffset.Now.AddMinutes(-1), null);
            Require(ColorOf(loop).A == 0 && loop.ToolTip.ToString()!.Contains("종료 시간 만료"), "Expired Loop remained active.");
        }
        finally { holder.Close(); }
    }

    // Loop is offered only to LoopAutoRecommandUsers.AllowedUsers: other accounts never see it, and a saved Loop never turns on.
    private static void VerifyOwnerAccountOnly(string fixture)
    {
        FieldInfo accountOverride = typeof(MainWindow).Assembly.GetType("TerminalNotes.Services.LoopAutoRecommandUsers")!
            .GetField("AccountOverride", BindingFlags.Static | BindingFlags.NonPublic)!;
        // MASTER_USERS in .env reaches the app as assembly metadata; every account-limited feature follows that one list.
        foreach (var (typeName, key) in new[] { ("TerminalNotes.Services.LoopAutoRecommandUsers", "TerminalNotes.MasterUsers"),
            ("TerminalNotes.Services.AnnouncementEditors", "TerminalNotes.MasterUsers") })
        {
            var users = (string[])typeof(MainWindow).Assembly.GetType(typeName)!.GetField("AllowedUsers", Members)!.GetValue(null)!;
            string? configured = typeof(MainWindow).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(item => item.Key == key)?.Value;
            Require(configured is null ? users.Length > 0 : users.SequenceEqual(configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
                key + " did not follow the .env account list.");
        }
        string project = Path.Combine(fixture, "owner-only");
        Directory.CreateDirectory(project);
        try
        {
            // The first master account of this build (MASTER_USERS, default test_01), in other letter case.
            string master = ((string[])typeof(MainWindow).Assembly.GetType("TerminalNotes.Services.LoopAutoRecommandUsers")!
                .GetField("AllowedUsers", Members)!.GetValue(null)!)[0].ToUpperInvariant();
            foreach (string account in new[] { @"CORP\someone", @"CORP\jy01.ahn", @"CORP\" + master })
            {
                bool owner = account.EndsWith(master, StringComparison.Ordinal);
                accountOverride.SetValue(null, (Func<string>)(() => account));
                var window = new MainWindow(Path.Combine(fixture, "owner-" + owner + ".json"), autoStartCodex: false, initialWorkspace: fixture);
                try
                {
                    Field<HashSet<string>>(window, "_loadedSingleImprovementPreferences").Add(project);
                    Field<Dictionary<string, bool>>(window, "_projectTaskContinuousModes")[project] = true;
                    Call(window, "EnsureProjectTaskRow", project);
                    object row = Field<IDictionary>(window, "_projectTaskRows")[project]!;
                    var loop = (ToggleButton)row.GetType().GetProperty("Loop")!.GetValue(row)!;
                    Call(window, "UpdateProjectTaskActivity");
                    if (owner)
                        Require(loop.Visibility == Visibility.Visible && loop.IsChecked == true, "An allowed account lost its Loop button.");
                    else
                    {
                        Require(loop.Visibility == Visibility.Collapsed && loop.IsChecked != true &&
                            !Field<Dictionary<string, bool>>(window, "_projectTaskContinuousModes").GetValueOrDefault(project),
                            "Another account saw Loop or kept a saved Loop turned on.");
                        var turnOn = (Task<bool>)Call(window, "ApplyProjectLoopModeAsync", project, true, null, true)!;
                        Require(turnOn.IsCompleted && !turnOn.Result && SingleProjectContinuousImprovementStore.Read(project) is null,
                            "Another account could turn Loop on.");
                    }
                }
                finally { Call(window, "DisposeResources"); }
            }
        }
        finally { accountOverride.SetValue(null, null); }
    }

    private static void VerifyRuntimeState(string fixture)
    {
        var window = new MainWindow(Path.Combine(fixture, "settings.json"), autoStartCodex: false, initialWorkspace: fixture);
        using var pane = new TerminalPane(fixture, useProjectContext: false);
        using var host = new OpenCodeHeadlessHost(new Uri("http://127.0.0.1:43127/"));
        using var self = Process.GetCurrentProcess(); // Only this test executable; detach before any owning cleanup.
        using var client = new OpenCodeRecoveryClient(new Uri("http://127.0.0.1:43127/"), fixture);
        using var controller = new OpenCodeAutoResumeController(fixture, null, client, () => false);
        try
        {
            Set(host, "_process", self);
            Set(pane, "_headlessOpenCodeHost", host);
            Set(pane, "_usesNativeOpenCodeStatus", true);
            Set(pane, "_ready", true);
            Set(pane, "_openCodeSnapshot", new OpenCodeStatusSnapshot(OpenCodeStatusKind.Idle, true, "fixture"));
            Set(pane, "_autoResume", controller);
            controller.SetContinuousImprovement(true, null);
            Set(controller, "_sessionId", "fixture");
            Set(controller, "_hasVerifiedCompletion", true);
            Set(controller, "_status", "Loop · 개선 검토 대기 · 20초 후");
            Require((bool)pane.GetType().GetProperty("IsContinuousImprovementActive", Members)!.GetValue(pane)! && pane.HasCompletedWork && pane.IsRunning,
                "Fixture did not represent completed work awaiting the next Loop review.");
            object state = Activator.CreateInstance(typeof(MainWindow).GetNestedType("TerminalTabState", BindingFlags.NonPublic)!)!;
            state.GetType().GetProperty("Pane")!.SetValue(state, pane);
            object runtime = Call(window, "RuntimeFor", fixture)!;
            Set(runtime, "SingleTerminal", new TabItem { Tag = state });
            Set(runtime, "SingleSubmitted", true);
            Field<HashSet<string>>(window, "_loadedSingleImprovementPreferences").Add(fixture);
            Field<Dictionary<string, bool>>(window, "_projectTaskContinuousModes")[fixture] = true;
            Call(window, "EnsureProjectTaskRow", fixture);
            object row = Field<IDictionary>(window, "_projectTaskRows")[fixture]!;
            var loop = (ToggleButton)row.GetType().GetProperty("Loop")!.GetValue(row)!;
            var loopMode = (Button)row.GetType().GetProperty("LoopMode")!.GetValue(row)!;
            var loopChoice = (MenuItem)row.GetType().GetProperty("LoopChoice")!.GetValue(row)!;
            var loopFull = (MenuItem)row.GetType().GetProperty("LoopFull")!.GetValue(row)!;
            Call(window, "UpdateProjectTaskActivity");
            Require(loop.IsChecked == true && ColorOf(loop) == Color.FromRgb(0x3D, 0x3D, 0x3D) && loop.ToolTip.ToString()!.Contains("20초 후"), "Completed-but-active Loop review lost its running color/countdown.");
            var card = (Border)row.GetType().GetProperty("Surface")!.GetValue(row)!;
            Require(card.Child is Grid header && header.RowDefinitions.Count == 1 && header.RowDefinitions[0].Height.Value == 36,
                "Task card reserved a second row for repeat controls.");
            card.Measure(new Size(184, 40)); card.Arrange(new Rect(0, 0, 184, 40)); card.UpdateLayout();
            loopChoice.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            CompleteOnDispatcher(WaitForOptionSave(window, fixture));
            Require(loop.IsChecked == true && Equals(loop.Tag, "Loop 1") && loopChoice.IsChecked && !loopFull.IsChecked &&
                loop.Content is Grid { Children: [_, TextBlock { Text: "1", Visibility: Visibility.Visible }] } &&
                SingleProjectContinuousImprovementStore.Read(fixture) is { Enabled: true, RecommendPlan: false, Loop: 1 },
                "Loop 1 was not chosen, shown on the button and kept.");
            loopFull.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            CompleteOnDispatcher(WaitForOptionSave(window, fixture));
            Require(loop.IsChecked == true && Equals(loop.Tag, "Loop 2") && loopFull.IsChecked && !loopChoice.IsChecked &&
                loop.Content is Grid { Children: [_, TextBlock { Text: "2" }] } && ColorOf(loop) == Color.FromRgb(0x3D, 0x3D, 0x3D) &&
                SingleProjectContinuousImprovementStore.Read(fixture) is { Enabled: true, RecommendPlan: true, Loop: 2 },
                "Loop 2 was not chosen and kept.");
            // Clicking Loop (here the accessible toggle) opens Loop 1 · Loop 2 and, while it is on, 끄기 — it does not toggle.
            var toggle = (IToggleProvider)new ToggleButtonAutomationPeer(loop).GetPattern(PatternInterface.Toggle)!;
            toggle.Toggle();
            var menu = loop.ContextMenu!;
            var off = menu.Items.OfType<MenuItem>().Last();
            Require(menu.IsOpen && loop.IsChecked == true && loopChoice.Parent == menu && loopFull.Parent == menu &&
                loopChoice.Header is string one && one.StartsWith("Loop 1", StringComparison.Ordinal) &&
                loopFull.Header is string two && two.StartsWith("Loop 2", StringComparison.Ordinal) && loopFull.ToolTip.ToString()!.Contains("Auto Recommand") &&
                loopFull.IsChecked && !loopChoice.IsChecked && Equals(loop.Tag, "Loop 2") && off is { Header: "Loop 끄기", Visibility: Visibility.Visible } &&
                loopMode.Visibility == Visibility.Collapsed &&
                !((Button)row.GetType().GetProperty("Select")!.GetValue(row)!).ContextMenu.Items.Contains(loopChoice),
                "Clicking Loop did not offer Loop 1 · Loop 2 · 끄기, or it toggled.");
            menu.IsOpen = false;
            off.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            CompleteOnDispatcher(WaitForOptionSave(window, fixture));
            Require(loop.IsChecked == false && Equals(loop.Tag, "Loop 2") && !loopFull.IsChecked &&
                loop.Content is Grid { Children: [_, TextBlock { Visibility: Visibility.Collapsed }] } &&
                SingleProjectContinuousImprovementStore.Read(fixture) is { Enabled: false, RecommendPlan: true },
                "Loop 끄기 did not turn Loop off while keeping Loop 2.");
            loopFull.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            CompleteOnDispatcher(WaitForOptionSave(window, fixture));
            Require(loop.IsChecked == true && SingleProjectContinuousImprovementStore.Read(fixture) is { Enabled: true, RecommendPlan: true },
                "Loop 2 could not be turned back on.");
            Set(runtime, "SingleStopped", true);
            Call(window, "UpdateProjectTaskActivity");
            Require(loop.IsChecked == true && ColorOf(loop).A == 0, "Manual stop did not clear active color while retaining preference.");
            Set(runtime, "SingleStopped", false);
            Set(host, "_process", null);
            Call(window, "UpdateProjectTaskActivity");
            Require(ColorOf(loop).A == 0, "A non-running terminal appeared to run Loop.");
            Field<Dictionary<string, DateTimeOffset?>>(window, "_projectTaskImprovementUntil")[fixture] = DateTimeOffset.Now.AddMinutes(-1);
            Call(window, "UpdateProjectTaskActivity");
            Require(loop.IsChecked == false && loop.ToolTip.ToString()!.Contains("종료 시간 만료"), "Expired schedule did not turn off the Loop control.");
        }
        finally
        {
            Set(host, "_process", null);
            Call(window, "DisposeResources");
        }
    }

    private static async Task<bool> WaitForOptionSave(MainWindow window, string project)
    {
        while (Field<HashSet<string>>(window, "_projectTaskOptionWrites").Contains(project)) await Task.Delay(10);
        return true;
    }

    private static void VerifyRecommendedPlanOwnership(string fixture)
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            foreach (string scenario in new[] { "save", "unchanged", "cancel", "lost-owner", "draft-edit", "input-edit", "external-edit" })
                VerifyRecommendedPlanScenario(Path.Combine(fixture, scenario), scenario);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static void VerifyRecommendedPlanScenario(string project, string scenario)
    {
        Directory.CreateDirectory(project);
        var window = new MainWindow(Path.Combine(project, "settings.json"), autoStartCodex: false, initialWorkspace: project);
        using var cancellation = new CancellationTokenSource();
        string path = (string)Call(typeof(MainWindow), "DefaultProjectPlanPath", project)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string original = "# Plan\n\n## Requirements\n- Preserve port 43127\n- Keep exception code E42\n";
        if (scenario == "unchanged") original = "# Plan\r\n\r\n## Requirements\r\n- Preserve port 43127\r\n- Keep exception code E42\r\n\r\n## Requirements\r\n- Preserve port 43127\r\n";
        string external = original + "\nExternal change must survive.\n";
        File.WriteAllText(path, original);
        string input = "Unsent user request with number 8941";
        object draft = Activator.CreateInstance(typeof(MainWindow).GetNestedType("ProjectInputDraft", BindingFlags.NonPublic)!)!;
        Set(draft, "Text", input); Set(draft, "Revision", 7L);
        Field<IDictionary>(window, "_projectInputDrafts")[project] = draft;
        Set(window, "_workspace", project);
        Field<HashSet<string>>(window, "_loadedSingleImprovementPreferences").Add(project);
        Field<Dictionary<string, bool>>(window, "_projectTaskContinuousModes")[project] = true;
        Field<Dictionary<string, bool>>(window, "_projectTaskRecommendPlans")[project] = true;
        bool owned = true;
        int generated = 0, verified = 0, initialWindows = Application.Current.Windows.Count;
        Func<string, string, Func<bool>, CancellationToken, Task<string>> generator = (_, prompt, stillOwned, token) =>
        {
            generated++;
            Require(stillOwned() && !token.IsCancellationRequested && prompt.Contains("43127"), "Planner did not capture an owned clean plan.");
            Require(Field<IDictionary>(window, "_planMakers").Contains(project), "Dedicated planner did not register its ownership.");
            switch (scenario)
            {
                case "cancel": cancellation.Cancel(); break;
                case "lost-owner": owned = false; break;
                case "draft-edit":
                    object edit = Field<IDictionary>(window, "_tabDocumentStates")[path]!;
                    edit.GetType().GetProperty("Draft")!.SetValue(edit, original + "\nUser draft change.\n");
                    break;
                case "input-edit": Set(draft, "Text", input + " changed"); Set(draft, "Revision", 8L); break;
                case "external-edit": File.WriteAllText(path, external); break;
            }
            return Task.FromResult(scenario == "unchanged" ? "" : "## Follow up\n- Add unique validation 7923\n");
        };
        Set(window, "RecommendedPlanDraftOverride", generator);
        var request = new OpenCodeRecommendedPlanRequest(project, "session-fixture", "message-fixture", path, PlanHash(original), null,
            () => owned, _ => { verified++; return Task.FromResult(owned); });
        try
        {
            var task = (Task<OpenCodeRecommendedPlanResult?>)Call(window, "RecommendProjectPlanAsync", request, cancellation.Token)!;
            var result = CompleteOnDispatcher(task);
            Require(generated == 1 && verified >= 1, "Dedicated fake planner/verification did not run exactly once.");
            string saved = File.ReadAllText(path);
            if (scenario is "save" or "unchanged")
            {
                Require(result is not null && result.PlanHash == PlanHash(saved) && result.Changed == (scenario == "save"), "Saved plan hash/change result was incorrect.");
                Require(saved.Contains("43127") && saved.Contains("E42") && (scenario != "save" || saved.Contains("7923")), "Recommended save lost a unique requirement.");
                if (scenario == "unchanged") Require(saved == original, "Empty recommended delta normalized or rewrote the original plan.");
            }
            else Require(result is null && saved == (scenario == "external-edit" ? external : original), "Canceled/stale planning overwrote the plan.");
            if (scenario == "draft-edit")
            {
                object edit = Field<IDictionary>(window, "_tabDocumentStates")[path]!;
                Require(((string)edit.GetType().GetProperty("Draft")!.GetValue(edit)!).Contains("User draft change."), "User plan draft was overwritten.");
            }
            Require(Field<string>(draft, "Text") == input + (scenario == "input-edit" ? " changed" : ""), "Planning modified the unsent request.");
            Require(Field<IDictionary>(window, "_planMakers").Count == 0 && Field<HashSet<string>>(window, "_recommendedPlanProjects").Count == 0,
                "Dedicated planner did not release its ownership after completion/cancellation.");
            Require(Application.Current.Windows.Count == initialWindows, "Fake planning opened a real planner window.");
        }
        finally { Call(window, "DisposeResources"); }
    }

    private static T CompleteOnDispatcher<T>(Task<T> task)
    {
        var frame = new DispatcherFrame();
        var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (task.IsCompleted || elapsed.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
        timer.Start();
        try { if (!task.IsCompleted) Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Require(task.IsCompleted, "Fake planner timed out.");
        return task.GetAwaiter().GetResult();
    }

    private static string PlanHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static Color ColorOf(ToggleButton loop) => ((SolidColorBrush)loop.Background).Color;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Members)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Members)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] args) => (target as Type ?? target.GetType()).GetMethod(name, Members)!.Invoke(target is Type ? null : target, args);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Capture(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
        Console.WriteLine("Snapshot: " + path);
    }
}
