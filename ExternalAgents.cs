using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TerminalNotes;

/// <summary>Agents running in terminals outside the app (2026-11-11 사용자: herdr처럼 "외부 터미널 감지"): Claude Code, Codex and
/// OpenCode started by hand in a PowerShell, cmd or Windows Terminal window (+ 터미널 opens such windows). They are found in a process
/// snapshot; the app's own servers and TUIs (reached from this app without a shell in between, or held by a terminal) are not.
/// Another console's text cannot be read, so whether one works is judged by its CPU time (with its children's).</summary>
internal static class ExternalAgents
{
    internal static readonly IReadOnlyDictionary<string, string> Known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["claude.exe"] = "Claude Code", ["codex.exe"] = "Codex", ["opencode.exe"] = "OpenCode", ["gemini.exe"] = "Gemini CLI"
    };
    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase)
        { "powershell.exe", "pwsh.exe", "cmd.exe", "bash.exe", "wsl.exe", "WindowsTerminal.exe", "OpenConsole.exe", "conhost.exe" };
    /// <summary>CPU use (of one core) above which an agent counts as working.</summary>
    internal const double WorkingCpu = 0.03;

    internal sealed record Entry(int Id, int Parent, string Exe);

    internal sealed record Agent(int ProcessId, string Name, string Host, bool Working, IntPtr Window);

    private static readonly Dictionary<int, (TimeSpan Cpu, long At)> Samples = [];

    /// <summary>The agents among a snapshot that are not this app's: top agent processes (not an agent's own child agent), reached
    /// through a shell or not from this app at all, and not held by a terminal of the app.</summary>
    internal static IReadOnlyList<Entry> Find(IReadOnlyList<Entry> all, int self, IReadOnlySet<int> ours)
    {
        var byId = all.GroupBy(entry => entry.Id).ToDictionary(group => group.Key, group => group.First());
        var found = new List<Entry>();
        foreach (var entry in all)
        {
            if (!Known.ContainsKey(entry.Exe) || ours.Contains(entry.Id)) continue;
            if (byId.TryGetValue(entry.Parent, out var parent) && Known.ContainsKey(parent.Exe)) continue;
            bool shell = false, mine = false;
            var seen = new HashSet<int> { entry.Id };
            for (var current = entry; byId.TryGetValue(current.Parent, out var up) && seen.Add(up.Id); current = up)
            {
                if (ours.Contains(up.Id)) { mine = true; break; }
                if (up.Id == self) { mine = !shell; break; }
                if (Shells.Contains(up.Exe)) shell = true;
            }
            if (!mine) found.Add(entry);
        }
        return found;
    }

    /// <summary>The external agents now, each with its window and whether it works.</summary>
    internal static IReadOnlyList<Agent> Scan(IReadOnlySet<int> ours)
    {
        var all = Snapshot();
        var children = all.ToLookup(entry => entry.Parent);
        long now = Environment.TickCount64;
        var result = new List<Agent>();
        var alive = new HashSet<int>();
        foreach (var entry in Find(all, Environment.ProcessId, ours))
        {
            alive.Add(entry.Id);
            TimeSpan cpu = Cpu(entry.Id) + Descendants(entry.Id, children).Aggregate(TimeSpan.Zero, (sum, child) => sum + Cpu(child));
            bool working = Samples.TryGetValue(entry.Id, out var last) && now > last.At &&
                (cpu - last.Cpu).TotalMilliseconds / (now - last.At) > WorkingCpu;
            Samples[entry.Id] = (cpu, now);
            var (window, host) = HostOf(entry, all);
            result.Add(new Agent(entry.Id, Known[entry.Exe], host, working, window));
        }
        foreach (int gone in Samples.Keys.Where(id => !alive.Contains(id)).ToList()) Samples.Remove(gone);
        return result;
    }

    /// <summary>Brings an agent's terminal window to the front.</summary>
    internal static void Focus(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        if (IsIconic(window)) ShowWindow(window, 9); // SW_RESTORE
        SetForegroundWindow(window);
    }

    private static IEnumerable<int> Descendants(int id, ILookup<int, Entry> children)
    {
        var seen = new HashSet<int> { id };
        var queue = new Queue<int>([id]);
        while (queue.Count > 0)
            foreach (var child in children[queue.Dequeue()])
                if (seen.Add(child.Id) && seen.Count < 200) { queue.Enqueue(child.Id); yield return child.Id; }
    }

    private static TimeSpan Cpu(int id)
    {
        try { using var process = Process.GetProcessById(id); return process.TotalProcessorTime; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return TimeSpan.Zero; }
    }

    // The first ancestor with a window: the console or Windows Terminal it runs in.
    private static (IntPtr Window, string Host) HostOf(Entry entry, IReadOnlyList<Entry> all)
    {
        var byId = all.GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.First());
        var seen = new HashSet<int>();
        for (var current = entry; seen.Add(current.Id); )
        {
            try
            {
                using var process = Process.GetProcessById(current.Id);
                if (process.MainWindowHandle != IntPtr.Zero) return (process.MainWindowHandle, HostName(current.Exe));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { }
            if (!byId.TryGetValue(current.Parent, out var up)) break;
            current = up;
        }
        return (IntPtr.Zero, "터미널");
    }

    private static string HostName(string exe) => exe.ToLowerInvariant() switch
    {
        "windowsterminal.exe" => "Windows Terminal", "powershell.exe" or "pwsh.exe" => "PowerShell", "cmd.exe" => "명령 프롬프트",
        "code.exe" => "VS Code", _ => System.IO.Path.GetFileNameWithoutExtension(exe)
    };

    internal static List<Entry> Snapshot()
    {
        var entries = new List<Entry>();
        IntPtr snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
        if (snapshot == new IntPtr(-1)) return entries;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                entries.Add(new Entry((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile));
        }
        finally { CloseHandle(snapshot); }
        return entries;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
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
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
}
