using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TerminalNotes;
using Application = System.Windows.Application;
using Entry = TerminalNotes.ExternalAgents.Entry;

// 예약 작업 (when it runs, missed and busy rules, its window), 외부 터미널 감지 (which processes are outside agents) and 지난 대화 검색
// (sessions listed, matches and snippets, its window).
internal static class Program
{
    private static int _failures;

    [STAThread]
    private static int Main()
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(); }
            catch (Exception ex) { _failures++; Console.WriteLine("FAIL " + ex); }
            finally { app.Shutdown(); }
        };
        app.Run();
        Console.WriteLine(_failures == 0 ? "PASS herdr extras: scheduled jobs, outside agents, conversation search" : $"FAIL herdr extras ({_failures})");
        return _failures;
    }

    private static void Assert(bool condition, string name)
    {
        if (condition) return;
        _failures++;
        Console.WriteLine("FAIL " + name);
    }

    private static async Task RunAsync()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes herdr extras " + Guid.NewGuid().ToString("N"));
        string task = Path.Combine(fixture, "build-task");
        Directory.CreateDirectory(task);
        AppContext.SetData("TerminalNotes.ScheduledJobsPath", Path.Combine(fixture, "jobs.json"));
        try
        {
            Jobs(task);
            Outside();
            Search();
            await Windows(task);
        }
        finally { try { Directory.Delete(fixture, true); } catch { } }
    }

    private static void Jobs(string task)
    {
        // 2026-11-09 is a Monday.
        var weekdays = new ScheduledJobs.Job { Project = task, Instruction = "빌드 확인", Days = ScheduledJobs.Weekdays, Time = "09:00", Created = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.FromHours(9)) };
        Assert(ScheduledJobs.NextRun(weekdays, new DateTime(2026, 11, 7, 10, 0, 0)) == new DateTime(2026, 11, 9, 9, 0, 0), "a weekday job skips the weekend");
        Assert(ScheduledJobs.NextRun(weekdays, new DateTime(2026, 11, 9, 8, 59, 0)) == new DateTime(2026, 11, 9, 9, 0, 0), "the same day before its time");
        Assert(ScheduledJobs.NextRun(weekdays, new DateTime(2026, 11, 9, 9, 0, 0)) == new DateTime(2026, 11, 10, 9, 0, 0), "after its time, the next day");
        var created = new DateTimeOffset(2026, 11, 9, 8, 0, 0, TimeSpan.FromHours(9));
        var job = weekdays with { Created = created };
        Assert(ScheduledJobs.Check(job, new DateTime(2026, 11, 9, 8, 30, 0)).Due == ScheduledJobs.Due.NotYet, "not yet");
        Assert(ScheduledJobs.Check(job, new DateTime(2026, 11, 9, 9, 0, 30)) is (ScheduledJobs.Due.Now, var at) && at == new DateTime(2026, 11, 9, 9, 0, 0), "due at its time");
        Assert(ScheduledJobs.Check(job, new DateTime(2026, 11, 9, 16, 0, 0)).Due == ScheduledJobs.Due.Missed, "missed by more than six hours");
        var ran = job with { LastRun = new DateTimeOffset(2026, 11, 9, 9, 1, 0, TimeSpan.FromHours(9)) };
        Assert(ScheduledJobs.Check(ran, new DateTime(2026, 11, 9, 12, 0, 0)).Due == ScheduledJobs.Due.NotYet, "once a day");
        Assert(ScheduledJobs.Check(ran with { Enabled = false }, new DateTime(2026, 11, 10, 9, 0, 30)).Due == ScheduledJobs.Due.NotYet, "an off job does not run");
        Assert(ScheduledJobs.When(job) == "평일 09:00" && ScheduledJobs.When(job with { Days = 0b10101 }) == "월·수·금 09:00", "when it runs, in words");

        // Saving checks what it is given and keeps it.
        Assert(Refused(job with { Instruction = " " }) && Refused(job with { Time = "25:00" }) && Refused(job with { Days = 0 }) && Refused(job with { Project = task + "-gone" }),
            "a job without an instruction, a time, a day or its task is refused");
        var saved = ScheduledJobs.Save(job with { Time = "7:05" });
        Assert(saved.Time == "07:05" && ScheduledJobs.List().Single().Id == saved.Id, "a job is saved with its time written out");
        ScheduledJobs.MarkRun(saved.Id, DateTimeOffset.Now, "보냄");
        ScheduledJobs.SetEnabled(saved.Id, false);
        Assert(ScheduledJobs.List().Single() is { LastResult: "보냄", Enabled: false }, "its last result and switch are kept");
        ScheduledJobs.Delete(saved.Id);
        Assert(ScheduledJobs.List().Count == 0, "a job can be deleted");
    }

    private static bool Refused(ScheduledJobs.Job job)
    {
        try { ScheduledJobs.Save(job); return false; }
        catch (ArgumentException) { return true; }
    }

    private static void Outside()
    {
        const int app = 100;
        var all = new List<Entry>
        {
            new(1, 0, "explorer.exe"), new(app, 1, "TerminalNotes.exe"),
            new(101, app, "opencode.exe"),            // the app's server
            new(102, app, "opencode.exe"),            // the app's TUI
            new(103, 101, "opencode.exe"),            // a server's worker
            new(110, app, "powershell.exe"), new(111, 110, "claude.exe"),  // + 터미널 window, Claude Code typed in it
            new(112, 111, "node.exe"),
            new(120, 1, "WindowsTerminal.exe"), new(121, 120, "pwsh.exe"), new(122, 121, "codex.exe"),
            new(130, 1, "opencode.exe"),              // a server kept from an earlier run, held by a terminal
            new(140, 101, "bash.exe"), new(141, 140, "claude.exe"),       // an agent of the app's own server ran claude
            new(150, 1, "notepad.exe")
        };
        var found = ExternalAgents.Find(all, app, new HashSet<int> { 130, 101 }).Select(entry => entry.Id).ToList();
        Assert(found.SequenceEqual([111, 122]), "outside agents are the ones typed in other terminals: " + string.Join(",", found));
        Assert(ExternalAgents.Find(all, app, new HashSet<int>()).Select(entry => entry.Id).Contains(130), "a kept server not held by a terminal shows");
        Assert(ExternalAgents.Snapshot().Any(entry => entry.Id == Environment.ProcessId), "the process snapshot sees this process");
    }

    private static void Search()
    {
        using var list = JsonDocument.Parse("""
            [{"id":"ses_a","title":"빌드 오류 고치기","time":{"updated":1762837200000}},
             {"id":"ses_b","title":"하위","parentID":"ses_a","time":{"updated":1}},
             {"id":"ses_c","title":"Terminal Notes · 스킬 배우기","time":{"updated":2}},
             {"id":"ses_d","title":"","time":{"updated":3}}]
            """);
        var sessions = ConversationSearchWindow.Sessions(list.RootElement, @"C:\work\app").ToList();
        Assert(sessions.Select(session => session.Id).SequenceEqual(["ses_a", "ses_d"]) && sessions[1].Title == "(제목 없음)", "sub-agent and the app's own sessions are left out");
        var text = "사용자: 빌드가 깨져\n[도구 bash] command=dotnet build (실패: CS0119 Key)\n에이전트: 이름 충돌을 고쳤습니다.";
        var hit = ConversationSearchWindow.Match(sessions[0], text, "cs0119");
        Assert(hit is { } found && found.Snippet.Contains("CS0119", StringComparison.Ordinal), "a match in what was run, with its snippet");
        Assert(ConversationSearchWindow.Match(sessions[0], text, "오류 고치") is { Snippet: "" }, "a match in the title");
        Assert(ConversationSearchWindow.Match(sessions[0], text, "배포") is null, "no match");
    }

    private static async Task Windows(string task)
    {
        ScheduledJobs.Save(new ScheduledJobs.Job { Project = task, Instruction = "빌드하고 테스트해서 실패하면 고쳐", Days = ScheduledJobs.Weekdays, Time = "09:00" });
        var jobs = new ScheduledJobsWindow([task], Path.GetFileName);
        jobs.Show();
        await Task.Delay(500);
        Assert(Descendants(jobs).OfType<Border>().Count(border => AutomationProperties.GetAutomationId(border) == "ScheduledJobRow") == 1, "the job is listed");
        await SaveImage(jobs, "scheduled-jobs.png");
        jobs.Close();

        var search = new ConversationSearchWindow(() => null, [task]);
        search.Show();
        await Task.Delay(400);
        var box = Descendants(search).OfType<TextBox>().First();
        box.Text = "빌드";
        await Task.Delay(900);
        Assert(Descendants(search).OfType<TextBlock>().Any(text => text.IsVisible && text.Text.StartsWith("열린 OpenCode 터미널", StringComparison.Ordinal)), "without a running server it says what is needed");
        await SaveImage(search, "conversation-search.png");
        search.Close();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var inner in Descendants(child)) yield return inner;
        }
    }

    private static async Task SaveImage(Window window, string name)
    {
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        string folder = Path.Combine(Path.GetTempPath(), "TerminalNotes.HerdrExtras");
        Directory.CreateDirectory(folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, name));
        encoder.Save(stream);
        Console.WriteLine("PREVIEW " + Path.Combine(folder, name));
    }
}
