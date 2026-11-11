using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using TerminalNotes;
using TerminalNotes.Services;

// 앱 꺼도 계속 실행: a released command survives the app's job; the kept list claims, forgets and sweeps; a real OpenCode server is
// detached, still answers, is taken back by a new host and stopped when that host is disposed.
internal static class Program
{
    private static int _failures;

    private static void Assert(bool condition, string name)
    {
        if (condition) return;
        _failures++;
        Console.WriteLine("FAIL " + name);
    }

    private static async Task<int> Main()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes keep running " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        AppContext.SetData("TerminalNotes.KeptServersPath", Path.Combine(fixture, "kept-servers.json"));
        try
        {
            await Released();
            Registry(fixture);
            await RealServer(fixture);
        }
        catch (Exception exception) { _failures++; Console.WriteLine("FAIL " + exception); }
        finally { try { Directory.Delete(fixture, true); } catch { } }
        Console.WriteLine(_failures == 0 ? "PASS keep running: release, kept list, detach and adopt a real server" : $"FAIL keep running ({_failures})");
        return _failures;
    }

    private static ProcessStartInfo Sleeper() => new()
    {
        FileName = Path.Combine(Environment.SystemDirectory, "ping.exe"), Arguments = "-n 60 127.0.0.1", UseShellExecute = false,
        WorkingDirectory = Path.GetTempPath()
    };

    private static async Task Released()
    {
        // Baseline: disposing the owner stops the command.
        var owned = OwnedWindowsProcess.Start(Sleeper(), quietOutput: true);
        int ownedId = owned.Process.Id;
        owned.Dispose();
        await Task.Delay(300);
        Assert(!Running(ownedId), "a disposed owned command stops");

        // Released: the job handle is closed by Release itself; the command goes on.
        var released = OwnedWindowsProcess.Start(Sleeper(), quietOutput: true);
        int id = released.Release();
        await Task.Delay(500);
        Assert(Running(id), "a released command outlives its job handle");
        try { Process.GetProcessById(id).Kill(); } catch { }
    }

    private static void Registry(string fixture)
    {
        var released = OwnedWindowsProcess.Start(Sleeper(), quietOutput: true);
        int id = released.Release();
        string project = Path.Combine(fixture, "task");
        Directory.CreateDirectory(project);
        var server = new KeptServers.Server(project, "2", 51234, id, "ses_1", DateTimeOffset.UtcNow, KeptServers.StartTimeOf(id));
        KeptServers.Add(server);
        Assert(KeptServers.Enabled && KeptServers.List().Count == 1, "a kept server is written down");
        var taken = KeptServers.Take(project + "\\", "1");
        Assert(taken?.ProcessId == id && taken.SessionId == "ses_1", "another terminal number of the same task takes it, with its session");
        Assert(KeptServers.Take(project, "2") is null, "a claimed server is not taken twice");
        Assert(KeptServers.List().Count == 1, "it stays written down until taken back");
        KeptServers.Forget(id);
        Assert(KeptServers.List().Count == 0, "taken back, it is forgotten");

        // Sweep: an old one is stopped, a dead one forgotten, a recent one kept.
        KeptServers.Add(server with { Kept = DateTimeOffset.UtcNow - KeptServers.MaximumAge - TimeSpan.FromMinutes(1) });
        KeptServers.Add(new KeptServers.Server(project, "3", 1, 999_999, "ses_x", DateTimeOffset.UtcNow, null));
        var left = KeptServers.Sweep();
        Thread.Sleep(300);
        Assert(left.Count == 0 && KeptServers.List().Count == 0 && !Running(id), "the sweep stops servers older than a day and forgets ended ones");
        KeptServers.Enabled = false;
        Assert(!KeptServers.Enabled, "keeping can be turned off");
        KeptServers.Enabled = true;
    }

    private static async Task RealServer(string fixture)
    {
        var installed = await AutoInstall.CheckAsync(CancellationToken.None);
        if (!installed.IsInstalled || installed.Path is null || !installed.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("SKIP real server: OpenCode is not installed");
            return;
        }
        string project = Path.Combine(fixture, "server-task");
        Directory.CreateDirectory(project);
        int port = FreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/");
        var first = new OpenCodeHeadlessHost(endpoint);
        await first.StartAsync(project, null);
        int? id = first.Detach();
        first.Dispose();
        Assert(id is int, "a launched server can be detached");
        await Task.Delay(500);
        Assert(id is int alive && Running(alive) && await Healthy(endpoint), "the detached server still answers after its host is disposed");

        var second = new OpenCodeHeadlessHost(endpoint);
        second.AdoptOnStart(id!.Value);
        var watch = Stopwatch.StartNew();
        await second.StartAsync(project, null);
        Assert(second.IsRunning && second.ProcessId == (uint)id.Value, "a new host takes the same server back");
        Console.WriteLine($"adopted in {watch.ElapsedMilliseconds} ms");
        int? again = second.Detach();
        Assert(again == id, "a taken-back server can be kept again");
        var third = new OpenCodeHeadlessHost(endpoint);
        third.AdoptOnStart(id.Value);
        await third.StartAsync(project, null);
        third.Dispose();
        await Task.Delay(1000);
        Assert(!Running(id.Value), "closing the terminal stops a taken-back server");
    }

    private static bool Running(int id)
    {
        try { using var process = Process.GetProcessById(id); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task<bool> Healthy(Uri endpoint)
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
        try { return (await client.GetAsync(new Uri(endpoint, "global/health"))).StatusCode == HttpStatusCode.OK; }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { return false; }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
