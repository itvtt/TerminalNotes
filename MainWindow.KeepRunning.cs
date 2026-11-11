using System.IO;
using System.Windows;
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

    /// <summary>The terminals whose servers closing the app would keep: task terminals whose agent works or waits for an answer.</summary>
    private List<TerminalPane> PanesToKeep() => !KeepsAgents || !KeptServers.Enabled ? [] :
        TerminalTabs.Items.Cast<TabItem>().Select(tab => ((TerminalTabState)tab.Tag).Pane)
            .Where(pane => AgentStateOf(pane.Activity, pane.HasCompletedWork) is AgentState.Working or AgentState.Blocked && pane.CanKeepRunningAfterClose).ToList();

    /// <summary>What closing the app asks: which agents go on (the others' terminals end).</summary>
    internal string ExitConfirmation()
    {
        var kept = PanesToKeep();
        if (kept.Count == 0) return "앱을 닫으면 열려 있는 모든 터미널과 실행 중인 프로그램이 종료됩니다. 닫을까요?";
        string names = string.Join(", ", kept.Select(pane => ProjectDisplayName(pane.WorkingDirectory)).Distinct(StringComparer.OrdinalIgnoreCase).Take(3));
        bool others = TerminalTabs.Items.Count > kept.Count;
        return $"작업 중인 에이전트 {kept.Count}개({names})는 앱을 닫아도 계속 실행되고, 다시 열면 그대로 이어집니다." +
            (others ? "\n나머지 터미널은 종료됩니다." : "") + " 닫을까요?";
    }

    /// <summary>At close, before the terminals are disposed: the working ones keep their servers. Returns how many.</summary>
    internal int KeepWorkingAgents()
    {
        int kept = 0;
        foreach (var pane in PanesToKeep())
        {
            try
            {
                if (pane.KeepRunningAfterClose()) kept++;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                System.Diagnostics.Trace.WriteLine("Keeping an agent failed: " + exception.GetType().Name);
            }
        }
        if (kept > 0) TaskMemoryGuard.Unlimit();
        return kept;
    }

    /// <summary>A task's terminal the way starting a task opens it (its row, a free slot, the task selected, its terminal shown: a
    /// terminal starts only once it is laid out). A terminal the task already has is returned as it is, without changing the view.</summary>
    private TerminalPane OpenTaskTerminal(string project)
    {
        project = CanonicalProjectPath(project);
        if (RuntimeFor(project).SingleTerminal is { Tag: TerminalTabState existing } tab && TerminalTabs.Items.Contains(tab) && !existing.Pane.IsDisposed)
            return existing.Pane;
        ShowForBackgroundWork();
        EnsureProjectTaskRow(project);
        if (!EnsureStartedProjectSlotVisible(project)) throw new InvalidOperationException("터미널 칸이 모두 사용 중입니다.");
        SelectTaskProject(project, ensurePlanDocument: false);
        if (!HasProjectName(project)) RememberProjectName(project, Path.GetFileName(project));
        var created = EnsureSingleProjectTerminalWithoutPlanDocument(project);
        ShowProjectTask(project);
        var pane = ((TerminalTabState)created.Tag).Pane;
        if (_backgroundShow != BackgroundShow.None) _backgroundPanes.Add(pane);
        return pane;
    }

    private enum BackgroundShow { None, FromTray, FromMinimized }
    private static readonly TimeSpan BackgroundShowLimit = TimeSpan.FromMinutes(3);
    private BackgroundShow _backgroundShow;
    private Point _backgroundPlace;
    private DateTimeOffset _backgroundSince;
    private readonly List<TerminalPane> _backgroundPanes = [];

    /// <summary>A terminal starts only once it is laid out, which a window in the tray or minimized never is (measured 2026-11-12: from
    /// the tray, and three times in four from a minimized window, a new terminal never started). For work that starts on its own (a
    /// scheduled job, taking back a kept agent) the window is shown at its size off the screens, without focus, until those terminals
    /// run; then it goes back (<see cref="ReturnFromBackgroundWork"/>). Its position is not saved, only its size.</summary>
    private void ShowForBackgroundWork()
    {
        if (_closing || _closePending || _backgroundShow != BackgroundShow.None || IsVisible && WindowState != WindowState.Minimized) return;
        _backgroundShow = IsVisible ? BackgroundShow.FromMinimized : BackgroundShow.FromTray;
        _backgroundSince = DateTimeOffset.UtcNow;
        _backgroundPlace = _backgroundShow == BackgroundShow.FromMinimized ? RestoreBounds.TopLeft : new Point(Left, Top);
        double away = SystemParameters.VirtualScreenLeft - Math.Max(Width, 800) - 400;
        if (_backgroundShow == BackgroundShow.FromTray)
        {
            WindowState = WindowState.Normal;
            Left = away;
            Top = SystemParameters.VirtualScreenTop;
            ShowActivated = false;
            Show();
            ShowActivated = true;
        }
        else
        {
            Left = away;
            Top = SystemParameters.VirtualScreenTop;
            ShowWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle, 4); // SW_SHOWNOACTIVATE: restored, not focused
        }
    }

    /// <summary>The terminals run (or it took too long): the window goes back where it was, to the tray or minimized.</summary>
    private void ReturnFromBackgroundWork()
    {
        if (_backgroundShow == BackgroundShow.None) return;
        var was = _backgroundShow;
        _backgroundShow = BackgroundShow.None;
        _backgroundPanes.Clear();
        if (was == BackgroundShow.FromTray && _tray is not null) Hide();
        else ShowWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle, 7); // SW_SHOWMINNOACTIVE
        Left = _backgroundPlace.X;
        Top = _backgroundPlace.Y;
    }

    /// <summary>The user opened the window meanwhile: it comes back on the screen and stays.</summary>
    private void BringBackFromBackgroundWork()
    {
        if (_backgroundShow == BackgroundShow.None) return;
        _backgroundShow = BackgroundShow.None;
        _backgroundPanes.Clear();
        Left = _backgroundPlace.X;
        Top = _backgroundPlace.Y;
    }

    /// <summary>Each board tick: back once the terminals it was shown for run.</summary>
    private void CheckBackgroundWork()
    {
        if (_backgroundShow == BackgroundShow.None) return;
        if (_backgroundPanes.Count > 0 && _backgroundPanes.All(pane => pane.IsReady || pane.IsDisposed) ||
            DateTimeOffset.UtcNow - _backgroundSince > BackgroundShowLimit) ReturnFromBackgroundWork();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

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
                OpenTaskTerminal(project);
                opened++;
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException) { }
        }
        if (opened > 0) SetStatus($"앱을 닫은 동안 계속 실행된 에이전트 {servers.Count}개에 다시 연결합니다.");
    }
}
