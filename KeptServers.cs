using System.Diagnostics;
using System.IO;
using System.Text.Json;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TerminalNotes.KeepRunningTests")]

namespace TerminalNotes;

/// <summary>OpenCode servers kept running after the app closed (2026-11-11 사용자: herdr처럼 "앱 꺼도 계속 실행"): when the app closes,
/// a task terminal whose agent is still working (or waiting for an answer) leaves its server running and is written down here; when
/// the app opens that task's terminal again it takes the same server and session back instead of starting new ones. Servers not
/// taken back within <see cref="MaximumAge"/> are stopped at the next start, so nothing runs on forever unseen.</summary>
internal static class KeptServers
{
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromHours(24);

    internal sealed record Server(string Directory, string PaneKey, int Port, int ProcessId, string SessionId, DateTimeOffset Kept, DateTime? ProcessStart);

    internal sealed record State
    {
        public bool Enabled { get; init; } = true;
        public List<Server> Servers { get; init; } = [];
    }

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static string FilePath => AppContext.GetData("TerminalNotes.KeptServersPath") as string ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerminalNotes", "kept-servers.json");

    /// <summary>앱을 닫아도 작업 중인 에이전트 계속 실행 (on unless turned off).</summary>
    internal static bool Enabled
    {
        get => Read().Enabled;
        set => Update(state => state with { Enabled = value });
    }

    internal static IReadOnlyList<Server> List() => Read().Servers;

    internal static void Add(Server server) => Update(state => state with
    {
        Servers = state.Servers.Where(other => other.ProcessId != server.ProcessId && !Same(other, server.Directory, server.PaneKey)).Append(server).ToList()
    });

    // Servers a terminal of this app already claimed (one terminal each).
    private static readonly HashSet<int> Claimed = [];

    /// <summary>The kept server for a task terminal: the same terminal's first, else that task's (with its own session); claimed so
    /// no other terminal takes it too. It stays written down until <see cref="Forget"/> (taken back), so a terminal that never started
    /// leaves it for the next time. Null when there is none or it is no longer the same running server.</summary>
    internal static Server? Take(string directory, string paneKey)
    {
        lock (Gate)
        {
            var open = Read().Servers.Where(other => !Claimed.Contains(other.ProcessId) && Alive(other)).ToList();
            var server = open.FirstOrDefault(other => Same(other, directory, paneKey)) ?? open.FirstOrDefault(other => SameDirectory(other.Directory, directory));
            if (server is not null) Claimed.Add(server.ProcessId);
            return server;
        }
    }

    /// <summary>Taken back (or stopped): no longer written down.</summary>
    internal static void Forget(int processId) => Update(state => state with { Servers = state.Servers.Where(other => other.ProcessId != processId).ToList() });

    /// <summary>At start: forgets servers that ended and stops those kept longer than <see cref="MaximumAge"/>; returns the rest.</summary>
    internal static IReadOnlyList<Server> Sweep(DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        lock (Gate)
        {
            var state = Read();
            var kept = new List<Server>();
            foreach (var server in state.Servers)
            {
                if (!Alive(server)) continue;
                if (at - server.Kept > MaximumAge) { Stop(server); continue; }
                kept.Add(server);
            }
            Write(state with { Servers = kept });
            return kept;
        }
    }

    /// <summary>Stops a kept server (only when it is still the same process).</summary>
    internal static void Stop(Server server)
    {
        try
        {
            using var process = Process.GetProcessById(server.ProcessId);
            if (Matches(process, server)) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    internal static bool Alive(Server server)
    {
        try
        {
            using var process = Process.GetProcessById(server.ProcessId);
            return Matches(process, server);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    // The same process, not another that got its id later.
    private static bool Matches(Process process, Server server) =>
        !process.HasExited && (server.ProcessStart is not { } started || Math.Abs((process.StartTime.ToUniversalTime() - started).TotalSeconds) < 2);

    internal static DateTime? StartTimeOf(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    private static bool Same(Server server, string directory, string paneKey) => server.PaneKey == paneKey && SameDirectory(server.Directory, directory);

    private static bool SameDirectory(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static State Read()
    {
        lock (Gate)
        {
            try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath), Json) ?? new() : new(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return new(); }
        }
    }

    private static void Update(Func<State, State> change) { lock (Gate) Write(change(Read())); }

    private static void Write(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(state, Json));
            File.Move(FilePath + ".tmp", FilePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
