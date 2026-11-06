using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using TerminalNotes.Services;

namespace TerminalNotes;

// Skills added, removed or changed while terminals run reach them at once (2026-11-06 사용자: "중간에 스킬을 추가하거나 빼거나
// 되면 그것도 바로 반영되는거지? 바로 되게해"). OpenCode reads its skill folders once per instance, so every open OpenCode
// terminal that sees the changed folder resets its instance as soon as it is idle (TerminalPane.RequestSkillReload); a busy one
// does it when its current work ends. Changes come from the skill store (InstalledSkills.Changed) and, for the global folder,
// from anything else that edits it (a file watcher).
public partial class MainWindow
{
    private FileSystemWatcher? _globalSkillsWatcher;
    private DispatcherTimer? _globalSkillsSettle;
    private DateTimeOffset _skillsChangedByApp = DateTimeOffset.MinValue;

    private void InitializeSkillReload()
    {
        if (_globalSkillsSettle is not null) return;
        InstalledSkills.Changed += OnInstalledSkillsChanged;
        _globalSkillsSettle = new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Background, (_, _) =>
        {
            _globalSkillsSettle!.Stop();
            // The store's own writes were already handled when it raised them.
            if (DateTimeOffset.Now - _skillsChangedByApp > TimeSpan.FromSeconds(3)) ReloadSkillsIn(InstalledSkills.GlobalDirectory);
        }, Dispatcher);
        _globalSkillsSettle.Stop();
        try
        {
            string folder = InstalledSkills.GlobalDirectory;
            if (!Directory.Exists(folder)) return;
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
            };
            FileSystemEventHandler changed = (_, e) => OnGlobalSkillFileChanged(e.FullPath);
            watcher.Created += changed;
            watcher.Changed += changed;
            watcher.Deleted += changed;
            watcher.Renamed += (_, e) => OnGlobalSkillFileChanged(e.FullPath);
            watcher.EnableRaisingEvents = true;
            _globalSkillsWatcher = watcher;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            ApplicationDiagnostics.RecordException("skill_watch_failed", exception);
        }
    }

    private void DisposeSkillReload()
    {
        InstalledSkills.Changed -= OnInstalledSkillsChanged;
        _globalSkillsSettle?.Stop();
        try { _globalSkillsWatcher?.Dispose(); } catch (Exception exception) { ApplicationDiagnostics.RecordException("skill_watch_dispose_failed", exception); }
        _globalSkillsWatcher = null;
    }

    private void OnInstalledSkillsChanged(string folder)
    {
        if (_closing || _resourcesDisposed || Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(() => OnInstalledSkillsChanged(folder)); return; }
        _skillsChangedByApp = DateTimeOffset.Now;
        ReloadSkillsIn(folder);
    }

    // Watcher thread: the store's bookkeeping folder (.terminal-notes) holds no skills, so its writes are ignored.
    private void OnGlobalSkillFileChanged(string path)
    {
        if (path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part == ".terminal-notes")) return;
        if (_closing || _resourcesDisposed || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_closing || _resourcesDisposed || _globalSkillsSettle is null) return;
            _globalSkillsSettle.Stop();
            _globalSkillsSettle.Start();
        });
    }

    // The global folder reaches every OpenCode terminal; a task's own .opencode\skills reaches the terminals in that task's folder.
    private void ReloadSkillsIn(string folder)
    {
        if (_closing || _resourcesDisposed) return;
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)); }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { return; }
        bool global = string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetFullPath(InstalledSkills.GlobalDirectory)), StringComparison.OrdinalIgnoreCase);
        string? project = global ? null : Path.GetDirectoryName(Path.GetDirectoryName(full));
        int waiting = 0, reloading = 0;
        foreach (var pane in TerminalTabs.Items.OfType<TabItem>().Select(tab => (tab.Tag as TerminalTabState)?.Pane).OfType<TerminalPane>())
        {
            if (!global && (project is null || !IsSameOrInside(pane.WorkingDirectory, project))) continue;
            if (pane.NativeOpenCodeEndpoint is null) continue;
            pane.RequestSkillReload();
            if (pane.SkillReloadPending) waiting++; else reloading++;
        }
        if (waiting + reloading == 0) return;
        SetInlineStatus($"스킬 변경을 열린 OpenCode 터미널 {waiting + reloading}개에 바로 반영합니다" +
            (waiting > 0 ? $" · 작업 중인 {waiting}개는 지금 작업이 끝나는 대로 반영합니다." : "."));
    }

    private static bool IsSameOrInside(string path, string folder)
    {
        try
        {
            string child = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            return string.Equals(child, parent, StringComparison.OrdinalIgnoreCase) ||
                child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { return false; }
    }
}
