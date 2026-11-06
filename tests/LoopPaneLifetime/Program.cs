using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TerminalNotes;
using TerminalNotes.Services;

internal static class Program
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int _checks;

    [STAThread]
    private static int Main()
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string root = Path.Combine(Path.GetTempPath(), "TerminalNotes LoopPaneLifetime " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        object? previousData = AppContext.GetData("TerminalNotes.DataDirectory");
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(root, "data"));
        try
        {
            Verify(root);
            Console.WriteLine($"PASS {_checks} pane lifetime assertions: repeated replacement, exact handler removal, active callbacks, queued invalidation, close and close-all; no shell/PTY/model/WSL.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine("FAIL " + exception); return 1; }
        finally
        {
            AppContext.SetData("TerminalNotes.DataDirectory", previousData);
            Console.WriteLine("Isolated fixture: " + root);
        }
    }

    private static void Verify(string root)
    {
        string settings = Path.Combine(root, "settings.json");
        File.WriteAllText(settings, JsonSerializer.Serialize(new { Workspace = root, SetupCompleted = true, WorkMode = "Advanced" }));
        var hooks = new OpenCodeStartupHooks(_ => throw new InvalidOperationException("Unexpected startup probe."),
            () => false, _ => throw new InvalidOperationException("Unexpected installation."));
        var window = new MainWindow(settings, autoStartCodex: false, startupHooks: hooks);
        var panes = new List<TerminalPane>();
        try
        {
            // Never show the window: Loaded cannot launch terminals/browser/native servers.
            var sampler = Field(window, "_resourceDiagnostics")!;
            ((IDisposable)sampler).Dispose();
            ((Task)sampler.GetType().GetProperty("Completion", Members)!.GetValue(sampler)!).GetAwaiter().GetResult();
            Set(window, "_resourceDiagnostics", null);
            Set(window, "_autoResumeDiagnosticRestoreTask", Task.CompletedTask);
            Set(window, "_autoResumeEnabled", false);
            VerifyStatusRoutes(window);
            Call(window, "InitializeTaskNotificationOptions", true, null);
            var notices = new List<TaskNotificationNotice>();
            typeof(MainWindow).GetProperty("TaskNotificationSinkOverride", Members)!.SetValue(window,
                (Action<TaskNotificationNotice>)notices.Add);
            string project = Path.Combine(root, "restarting"), otherProject = Path.Combine(root, "unrelated");
            Directory.CreateDirectory(project); Directory.CreateDirectory(otherProject);
            var unrelated = Create(window, otherProject, panes);
            var tab = Create(window, project, panes);
            VerifyTerminalErrorRoute(window, Pane(tab));
            for (int index = 0; index < 32; index++)
            {
                var retired = Pane(tab);
                Call(window, "AttachAutoResumeDiagnostics", project, retired);
                Check(Map(window, "_autoResumeDiagnosticHandlers").Count == 2 && Map(window, "_autoResumeResourceWaitHandlers").Count == 2,
                    "Duplicate attach or repeated replacements grew the handler registries.");
                Map(window, "_automationPaneTokens")[retired] = (long)index;
                Call(Field(window, "_automationPanesToReplace")!, "Add", retired);
                var queuedDiagnostic = Event(retired, "AutoResumeDiagnosticEmitted")!;
                var stale = Diagnostic("retired-" + index);
                Task.Run(() => queuedDiagnostic.DynamicInvoke(stale)).GetAwaiter().GetResult();
                Action<OpenCodeAutoResumeDiagnostic> externalObserver = _ => { };
                var diagnosticEvent = typeof(TerminalPane).GetEvent("AutoResumeDiagnosticEmitted", Members)!;
                diagnosticEvent.GetAddMethod(true)!.Invoke(retired, [externalObserver]);
                typeof(MainWindow).GetNestedType("TerminalTabState", BindingFlags.NonPublic)!
                    .GetProperty("ExitCode", Members)!.SetValue(tab.Tag, 0);
                tab = Create(window, project, panes);
                Check(!ReferenceEquals(Pane(tab), retired), "An exited pane was reused instead of replaced.");
                Check(!Map(window, "_autoResumeDiagnosticHandlers").Contains(retired) &&
                    !Map(window, "_autoResumeResourceWaitHandlers").Contains(retired) &&
                    !Map(window, "_automationPaneTokens").Contains(retired) &&
                    !(bool)Call(Field(window, "_automationPanesToReplace")!, "Contains", retired)!,
                    "A retired pane remains strongly held by an app-owned pane registry.");
                Check(Event(retired, "AutoResumeDiagnosticEmitted")?.GetInvocationList() is [var remaining] &&
                    remaining.Equals(externalObserver) && Event(retired, "AutoResumeResourceWaitStarted") is null,
                    "Retirement failed to remove exact app handlers or removed another subscriber.");
                Call(window, "DisposeSafely", retired);
                var active = Diagnostic("active-" + index);
                Event(Pane(tab), "AutoResumeDiagnosticEmitted")!.DynamicInvoke(active);
                Drain();
                Check(ReferenceEquals(Map(window, "_autoResumeDiagnostics")[project], active),
                    "A retired queued callback overwrote the new active pane diagnostic.");
                Check(Map(window, "_autoResumeDiagnosticHandlers").Count == 2 &&
                    Map(window, "_autoResumeResourceWaitHandlers").Count == 2 &&
                    Event(Pane(unrelated), "AutoResumeDiagnosticEmitted") is not null,
                    "Idempotent retirement damaged another active pane or grew the handler maps.");
                Check(((IList)Field(window, "_createdWorkPanes")!).Count == 2,
                    "Repeated Advanced-mode replacement retained retired or dead weak-reference wrappers.");
            }

            EmitResourceWait(Pane(tab), "replacement-active");
            EmitResourceWait(Pane(unrelated), "unrelated-active");
            Check(notices.Count == 2 && notices.Select(notice => notice.ProjectKey).ToHashSet().SetEquals([project, otherProject]),
                "Remaining panes lost their own resource-wait notifications after repeated retirement.");
            VerifyResourcePopups(window, Pane(tab));

            var closed = Pane(tab);
            Map(window, "_automationPaneTokens")[closed] = 1L;
            Call(Field(window, "_automationPanesToReplace")!, "Add", closed);
            Call(window, "CloseTerminal", tab, closed);
            Check(Map(window, "_autoResumeDiagnosticHandlers").Count == 1 &&
                Map(window, "_autoResumeResourceWaitHandlers").Count == 1 &&
                !Map(window, "_automationPaneTokens").Contains(closed) &&
                !(bool)Call(Field(window, "_automationPanesToReplace")!, "Contains", closed)! &&
                Event(closed, "AutoResumeDiagnosticEmitted") is null && Event(closed, "AutoResumeResourceWaitStarted") is null,
                "Individual close retained pane keys or subscriptions.");
            EmitResourceWait(Pane(unrelated), "after-other-close");
            Check(notices.Count == 3, "Closing another pane silenced the surviving pane.");
            Map(window, "_automationPaneTokens")[Pane(unrelated)] = 1L;
            Call(Field(window, "_automationPanesToReplace")!, "Add", Pane(unrelated));
            Call(window, "CloseAllTerminals", false);
            Check(Map(window, "_autoResumeDiagnosticHandlers").Count == 0 &&
                Map(window, "_autoResumeResourceWaitHandlers").Count == 0 &&
                Map(window, "_automationPaneTokens").Count == 0 &&
                (int)Field(window, "_automationPanesToReplace")!.GetType().GetProperty("Count")!.GetValue(Field(window, "_automationPanesToReplace"))! == 0 &&
                Event(Pane(unrelated), "AutoResumeDiagnosticEmitted") is null &&
                Event(Pane(unrelated), "AutoResumeResourceWaitStarted") is null,
                "Close-all retained pane keys or subscriptions.");
            Check(!(bool)Field(window, "_windowInitialized")! && panes.All(pane => !pane.IsRunning),
                "The isolated fixture started a real terminal or loaded the application.");
            Drain();
            Check(Map(window, "_retiringWorkPanes").Count == 0,
                "Completed close-all cleanup retained panes in Advanced mode without a Basic admission query.");
            Check(((IList)Field(window, "_createdWorkPanes")!).Count == 0,
                "Close-all retained weak-reference wrappers for disposed panes.");
            VerifyRetiredCleanup(window, closed);
            Call(window, "DisposeTaskNotifications");
            Call(window, "DisposeTaskNotifications");
            VerifyVisibleResourcePopup(window);
        }
        finally
        {
            Call(window, "DisposeResources");
            foreach (var pane in panes) pane.Dispose();
            window.Close();
        }
    }

    private static void VerifyStatusRoutes(MainWindow window)
    {
        Call(window, "SetStatus", "저장 완료 · C:\\오류\\plan.md");
        object presenter = Field(window, "_statusNotifications")!;
        var entries = (IList)Field(presenter, "_entries")!;
        int count = entries.Count;
        foreach (string project in new[] { "오류", "실패", "과부하" })
        {
            string selection = project + " · C:\\isolated\\plan.md";
            Call(window, "SetInlineStatus", selection);
            Check(((TextBlock)Field(window, "StatusLabel")!).Text == selection && entries.Count == count,
                "Project selection lost its inline label or interpreted a project name as a popup warning.");
        }
        Call(window, "SetAttentionStatus", "CMD 이어가기 · Unexpected provider response 418");
        Check(entries.Count == count + 1 && (bool)Field(presenter, "_pending")!,
            "A typed failure with a neutral heading did not retain its deferred warning.");
        Call(window, "SetStatus", "검색 결과가 없습니다");
        Call(window, "SetProgressStatus", "오류 복구 중 · 원래 요청을 이어갑니다");
        Check(entries.Count == count + 1 && (bool)Field(presenter, "_pending")!,
            "Routine result/recovery changed actionable history or erased its deferred popup.");
    }

    private static void VerifyTerminalErrorRoute(MainWindow window, TerminalPane pane)
    {
        object presenter = Field(window, "_statusNotifications")!;
        var entries = (IList)Field(presenter, "_entries")!;
        int count = entries.Count;
        var observed = new List<string>();
        Action<string> handler = observed.Add;
        pane.StatusChanged += handler;
        try
        {
            foreach (string error in new[] { "Unexpected provider response 418",
                "터미널 화면과 연결이 끊겨 이 세션을 종료했습니다. 새 터미널을 열어주세요." })
            {
                Call(pane, "ShowError", error);
                Check(((TextBlock)Field(pane, "_notice")!).Text == error && observed.LastOrDefault() == error &&
                    ((TextBlock)Field(window, "StatusLabel")!).Text == error,
                    "A terminal failure lost its existing in-pane/inline display or StatusChanged subscriber.");
            }
            Check(entries.Count == count + 2 && (bool)Field(presenter, "_pending")!,
                "English terminal errors/fatal disconnects failed to queue actionable notifications.");
        }
        finally { pane.StatusChanged -= handler; }
    }

    private static TabItem Create(MainWindow window, string project, List<TerminalPane> panes)
    {
        var tab = (TabItem)Call(window, "EnsureSingleProjectTerminalWithoutPlanDocument", project)!;
        var pane = Pane(tab);
        panes.Add(pane);
        // Disable unloaded native polling as well as all actual process startup.
        (Field(pane, "_nativeRefreshTimer") as DispatcherTimer)?.Stop();
        Set(pane, "_nativeRefreshTimer", null);
        return tab;
    }

    private static void VerifyResourcePopups(MainWindow window, TerminalPane pane)
    {
        var sinkProperty = typeof(MainWindow).GetProperty("TaskNotificationSinkOverride", Members)!;
        object? originalSink = sinkProperty.GetValue(window);
        sinkProperty.SetValue(window, null);
        var pending = Map(window, "_pendingResourceNotifications");
        bool relevant = true;
        EmitResourceWait(pane, "deferred-popup", () => relevant);
        Check(pending.Count == 1, "A resource-wait event failed to queue its own app notification while hidden.");
        EmitResourceWait(pane, "deferred-popup", () => relevant);
        Check(pending.Count == 1, "The same pending wait episode queued duplicate app notifications.");
        relevant = false;
        Call(window, "ShowPendingResourceNotifications");
        Check(pending.Count == 0, "A recovered wait left a stale deferred app popup.");
        Call(window, "InitializeTaskNotificationOptions", false, null);
        EmitResourceWait(pane, "muted-popup");
        Check(pending.Count == 0, "A disabled task notification queued a resource popup.");
        Call(window, "InitializeTaskNotificationOptions", true, null);

        Type pressure = typeof(MainWindow).Assembly.GetType("TerminalNotes.Services.TerminalResourcePressure", true)!;
        object state = pressure.GetField("State", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        object? originalPressure = Field(state, "_current");
        Type noticeType = typeof(MainWindow).Assembly.GetType("TerminalNotes.Services.TerminalResourcePressureNotice", true)!;
        bool wasNative = (bool)Field(pane, "_usesNativeOpenCodeStatus")!;
        try
        {
            // Synthetic activity supplies the busy state without any live process.
            Set(pane, "_usesNativeOpenCodeStatus", false);
            Set(pane, "_automationBusy", true);
            object Notice(string code, bool critical) => Activator.CreateInstance(noticeType,
                code, "Synthetic memory/commit 98%", critical, 98, 98d, 16UL * 1024 * 1024)!;
            void Publish(object? notice, bool worker = false)
            {
                Set(state, "_current", notice);
                var changed = (Delegate)Field(state, "Changed")!;
                if (worker) Task.Run(() => changed.DynamicInvoke([notice])).GetAwaiter().GetResult();
                else changed.DynamicInvoke([notice]);
            }
            Publish(Notice("cpu", false));
            Check(pending.Count == 0, "CPU-only active work generated a critical-memory popup.");
            Publish(Notice("memory", true));
            Check(pending.Contains("critical-memory"), "Critical memory during active work did not queue an owned app notification.");
            Call(window, "RefreshCriticalResourcePressureNotification");
            Check(pending.Count == 1, "Repeated samples duplicated a critical-memory episode.");
            Publish(null);
            Check(pending.Count == 0, "Recovery left a stale deferred critical-memory popup.");
            Publish(Notice("memory", true), worker: true);
            Publish(null);
            Drain();
            Check(pending.Count == 0, "A queued critical-memory callback survived a later recovery generation.");
            Publish(Notice("memory", true));
            Check(pending.Count == 1, "A new critical-memory episode was silenced by the earlier episode.");
            Publish(null);
            Call(window, "InitializeTaskNotificationOptions", false, null);
            Publish(Notice("memory", true));
            Check(pending.Count == 0, "Critical-memory popups ignored disabled notification preferences.");
            Publish(null);
        }
        finally
        {
            Set(state, "_current", originalPressure);
            Set(pane, "_usesNativeOpenCodeStatus", wasNative);
            Set(pane, "_automationBusy", false);
            Call(window, "InitializeTaskNotificationOptions", true, null);
            sinkProperty.SetValue(window, originalSink);
            Call(window, "ShowPendingResourceNotifications");
        }
    }

    private static void VerifyRetiredCleanup(MainWindow window, TerminalPane pane)
    {
        var retiring = Map(window, "_retiringWorkPanes");
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        retiring[pane] = cleanup.Task;
        Task release = (Task)Call(window, "ReleaseRetiredWorkPaneAsync", pane, cleanup.Task)!;
        Check(retiring.Contains(pane), "Pending owned-process cleanup was discarded before completion.");
        cleanup.SetResult();
        release.GetAwaiter().GetResult(); Drain();
        Check(!retiring.Contains(pane), "Successful cleanup did not release its pane without a work-mode query.");
        var failed = Task.FromException(new IOException("Synthetic exact cleanup failure"));
        retiring[pane] = failed;
        ((Task)Call(window, "ReleaseRetiredWorkPaneAsync", pane, failed)!).GetAwaiter().GetResult(); Drain();
        Check(retiring.Contains(pane), "Failed cleanup incorrectly released the admission guard.");
        var old = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        retiring[pane] = old.Task;
        Task oldRelease = (Task)Call(window, "ReleaseRetiredWorkPaneAsync", pane, old.Task)!;
        var newer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        retiring[pane] = newer.Task;
        old.SetResult(); oldRelease.GetAwaiter().GetResult(); Drain();
        Check(ReferenceEquals(retiring[pane], newer.Task), "An older completion removed a newer pending cleanup guard.");
        retiring.Clear();
    }

    private static void VerifyVisibleResourcePopup(MainWindow window)
    {
        // All pane hosts are empty after close-all. Remove only the real app startup
        // handler so this isolated owner can be shown without launching anything.
        window.Loaded -= typeof(MainWindow).GetMethod("Window_Loaded", Members)!.CreateDelegate<RoutedEventHandler>(window);
        window.ShowActivated = false;
        window.Show(); Drain();
        Call(window, "QueueResourceNotification", "synthetic-visible-warning",
            "Synthetic critical memory warning; automatic recovery continues.", (Func<bool>)(() => true));
        Drain();
        object presenter = Field(window, "_statusNotifications")!;
        var popup = (Window)Field(presenter, "_window")!;
        var entries = (IList)Field(presenter, "_entries")!;
        Check(popup.IsVisible && ReferenceEquals(popup.Owner, window) && !popup.ShowActivated && !popup.ShowInTaskbar,
            "A visible resource warning did not use the existing nonactivating owned app popup.");
        Check((string)entries[0]!.GetType().GetProperty("Message", Members)!.GetValue(entries[0])! ==
            "Synthetic critical memory warning; automatic recovery continues.", "The app popup omitted the resource warning.");
        int count = entries.Count;
        window.Hide();
        bool relevant = true;
        Call(window, "QueueResourceNotification", "synthetic-stale-hidden-warning", "A recovered warning must not appear.",
            (Func<bool>)(() => relevant));
        relevant = false;
        window.Show(); Drain();
        Check(entries.Count == count && !Map(window, "_pendingResourceNotifications").Contains("synthetic-stale-hidden-warning"),
            "Restoring the app displayed a resource warning that had already recovered.");
        Check(!(bool)Field(window, "_windowInitialized")!, "Visible popup verification ran real app startup.");
        window.Hide();
    }

    private static void EmitResourceWait(TerminalPane pane, string episode, Func<bool>? relevant = null)
    {
        object notice = Activator.CreateInstance(typeof(TerminalPane).GetNestedType("AutoResumeResourceWaitNotice", BindingFlags.NonPublic)!,
            episode, "Synthetic memory pressure", relevant ?? (() => true))!;
        Event(pane, "AutoResumeResourceWaitStarted")!.DynamicInvoke(notice);
    }

    private static OpenCodeAutoResumeDiagnostic Diagnostic(string code) => new(DateTimeOffset.UtcNow, code, "test", code, null, null, null);
    private static TerminalPane Pane(TabItem tab) => (TerminalPane)tab.Tag.GetType().GetProperty("Pane", Members)!.GetValue(tab.Tag)!;
    private static Delegate? Event(TerminalPane pane, string name) => (Delegate?)Field(pane, name);
    private static IDictionary Map(MainWindow window, string name) => (IDictionary)Field(window, name)!;
    private static object? Field(object target, string name) => target.GetType().GetField(name, Members)!.GetValue(target);
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Members)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] arguments)
    {
        try { return target.GetType().GetMethod(name, Members)!.Invoke(target, arguments); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        { ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
    }
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, (Action)(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Check(bool condition, string message)
    { _checks++; if (!condition) throw new InvalidOperationException(message); }
}
