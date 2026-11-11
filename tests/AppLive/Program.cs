using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TerminalNotes;
using TerminalNotes.Services;
using Application = System.Windows.Application;

// End to end in the real app window with real OpenCode terminals and the real (free) model:
// a scheduled job opens its task's terminal and sends its instruction; the agent works (the board says so); the app closes while it
// works and its server is kept; the work finishes while the app is closed; the app opens again, takes the same server and session
// back, tells that the work finished meanwhile and learns from it; closing then with the agent idle stops the server.
internal static class Program
{
    private static int _failures;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly List<string> Notices = [];
    private static readonly List<string> Learned = [];

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(); }
            catch (Exception exception) { _failures++; Console.WriteLine("FAIL " + exception); }
            finally { app.Shutdown(); }
        };
        app.Run();
        Console.WriteLine(_failures == 0 ? "PASS app live: scheduled job, board, keep running across close, take back, told and learned" : $"FAIL app live ({_failures})");
        return _failures;
    }

    private static void Assert(bool condition, string name)
    {
        Console.WriteLine((condition ? "ok   " : "FAIL ") + name);
        if (!condition) _failures++;
    }

    private static void Log(string text) => Console.WriteLine($"[{Clock.Elapsed:mm\\:ss}] {text}");

    private static async Task RunAsync()
    {
        var installed = await AutoInstall.CheckAsync(CancellationToken.None);
        if (!installed.IsInstalled) throw new InvalidOperationException("OpenCode is not installed");
        string root = Path.Combine(Path.GetTempPath(), "TerminalNotes app live " + Guid.NewGuid().ToString("N")[..8]);
        string project = Path.Combine(root, "report-task"), config = Path.Combine(root, "config"), settings = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(Path.Combine(config, "opencode", "skills"));
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new { Workspace = root, CodexWorkspace = root, SetupCompleted = true, OpenCodeOnly = "Y", Width = 1200, Height = 800 }));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
        Environment.SetEnvironmentVariable("OPENCODE_DISABLE_AUTOUPDATE", "1");
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(root, "data"));
        AppContext.SetData("TerminalNotes.SkillsDirectory", Path.Combine(config, "opencode", "skills"));
        AppContext.SetData("TerminalNotes.MySkillsDirectory", Path.Combine(root, "my-skills"));
        AppContext.SetData("TerminalNotes.KeptServersPath", Path.Combine(root, "kept-servers.json"));
        AppContext.SetData("TerminalNotes.ScheduledJobsPath", Path.Combine(root, "jobs.json"));
        var hooks = new OpenCodeStartupHooks(_ => Task.FromResult(true), () => false, _ => throw new InvalidOperationException("no installs in tests"));
        string done = Path.Combine(project, "done.txt");
        int keptId = 0;
        string keptSession = "";
        try
        {
            // ── 1. The app with a scheduled job due now.
            var first = Open(settings, hooks);
            await Until(() => Field<bool>(first, "_openCodeReady"), "OpenCode checked at start", 30);
            // A scheduled job usually comes while the app sits in the tray.
            Call(first, "HideToTray");
            await Task.Delay(500);
            Log("window visible: " + first.IsVisible);
            var now = DateTime.Now;
            var job = ScheduledJobs.Save(new ScheduledJobs.Job
            {
                Project = project, Days = ScheduledJobs.EveryDay, Time = now.AddMinutes(-1).ToString("HH:mm"), Created = DateTimeOffset.Now.AddMinutes(-10),
                Instruction = "PowerShell로 Start-Sleep -Seconds 60 을 실행한 다음, 이 폴더에 done.txt 파일을 만들고 ok 라고 써라. 그 외에는 아무것도 하지 마라."
            });
            Log("job saved for " + ScheduledJobs.When(job));
            int polls = 0;
            await Until(() =>
            {
                Call(first, "RunDueJobs");
                if (++polls % 5 == 1)
                {
                    var panes = Panes(first);
                    Log($"  window visible {first.IsVisible} {first.WindowState} · tabs {panes.Count}" + string.Join("", panes.Select(p => $" · ready {p.IsReady} running {p.IsRunning} '{p.ActivityLabel}'" +
                        $" loaded {p.IsLoaded} visible {p.IsVisible} size {p.ActualWidth:0}x{p.ActualHeight:0} dims {Field<object?>(p, "_pendingWebReadyDimensions")} bg {Field<bool>(p, "_allowBackgroundAutomationStartup")}" +
                        $" failed {Field<bool>(p, "_failed")} tui {Field<bool>(p, "_usesNativeOpenCodeTui")} nativeSurface {Field<bool>(p, "_useNativeTerminalSurface")} init {Field<object?>(p, "_initializeTask") is not null}" +
                        (Field<object?>(p, "_nativeTerminalSurface") is FrameworkElement s ? $" surface loaded {s.IsLoaded} {s.Visibility} {s.ActualWidth:0}x{s.ActualHeight:0}" : " no surface") +
                        $" notice '{(Field<object?>(p, "_notice") as TextBlock)?.Text}'")) +
                        $" · due {ScheduledJobs.Check(ScheduledJobs.List().Single(), DateTime.Now).Due}");
                    if (panes.Count == 0)
                        try { first.GetType().GetMethod("OpenTaskTerminal", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(first, [project]); }
                        catch (TargetInvocationException exception) { Log("  open failed: " + exception.InnerException?.Message); }
                }
                return ScheduledJobs.List().Single().LastResult is not null;
            }, "the due job is sent", 150, 2000);
            var sent = ScheduledJobs.List().Single();
            Log($"job result: {sent.LastResult}");
            Assert(sent.LastResult == "보냄", "the scheduled job opened its task's terminal and sent the instruction");
            var pane = Panes(first).Single();
            Assert(string.Equals(Path.GetFullPath(pane.WorkingDirectory), Path.GetFullPath(project), StringComparison.OrdinalIgnoreCase), "in the task's own terminal");
            await Until(() => !first.IsVisible, "the window goes back to the tray once the terminal runs", 10);
            Assert(SystemParameters.VirtualScreenLeft <= first.Left && first.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth,
                $"and back where it was ({first.Left:0})");
            await Until(() => State(pane) == MainWindow.AgentState.Working, "the agent works", 90);
            await Task.Delay(1500);
            Log("board: " + first.AgentBoardSummary + " · " + pane.ActivityLabel);
            Assert(first.AgentBoardSummary.Contains("작업 중", StringComparison.Ordinal), "the board says it works");
            Assert(Notices.Count == 0, "working is not notified");

            // ── 2. Close while it works: the confirmation says it goes on; the server is kept. (The window's × only hides it in the
            // tray; 종료 asks, then disposes and closes: the same steps here, without shutting the test down.)
            keptSession = pane.LearningSource?.SessionId ?? "";
            string question = first.ExitConfirmation();
            Log("exit question: " + question.Replace('\n', ' '));
            Assert(question.StartsWith("작업 중인 에이전트 1개(report-task)는 앱을 닫아도 계속 실행되고", StringComparison.Ordinal), "the exit question says the working agent goes on");
            Call(first, "DisposeResources");
            first.Close();
            await Task.Delay(1500);
            var kept = KeptServers.List();
            Log($"kept: {string.Join(", ", kept.Select(server => $"{server.ProcessId}:{server.Port}:{server.SessionId}"))}");
            Assert(kept.Count == 1 && KeptServers.Alive(kept[0]) && kept[0].SessionId == keptSession, "closing the app keeps the working agent's server");
            keptId = kept.FirstOrDefault()?.ProcessId ?? 0;
            Assert(!File.Exists(done), "the work is not done yet when the app closes");

            // ── 3. The work goes on while the app is closed.
            await Until(() => File.Exists(done), "the work finishes while the app is closed", 300, 2000);
            Log("done.txt: " + File.ReadAllText(done).Trim());
            await Task.Delay(8000);

            // ── 4. The app again: the same server and session are taken back; the finish is told and learned from.
            var second = Open(settings, hooks);
            await Until(() => Panes(second).Count == 1, "the task's terminal opens again at start", 60);
            var again = Panes(second).Single();
            int readyPolls = 0;
            await Until(() =>
            {
                if (++readyPolls % 40 == 1) Log("  " + Describe(again));
                return again.IsReady;
            }, "the terminal is ready", 120);
            second.WindowState = WindowState.Minimized;
            Log($"taken back: pid {again.ServerProcessId} (kept {keptId}), session {again.LearningSource?.SessionId}");
            Assert(again.WasKeptRunning && again.ServerProcessId == keptId, "it took the same server back (no new one)");
            Assert(again.LearningSource?.SessionId == keptSession, "in the same session");
            Assert(KeptServers.List().Count == 0, "taken back, it is no longer written down");
            // Told as a notification, or (the user looking at that very terminal) on the window's status line.
            await Until(() => Notices.Any(notice => notice.Contains("앱을 닫은 동안", StringComparison.Ordinal)) ||
                Texts(second).Any(text => text.Contains("앱을 닫은 동안 작업을 마쳤습니다", StringComparison.Ordinal)), "the finish while closed is told", 40);
            Log("notices: " + string.Join(" | ", Notices));
            Assert(Learned.Count == 1, "and learned from once");

            // ── 4b. 지난 대화 검색 from the window's button, through this terminal's server: the job's conversation is found and 이어가기
            // opens that session.
            second.WindowState = WindowState.Normal;
            await Task.Delay(500);
            (string Project, string Session)? resumed = null;
            ConversationSearchWindow.ResumeOverride = (folder, session) => resumed = (folder, session);
            Call(second, "OpenConversationSearch");
            var search = Application.Current.Windows.OfType<ConversationSearchWindow>().Single();
            await Task.Delay(500);
            Descendants(search).OfType<TextBox>().First().Text = "done.txt";
            await Until(() => Descendants(search).OfType<Border>().Any(border => System.Windows.Automation.AutomationProperties.GetAutomationId(border) == "ConversationHit"),
                "the job's conversation is found in the search window", 60);
            var hit = Descendants(search).OfType<Border>().First(border => System.Windows.Automation.AutomationProperties.GetAutomationId(border) == "ConversationHit");
            Log("search: " + string.Join(" | ", Texts(hit).Take(3)));
            Assert(Texts(hit).Any(text => text.Contains("report-task", StringComparison.Ordinal)), "the hit names its task");
            Descendants(hit).OfType<Button>().First(button => button.Content as string == "이어가기").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(resumed is { } chosen && chosen.Session == keptSession && string.Equals(Path.GetFullPath(chosen.Project), Path.GetFullPath(project), StringComparison.OrdinalIgnoreCase),
                "이어가기 opens that very session in its task folder");
            search.Close();

            // ── 5. Closing with the agent idle stops the server.
            Assert(second.ExitConfirmation().StartsWith("앱을 닫으면 열려 있는 모든 터미널", StringComparison.Ordinal), "with the agent idle, the exit question says everything ends");
            Call(second, "DisposeResources");
            second.Close();
            await Task.Delay(6000);
            Assert(!Running(keptId) && KeptServers.List().Count == 0, "closing with an idle agent stops its server and keeps nothing");
        }
        finally
        {
            if (keptId != 0 && Running(keptId)) try { Process.GetProcessById(keptId).Kill(true); } catch { }
            foreach (var server in KeptServers.List()) KeptServers.Stop(server);
            await Task.Delay(500);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static MainWindow Open(string settings, OpenCodeStartupHooks hooks)
    {
        var window = new MainWindow(settings, autoStartCodex: true, startupHooks: hooks)
        {
            AgentNotificationOverride = (title, body, _) => { Notices.Add(title + ": " + body.Replace('\n', ' ')); Log("notice " + title + ": " + body.Replace('\n', ' ')); },
            LearnOverride = pane => { Learned.Add(pane.WorkingDirectory); Log("learn " + pane.WorkingDirectory); },
            ExternalScanOverride = _ => []
        };
        window.Show();
        return window;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var inner in Descendants(child)) yield return inner;
        }
    }

    private static IEnumerable<string> Texts(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock { Text.Length: > 0 } text) yield return text.Text;
            foreach (string inner in Texts(child)) yield return inner;
        }
    }

    private static string Describe(TerminalPane p) =>
        $"ready {p.IsReady} running {p.IsRunning} '{p.ActivityLabel}' kept {p.WasKeptRunning} server {p.ServerProcessId} loaded {p.IsLoaded} size {p.ActualWidth:0}x{p.ActualHeight:0}" +
        $" dims {Field<object?>(p, "_pendingWebReadyDimensions")} started {Field<int>(p, "_openCodeTuiStartAttempted")} failed {Field<bool>(p, "_failed")}" +
        $" wait '{Field<object?>(p, "_resourceWaitReason")}' notice '{(Field<object?>(p, "_notice") as TextBlock)?.Text}' stage '{Field<object?>(p, "_tuiConnectionStage")}'";

    private static List<TerminalPane> Panes(MainWindow window) =>
        ((TabControl)window.FindName("TerminalTabs")).Items.Cast<TabItem>().Select(tab => (TerminalPane)tab.Tag.GetType().GetProperty("Pane")!.GetValue(tab.Tag)!).ToList();

    private static MainWindow.AgentState State(TerminalPane pane) => MainWindow.AgentStateOf(pane.Activity, pane.HasCompletedWork);

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void Call(object target, string name) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);

    private static async Task Until(Func<bool> condition, string name, int seconds, int every = 250)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (condition()) { Log("✓ " + name); return; }
            await Task.Delay(every);
        }
        throw new TimeoutException(name);
    }

    private static bool Running(int id)
    {
        try { using var process = Process.GetProcessById(id); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
