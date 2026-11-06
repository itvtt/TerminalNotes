using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TerminalNotes;
using TerminalNotes.Services;

internal static class Program
{
    private static int _failure;
    private static int _assertions;
    private static readonly MethodInfo Sse = typeof(OpenCodeStatusMonitor).GetMethod("HandleSseData", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { await VerifyAsync(); }
            catch (Exception exception) { _failure = 1; Console.Error.WriteLine("FAIL " + exception); }
            finally { app.Shutdown(_failure); }
        };
        app.Run();
        return _failure;
    }

    private static async Task VerifyAsync()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes status dispatch " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var panes = new List<TerminalPane>();
        try
        {
            var monitors = new List<OpenCodeStatusMonitor>();
            int visualChanges = 0;
            int confirmedSubmissions = 0;
            for (int i = 0; i < 3; i++)
            {
                string project = Path.Combine(fixture, "project-" + i);
                Directory.CreateDirectory(project);
                // Unloaded panes cannot start a browser, terminal, native server, or model.
                var pane = new TerminalPane(project, startupCommand: "opencode", protectTuiViewport: true, useProjectContext: false);
                panes.Add(pane);
                Field<DispatcherTimer>(pane, "_nativeRefreshTimer").Stop();
                Set(pane, "_nativeRefreshTimer", null); // No real API polling in this fake-only fixture.
                var monitor = Field<OpenCodeStatusMonitor>(pane, "_openCodeStatus");
                monitors.Add(monitor);
                pane.GetType().GetEvent("NativeSubmissionConfirmed", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetAddMethod(true)!.Invoke(pane, [new Action<long, long>((_, _) => confirmedSubmissions++)]);
                Set(monitor, "_connected", true);
                Set(monitor, "_hasBaseline", true);
                monitor.SetSessionScope("session-" + i);
                monitor.ObserveSubmission();
                Event(monitor, "message.updated", new { info = new { id = "user-" + i, sessionID = "session-" + i, role = "user", time = new { created = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds() } } });
                Event(monitor, "message.updated", new { info = new { id = "assistant-" + i, sessionID = "session-" + i, role = "assistant", parentID = "user-" + i } });
                Event(monitor, "session.status", new { sessionID = "session-" + i, status = new { type = "busy" } });
                // Model the existing all-project visual refresh cost after each pane event.
                pane.ActivityChanged += () => { visualChanges++; Thread.Sleep(1); };
            }
            Console.WriteLine("Fixture prepared: three unloaded native panes; no native process/API requests.");
            await DrainAsync();
            Console.WriteLine("Starting three fake SSE streams.");
            Require(confirmedSubmissions == 3, "Display batching lost exact native submission confirmation signals.");
            visualChanges = 0;
            int normalPosts = 0;
            Dispatcher dispatcher = Application.Current.Dispatcher;
            DispatcherHookEventHandler posted = (_, args) => { if (args.Operation.Priority == DispatcherPriority.Normal) Interlocked.Increment(ref normalPosts); };
            dispatcher.Hooks.OperationPosted += posted;
            TimeSpan inputLatency;
            try
            {
                inputLatency = await Task.Run(async () =>
                {
                    Task[] streams = monitors.Select((monitor, index) => Task.Run(() =>
                    {
                        // Three concurrent fake SSE producers use the production JSON event parser.
                        for (int j = 0; j < 1500; j++)
                            Event(monitor, "message.part.delta", new { sessionID = "session-" + index, messageID = "assistant-" + index, partID = "part-" + index, field = "text", delta = "x" });
                    })).ToArray();
                    await Task.WhenAll(streams);
                    var watch = Stopwatch.StartNew();
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
                    return watch.Elapsed;
                });
                await DrainAsync();
            }
            finally { dispatcher.Hooks.OperationPosted -= posted; }
            Console.WriteLine($"MEASURE three streams/4500 deltas: Input={inputLatency.TotalMilliseconds:F0}ms NormalPosts={normalPosts} VisualChanges={visualChanges}");
            Require(inputLatency < TimeSpan.FromMilliseconds(400), "Concurrent native activity callbacks starved Input priority.");
            Require(normalPosts < 40 && visualChanges < 60, "Streaming deltas still cause unbounded per-token UI refreshes.");
            foreach (var pane in panes) RequireColor(pane, "#00853F", "Busy after concurrent streams");

            // Latest state must survive hidden panes and queued old events; no intermediate replay.
            panes[1].Visibility = Visibility.Collapsed;
            await Task.Run(() =>
            {
                Event(monitors[1], "session.status", new { sessionID = "session-1", status = new { type = "idle" } });
                Event(monitors[1], "session.status", new { sessionID = "session-1", status = new { type = "busy" } });
            });
            await DrainAsync();
            panes[1].Visibility = Visibility.Visible;
            RequireColor(panes[1], "#00853F", "Hidden pane latest busy");
            Set(monitors[1], "_lastActivityAt", (DateTimeOffset?)DateTimeOffset.UtcNow.AddMinutes(-1));
            Call(panes[1], "ApplyActivityChange");
            RequireColor(panes[1], "#00853F", "Quiet native busy");
            Set(panes[1], "_nativeAutomationSupervisionNotice", "진행 정체 확인 중");
            Call(panes[1], "ApplyActivityChange");
            RequireColor(panes[1], "#00853F", "Busy with supervision notice");
            using var recoveryClient = new OpenCodeRecoveryClient(new Uri("http://127.0.0.1:43127/"), panes[1].WorkingDirectory);
            using var recovery = new OpenCodeAutoResumeController(panes[1].WorkingDirectory, null, recoveryClient, () => false);
            Set(recovery, "_isStalled", true);
            Set(panes[1], "_autoResume", recovery);
            Call(panes[1], "ApplyActivityChange");
            RequireColor(panes[1], "#00853F", "Busy with stalled recovery diagnostic");
            await Task.Run(() => Event(monitors[1], "question.asked", new { id = "stalled-question", sessionID = "session-1" }));
            await DrainAsync();
            RequireColor(panes[1], "#FF8A00", "Stalled diagnostic cannot hide input request");
            await Task.Run(() => Event(monitors[1], "question.replied", new { requestID = "stalled-question", sessionID = "session-1" }));
            await DrainAsync();
            RequireColor(panes[1], "#00853F", "Exact busy resumes after stalled question");
            await Task.Run(() => Event(monitors[0], "permission.asked", new { id = "approval-0", sessionID = "session-0" }));
            await DrainAsync();
            RequireColor(panes[0], "#FF8A00", "Approval");
            await Task.Run(() => Event(monitors[0], "permission.replied", new { requestID = "approval-0", sessionID = "session-0" }));
            await DrainAsync();
            RequireColor(panes[0], "#00853F", "Approval reply resumes busy");
            await Task.Run(() => Event(monitors[2], "question.asked", new { id = "question-2", sessionID = "session-2" }));
            await DrainAsync();
            RequireColor(panes[2], "#FF8A00", "Question");
            await Task.Run(() =>
            {
                Event(monitors[2], "question.replied", new { requestID = "question-2", sessionID = "session-2" });
                Event(monitors[2], "session.status", new { sessionID = "session-2", status = new { type = "idle" } });
            });
            await DrainAsync();
            RequireColor(panes[2], "#252D3A", "Idle");
            panes[0].Interrupt();
            await Task.Run(() => Event(monitors[0], "session.status", new { sessionID = "session-0", status = new { type = "idle" } }));
            await DrainAsync();
            RequireColor(panes[0], "#252D3A", "Manual stop with confirmed native idle");
            Set(panes[1], "_failed", true);
            Call(panes[1], "ApplyActivityChange");
            RequireColor(panes[1], "#252D3A", "Exited");
            await Task.Run(() => Event(monitors[1], "session.status", new { sessionID = "session-1", status = new { type = "busy" } }));
            panes[1].Dispose();
            await DrainAsync();
            RequireColor(panes[1], "#252D3A", "Disposed pane ignores queued busy");
            Console.WriteLine($"PASS {_assertions} assertions: concurrent SSE responsiveness and latest native activity colors, hidden/disposed guards.");
        }
        finally
        {
            foreach (var pane in panes) pane.Dispose();
            Directory.Delete(fixture, true);
        }
    }

    private static async Task DrainAsync()
    {
        await Task.Delay(150);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static void Event(OpenCodeStatusMonitor monitor, string type, object properties) => Sse.Invoke(monitor, [JsonSerializer.Serialize(new { type, properties })]);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Call(object target, string name) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
    private static void RequireColor(TerminalPane pane, string color, string context) => Require(((SolidColorBrush)pane.Background).Color == (Color)ColorConverter.ConvertFromString(color), context + " lost its background color.");
    private static void Require(bool condition, string message) { _assertions++; if (!condition) throw new InvalidOperationException(message); }
}
