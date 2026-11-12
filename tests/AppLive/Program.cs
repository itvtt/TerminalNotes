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
// works: the question says the agent stops, and its server and work end with the app; the app opens again with nothing running,
// the task's conversation is found in 지난 대화 검색 and goes on in that session; closing then stops the new server.
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
        Console.WriteLine(_failures == 0 ? "PASS app live: scheduled job, board, closing ends the work, nothing comes back, search and resume" : $"FAIL app live ({_failures})");
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
        AppContext.SetData("TerminalNotes.ClaudeSkillsDirectory", Path.Combine(root, "claude-skills"));
        AppContext.SetData("TerminalNotes.KeptServersPath", Path.Combine(root, "kept-servers.json"));
        AppContext.SetData("TerminalNotes.ScheduledJobsPath", Path.Combine(root, "jobs.json"));
        var hooks = new OpenCodeStartupHooks(_ => Task.FromResult(true), () => false, _ => throw new InvalidOperationException("no installs in tests"));
        string done = Path.Combine(project, "done.txt");
        int serverId = 0, againId = 0;
        string session = "";
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

            // ── 2. Close while it works: the question says the agent stops; its server and work end with the app (2026-11-12 사용자:
            // "트레이에서 앱을 닫으면 모든 실행이 종료되어야 하는데"). (The window's × only hides it in the tray; 종료 asks, then disposes
            // and closes: the same steps here, without shutting the test down.)
            session = pane.LearningSource?.SessionId ?? "";
            serverId = pane.ServerProcessId ?? 0;
            string question = first.ExitConfirmation();
            Log("exit question: " + question.Replace('\n', ' '));
            Assert(question.StartsWith("앱을 닫으면 열려 있는 모든 터미널", StringComparison.Ordinal) &&
                question.Contains("작업 중인 에이전트 1개(report-task)도 멈춥니다", StringComparison.Ordinal), "the exit question says the working agent stops");
            Call(first, "DisposeResources");
            first.Close();
            await Until(() => !Running(serverId), "closing the app stops the working agent's server", 15);
            Assert(!File.Exists(done), "the work is not done when the app closes");
            // The instruction waits 60 seconds before writing done.txt: with the server gone it never comes.
            await Task.Delay(75_000);
            Assert(!File.Exists(done), "the work does not go on after the app closed");

            // ── 3. The app again: nothing comes back on its own; the task's terminal is opened by hand.
            var second = Open(settings, hooks);
            await Until(() => Field<bool>(second, "_openCodeReady"), "OpenCode checked at start", 30);
            await Task.Delay(3000);
            Assert(Panes(second).Count == 0, "no terminal opens again on its own");
            second.GetType().GetMethod("OpenTaskTerminal", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(second, [project]);
            var again = Panes(second).Single();
            await Until(() => again.IsReady, "the task's terminal is ready", 120);
            againId = again.ServerProcessId ?? 0;
            Assert(againId != 0 && againId != serverId, "with a server of its own");

            // ── 4b. 지난 대화 검색 from the window's button, through this terminal's server: the job's conversation is found and 이어가기
            // opens that session.
            second.WindowState = WindowState.Normal;
            await Task.Delay(500);
            (string Project, string Session)? resumed = null;
            ConversationSearchWindow.ResumeOverride = (session, _) => resumed = (session.Project, session.Id);
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
            Assert(resumed is { } chosen && chosen.Session == session && string.Equals(Path.GetFullPath(chosen.Project), Path.GetFullPath(project), StringComparison.OrdinalIgnoreCase),
                "이어가기 opens that very session in its task folder");
            search.Close();

            // ── 4. Closing with the agent idle stops its server too.
            Assert(second.ExitConfirmation() == "앱을 닫으면 열려 있는 모든 터미널과 실행 중인 프로그램이 종료됩니다. 닫을까요?", "with the agent idle, the exit question is the usual one");
            Call(second, "DisposeResources");
            second.Close();
            await Until(() => !Running(againId), "closing the app stops the idle agent's server", 15);
        }
        finally
        {
            foreach (int id in new[] { serverId, againId })
                if (id != 0 && Running(id)) try { Process.GetProcessById(id).Kill(true); } catch { }
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
        $"ready {p.IsReady} running {p.IsRunning} '{p.ActivityLabel}' server {p.ServerProcessId} loaded {p.IsLoaded} size {p.ActualWidth:0}x{p.ActualHeight:0}" +
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
