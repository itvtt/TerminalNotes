using System.IO;
using System.Windows;
using System.Windows.Controls;
using TerminalNotes.Services;

namespace TerminalNotes;

// Closing the app ends everything it runs (2026-11-12 사용자: "트레이에서 앱을 닫으면 모든 실행이 종료되어야 하는데? 개별로 터미널로
// 빼낸거 말고는 앱내에서 도는건 종료되게 해야해"): its terminals, their servers and agents. Only windows it opened apart (+ 터미널,
// CMD에서 이어가기) go on. 1.8.8 to 2.0.0 kept a working agent's server after the app closed; that is gone, and servers those
// versions left running are stopped at the next start (LeftoverServers). Here also: work that starts on its own while the window is
// in the tray or minimized (a scheduled job) gets its terminal laid out off the screens.
public partial class MainWindow
{
    /// <summary>The app itself, not a test window with its own settings file (unless that points the leftover list elsewhere).</summary>
    private bool IsTheApp => AppContext.GetData("TerminalNotes.KeptServersPath") is string ||
        string.Equals(Path.GetFullPath(_settingsPath), Path.GetFullPath(DefaultSettingsPath), StringComparison.OrdinalIgnoreCase);

    /// <summary>What closing the app asks: everything ends, working agents too.</summary>
    internal string ExitConfirmation()
    {
        const string everything = "앱을 닫으면 열려 있는 모든 터미널과 실행 중인 프로그램이 종료됩니다.";
        var working = TerminalTabs.Items.Cast<TabItem>().Select(tab => ((TerminalTabState)tab.Tag).Pane)
            .Where(pane => StateOf(pane) is AgentState.Working or AgentState.Blocked).ToList();
        if (working.Count == 0) return everything + " 닫을까요?";
        string names = string.Join(", ", working.Select(pane => ProjectDisplayName(pane.WorkingDirectory)).Distinct(StringComparer.OrdinalIgnoreCase).Take(3));
        return $"{everything}\n작업 중인 에이전트 {working.Count}개({names})도 멈춥니다. 닫을까요?";
    }

    /// <summary>At start: servers an earlier version kept running after it closed are stopped (off the window's thread).</summary>
    private void StopLeftoverServers()
    {
        if (!IsTheApp) return;
        _ = Task.Run(() =>
        {
            try { LeftoverServers.StopAll(); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                System.Diagnostics.Trace.WriteLine("Stopping leftover servers failed: " + exception.GetType().Name);
            }
        });
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
    /// scheduled job) the window is shown at its size off the screens, without focus, until those terminals
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
}
