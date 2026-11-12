using System.Diagnostics;
using System.IO;
using System.Text.Json;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TerminalNotes.AppExitTests")]

namespace TerminalNotes;

/// <summary>1.8.8 to 2.0.0 left a working agent's OpenCode server running after the app closed and wrote it down in
/// kept-servers.json; that was taken out (2026-11-12 사용자: "트레이에서 앱을 닫으면 모든 실행이 종료되어야 하는데"). At start the
/// servers on that list that still run are stopped and the list is deleted, so none runs on unseen.</summary>
internal static class LeftoverServers
{
    internal static string FilePath => AppContext.GetData("TerminalNotes.KeptServersPath") as string ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerminalNotes", "kept-servers.json");

    /// <summary>Stops the listed servers still running and deletes the list. Returns how many were stopped.</summary>
    internal static int StopAll()
    {
        string path = FilePath;
        if (!File.Exists(path)) return 0;
        int stopped = 0;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("Servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
                foreach (var server in servers.EnumerateArray())
                {
                    if (server.ValueKind != JsonValueKind.Object || !server.TryGetProperty("ProcessId", out var id) || !id.TryGetInt32(out int processId)) continue;
                    DateTime? started = server.TryGetProperty("ProcessStart", out var start) && start.ValueKind == JsonValueKind.String &&
                        start.TryGetDateTime(out var at) ? at.ToUniversalTime() : null;
                    if (Stop(processId, started)) stopped++;
                }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return stopped;
    }

    // Only the same server: the process started when the list says (another process may have its id since); without a start time
    // written down, only an OpenCode process.
    private static bool Stop(int processId, DateTime? started)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return false;
            bool same = started is { } at ? Math.Abs((process.StartTime.ToUniversalTime() - at).TotalSeconds) < 2
                : process.ProcessName.StartsWith("opencode", StringComparison.OrdinalIgnoreCase);
            if (!same) return false;
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}
