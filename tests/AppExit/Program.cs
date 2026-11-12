using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using TerminalNotes;
using TerminalNotes.Services;

// Closing the app ends everything it runs (2026-11-12 사용자: "트레이에서 앱을 닫으면 모든 실행이 종료되어야 하는데? 개별로 터미널로
// 빼낸거 말고는 앱내에서 도는건 종료되게 해야해"). The real app window, in a process of its own like the app, opens a task terminal (a
// real OpenCode server and TUI; no model call) and a + 터미널 window, then closes the way 종료 does. Afterwards none of the programs
// it ran is left but the + 터미널 window. At its start it also stopped a server an earlier version (1.8.8 to 2.0.0) left running.
internal static class Program
{
    private static int _failures;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static void Assert(bool condition, string name)
    {
        Console.WriteLine((condition ? "ok   " : "FAIL ") + name);
        if (!condition) _failures++;
    }

    [STAThread]
    private static int Main(string[] args) => args is ["--app", var root] ? RunApp(root) : Check();

    private sealed record Seen(int Id, string Name, DateTime Started);
    private sealed record Report(List<Seen> App, List<Seen> Outside, int ServerId, bool Ready, string Log);

    // ── The test: what the app's process leaves behind.
    private static int Check()
    {
        string root = Path.Combine(Path.GetTempPath(), "TerminalNotes app exit " + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var started = new List<Process>();
        try
        {
            // Leftovers: only the same process is stopped (the start time written down must match); the list is deleted.
            string list = Path.Combine(root, "kept-servers.json");
            AppContext.SetData("TerminalNotes.KeptServersPath", list);
            var leftover = Sleeper(started);
            var other = Sleeper(started);
            WriteLeftovers(list, (leftover.Id, leftover.StartTime.ToUniversalTime()), (other.Id, other.StartTime.ToUniversalTime().AddHours(-1)), (999_999, null));
            int stopped = LeftoverServers.StopAll();
            Thread.Sleep(500);
            Assert(stopped == 1 && leftover.HasExited && !other.HasExited && !File.Exists(list), "a leftover server is stopped, another process with its id is not, the list is deleted");

            // The app in its own process; it finds a leftover server at start.
            var atStart = Sleeper(started);
            WriteLeftovers(list, (atStart.Id, atStart.StartTime.ToUniversalTime()));
            var app = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, ["--app", root]) { UseShellExecute = false, CreateNoWindow = true })!;
            started.Add(app);
            if (!app.WaitForExit(TimeSpan.FromMinutes(5))) { app.Kill(true); Assert(false, "the app closed within 5 minutes"); return 1; }
            string reportPath = Path.Combine(root, "report.json");
            if (!File.Exists(reportPath)) { Assert(false, "the app wrote its report (exit " + app.ExitCode + ")"); return 1; }
            var report = JsonSerializer.Deserialize<Report>(File.ReadAllText(reportPath))!;
            Console.Write(report.Log);
            Assert(atStart.HasExited && !File.Exists(list), "the app stopped the leftover server at start and deleted the list");
            Assert(report.Ready && report.ServerId != 0 && report.App.Any(seen => seen.Id == report.ServerId), "the task terminal ran its OpenCode server");
            Thread.Sleep(3000);
            var alive = report.App.Where(Running).ToList();
            Console.WriteLine($"app ran {report.App.Count}: {string.Join(", ", report.App.Select(seen => seen.Name + ":" + seen.Id))}");
            Assert(report.App.Count >= 2 && alive.Count == 0, "after the app closed none of its programs runs" +
                (alive.Count > 0 ? ": " + string.Join(", ", alive.Select(seen => seen.Name + ":" + seen.Id)) : ""));
            Console.WriteLine($"outside {report.Outside.Count}: {string.Join(", ", report.Outside.Select(seen => seen.Name + ":" + seen.Id))}");
            Assert(report.Outside.Any(seen => seen.Name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) && Running(seen)),
                "the + 터미널 window goes on");
            foreach (var seen in report.Outside.Where(Running)) try { Process.GetProcessById(seen.Id).Kill(true); } catch { }
        }
        catch (Exception exception) { _failures++; Console.WriteLine("FAIL " + exception); }
        finally
        {
            foreach (var process in started) try { if (!process.HasExited) process.Kill(true); } catch { }
            Thread.Sleep(500);
            try { Directory.Delete(root, true); } catch { }
        }
        Console.WriteLine(_failures == 0 ? "PASS app exit: everything the app ran ends with it but the + 터미널 window; leftovers stopped" : $"FAIL app exit ({_failures})");
        return _failures;
    }

    private static Process Sleeper(List<Process> started)
    {
        var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
        started.Add(process);
        return process;
    }

    private static void WriteLeftovers(string path, params (int Id, DateTime? Started)[] servers) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Enabled = true,
            Servers = servers.Select(server => new { Directory = Path.GetTempPath(), PaneKey = "1", Port = 1, ProcessId = server.Id, SessionId = "ses_x", Kept = DateTimeOffset.UtcNow, ProcessStart = server.Started })
        }));

    private static bool Running(Seen seen)
    {
        try
        {
            using var process = Process.GetProcessById(seen.Id);
            return !process.HasExited && Math.Abs((process.StartTime - seen.Started).TotalSeconds) < 2;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    // ── The app: a process without a console, like Terminal Notes.
    private static int RunApp(string root)
    {
        FreeConsole();
        var log = new System.Text.StringBuilder();
        void Log(string text) => log.AppendLine($"[{Clock.Elapsed:mm\\:ss}] {text}");
        string project = Path.Combine(root, "exit-task"), config = Path.Combine(root, "config"), settings = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(Path.Combine(config, "opencode", "skills"));
        File.WriteAllText(settings, JsonSerializer.Serialize(new { Workspace = root, CodexWorkspace = root, SetupCompleted = true, OpenCodeOnly = "Y", Width = 1100, Height = 700 }));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
        Environment.SetEnvironmentVariable("OPENCODE_DISABLE_AUTOUPDATE", "1");
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(root, "data"));
        AppContext.SetData("TerminalNotes.SkillsDirectory", Path.Combine(config, "opencode", "skills"));
        AppContext.SetData("TerminalNotes.MySkillsDirectory", Path.Combine(root, "my-skills"));
        AppContext.SetData("TerminalNotes.ClaudeSkillsDirectory", Path.Combine(root, "claude-skills"));
        AppContext.SetData("TerminalNotes.KeptServersPath", Path.Combine(root, "kept-servers.json"));
        AppContext.SetData("TerminalNotes.ScheduledJobsPath", Path.Combine(root, "jobs.json"));
        var report = new Report([], [], 0, false, "");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            MainWindow? window = null;
            try
            {
                var hooks = new OpenCodeStartupHooks(_ => Task.FromResult(true), () => false, _ => throw new InvalidOperationException("no installs in tests"));
                window = new MainWindow(settings, autoStartCodex: true, startupHooks: hooks) { ExternalScanOverride = _ => [] };
                window.Show();
                bool checkedAtStart = await Until(() => (bool)Field(window, "_openCodeReady")!, 60);
                Log($"OpenCode checked {checkedAtStart} · window {window.IsVisible} {window.ActualWidth:0}x{window.ActualHeight:0}");
                var pane = (TerminalPane)typeof(MainWindow).GetMethod("OpenTaskTerminal", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [project])!;
                int polls = 0;
                bool ready = await Until(() =>
                {
                    if (++polls % 40 == 1) Log($"  ready {pane.IsReady} running {pane.IsRunning} '{pane.ActivityLabel}' loaded {pane.IsLoaded} {pane.ActualWidth:0}x{pane.ActualHeight:0} disposed {pane.IsDisposed}");
                    return pane.IsReady;
                }, 150);
                Log($"task terminal ready {ready} · server {pane.ServerProcessId}");
                // + 터미널's own window: its start info and launcher, with a program that ends at once in place of OpenCode (the
                // PowerShell window stays, -NoExit), so a second OpenCode does not load on this small PC.
                window.StartExternalWindow(MainWindow.OpenCodeWindowStart(project, Path.Combine(Environment.SystemDirectory, "whoami.exe"), "exit-task", "{}"));
                await Task.Delay(3000);
                var tree = Descendants(Environment.ProcessId);
                var outsideRoots = tree.Where(seen => seen.Parent == Environment.ProcessId && seen.Name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)).Select(seen => seen.Id).ToHashSet();
                var outside = tree.Where(seen => Under(seen, outsideRoots, tree)).ToList();
                report = report with
                {
                    App = tree.Where(seen => !outside.Contains(seen)).Select(seen => new Seen(seen.Id, seen.Name, seen.Started)).ToList(),
                    Outside = outside.Select(seen => new Seen(seen.Id, seen.Name, seen.Started)).ToList(),
                    ServerId = pane.ServerProcessId ?? 0, Ready = ready
                };
                Log("exit question: " + window.ExitConfirmation().Replace('\n', ' '));
            }
            catch (Exception exception) { Log("FAIL " + exception); }
            finally
            {
                File.WriteAllText(Path.Combine(root, "report.json"), JsonSerializer.Serialize(report with { Log = log.ToString() }));
                // What 종료 does after the question.
                if (window is not null)
                {
                    typeof(MainWindow).GetMethod("DisposeResources", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                    window.Close();
                }
                app.Shutdown();
            }
        };
        app.Run();
        return 0;
    }

    private static object? Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private static async Task<bool> Until(Func<bool> condition, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(250);
        }
        return condition();
    }

    private sealed record Entry(int Id, int Parent, string Name, DateTime Started);

    private static bool Under(Entry seen, HashSet<int> roots, List<Entry> tree)
    {
        for (Entry? at = seen; at is not null; at = tree.FirstOrDefault(other => other.Id == at.Parent))
            if (roots.Contains(at.Id)) return true;
        return false;
    }

    /// <summary>Every process started under this one (a process list snapshot).</summary>
    private static List<Entry> Descendants(int rootId)
    {
        var all = new List<(int Id, int Parent, string Name)>();
        IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
        try
        {
            var entry = new ProcessEntry { Size = Marshal.SizeOf<ProcessEntry>() };
            for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                all.Add(((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile));
        }
        finally { CloseHandle(snapshot); }
        var found = new List<Entry>();
        // A child starts after its parent: a process whose parent id was reused by this one (OneDrive's, once) is not under it.
        DateTime rootStarted;
        using (var root = Process.GetProcessById(rootId)) rootStarted = root.StartTime;
        var parents = new Dictionary<int, DateTime> { [rootId] = rootStarted };
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var process in all)
            {
                if (!parents.TryGetValue(process.Parent, out var parentStarted) || parents.ContainsKey(process.Id)) continue;
                DateTime started;
                try { using var running = Process.GetProcessById(process.Id); started = running.StartTime; }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { continue; }
                if (started < parentStarted) continue;
                found.Add(new Entry(process.Id, process.Parent, process.Name, started));
                parents[process.Id] = started;
                grew = true;
            }
        }
        return found;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public int Size;
        public uint Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
}
