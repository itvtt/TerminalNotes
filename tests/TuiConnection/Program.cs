using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Threading.Channels;
using TerminalNotes;
using TerminalNotes.Services;
using TerminalNotes.TerminalEngine;

internal static partial class Program
{
    private static int _failure, _assertions;
    [STAThread] private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { await VerifyAsync(); }
            catch (Exception exception) { _failure = 1; Console.Error.WriteLine("FAIL " + exception); }
            finally { app.Shutdown(_failure); }
        };
        app.Run(); return _failure;
    }
    private static async Task VerifyAsync()
    {
        VerifyTuiOutputProof();
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes TUI connection " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        TerminalPane? pane = null;
        MainWindow? window = null;
        IDisposable? owned = null;
        try
        {
            // Do not show/load the window: no startup, native API, or user settings are involved.
            window = new MainWindow(Path.Combine(fixture, "fixture-settings.json"), autoStartCodex: false);
            Type launchType = typeof(MainWindow).GetNestedType("ResolvedTerminalLaunch", BindingFlags.NonPublic)!;
            object launch = Activator.CreateInstance(launchType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [false, "opencode", "Fixture TUI", fixture, TerminalTool.OpenCode], null)!;
            TabItem tab = (TabItem)Invoke(window, "CreateTerminalTabCore", 1, launch, "Fixture TUI", false,
                null!, true, true, "fixture-tui", null)!;
            object tabState = tab.Tag;
            pane = (TerminalPane)tabState.GetType().GetProperty("Pane")!.GetValue(tabState)!;
            tabState.GetType().GetProperty("ExitCode")!.SetValue(tabState, 7);
            Field<DispatcherTimer>(pane, "_nativeRefreshTimer").Stop(); Set(pane, "_nativeRefreshTimer", null);
            string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var info = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 90" }) info.ArgumentList.Add(arg);
            Type ownerType = typeof(OpenCodeHeadlessHost).Assembly.GetType("TerminalNotes.Services.OwnedWindowsProcess")!;
            object owner = ownerType.GetMethod("Start", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [info, null, null])!;
            owned = (IDisposable)owner;
            var backend = Field<OpenCodeHeadlessHost>(pane, "_headlessOpenCodeHost");
            Set(backend, "_process", ownerType.GetProperty("Process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner));
            Set(backend, "_ownedProcess", owner);
            Set(pane, "_headlessSessionId", "ses_exact_existing");
            Set(pane, "_nativeComposerDraft", "unsent draft stays local");
            Set(pane, "_failed", true);
            // RED: the existing failure latch makes every fresh TUI attach error unretryable.
            Require((bool)Property(pane, "CanRetryError")!, "A failed TUI with a retained live server/session offers no reconnect without a confirmed prompt.");

            int selections = 0, launches = 0;
            bool blockSelection = false, failLaunch = false;
            var selectionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var transport = new FakeSelectionTransport(fixture);
            using var http = new HttpClient(transport);
            Func<string, CancellationToken, Task> select = async (id, token) =>
            {
                Require(id == "ses_exact_existing", "Reconnect changed the exact retained session.");
                selections++;
                if (blockSelection) { selectionEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
                using var selector = new OpenCodeAutomationClient(new Uri("http://127.0.0.1:43129/"), fixture, http);
                await selector.SelectStartupSessionAsync(id, token);
            };
            Func<string, CancellationToken, Task<string>> command = (id, token) =>
            {
                launches++;
                return Task.FromResult(failLaunch ? "Write-Output 'fake early attach error'; exit 7" : "Write-Output 'fake VT frame'; Start-Sleep -Seconds 90");
            };
            Type hooksType = typeof(TerminalPane).Assembly.GetType("TerminalNotes.OpenCodeTuiConnectionHooks")!;
            object hooks = Activator.CreateInstance(hooksType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                [select, command, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(30)], null)!;
            Set(pane, "_tuiConnectionHooks", hooks);
            Set(pane, "_tuiConnectionFailed", true);
            Require(await RetryAsync(pane), "Manual reconnect did not recover the retained session.");
            Require(pane.IsReady && backend.IsRunning && selections == 1 && launches == 1, "Reconnect replaced the backend or duplicated the attach.");
            Require(tabState.GetType().GetProperty("ExitCode")!.GetValue(tabState) is null,
                "Successful retained-session Ready left a latched exited tab that would replace the backend on its next use.");
            Require(Field<string>(pane, "_nativeComposerDraft") == "unsent draft stays local", "Reconnect changed an unsent draft.");
            ConPtySession previous = Field<ConPtySession>(pane, "_session");
            Set(pane, "_tuiReconnectAttempts", 2);
            await (Task)Invoke(previous, "StopAndWaitAsync")!;
            Invoke(pane, "HandleTerminalSessionExit", previous, 0);
            await Task.Delay(150); // Intentional normal exit offers manual reconnect, without auto restart.
            Require((bool)Property(pane, "CanRetryError")!, "Unexpected attach exit lost its manual reconnect action.");
            Require(await RetryAsync(pane), "Second reconnect could not replace the single-use ConPTY session.");
            Invoke(pane, "HandleTerminalSessionExit", previous, 7);
            Require(pane.IsReady, "A stale old-session exit failed its new replacement.");

            await VerifyQueuedCallbacksAndProtocolRepliesAsync(pane, previous, backend);
            await VerifyPlanMakerTuiSelectionAsync(pane, backend, fixture);

            ConPtySession current = Field<ConPtySession>(pane, "_session");
            failLaunch = true;
            Set(pane, "_tuiReconnectAttempts", 0);
            await (Task)Invoke(current, "StopAndWaitAsync")!; await Task.Delay(150);
            int beforeAutomatic = launches;
            Invoke(pane, "HandleTerminalSessionExit", current, 7);
            await UntilAsync(() => Field<int>(pane, "_tuiReconnectAttempts") == 2 && !Field<bool>(pane, "_tuiConnecting") && !Field<bool>(pane, "_tuiReconnectScheduled"));
            Require(launches == beforeAutomatic + 2 && !pane.IsReady && (bool)Property(pane, "CanRetryError")!, "Early nonzero attach recovery did not stop after two automatic retries with manual reconnect available.");
            bool early = await RetryAsync(pane);
            Require(!early && !pane.IsReady && (bool)Property(pane, "CanRetryError")!, "Early attach failure was reported as a ready connection or lost manual retry.");
            await VerifyLoopReconnectAsync(pane, fixture, () => launches, value => failLaunch = value);
            // Manual reconnection failure stays manual when there is no authorized unfinished work.
            failLaunch = false; blockSelection = true;
            Task<bool> blocked = RetryAsync(pane);
            await selectionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            int beforeStop = launches;
            await ((Task)Invoke(pane, "StopForTerminalLifecycleAsync")!);
            Require(!await blocked && launches == beforeStop && !(bool)Property(pane, "CanRetryError")!, "Stop permitted an in-flight reconnect to launch a replacement.");
            Require(!backend.IsRunning, "Stop left the fixture backend running.");
            Require(transport.Selections >= 1 && transport.OtherPosts == 0, "TUI reconnect created a session or replayed a prompt.");
            Console.WriteLine($"PASS {_assertions} assertions: retained exact session/server/draft, fresh-pane retry, early exit, stale callback, cancel on Stop.");
        }
        finally { pane?.Dispose(); owned?.Dispose(); if (window is not null) Invoke(window, "DisposeResources"); Directory.Delete(fixture, true); }
    }
    private static async Task VerifyQueuedCallbacksAndProtocolRepliesAsync(TerminalPane pane, ConPtySession old, OpenCodeHeadlessHost backend)
    {
        ConPtySession current = Field<ConPtySession>(pane, "_session");
        // Queue the source's fault before its identity changes, then drain the Dispatcher after replacement.
        Task.Run(() => Invoke(pane, "OnInputFailed", current, new IOException("fixture old writer fault"))).GetAwaiter().GetResult();
        using (var replacementIdentity = new ConPtySession())
        {
            Set(pane, "_session", replacementIdentity);
            await pane.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(!Field<bool>(pane, "_failed") && Field<int>(pane, "_sessionCloseStarted") == 0 && backend.IsRunning,
                "An already-queued old writer fault closed its replacement or retained backend.");
            Set(pane, "_session", current);
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Type proofType = typeof(TerminalPane).GetNestedType("TuiOutputProof", BindingFlags.NonPublic)!;
        Set(pane, "_tuiOutputProof", Activator.CreateInstance(proofType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [current, completion], null));
        Invoke(pane, "ObserveTuiOutput", old, "stale fixture frame");
        Require(!completion.Task.IsCompleted, "In-flight old output proved first output for a new attach.");
        Invoke(pane, "ObserveTuiOutput", current, "current fixture frame");
        Require(completion.Task.IsCompletedSuccessfully, "Current attach output failed its own first-output proof.");
        Set(pane, "_tuiOutputProof", null);

        // Replace only the fixture session's input buffer, so its real child need not interpret replies.
        object originalBuffer = Field<object>(current, "_inputQueue");
        Type bufferType = typeof(TerminalPane).Assembly.GetType("TerminalNotes.Services.TerminalInputBuffer")!;
        object buffer = Activator.CreateInstance(bufferType, nonPublic: true)!;
        var reader = (ChannelReader<byte[]>)bufferType.GetProperty("Reader")!.GetValue(buffer)!;
        Set(current, "_inputQueue", buffer);
        Set(pane, "_ready", false); Set(pane, "_tuiConnecting", true);
        try
        {
            Field<TerminalEmulator>(pane, "_nativeTerminalEmulator").Feed("\x1b[6n");
            Require(reader.TryRead(out byte[]? reply) && TerminalInputCapture.IsTerminalResponse(Encoding.UTF8.GetString(reply!)),
                "A live attach's startup cursor query could not receive its protocol reply before readiness.");
            // User keys typed before readiness are held (never written to the TUI) and count as pending
            // user input; programmatic prompts are still refused.
            Require(Invoke(pane, "HandleNativeSurfaceInput", "ordinary user text", false, null!) is null &&
                Invoke(pane, "HandleNativeSurfaceInput", "programmatic prompt", true, null!) is Exception && !reader.TryRead(out _) &&
                Field<System.Text.StringBuilder>(pane, "_heldStartupInput").ToString() == "ordinary user text" &&
                Invoke(pane, "HasLocalPendingInput") is true,
                "Startup handshake bypass admitted user or programmatic prompt input.");
            Field<System.Text.StringBuilder>(pane, "_heldStartupInput").Clear();
            Invoke(pane, "HandleNativeTerminalResponse", "unrecognized prompt");
            Require(!reader.TryRead(out _), "Startup protocol bypass admitted unrecognized text.");
            Set(pane, "_tuiConnecting", false);
            Field<TerminalEmulator>(pane, "_nativeTerminalEmulator").Feed("\x1b[6n");
            Require(!reader.TryRead(out _), "An unready non-connecting view wrote a protocol response.");
        }
        finally { Set(current, "_inputQueue", originalBuffer); Set(pane, "_ready", true); Set(pane, "_tuiConnecting", false); bufferType.GetMethod("Complete")!.Invoke(buffer, null); }
    }
    private static Task<bool> RetryAsync(TerminalPane pane) => (Task<bool>)Invoke(pane, "RetryErrorAsync")!;
    private static object? Invoke(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static object? Property(object target, string name) => target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Require(bool condition, string message) { _assertions++; if (!condition) throw new InvalidOperationException(message); }
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(25, timeout.Token);
    }
    private sealed class FakeSelectionTransport(string project) : HttpMessageHandler
    {
        internal int Selections, OtherPosts;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string path = request.RequestUri!.AbsolutePath;
            string content;
            if (request.Method == HttpMethod.Get && path == "/session/ses_exact_existing")
                content = JsonSerializer.Serialize(new { id = "ses_exact_existing", directory = project });
            else if (request.Method == HttpMethod.Post && path == "/tui/select-session")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Require(body.RootElement.GetProperty("sessionID").GetString() == "ses_exact_existing", "Native select changed its exact session identity.");
                Selections++; content = "true";
            }
            else { if (request.Method == HttpMethod.Post) OtherPosts++; throw new InvalidOperationException("Unexpected fake API route."); }
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(content, Encoding.UTF8, "application/json") };
        }
    }
}
