using System.IO;
using System.Windows.Controls;
using TerminalNotes.Services;

namespace TerminalNotes;

// 앱 꺼도 계속 실행 (herdr-like, see KeptServers): closing the app keeps the servers of task terminals whose agent is working or
// waiting for an answer; the next start opens those tasks' terminals again, which take the same servers and sessions back.
public partial class MainWindow
{
    /// <summary>Only the app itself keeps servers (a test window with its own settings file does not, unless it points the list elsewhere).</summary>
    private bool KeepsAgents => AppContext.GetData("TerminalNotes.KeptServersPath") is string ||
        string.Equals(Path.GetFullPath(_settingsPath), Path.GetFullPath(DefaultSettingsPath), StringComparison.OrdinalIgnoreCase);

    /// <summary>At close, before the terminals are disposed: the working ones keep their servers. Returns how many.</summary>
    internal int KeepWorkingAgents()
    {
        if (!KeepsAgents || !KeptServers.Enabled) return 0;
        int kept = 0;
        foreach (var tab in TerminalTabs.Items.Cast<TabItem>())
        {
            var pane = ((TerminalTabState)tab.Tag).Pane;
            try
            {
                if (AgentStateOf(pane.Activity, pane.HasCompletedWork) is AgentState.Working or AgentState.Blocked && pane.KeepRunningAfterClose()) kept++;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                System.Diagnostics.Trace.WriteLine("Keeping an agent failed: " + exception.GetType().Name);
            }
        }
        if (kept > 0) TaskMemoryGuard.Unlimit();
        return kept;
    }

    /// <summary>At start: the tasks whose agents went on working while the app was closed get their terminals again.</summary>
    private void ReconnectKeptAgents()
    {
        if (!KeepsAgents || _closing) return;
        IReadOnlyList<KeptServers.Server> servers;
        try { servers = KeptServers.Sweep(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return; }
        var projects = servers.Select(server => server.Directory).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int opened = 0;
        foreach (string project in projects)
        {
            try
            {
                EnsureSingleProjectTerminal(CanonicalProjectPath(project));
                opened++;
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException) { }
        }
        if (opened > 0) SetStatus($"앱을 닫은 동안 계속 실행된 에이전트 {servers.Count}개에 다시 연결합니다.");
    }
}
