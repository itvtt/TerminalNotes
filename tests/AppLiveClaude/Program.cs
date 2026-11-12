using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TerminalNotes;
using TerminalNotes.Services;
using Application = System.Windows.Application;

// The real app with a real Claude Code task terminal (haiku): a scheduled job from the tray opens it and types its instruction; Claude
// Code asks for a permission (the board says blocked, the user is told, the exit question says its work would stop); approved, it
// finishes (told, and its record is read for learning); 지난 대화 검색 finds the conversation and goes on with claude --resume.
internal static class Program
{
    private static int _failures;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly List<string> Notices = [];

    [STAThread]
    private static int Main()
    {
        // Claude Code terminals for tasks are a build choice (features.json beside the app): on for this test only.
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "features.json"), "{ \"AgentSelection\": true }");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(); }
            catch (Exception exception) { _failures++; Console.WriteLine("FAIL " + exception); }
            finally { app.Shutdown(); }
        };
        app.Run();
        Console.WriteLine(_failures == 0 ? "PASS app live (Claude Code task terminal)" : $"FAIL app live (Claude Code) ({_failures})");
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
        string root = Path.Combine(Path.GetTempPath(), "TerminalNotes app claude " + Guid.NewGuid().ToString("N")[..8]);
        string project = Path.Combine(root, "csvtask"), settings = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new
        {
            Workspace = root, CodexWorkspace = root, SetupCompleted = true, OpenCodeOnly = "Y", Width = 1200, Height = 800, UseAutoMode = false,
            TaskAgents = new Dictionary<string, string> { [project] = "ClaudeCode" }
        }));
        Environment.SetEnvironmentVariable("ANTHROPIC_MODEL", "haiku");
        // Started from a Claude Code session, this test would pass on its markers and Claude Code would save no conversation.
        foreach (string name in Environment.GetEnvironmentVariables().Keys.Cast<string>()
                     .Where(name => name.StartsWith("CLAUDE_CODE", StringComparison.OrdinalIgnoreCase) || name.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase)).ToList())
            Environment.SetEnvironmentVariable(name, null);
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(root, "data"));
        AppContext.SetData("TerminalNotes.SkillsDirectory", Path.Combine(root, "opencode-skills"));
        AppContext.SetData("TerminalNotes.ClaudeSkillsDirectory", Path.Combine(root, "claude-skills"));
        AppContext.SetData("TerminalNotes.MySkillsDirectory", Path.Combine(root, "my-skills"));
        AppContext.SetData("TerminalNotes.KeptServersPath", Path.Combine(root, "kept-servers.json"));
        AppContext.SetData("TerminalNotes.ScheduledJobsPath", Path.Combine(root, "jobs.json"));
        var hooks = new OpenCodeStartupHooks(_ => Task.FromResult(true), () => false, _ => throw new InvalidOperationException("no installs in tests"));
        string done = Path.Combine(project, "done.txt");
        MainWindow? window = null;
        try
        {
            window = new MainWindow(settings, autoStartCodex: true, startupHooks: hooks)
            {
                AgentNotificationOverride = (title, body, _) => { Notices.Add(title + ": " + body.Replace('\n', ' ')); Log("notice " + title + ": " + body.Replace('\n', ' ')); },
                ExternalScanOverride = _ => []
            };
            window.Show();
            await Until(() => Field<bool>(window, "_openCodeReady"), "the app is ready", 30);
            Assert(AppFeatures.AgentSelection, "Claude Code task terminals are on for this test");
            Call(window, "HideToTray");

            // 1. The job from the tray opens the Claude Code terminal, which may first ask to trust the folder.
            var job = ScheduledJobs.Save(new ScheduledJobs.Job
            {
                Project = project, Days = ScheduledJobs.EveryDay, Time = DateTime.Now.AddMinutes(-1).ToString("HH:mm"), Created = DateTimeOffset.Now.AddMinutes(-10),
                Instruction = "Bash로 sleep 20 을 실행한 다음, 이 폴더에 done.txt 파일을 만들고 ok 라고 한 줄만 써라. 다른 것은 하지 마라."
            });
            int polls = 0;
            bool trusted = false;
            Task? answering = null;
            await Until(() =>
            {
                Call(window, "RunDueJobs");
                var pane = Panes(window).FirstOrDefault();
                if (pane is not null && answering is not { IsCompleted: false } && pane.IsAskingUser && ScheduledJobs.List().Single().LastResult is null)
                {
                    // Claude Code's first-run questions (folder trust, and others of this PC's setup): blocked on the board and told; the
                    // user answers them in the terminal. Trust: Yes. Anything else: its default (the safe No).
                    string screen = pane.ScreenText();
                    bool trust = screen.Contains("Yes, I trust this folder", StringComparison.OrdinalIgnoreCase);
                    Log($"question on screen ({(trust ? "folder trust" : screen.Split('\n').FirstOrDefault(line => line.Contains("detected", StringComparison.OrdinalIgnoreCase) || line.Contains('?'))?.Trim())}) · board state {State(pane)}");
                    if (!trusted) Assert(State(pane) == MainWindow.AgentState.Blocked, "Claude Code's question shows as blocked on the board");
                    trusted = true;
                    answering = AnswerAsync(pane, down: trust);
                }
                if (++polls % 5 == 1 && pane is not null) Log($"  ready {pane.IsReady} running {pane.IsRunning} typed {pane.IsTypedAgent} '{pane.ActivityLabel}' state {State(pane)} block '{pane.PlanExecutionBlockReason}'");
                if (polls == 10 && pane is not null) Log("screen:\n" + string.Join("\n", pane.ScreenText().Split('\n').Where(line => line.Trim().Length > 0).Take(30)));
                return ScheduledJobs.List().Single().LastResult is not null;
            }, "the job's instruction is typed into Claude Code", 180, 2000);
            var claude = Panes(window).Single();
            Log("job: " + ScheduledJobs.List().Single().LastResult);
            Assert(ScheduledJobs.List().Single().LastResult == "보냄", "the scheduled job was typed into the Claude Code terminal");
            Assert(claude.IsTypedAgent && claude.StartupCommand.StartsWith("claude", StringComparison.Ordinal), "in a Claude Code terminal");

            // 2. Claude Code asks to create the file: blocked on the board, told, and the exit question says the work would stop.
            bool shown = false;
            await Until(() =>
            {
                if (!shown && claude.IsTypedAgentWorking)
                {
                    shown = true;
                    Log("working screen:\n" + string.Join("\n", claude.ScreenText().Split('\n').Where(line => line.Trim().Length > 0).TakeLast(4)));
                    Log("board: " + window.AgentBoardSummary);
                    Assert(window.AgentBoardSummary.Contains("작업 중", StringComparison.Ordinal), "while Claude Code works the board says so");
                    string working = window.ExitConfirmation();
                    Log("exit question while working: " + working.Replace('\n', ' '));
                    Assert(working.Contains("작업 중인 에이전트 1개(csvtask)도 멈춥니다", StringComparison.Ordinal), "the exit question says its work would stop");
                }
                return claude.IsAskingUser || File.Exists(done);
            }, "Claude Code asks for permission (or is done)", 120, 500);
            Log("screen:\n" + string.Join("\n", claude.ScreenText().Split('\n').Where(line => line.Trim().Length > 0).TakeLast(12)));
            if (!File.Exists(done))
            {
                await Task.Delay(4000);
                Log("board: " + window.AgentBoardSummary + " · notices: " + string.Join(" | ", Notices));
                Assert(window.AgentBoardSummary.Contains("막힘", StringComparison.Ordinal), "the board says it is blocked");
                Assert(Notices.Any(notice => notice.StartsWith("확인 필요", StringComparison.Ordinal)), "the user is told it needs them");
                string question = window.ExitConfirmation();
                Log("exit question: " + question.Replace('\n', ' '));
                Assert(question.Contains("작업 중인 에이전트 1개(csvtask)도 멈춥니다", StringComparison.Ordinal), "the exit question says its work would stop");
                Log("approving the first choice");
                await AnswerAsync(claude, down: false);
            }

            // 3. It finishes: told, and its record read for learning (too short a task to ask the model about).
            await Until(() => File.Exists(done), "the work is done", 120, 1000);
            await Until(() => Notices.Any(notice => notice.StartsWith("작업 완료", StringComparison.Ordinal)), "the finish is told", 60, 1000);
            var sessions = ClaudeCodeSessions.List(project);
            await Until(() => sessions.Any(session => MySkills.ReadSettings().LearnedUpTo.ContainsKey(session.Id)), "its Claude Code record was read for learning", 30, 1000);
            Assert(sessions.Count == 1, $"Claude Code's record for the task is found ({sessions.Count})");

            // 4. 지난 대화 검색 from the window finds it (no OpenCode needed) and goes on with claude --resume.
            window.Show();
            window.WindowState = WindowState.Normal;
            await Task.Delay(500);
            string? command = null;
            ConversationSearchWindow.ResumeOverride = (_, run) => command = run;
            Call(window, "OpenConversationSearch");
            var search = Application.Current.Windows.OfType<ConversationSearchWindow>().Single();
            await Task.Delay(400);
            Descendants(search).OfType<TextBox>().First().Text = "done.txt";
            await Until(() => Descendants(search).OfType<Border>().Any(border => System.Windows.Automation.AutomationProperties.GetAutomationId(border) == "ConversationHit"),
                "the Claude Code conversation is found", 30);
            var hit = Descendants(search).OfType<Border>().First(border => System.Windows.Automation.AutomationProperties.GetAutomationId(border) == "ConversationHit");
            Log("hit: " + string.Join(" | ", Texts(hit).Take(2)));
            Assert(Texts(hit).Any(text => text.Contains("Claude Code", StringComparison.Ordinal)), "the hit says it is a Claude Code conversation");
            Descendants(hit).OfType<Button>().First(button => button.Content as string == "이어가기").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(command == "claude --resume " + sessions[0].Id, "이어가기 goes on with claude --resume: " + command);
            search.Close();

            Assert(window.ExitConfirmation().StartsWith("앱을 닫으면", StringComparison.Ordinal), "with the work done, the exit question is the usual one");
        }
        finally
        {
            if (window is not null)
            {
                Call(window, "DisposeResources");
                window.Close();
            }
            await Task.Delay(1500);
            try { Directory.Delete(ClaudeCodeSessions.FolderFor(project), true); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static List<TerminalPane> Panes(MainWindow window) =>
        ((TabControl)window.FindName("TerminalTabs")).Items.Cast<TabItem>().Select(tab => (TerminalPane)tab.Tag.GetType().GetProperty("Pane")!.GetValue(tab.Tag)!).ToList();

    private static MainWindow.AgentState State(TerminalPane pane) => MainWindow.StateOf(pane);

    // Answers a choice on Claude Code's screen with key presses, as the user would (not a paste): optionally one down, then Enter.
    private static async Task AnswerAsync(TerminalPane pane, bool down)
    {
        await Task.Delay(1500);
        var press = typeof(TerminalPane).GetMethod("HandleNativeSurfaceInput", BindingFlags.Instance | BindingFlags.NonPublic)!;
        if (down) { press.Invoke(pane, ["\u001b[B", false, null]); await Task.Delay(500); }
        press.Invoke(pane, ["\r", false, null]);
    }

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

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var inner in Descendants(child)) yield return inner;
        }
    }

    private static IEnumerable<string> Texts(DependencyObject root) =>
        Descendants(root).OfType<TextBlock>().Where(text => text.Text.Length > 0).Select(text => text.Text);
}
