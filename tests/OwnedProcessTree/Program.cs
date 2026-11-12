using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using TerminalNotes.Services;

internal static class Program
{
    private static int _assertions;
    private static readonly string Executable = Environment.ProcessPath!;
    private static readonly Type? OwnerType = typeof(OpenCodeHeadlessHost).Assembly.GetType("TerminalNotes.Services.OwnedWindowsProcess");

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--leaf")
        {
            await WriteReadyAsync(args[1], JsonSerializer.Serialize(new Receipt(Environment.ProcessId, DateTimeOffset.UtcNow, Environment.GetEnvironmentVariable("OWNED_FIXTURE_UNICODE"), Environment.CurrentDirectory, args.Skip(2).ToArray())));
            await Task.Delay(Timeout.Infinite);
            return 0;
        }
        if (args.Length > 0 && args[0] == "--parent")
        {
            using var child = Process.Start(Info("--leaf", args[1], "한글 공백", "quote\"end", "slash\\"))!;
            await UntilAsync(() => File.Exists(args[1]), "test leaf readiness");
            if (args.Contains("--hold")) await Task.Delay(Timeout.Infinite);
            return 0;
        }
        if (args.Length > 0 && args[0] is "--probe" or "--probe-exit")
        {
            using var child = Process.Start(Info("--leaf", args[1]))!;
            await UntilAsync(() => File.Exists(args[1]), "probe child readiness");
            Console.WriteLine("OpenCode 6.7.8");
            Console.Write(new string('x', 128 * 1024));
            Console.Error.Write(new string('e', 128 * 1024));
            if (args[0] == "--probe") await Task.Delay(Timeout.Infinite);
            return 0;
        }
        if (args.Length > 0 && args[0] == "--owner")
        {
            var owned = Start(Info("--parent", args[1], "--hold"));
            await UntilAsync(() => File.Exists(args[1]), "owned test leaf readiness");
            await WriteReadyAsync(args[2], owned.Process.Id.ToString());
            await Task.Delay(Timeout.Infinite);
            return 0;
        }
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes owned process " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var ownedHandles = new List<Process>();
        var owners = new List<IDisposable>();
        try
        {
            Process sentinel = Process.Start(Info("--leaf", Path.Combine(fixture, "sentinel.json")))!;
            ownedHandles.Add(sentinel);
            await UntilAsync(() => File.Exists(Path.Combine(fixture, "sentinel.json")), "unrelated sentinel readiness");
            string escapedLeaf = Path.Combine(fixture, "parent-exit.json");
            var escaped = Start(Info("--parent", escapedLeaf));
            if (escaped.Owner is not null) owners.Add(escaped.Owner);
            ownedHandles.Add(escaped.Process);
            await UntilAsync(() => File.Exists(escapedLeaf), "early-exit leaf readiness");
            Receipt escapedReceipt = Read(escapedLeaf);
            Capture(escapedReceipt.ProcessId, ownedHandles);
            await escaped.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using (var host = new OpenCodeHeadlessHost(new Uri("http://127.0.0.1:43127/")))
            {
                Set(host, "_process", escaped.Process);
                if (escaped.Owner is not null) Set(host, "_ownedProcess", escaped.Owner);
                host.Dispose();
            }
            await Task.Delay(250);
            Require(!IsAlive(escapedReceipt.ProcessId), "A test child outlived its exited wrapper and host disposal.");
            Require(!sentinel.HasExited, "Cleanup touched an unrelated test-owned sentinel.");

            string liveLeaf = Path.Combine(fixture, "dispose.json");
            var liveInfo = Info("--parent", liveLeaf, "--hold");
            liveInfo.Environment["OWNED_FIXTURE_UNICODE"] = "보존 \"quoted\" value";
            liveInfo.WorkingDirectory = fixture;
            var live = Start(liveInfo);
            if (live.Owner is not null) owners.Add(live.Owner);
            ownedHandles.Add(live.Process);
            await UntilAsync(() => File.Exists(liveLeaf), "live child readiness");
            Receipt liveReceipt = Read(liveLeaf);
            Capture(liveReceipt.ProcessId, ownedHandles);
            Require(liveReceipt.Environment == "보존 \"quoted\" value" && liveReceipt.Arguments.SequenceEqual(new[] { "한글 공백", "quote\"end", "slash\\" }), "Owned launch changed Unicode environment or argument quoting.");
            Require(liveReceipt.Directory == fixture, "Owned launch changed its configured working directory.");
            using (var host = new OpenCodeHeadlessHost(new Uri("http://127.0.0.1:43128/")))
            {
                Set(host, "_process", live.Process);
                if (live.Owner is not null) Set(host, "_ownedProcess", live.Owner);
                host.Dispose();
            }
            await UntilAsync(() => !IsAlive(liveReceipt.ProcessId), "disposed owned child exit");
            Require(!IsAlive(liveReceipt.ProcessId) && !sentinel.HasExited, "Live tree disposal leaked a child or killed the unrelated sentinel.");

            string crashLeaf = Path.Combine(fixture, "owner-crash.json"), crashReady = Path.Combine(fixture, "owner-ready.txt");
            Process crashOwner = Process.Start(Info("--owner", crashLeaf, crashReady))!;
            ownedHandles.Add(crashOwner);
            await UntilAsync(() => File.Exists(crashReady), "test owner readiness");
            int crashRoot = int.Parse(await File.ReadAllTextAsync(crashReady));
            int crashChild = Read(crashLeaf).ProcessId;
            Capture(crashRoot, ownedHandles);
            Capture(crashChild, ownedHandles);
            crashOwner.Kill(); // Only this fixture-owned owner, deliberately without tree cleanup.
            await crashOwner.WaitForExitAsync();
            await UntilAsync(() => !IsAlive(crashRoot) && !IsAlive(crashChild), "kernel job cleanup after owner crash");
            Require(!IsAlive(crashRoot) && !IsAlive(crashChild) && !sentinel.HasExited, "Abnormal owner exit leaked its job tree or touched the sentinel.");
            await VerifyProbesAsync(fixture, ownedHandles, sentinel);
            Console.WriteLine($"PASS {_assertions} assertions: early wrapper exit, host disposal, abrupt owner exit, Unicode/quoting/cwd, bounded probe output/cancel/timeout, sentinel.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine("FAIL " + exception); return 1; }
        finally
        {
            foreach (var owner in owners) owner.Dispose();
            // Only handles created/captured from this fixture's private receipt files.
            foreach (var process in ownedHandles)
            {
                try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
                process.Dispose();
            }
            Directory.Delete(fixture, true);
        }
    }

    private static (Process Process, IDisposable? Owner) Start(ProcessStartInfo info)
    {
        if (OwnerType is null) return (Process.Start(info)!, null); // RED exercises the current unowned host launch.
        object owner = OwnerType.GetMethod("Start", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [info, null, null])!;
        return ((Process)OwnerType.GetProperty("Process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!, (IDisposable)owner);
    }
    private static ProcessStartInfo Info(params string[] args)
    {
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Environment.CurrentDirectory };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }
    private static Receipt Read(string path) => JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path))!;
    private static async Task VerifyProbesAsync(string fixture, List<Process> handles, Process sentinel)
    {
        Type runnerType = typeof(OpenCodeHeadlessHost).Assembly.GetType("TerminalNotes.Services.WindowsOpenCodeCommandRunner")!;
        object runner = Activator.CreateInstance(runnerType, true)!;
        MethodInfo probe = runnerType.GetMethod("ProbeVersionAsync")!;
        async Task<object> RunAsync(string command, CancellationToken token)
        {
            Task task = (Task)probe.Invoke(runner, [command, fixture, token])!;
            await task;
            return task.GetType().GetProperty("Result")!.GetValue(task)!;
        }
        async Task<string> ScriptAsync(string name, bool hold)
        {
            string path = Path.Combine(fixture, name + ".cmd");
            await File.WriteAllTextAsync(path, $"@echo off\r\n\"{Executable}\" {(hold ? "--probe" : "--probe-exit")} \"{Path.Combine(fixture, name + ".json")}\"\r\n");
            return path;
        }
        string complete = await ScriptAsync("complete", false);
        var elapsed = Stopwatch.StartNew();
        object result = await RunAsync(complete, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        int completedChild = Read(Path.Combine(fixture, "complete.json")).ProcessId;
        Capture(completedChild, handles);
        Require((bool)result.GetType().GetProperty("Succeeded")!.GetValue(result)! && (string?)result.GetType().GetProperty("Version")!.GetValue(result) == "6.7.8", "Bounded output draining lost the valid probe version.");
        Require(elapsed.Elapsed < TimeSpan.FromSeconds(10) && !IsAlive(completedChild), "A completed probe leaked a child or waited indefinitely for inherited output EOF.");

        string cancel = await ScriptAsync("cancel", true);
        using var cancellation = new CancellationTokenSource();
        Task<object> canceledProbe = RunAsync(cancel, cancellation.Token);
        await UntilAsync(() => File.Exists(Path.Combine(fixture, "cancel.json")), "running cancel probe readiness");
        int canceledChild = Read(Path.Combine(fixture, "cancel.json")).ProcessId;
        Capture(canceledChild, handles);
        cancellation.Cancel();
        bool canceled = false;
        try { await canceledProbe.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { canceled = true; }
        Require(canceled && !IsAlive(canceledChild), "Running probe cancellation did not cancel and finish its owned child.");

        string timeout = await ScriptAsync("timeout", true);
        Task<object> timedProbe = RunAsync(timeout, CancellationToken.None);
        await UntilAsync(() => File.Exists(Path.Combine(fixture, "timeout.json")), "timeout probe readiness");
        int timedChild = Read(Path.Combine(fixture, "timeout.json")).ProcessId;
        Capture(timedChild, handles);
        object timedResult = await timedProbe.WaitAsync(TimeSpan.FromSeconds(40));
        string? error = (string?)timedResult.GetType().GetProperty("Error")!.GetValue(timedResult);
        Require(!(bool)timedResult.GetType().GetProperty("Succeeded")!.GetValue(timedResult)! && error?.Contains("timed out", StringComparison.Ordinal) == true && !IsAlive(timedChild), "Probe timeout leaked its child or lost its sanitized timeout result.");
        Require(!sentinel.HasExited, "Probe cleanup touched the unrelated sentinel.");
    }
    private static async Task WriteReadyAsync(string path, string value)
    {
        string temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, value);
        File.Move(temporary, path);
    }
    private static void Capture(int pid, List<Process> handles) { try { var process = Process.GetProcessById(pid); _ = process.SafeHandle; handles.Add(process); } catch (ArgumentException) { } }
    private static bool IsAlive(int pid) { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static async Task UntilAsync(Func<bool> condition, string reason)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) { try { await Task.Delay(25, limit.Token); } catch (OperationCanceledException) { throw new TimeoutException(reason); } }
    }
    private static void Require(bool condition, string message) { _assertions++; if (!condition) throw new InvalidOperationException(message); }
    private sealed record Receipt(int ProcessId, DateTimeOffset CreatedAt, string? Environment, string Directory, string[] Arguments);
}
