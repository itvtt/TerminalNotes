using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TerminalNotes;
using TerminalNotes.Services;

internal static class Program
{
    private static int _fail;
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                if (args.Contains("--archive-notice-only"))
                {
                    await VerifyInAppArchiveNoticeAsync();
                    Console.WriteLine("PROJECT ARCHIVE NOTICE TESTS PASSED");
                    return;
                }
                if (!args.Contains("--task-refresh-only"))
                {
                    await VerifyDelayedOwnedCloseAsync("success");
                    await VerifyDelayedOwnedCloseAsync("success-archive-tab");
                    await VerifyDelayedOwnedCloseAsync("notification-failure");
                    await VerifyDelayedOwnedCloseAsync("new-intent");
                    await VerifyDelayedOwnedCloseAsync("locked");
                }
                if (!args.Contains("--archive-close-only") && !args.Contains("--task-refresh-only")) await VerifyRecycleDeleteAsync();
                if (!args.Contains("--archive-close-only")) await VerifyManualTaskRefreshAsync();
                if (!args.Contains("--archive-close-only") && !args.Contains("--task-refresh-only")) await RunAsync();
                if (!args.Contains("--archive-close-only") && !args.Contains("--task-refresh-only")) await VerifyInAppArchiveNoticeAsync();
                Console.WriteLine("PROJECT ARCHIVE TESTS PASSED");
            }
            catch (Exception ex) { _fail++; Console.WriteLine("FAIL " + ex); }
            finally { app.Shutdown(); }
        };
        app.Run(); return _fail;
    }

    private static async Task VerifyRecycleDeleteAsync()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes recycle delete " + Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(fixture, "workspace");
        string project = Path.Combine(workspace, "fixture delete project");
        string kept = Path.Combine(workspace, "kept fixture task");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(kept);
        File.WriteAllText(Path.Combine(project, "code.txt"), "keep me in the recycle bin");
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(fixture, "app-data"));
        var window = new MainWindow(Path.Combine(fixture, "fixture-settings.json"), autoStartCodex: false);
        var recycled = new List<string>();
        try
        {
            Set(window, "_windowInitialized", true);
            Set(window, "_workspace", workspace);
            await (Task)Call(window, "RefreshProjectTasksAsync")!;
            var rows = Field<System.Collections.IDictionary>(window, "_projectTaskRows");
            Assert(rows.Contains(project) && rows.Contains(kept), "delete fixture lists both tasks");
            int asked = 0;
            Set(window, "RecycleProjectOverride", (Action<string>)(path => { recycled.Add(path); Directory.Delete(path, recursive: true); }));
            Set(window, "ProjectDeleteConfirmationOverride", (Func<string, string, bool>)((name, path) =>
            {
                asked++;
                Assert(name == "fixture delete project" && path == project, "delete confirmation names the exact task and folder");
                return false;
            }));
            await (Task)Call(window, "DeleteProjectToRecycleBinAsync", project)!;
            Assert(asked == 1 && recycled.Count == 0 && Directory.Exists(project) && rows.Contains(project),
                "cancelling the delete confirmation keeps the folder and the task");
            Set(window, "ProjectDeleteConfirmationOverride", (Func<string, string, bool>)((_, _) => { asked++; return true; }));
            await (Task)Call(window, "DeleteProjectToRecycleBinAsync", project)!;
            Assert(asked == 2 && recycled.SequenceEqual(new[] { project }) && !Directory.Exists(project) &&
                !rows.Contains(project) && rows.Contains(kept) && Directory.Exists(kept),
                "confirmed delete recycles exactly the project folder and removes only that task");
            Console.WriteLine("PASS recycle-bin task delete: confirmation, cancel, exact folder, other tasks kept");
        }
        finally
        {
            Call(window, "DisposeResources"); window.Close();
            try { Directory.Delete(fixture, recursive: true); } catch { }
        }
    }

    private static async Task VerifyInAppArchiveNoticeAsync()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes archive notice " + Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(fixture, "workspace");
        string project = Path.Combine(workspace, "fixture archive project");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(Path.Combine(workspace, "retained fixture task"));
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(fixture, "app-data"));
        var window = new MainWindow(Path.Combine(fixture, "fixture-settings.json"), autoStartCodex: false);
        try
        {
            // New settings start in basic mode, which has no task list: archive notices belong to the advanced list.
            Call(window, "SetWorkMode", false);
            // Render only this isolated fixture; suppress startup, native API, tray and model activity.
            Set(window, "_windowInitialized", true);
            Set(window, "_workspace", workspace);
            Set(window, "_taskSidebarWidth", 360d);
            await (Task)Call(window, "RefreshProjectTasksAsync")!;
            window.Show();
            window.UpdateLayout();
            var sidebar = Field<Grid>(window, "_taskSidebarSurface");
            var notice = Field<Border>(window, "_projectArchiveNotice");
            var noticeHost = Field<Canvas>(window, "_projectArchiveNoticeHost");
            var name = Field<TextBlock>(window, "_projectArchiveNoticeName");
            var scroll = Field<ScrollViewer>(window, "_taskListScroll");
            var actions = Field<StackPanel>(window, "_taskSidebarActions");
            var tabs = Field<RadioButton>(window, "_normalTasksTab");
            Rect Before(FrameworkElement element) => new(element.TranslatePoint(new Point(), sidebar), element.RenderSize);
            Rect tabsBefore = Before(tabs), actionsBefore = Before(actions), listBefore = Before(scroll);
            ((Button)actions.Children[0]).Focus();
            var focus = Keyboard.FocusedElement;
            await (Task)Call(window, "MoveProjectArchiveAsync", project)!;
            window.UpdateLayout();
            string archived = Path.Combine(workspace, "archive", "fixture archive project");
            Assert(Directory.Exists(archived) && !Directory.Exists(project) && tabs.IsChecked == true &&
                !Field<bool>(window, "_showArchivedTasks"), "actual archive finishes and keeps the normal task tab");
            Assert(notice.IsVisible && name.Text == "fixture archive project" &&
                ((TextBlock)((StackPanel)notice.Child).Children[0]).Text == "아카이브로 보냈습니다",
                "actual archive displays an in-app confirmation with project name (visible=" + notice.IsVisible + ", name=" + name.Text +
                ", status=" + ((TextBlock)window.FindName("StatusLabel")).Text + ", window=" + window.IsVisible + "/" + window.WindowState +
                ", sidebar=" + sidebar.IsVisible + ", collapsed=" + Field<bool>(window, "_taskSidebarCollapsed") + ")");
            Assert(ReferenceEquals(notice.Parent, noticeHost) && ReferenceEquals(noticeHost.Parent, sidebar) &&
                Grid.GetRow(noticeHost) == 2 && noticeHost.DesiredSize == new Size() &&
                notice.ActualWidth < sidebar.ActualWidth - 32 && notice.ActualWidth <= 300 &&
                !notice.IsHitTestVisible && !notice.Focusable && name.FontSize >= 13 && notice.ActualHeight <= 50 &&
                ReferenceEquals(Keyboard.FocusedElement, focus) && Application.Current.Windows.Count == 1,
                "compact floating toast reserves zero layout space, keeps focus and creates no window (host=" + noticeHost.DesiredSize +
                ", width=" + notice.ActualWidth + "/" + sidebar.ActualWidth + ", height=" + notice.ActualHeight + ", focus=" +
                ReferenceEquals(Keyboard.FocusedElement, focus) + ", windows=" + string.Join("|", Application.Current.Windows.Cast<Window>().Select(w => w.GetType().Name)) + ")");
            Assert(Before(tabs) == tabsBefore && Before(actions) == actionsBefore && Before(scroll) == listBefore &&
                !Before(notice).IntersectsWith(Before(actions)) && !Before(notice).IntersectsWith(Before(tabs)),
                "notice preserves layout and leaves tabs, refresh and new unobscured");
            SaveNoticeScreenshot(window, fixture, "normal");
            await Task.Delay(1200);
            var resetExpiry = System.Diagnostics.Stopwatch.StartNew();
            Call(window, "ShowProjectArchiveNotification", "second fixture project with a long readable name", archived);
            Set(window, "_taskSidebarWidth", 200d);
            Call(window, "ApplyTaskSidebarState", false);
            window.UpdateLayout();
            Assert(notice.IsVisible && name.Text.StartsWith("second fixture", StringComparison.Ordinal) &&
                notice.ActualWidth <= 184 && name.TextTrimming == TextTrimming.CharacterEllipsis,
                "repeated notice updates readable content and fits the narrow sidebar");
            SaveNoticeScreenshot(window, fixture, "narrow");
            await Task.Delay(1200);
            Assert(notice.IsVisible, "second notice resets expiry instead of inheriting the first timeout");
            await Until(() => notice.Visibility == Visibility.Collapsed, "notice expires automatically");
            Assert(resetExpiry.Elapsed >= TimeSpan.FromMilliseconds(1950) && resetExpiry.Elapsed < TimeSpan.FromMilliseconds(2700) &&
                name.Text.Length == 0 && noticeHost.DesiredSize == new Size() &&
                typeof(MainWindow).GetField("_projectArchiveNoticeTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window) is null,
                "floating toast clears after two seconds, releases its timer and leaves zero layout space");
            window.UpdateLayout();
            Rect listAfterExpiry = Before(scroll);
            Call(window, "ShowProjectArchiveNotification", "collapse fixture", archived);
            window.UpdateLayout();
            Assert(Before(scroll) == listAfterExpiry, "narrow notice also shows without shifting the list");
            Call(window, "SetTaskSidebarCollapsed", true);
            Assert(notice.Visibility == Visibility.Collapsed, "collapsing clears the notice immediately");
            Call(window, "ShowProjectArchiveNotification", "hidden fixture", archived);
            Call(window, "SetTaskSidebarCollapsed", false);
            await Task.Delay(250);
            Assert(notice.Visibility == Visibility.Collapsed, "collapsed sidebar notifications never reappear on expand");
            Call(window, "ShowProjectArchiveNotification", "hide fixture", archived);
            window.Hide(); window.Show();
            Assert(notice.Visibility == Visibility.Collapsed, "hidden window restore never replays a stale notice");
            Call(window, "ShowProjectArchiveNotification", "minimize fixture", archived);
            window.WindowState = WindowState.Minimized;
            window.WindowState = WindowState.Normal;
            Assert(notice.Visibility == Visibility.Collapsed, "minimize and restore clears the notice");
            Set(window, "_closePending", true);
            Call(window, "ShowProjectArchiveNotification", "close fixture", archived);
            Assert(notice.Visibility == Visibility.Collapsed, "closing suppresses in-app notification delivery");
            Set(window, "_closePending", false);
            Call(window, "ShowProjectArchiveNotification", "close fixture", archived);
            window.Close();
            Assert(notice.Visibility == Visibility.Collapsed, "closing the app clears any visible notice");
        }
        finally
        {
            Call(window, "DisposeResources"); window.Close();
            if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
        }
    }

    private static void SaveNoticeScreenshot(MainWindow window, string fixture, string layout)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        string screenshot = Path.Combine(Path.GetTempPath(), Path.GetFileName(fixture) + "-" + layout + ".png");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(screenshot); encoder.Save(output);
        Console.WriteLine("SCREENSHOT " + screenshot);
    }

    private static async Task VerifyManualTaskRefreshAsync()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes refresh " + Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(fixture, "workspace");
        string missing = Path.Combine(workspace, "removed project");
        string retained = Path.Combine(workspace, "retained project");
        string archived = Path.Combine(workspace, "archive", "removed archive");
        Directory.CreateDirectory(missing); Directory.CreateDirectory(retained); Directory.CreateDirectory(archived);
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(fixture, "app-data"));
        var window = new MainWindow(Path.Combine(fixture, "fixture-settings.json"), autoStartCodex: false);
        TerminalPane? pane = null;
        try
        {
            // Never load the window: all panes and history below are fake, owned fixture state.
            Set(window, "_workspace", workspace);
            await (Task)Call(window, "RefreshProjectTasksAsync")!;
            Call(window, "SelectTaskProject", missing, true);
            var editor = Field<TextBox>(window, "_tabDocumentEditor");
            await Until(() => editor.IsEnabled, "fixture plan loaded");
            editor.Text = "unsaved retained draft";
            string plan = TerminalTabDocumentStore.GetProjectPlanPath(missing);
            var drafts = Field<System.Collections.IDictionary>(window, "_tabDocumentStates");
            object draft = drafts[plan]!;
            Type launchType = typeof(MainWindow).GetNestedType("ResolvedTerminalLaunch", BindingFlags.NonPublic)!;
            object launch = Activator.CreateInstance(launchType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [false, "opencode", "Fixture", missing, TerminalTool.OpenCode], null)!;
            TabItem tab = (TabItem)Call(window, "CreateTerminalTabCore", 1, launch, "Fixture", false, null, true, false, null, null)!;
            var tabs = (TabControl)window.FindName("TerminalTabs"); tabs.Items.Add(tab);
            pane = Pane(tab);
            Field<System.Windows.Threading.DispatcherTimer>(pane, "_nativeRefreshTimer").Stop();
            Set(pane, "_nativeRefreshTimer", null);
            Set(Field<OpenCodeStatusMonitor>(pane, "_openCodeStatus"), "_snapshot", new OpenCodeStatusSnapshot(OpenCodeStatusKind.Busy, false));
            object runtime = Call(window, "RuntimeFor", missing)!;
            runtime.GetType().GetField("SingleTerminal")!.SetValue(runtime, tab);
            Directory.Delete(missing, true); Directory.Delete(archived, true);
            string added = Path.Combine(workspace, "new project");
            string addedArchive = Path.Combine(workspace, "archive", "new archive");
            Directory.CreateDirectory(added); Directory.CreateDirectory(addedArchive);
            var history = new AutomationStore(added);
            await history.StartApprovedPlanAsync("fixture history must remain unchanged");
            byte[] ledger = await File.ReadAllBytesAsync(history.StorePath);
            var actions = Field<StackPanel>(window, "_taskSidebarActions");
            // The 플랜-mode task picker shares this panel but stays collapsed outside 플랜 mode.
            var shownActions = actions.Children.OfType<UIElement>().Where(child => child.Visibility == Visibility.Visible).ToList();
            Assert(shownActions.Count == 2 && shownActions[0] is Button refresh &&
                AutomationProperties.GetName(refresh) == "태스크 새로고침" && Equals(refresh.ToolTip, "태스크 새로고침") &&
                shownActions[1] is Button create && Equals(create.Content, "＋ new"),
                "accessible refresh icon appears immediately left of new");
            var category = Field<TreeViewItem>(window, "_taskOrderGroup"); category.IsExpanded = false;
            int generation = Field<int>(window, "_taskScanGeneration");
            Task first = (Task)Call(window, "RefreshTaskListManuallyAsync")!;
            Task duplicate = (Task)Call(window, "RefreshTaskListManuallyAsync")!;
            await Task.WhenAll(first, duplicate);
            var rows = Field<System.Collections.IDictionary>(window, "_projectTaskRows");
            Assert(!rows.Contains(missing) && !rows.Contains(archived) && rows.Contains(added) && rows.Contains(addedArchive) && rows.Contains(retained),
                "manual refresh removes externally deleted normal/archive rows and discovers new folders despite cached names and pane");
            Assert(Field<int>(window, "_taskScanGeneration") == generation + 1 && !category.IsExpanded,
                "concurrent refresh requests coalesce and preserve category collapse");
            Assert(ReferenceEquals(runtime, Call(window, "RuntimeFor", missing)) && tabs.Items.Contains(tab) &&
                !Field<CancellationTokenSource>(pane, "_headlessLifetimeCancellation").IsCancellationRequested &&
                (OpenCodeStatusKind)pane.GetType().GetProperty("NativeOpenCodeStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pane)! == OpenCodeStatusKind.Busy && ReferenceEquals(drafts[plan], draft) &&
                (string)draft.GetType().GetProperty("Draft")!.GetValue(draft)! == "unsaved retained draft",
                "refresh preserves live pane ownership, running state and unsaved plan draft for missing project");
            byte[] refreshedLedger = await File.ReadAllBytesAsync(history.StorePath);
            Assert(!Directory.Exists(missing) && !Directory.Exists(archived) && ledger.SequenceEqual(refreshedLedger),
                "refresh never recreates missing folders or rewrites execution history");
            Assert(window.GetType().GetField("_selectedProjectPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is null,
                "refresh clears the deleted project's selection without destroying its retained state");
            Call(window, "SelectTaskProject", retained, true);
            await (Task)Call(window, "RefreshTaskListManuallyAsync")!;
            Assert(Field<string>(window, "_selectedProjectPath") == retained,
                "refresh preserves selection when the folder still exists");
            Field<RadioButton>(window, "_archivedTasksTab").IsChecked = true;
            ((Button)actions.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !Field<bool>(window, "_manualTaskRefresh"), "refresh click complete");
            Assert(Field<RadioButton>(window, "_archivedTasksTab").IsChecked == true && VisiblePaths(window).Contains(addedArchive) && !VisiblePaths(window).Contains(added),
                "refresh click retains archive mode and its filtered list");
        }
        finally
        {
            pane?.Dispose(); Call(window, "DisposeResources"); window.Close();
            if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
        }
    }

    private static async Task VerifyDelayedOwnedCloseAsync(string outcome)
    {
        string workspace = Path.Combine(Path.GetTempPath(), "TerminalNotes archive close " + Guid.NewGuid().ToString("N"));
        string project = Path.Combine(workspace, "fixture project");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(Path.Combine(project, "keep.txt"), "preserve project bytes");
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(workspace, "app-data"));
        var close = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new MainWindow(Path.Combine(workspace, "fixture-settings.json"), autoStartCodex: false);
        TerminalPane? pane = null;
        SafeFileHandle? sourceHandle = null;
        FileStream? unrelatedLock = null;
        Task? move = null;
        var notices = new List<(string Title, string Body)>();
        bool notifiedAfterMove = true;
        try
        {
            // Unloaded window/pane: no real startup/API/model or existing application is involved.
            Set(window, "_workspace", workspace);
            typeof(MainWindow).GetProperty("ProjectArchiveNotificationSinkOverride", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(window, (Action<string, string>)((title, body) =>
                {
                    notices.Add((title, body));
                    notifiedAfterMove &= !Directory.Exists(project) && Directory.Exists(Path.Combine(workspace, "archive", "fixture project"));
                    if (outcome == "notification-failure") throw new InvalidOperationException("fixture notification unavailable");
                }));
            Type launchType = typeof(MainWindow).GetNestedType("ResolvedTerminalLaunch", BindingFlags.NonPublic)!;
            object launch = Activator.CreateInstance(launchType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [false, "opencode", "Fixture", project, TerminalTool.OpenCode], null)!;
            TabItem tab = (TabItem)Call(window, "CreateTerminalTabCore", 1, launch, "Fixture", false, null, true, false, null, null)!;
            var tabs = (TabControl)window.FindName("TerminalTabs");
            tabs.Items.Add(tab);
            pane = Pane(tab);
            Field<System.Windows.Threading.DispatcherTimer>(pane, "_nativeRefreshTimer").Stop();
            Set(pane, "_nativeRefreshTimer", null);
            Set(Field<ConPtySession>(pane, "_session"), "_closeTask", close.Task);
            foreach (OpenCodeStatusKind state in new[] { OpenCodeStatusKind.Busy, OpenCodeStatusKind.Pending, OpenCodeStatusKind.NeedsInput })
            {
                Set(Field<OpenCodeStatusMonitor>(pane, "_openCodeStatus"), "_snapshot", new OpenCodeStatusSnapshot(state, false));
                await (Task)Call(window, "MoveProjectArchiveAsync", project)!;
                Assert(!(bool)Call(window, "CanMoveProject", project)! && tabs.Items.Contains(tab) &&
                    !Field<CancellationTokenSource>(pane, "_headlessLifetimeCancellation").IsCancellationRequested && notices.Count == 0,
                    "archive preserves " + state + " work instead of stopping it");
            }
            Set(Field<OpenCodeStatusMonitor>(pane, "_openCodeStatus"), "_snapshot", new OpenCodeStatusSnapshot(OpenCodeStatusKind.Idle, false));
            // A retained cwd/directory handle is released only when the fixture-owned close completes.
            sourceHandle = CreateFile(project, 0x80, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (sourceHandle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Assert((bool)Call(window, "CanMoveProject", project)!, "an idle native fixture is eligible for archive");
            move = (Task)Call(window, "MoveProjectArchiveAsync", project)!;
            await Task.Delay(350);
            Assert(!move.IsCompleted && Directory.Exists(project) && tabs.Items.Contains(tab),
                "archive must wait for owned close before attempting rename or removing its selectable tab");
            Assert(notices.Count == 0, "pending cleanup does not announce archive success");
            if (outcome == "success-archive-tab") Field<RadioButton>(window, "_archivedTasksTab").IsChecked = true;
            if (outcome == "new-intent")
            {
                object runtime = Call(window, "RuntimeFor", project)!;
                var intent = runtime.GetType().GetField("IntentVersion")!;
                intent.SetValue(runtime, (long)intent.GetValue(runtime)! + 1);
            }
            if (outcome == "locked")
            {
                // Hold a real exclusive archive lock after initial eligibility, independently of owned pane cleanup.
                unrelatedLock = new FileStream(Path.Combine(workspace, ".terminalnotes-archive.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            sourceHandle.Dispose(); close.TrySetResult();
            await move.WaitAsync(TimeSpan.FromSeconds(10));
            string archived = Path.Combine(workspace, "archive", "fixture project");
            if (outcome is "success" or "success-archive-tab" or "notification-failure")
            {
                Assert(!Directory.Exists(project) && File.ReadAllText(Path.Combine(archived, "keep.txt")) == "preserve project bytes" && !tabs.Items.Contains(tab),
                    "archive succeeds after exact owned cleanup and preserves project files");
                bool archiveTab = outcome == "success-archive-tab";
                Assert(Field<bool>(window, "_showArchivedTasks") == archiveTab &&
                    Field<RadioButton>(window, archiveTab ? "_archivedTasksTab" : "_normalTasksTab").IsChecked == true,
                    "archive keeps the currently selected task/archive tab after " + outcome);
                Assert(notifiedAfterMove && notices.Count == 1 && notices[0].Title == "아카이브로 보냈습니다" && notices[0].Body == "fixture project",
                    "successful archive emits one nonblocking confirmation with the project name after its folder moves");
                Assert(((TextBlock)window.FindName("StatusLabel")).Text.Contains("아카이브로 이동했습니다", StringComparison.Ordinal),
                    "completed archive status survives notification delivery failure");
            }
            else
            {
                Assert(notices.Count == 0, outcome + " emits no archive success confirmation");
                Assert(Directory.Exists(project) && !Directory.Exists(archived) && tabs.Items.Contains(tab) &&
                    File.ReadAllText(Path.Combine(project, "keep.txt")) == "preserve project bytes" &&
                    !File.Exists(Path.Combine(project, "terminal-notes", ".project-location.json")),
                    outcome + " aborts rename and retains the selectable pane, files and original location metadata");
                string status = ((TextBlock)window.FindName("StatusLabel")).Text;
                Assert(status.Contains(outcome == "new-intent" ? "상태가 변경" : "프로젝트 이동 실패", StringComparison.Ordinal),
                    outcome + " reports its concrete failure reason");
            }
            Assert(!Field<HashSet<string>>(window, "_projectArchiveWrites").Contains(project) && !Field<bool>(window, "_tabDocumentSaving"),
                "archive releases its reservation after " + outcome);
        }
        finally
        {
            sourceHandle?.Dispose(); unrelatedLock?.Dispose(); close.TrySetResult();
            if (move is not null) await move;
            pane?.Dispose(); Call(window, "DisposeResources"); window.Close();
            if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    private static async Task RunAsync()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "TerminalNotes archive " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(workspace, "app-data"));
        string source = Path.Combine(workspace, "한글 project");
        Directory.CreateDirectory(Path.Combine(source, ".git"));
        await File.WriteAllTextAsync(Path.Combine(source, ".git", "config"), "keep git config");
        await File.WriteAllBytesAsync(Path.Combine(source, "binary.dat"), [0, 1, 255, 10]);
        string plan = TerminalTabDocumentStore.GetProjectPlanPath(source);
        Directory.CreateDirectory(Path.GetDirectoryName(plan)!);
        await File.WriteAllTextAsync(plan, "# original plan\n");
        var store = new AutomationStore(source);
        var run = await store.StartApprovedPlanAsync("approved immutable snapshot");
        await store.PauseAsync(run.Id);
        byte[] originalLedger = await File.ReadAllBytesAsync(store.StorePath);
        string archive = await ProjectArchiveService.MoveAsync(workspace, source, "보관 프로젝트", false);
        Assert(!Directory.Exists(source) && Directory.Exists(archive), "archive physically moves the project under workspace/archive");
        bool staleRejected = false;
        try { await store.LoadAsync(); } catch (DirectoryNotFoundException) { staleRejected = true; }
        Assert(staleRejected && !Directory.Exists(source), "stale automation readers cannot recreate the former project directory");
        Assert((await File.ReadAllBytesAsync(Path.Combine(archive, "binary.dat"))).SequenceEqual(new byte[] { 0, 1, 255, 10 }) &&
            File.ReadAllText(Path.Combine(archive, ".git", "config")) == "keep git config", "binary files and git metadata survive the move");
        var movedStore = new AutomationStore(archive);
        var movedRun = (await movedStore.GetAsync(run.Id))!;
        Assert(movedRun.ProjectDirectory == archive && movedRun.Id == run.Id && movedRun.ApprovedPlanSnapshot == run.ApprovedPlanSnapshot && movedRun.State == AutomationState.Paused,
            "archived history keeps run identity, original plan and paused state with the new live path");
        byte[] archivedLedger = await File.ReadAllBytesAsync(movedStore.StorePath);
        Assert(originalLedger.SequenceEqual(archivedLedger), "moving and loading preserve the original ledger bytes");
        var resumed = await movedStore.ResumeAsync(run.Id, DateTimeOffset.UtcNow);
        var archivedClaim = (await movedStore.ClaimDueAsync(DateTimeOffset.UtcNow.AddSeconds(1))).Single();
        Assert(archivedClaim.ProjectDirectory == archive && archivedClaim.RunId == run.Id, "development resumes and claims work directly inside the archive");
        await Reject(() => ProjectArchiveService.MoveAsync(workspace, archive, "test", false), "running archive cannot be restored");
        await movedStore.StopAsync(run.Id);
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "collision.txt"), "do not overwrite");
        await Reject(() => ProjectArchiveService.MoveAsync(workspace, archive, "test", false), "restore collision never merges or overwrites projects");
        Assert(File.ReadAllText(Path.Combine(source, "collision.txt")) == "do not overwrite" && Directory.Exists(archive), "both colliding folders remain intact");
        string occupied = Path.Combine(workspace, "occupied-project");
        Directory.Move(source, occupied);
        await File.WriteAllTextAsync(Path.Combine(archive, "continued.txt"), "developed while archived");
        string restoredPath = await ProjectArchiveService.MoveAsync(workspace, archive, "보관 프로젝트", false);
        Assert(restoredPath == source && !Directory.Exists(archive) && File.ReadAllText(Path.Combine(source, "continued.txt")) == "developed while archived",
            "restore returns the developed project to the archive folder's parent");
        Assert((await new AutomationStore(source).GetAsync(run.Id))!.ProjectDirectory == source, "restored execution history binds back to the workspace project");
        await Reject(() => ProjectArchiveService.MoveAsync(workspace, workspace, "test", true), "workspace root cannot be archived");

        string settings = Path.Combine(workspace, "settings.json");
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new { Workspace = workspace, CodexWorkspace = workspace, SetupCompleted = true, WorkspaceDefaultsVersion = 1 }));
        var window = new MainWindow(settings, autoStartCodex: false);
        var notices = new List<(string Title, string Body)>();
        typeof(MainWindow).GetProperty("ProjectArchiveNotificationSinkOverride", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(window, (Action<string, string>)((title, body) => notices.Add((title, body))));
        try
        {
            window.Show();
            await Until(() => Field<System.Collections.IDictionary>(window, "_projectTaskRows").Contains(source), "task discovery");
            Field<System.Windows.Threading.DispatcherTimer>(window, "_automationTimer").Stop();
            Call(window, "SelectTaskProject", source, true);
            var editor = Field<TextBox>(window, "_tabDocumentEditor");
            await Until(() => editor.IsEnabled, "plan ready");
            editor.Text = "# draft saved by archive\n";
            await (Task)Call(window, "MoveProjectArchiveAsync", source)!;
            Assert(!Directory.Exists(source) && File.ReadAllText(TerminalTabDocumentStore.GetProjectPlanPath(archive)) == "# draft saved by archive\n",
                "UI archive saves the project draft before moving");
            Assert(Field<RadioButton>(window, "_normalTasksTab").IsChecked == true && VisiblePaths(window).Contains(occupied) && !VisiblePaths(window).Contains(archive),
                "sending to archive keeps the main tab and its normal task filter");
            Assert(notices.Count == 1 && notices[0].Title == "아카이브로 보냈습니다" && notices[0].Body == "보관 프로젝트",
                "archive confirms the saved display name without requiring a click");
            Assert(!Field<System.Collections.IDictionary>(window, "_projectTaskRows").Contains(Path.Combine(workspace, "archive")), "archive container never appears as a task");
            Field<RadioButton>(window, "_archivedTasksTab").IsChecked = true;
            Assert(VisiblePaths(window).Contains(archive) && !VisiblePaths(window).Contains(occupied), "archive tab shows archived projects separately from normal tasks");
            var tabs = (TabControl)window.FindName("TerminalTabs");
            // ResolveAutomationLaunch intentionally uses real OpenCode even when autoStartCodex is false.
            // Supply this test-owned ordinary shell first so navigation exercises the retained-tab path only.
            Type shellLaunchType = typeof(MainWindow).GetNestedType("ResolvedTerminalLaunch", BindingFlags.NonPublic)!;
            object shellLaunch = Activator.CreateInstance(shellLaunchType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [false, null, "Fixture shell", archive, TerminalTool.PowerShell], null)!;
            var shellTab = (TabItem)Call(window, "CreateTerminalTabCore", 1, shellLaunch, "Fixture shell", false, null, true, false, null, null)!;
            Call(window, "AttachSingleProjectTerminal", archive, shellTab);
            tabs.Items.Add(shellTab);
            Call(window, "OpenProjectTask", archive);
            await Until(() => tabs.Items.Count == 1 && Pane((TabItem)tabs.Items[0]).IsReady, "archived single terminal ready");
            var pane = Pane((TabItem)tabs.Items[0]);
            Assert(pane.WorkingDirectory == archive && Directory.Exists(archive) && !Directory.Exists(source), "opening an archived task creates its terminal at the archived path without restoring it");
            await Until(() => editor.IsEnabled, "archived plan ready");
            editor.Text = "# edited inside archive\n";
            await (Task)Call(window, "SaveTabDocumentAsync", false)!;
            Assert(File.ReadAllText(TerminalTabDocumentStore.GetProjectPlanPath(archive)) == editor.Text, "the archived task saves its plan at the archived path");
            Field<RadioButton>(window, "_normalTasksTab").IsChecked = true;
            Field<RadioButton>(window, "_archivedTasksTab").IsChecked = true;
            Assert(pane.IsRunning && ReferenceEquals(Pane((TabItem)tabs.Items[0]), pane), "switching main and archive tabs preserves the archived terminal process");
            Call(window, "ShowProjectTaskList");
            await Until(() => (bool)Call(window, "CanMoveProject", archive)!, "archived terminal settles");
            await (Task)Call(window, "MoveProjectArchiveAsync", archive)!;
            Assert(Directory.Exists(source) && !Directory.Exists(archive) && !pane.IsRunning && Field<RadioButton>(window, "_normalTasksTab").IsChecked == true,
                "UI restore retires only its idle old-path terminal and returns to the main tab");
            Assert(notices.Count == 1, "restore does not announce that a project was sent to archive");
            Call(window, "SelectTaskProject", source, true);
            await Until(() => editor.IsEnabled, "restored plan ready");
            editor.Text = "unsaved conflicting draft";
            await File.WriteAllTextAsync(plan, "external edit");
            await (Task)Call(window, "MoveProjectArchiveAsync", source)!;
            Assert(Directory.Exists(source) && !Directory.Exists(archive) && editor.Text == "unsaved conflicting draft" && File.ReadAllText(plan) == "external edit",
                "a plan conflict aborts archive and preserves both the draft and external edit");
            Assert(notices.Count == 1, "draft conflict does not announce archive success");
        }
        finally { Call(window, "DisposeResources"); window.Close(); }
        var fresh = new MainWindow(settings, autoStartCodex: false);
        try
        {
            fresh.Show();
            await Until(() => Field<System.Collections.IDictionary>(fresh, "_projectTaskRows").Contains(source), "restored project discovery after restart");
            Assert((string)Call(fresh, "ProjectDisplayName", source)! == "보관 프로젝트" &&
                !Field<Dictionary<string, bool>>(fresh, "_projectTaskMultiModes")[source], "project display name and mode persist through archive, restore and restart");
        }
        finally { Call(fresh, "DisposeResources"); fresh.Close(); }
        Console.WriteLine("Fixture: " + workspace);
    }

    private static IEnumerable<string> VisiblePaths(MainWindow window) =>
        Field<TreeViewItem>(window, "_taskOrderGroup").Items.Cast<TreeViewItem>().Select(node => (string)node.Tag);
    private static TerminalPane Pane(TabItem tab) => (TerminalPane)tab.Tag.GetType().GetProperty("Pane")!.GetValue(tab.Tag)!;
    private static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;
    private static void Set(object o, string name, object? value) => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(o, value);
    private static object? Call(object o, string name, params object?[] args) => o.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(o, args);
    private static void Assert(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
    private static async Task Until(Func<bool> condition, string name)
    {
        for (int i = 0; i < 250; i++) { if (condition()) return; await Task.Delay(100); }
        throw new TimeoutException(name);
    }
    private static async Task Reject(Func<Task<string>> action, string name)
    {
        bool failed = false;
        try { await action(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { failed = true; }
        Assert(failed, name);
    }
}
