using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TerminalNotes;
using TerminalNotes.Services;

internal static class Program
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int _assertions;

    [STAThread]
    private static int Main()
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes Loop wait " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        object? previousData = AppContext.GetData("TerminalNotes.DataDirectory");
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(fixture, "data"));
        try
        {
            Verify(fixture);
            Console.WriteLine($"PASS {_assertions} Loop resource notification assertions; unloaded panes, fake admission, no native processes/provider calls.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; }
        finally
        {
            AppContext.SetData("TerminalNotes.DataDirectory", previousData);
            // Retain the isolated fixture as evidence; never touch real settings or sessions.
            Console.WriteLine("Isolated fixture: " + fixture);
        }
    }

    private static void Verify(string fixture)
    {
        string project = Path.Combine(fixture, "waiting-project");
        Directory.CreateDirectory(project);
        string settings = Path.Combine(fixture, "settings.json");
        File.WriteAllText(settings, JsonSerializer.Serialize(new { Workspace = fixture, SetupCompleted = true }));
        var window = new MainWindow(settings, autoStartCodex: false);
        var panes = new List<TerminalPane>();
        try
        {
            Set(window, "_workspace", fixture);
            Set(window, "_selectedProjectPath", Path.Combine(fixture, "different-selected-project"));
            Call(window, "InitializeTaskNotificationOptions", true, null);
            var notices = new List<TaskNotificationNotice>();
            typeof(MainWindow).GetProperty("TaskNotificationSinkOverride", Members)!.SetValue(window,
                (Action<TaskNotificationNotice>)(notice => notices.Add(notice)));
            var pane = Pane(project); panes.Add(pane);
            object runtime = Call(window, "RuntimeFor", project)!;
            Bind(runtime, pane);
            Call(window, "AttachAutoResumeDiagnostics", project, pane);
            Call(window, "AttachAutoResumeDiagnostics", project, pane);

            var first = Admission();
            var statuses = new List<string>();
            Task<IDisposable> pending = first.Start(pane, statuses.Add, default);
            foreach (string code in new[] { "queue", "spacing", "measurement_unavailable", "recovery" }) first.Report(code);
            Require(notices.Count == 0, "Non-pressure wait emitted an overload notification.");
            first.Report("cpu", "터미널 시작 대기 · CPU 부하가 낮아지기를 기다리는 중 · CPU 95%");
            Require(notices.Count == 1 && notices[0].Kind == TaskNotificationKind.ResourceWaiting && notices[0].ProjectKey == project,
                "CPU wait did not reach the existing sink once for its actual project.");
            Require((bool)typeof(TerminalPane).GetProperty("IsAutoResumeResourceWaiting", Members)!.GetValue(pane)!,
                "Pending pressure was not visible to generic-stall suppression.");
            object firstNotice = typeof(TerminalPane).GetField("_autoResumeResourceWait", Members)!.GetValue(pane)!;
            string reason = (string)firstNotice.GetType().GetProperty("Reason")!.GetValue(firstNotice)!;
            Require(reason == statuses[^1] && reason.Contains("자동 진행") && reason.Contains("CPU 95%") && !reason.Contains("터미널 시작"),
                "Notification and inline status did not preserve and normalize the same measured reason.");
            var firstRelevant = (Func<bool>)firstNotice.GetType().GetProperty("StillRelevant")!.GetValue(firstNotice)!;
            foreach (string code in new[] { "cpu", "memory", "recovery", "cpu", "memory" }) first.Report(code);
            Require(notices.Count == 1 && statuses.Count == 10 && statuses[^1].Contains("memory"),
                "Changing pressure/recovery samples spammed alerts or lost inline status updates.");
            Finish(first, pending);
            Require(!firstRelevant() && !(bool)typeof(TerminalPane).GetProperty("IsAutoResumeResourceWaiting", Members)!.GetValue(pane)!,
                "Granted admission retained a live resource notification.");

            var next = Admission();
            pending = next.Start(pane, _ => { }, default);
            first.Report("memory", "stale old callback");
            Require(notices.Count == 1, "Old callback survived a new wait episode.");
            next.Report("memory");
            Require(notices.Count == 2 && notices[0].AttemptId != notices[1].AttemptId,
                "A separate memory episode did not receive its own notification.");
            Finish(next, pending);

            foreach (string preference in new[] { "off", "snooze" })
            {
                Call(window, "InitializeTaskNotificationOptions", preference != "off",
                    preference == "snooze" ? DateTimeOffset.UtcNow.AddMinutes(10) : null);
                var muted = Admission(); pending = muted.Start(pane, _ => { }, default);
                muted.Report("cpu");
                Call(window, "InitializeTaskNotificationOptions", true, null);
                muted.Report("memory");
                Require(notices.Count == 2, preference + " did not mute the episode or replayed it on enable.");
                Finish(muted, pending);
            }

            foreach (string invalidation in new[] { "cancel", "input", "disposed", "replaced", "stopped", "completed", "new-episode", "close" })
            {
                int baseline = notices.Count;
                var current = Pane(project); panes.Add(current); Bind(runtime, current);
                Call(window, "AttachAutoResumeDiagnostics", project, current);
                using var cancellation = new CancellationTokenSource();
                var held = Admission(); pending = held.Start(current, _ => { }, cancellation.Token);
                // Run the real pane event on a worker, leaving its UI callback queued.
                Task.Run(() => held.Report("cpu")).GetAwaiter().GetResult();
                Require(notices.Count == baseline, "Worker notification bypassed the UI dispatcher.");
                IHeldAdmission? newer = null; Task<IDisposable>? newPending = null;
                if (invalidation == "cancel") cancellation.Cancel();
                else if (invalidation == "input") { Set(current, "_manualInputOverride", true); cancellation.Cancel(); }
                else if (invalidation == "disposed") current.Dispose();
                else if (invalidation == "replaced") Bind(runtime, pane);
                else if (invalidation == "stopped") Set(runtime, "SingleStopped", true);
                else if (invalidation == "completed") Finish(held, pending);
                else if (invalidation == "new-episode")
                {
                    newer = Admission(); newPending = newer.Start(current, _ => { }, default);
                }
                else Set(window, "_closePending", true);
                Drain();
                Require(notices.Count == baseline, "Queued notification survived " + invalidation + ".");
                held.Report("memory"); Drain();
                Require(notices.Count == baseline, "Repeated/stale pressure callback survived " + invalidation + ".");
                if (newer is not null)
                {
                    newer.Report("memory");
                    Require(notices.Count == baseline + 1, "New valid episode was silenced by the old one.");
                    Finish(newer, newPending!);
                }
                if (!pending.IsCompleted) held.Release();
                try { pending.GetAwaiter().GetResult().Dispose(); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                Set(runtime, "SingleStopped", false); Set(window, "_closePending", false);
            }

            Call(window, "DisposeTaskNotifications");
            Require(((IDictionary)typeof(MainWindow).GetField("_autoResumeResourceWaitHandlers", Members)!.GetValue(window)!).Count == 0,
                "Notification disposal retained pane event subscriptions.");
        }
        finally
        {
            foreach (var pane in panes) pane.Dispose();
            Call(window, "DisposeResources");
        }
    }

    private static TerminalPane Pane(string project)
    {
        var pane = new TerminalPane(project, startupCommand: "opencode", protectTuiViewport: true, useProjectContext: false);
        (typeof(TerminalPane).GetField("_nativeRefreshTimer", Members)!.GetValue(pane) as DispatcherTimer)?.Stop();
        Set(pane, "_nativeRefreshTimer", null);
        return pane;
    }
    private static void Bind(object runtime, TerminalPane pane)
    {
        Type stateType = typeof(MainWindow).GetNestedType("TerminalTabState", BindingFlags.NonPublic)!;
        object state = Activator.CreateInstance(stateType, true)!;
        Set(state, "Pane", pane); Set(state, "PlanDocumentPath", "");
        Set(runtime, "SingleTerminal", new TabItem { Tag = state });
    }
    private static void Finish(IHeldAdmission held, Task<IDisposable> pending)
    { held.Release(); pending.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult().Dispose(); }
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, (Action)(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static IHeldAdmission Admission() => (IHeldAdmission)Activator.CreateInstance(
        typeof(HeldAdmission<>).MakeGenericType(typeof(TerminalPane).Assembly.GetType("TerminalNotes.Services.StartupResourceWait", true)!))!;
    private interface IHeldAdmission
    {
        Task<IDisposable> Start(TerminalPane pane, Action<string> waiting, CancellationToken token);
        void Report(string code, string? reason = null);
        void Release();
    }
    private sealed class HeldAdmission<TWait> : IHeldAdmission
    {
        private readonly TaskCompletionSource<IDisposable> _grant = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action<TWait>? _waiting;
        public Task<IDisposable> Start(TerminalPane pane, Action<string> waiting, CancellationToken token)
        {
            Func<Action<TWait>, CancellationToken, Task<IDisposable>> acquire = (report, cancellation) =>
            { _waiting = report; return _grant.Task.WaitAsync(cancellation); };
            return (Task<IDisposable>)Call(pane, "AcquireAutoResumeAdmissionAsync", waiting, token, acquire)!;
        }
        public void Report(string code, string? reason = null) =>
            _waiting!((TWait)Activator.CreateInstance(typeof(TWait), code, reason ?? (code + " changing resource metrics"))!);
        public void Release() => _grant.TrySetResult(new Lease());
    }
    private sealed class Lease : IDisposable { public void Dispose() { } }
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Members)!.Invoke(target, args);
    private static void Set(object target, string name, object? value)
    {
        if (target.GetType().GetProperty(name, Members) is { } property) property.SetValue(target, value);
        else target.GetType().GetField(name, Members)!.SetValue(target, value);
    }
    private static void Require(bool condition, string message)
    { _assertions++; if (!condition) throw new InvalidOperationException(message); }
}
