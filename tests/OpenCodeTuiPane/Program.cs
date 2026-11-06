using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Wpf;
using TerminalNotes;
using TerminalNotes.Controls;
using TerminalNotes.Services;
using TerminalNotes.TerminalEngine;

internal static class Program
{
    private static int _exitCode;

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { await VerifyAsync(); Console.WriteLine("PASS: OpenCode attach TUI renders natively and preserves an unsubmitted draft through resize."); }
            catch (Exception exception) { _exitCode = 1; Console.Error.WriteLine("FAIL: " + exception); }
            finally { app.Shutdown(_exitCode); }
        };
        app.Run();
        return _exitCode;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] out bool result);

    private static async Task VerifyAsync()
    {
        // As in the app: what the terminals run goes under the tasks' memory budget.
        AppContext.SetSwitch("TerminalNotes.TaskMemoryGuard", true);
        // A busy PC keeps a start waiting up to three minutes; that wait is not what this checks.
        typeof(TerminalPane).Assembly.GetType("TerminalNotes.Services.TerminalStartupAdmission", true)!
            .GetField("AcquireOverride", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, (Func<CancellationToken, Task<IDisposable>>)(_ => Task.FromResult<IDisposable>(new Admitted())));
        string payload = Environment.GetEnvironmentVariable("TERMINALNOTES_TEST_OPENCODE") ??
            throw new InvalidOperationException("Set TERMINALNOTES_TEST_OPENCODE to the installed native OpenCode executable.");
        payload = Path.GetFullPath(payload);
        if (!File.Exists(payload) || !payload.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("The explicit native OpenCode payload is missing or invalid.", payload);

        string root = Path.Combine(Path.GetTempPath(), "TerminalNotes OpenCode TUI " + Guid.NewGuid().ToString("N"));
        Console.WriteLine("Fixture: " + root);
        string project = Path.Combine(root, "project with spaces");
        Directory.CreateDirectory(project);
        string data = Path.Combine(root, "terminal-notes-data");
        Directory.CreateDirectory(data);
        // Installed the way npm does it: opencode.cmd runs node_modules\opencode-ai\bin\opencode.exe. The app starts that
        // program itself, with no PowerShell or cmd.exe around it.
        string shimDirectory = Path.Combine(root, "bin");
        string program = Path.Combine(shimDirectory, "node_modules", "opencode-ai", "bin", "opencode.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(program)!);
        File.Copy(payload, program);
        await File.WriteAllTextAsync(Path.Combine(shimDirectory, "opencode.cmd"),
            "@ECHO off\r\nGOTO start\r\n:find_dp0\r\nSET dp0=%~dp0\r\nEXIT /b\r\n:start\r\nSETLOCAL\r\nCALL :find_dp0\r\n\"%dp0%\\node_modules\\opencode-ai\\bin\\opencode.exe\"   %*\r\n");

        using var environment = new IsolatedEnvironment(root, shimDirectory);
        object? originalDataDirectory = AppContext.GetData("TerminalNotes.DataDirectory");
        AppContext.SetData("TerminalNotes.DataDirectory", data);

        string plan = Path.Combine(project, "plan.md");
        bool emptyPlan = Environment.GetEnvironmentVariable("TERMINALNOTES_TEST_EMPTY_PLAN") == "1";
        if (emptyPlan) await File.WriteAllTextAsync(plan, string.Empty);
        TerminalPane? pane = null;
        uint? serverPid = null;
        uint? tuiPid = null;
        bool passed = false;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        var placeholder = new Border { Background = Brushes.Transparent };
        Grid.SetColumn(placeholder, 1);
        grid.Children.Add(placeholder);
        var window = new Window { Width = 1500, Height = 950, Content = grid, ShowInTaskbar = false };
        try
        {
            pane = new TerminalPane(project, startupCommand: "opencode", protectTuiViewport: true,
                planDocumentPath: plan, projectName: "Native TUI smoke", paneKey: "native-tui-smoke");
            grid.Children.Add(pane);
            window.Show();
            await Until(() => pane.IsReady, "OpenCode server and attach readiness", TimeSpan.FromSeconds(90));

            Require((bool)Property(pane, "IsOpenCodeTui")!, "The OpenCode pane did not select the native TUI renderer.");
            Require((bool)Property(pane, "IsOpenCodeTuiAttached")!, "The attach PTY is not running.");
            serverPid = (uint?)Property(pane, "OpenCodeServerProcessId");
            tuiPid = (uint?)Property(pane, "OpenCodeTuiProcessId");
            string sessionId = (string?)Property(pane, "OpenCodeTuiSessionId") ?? "";
            Require(serverPid is > 0 && tuiPid is > 0, "The server or attached TUI process has no owned process ID.");
            Require(!string.IsNullOrWhiteSpace(sessionId), "The attach process has no verified root session ID.");
            Require(ProcessExists(serverPid) && ProcessExists(tuiPid), "The server or attached TUI process exited before rendering.");
            foreach (uint pid in new[] { serverPid!.Value, tuiPid!.Value })
            {
                using var started = System.Diagnostics.Process.GetProcessById((int)pid);
                Require(string.Equals(started.MainModule?.FileName, program, StringComparison.OrdinalIgnoreCase),
                    $"OpenCode was not started directly (PID {pid} is {started.ProcessName}, not the installed opencode.exe).");
            }
            Console.WriteLine("PASS: the OpenCode server and the attached TUI are opencode.exe itself, with no PowerShell or cmd.exe host.");
            var guard = typeof(TerminalPane).Assembly.GetType("TerminalNotes.Services.TaskMemoryGuard")!;
            var hidden = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            IntPtr budget = (IntPtr)guard.GetProperty("Job", hidden)!.GetValue(null)!;
            if (budget == IntPtr.Zero)
                Console.WriteLine("SKIP memory budget: " + guard.GetProperty("Unavailable", hidden)!.GetValue(null) + " (run the test exe directly, not under dotnet run)");
            else
            {
                foreach (uint pid in new[] { serverPid!.Value, tuiPid!.Value })
                {
                    using var started = System.Diagnostics.Process.GetProcessById((int)pid);
                    Require(IsProcessInJob(started.Handle, budget, out bool inside) && inside, $"PID {pid} is not under the tasks' memory budget.");
                }
                Console.WriteLine($"PASS: the OpenCode server and TUI run under the tasks' memory budget ({(long)guard.GetProperty("JobLimit", hidden)!.GetValue(null)! / (1024 * 1024)} MB now).");
            }

            var surface = Field<NativeTerminalSurface>(pane, "_nativeTerminalSurface");
            var emulator = Field<TerminalEmulator>(pane, "_nativeTerminalEmulator");
            var browser = Field<WebView2>(pane, "_browser");
            Require(surface.Visibility == Visibility.Visible, "The native terminal surface is not visible.");
            Require(pane.Children.OfType<ScrollViewer>().Any(viewport => ReferenceEquals(viewport.Content, surface)),
                "OpenCode TUI is not hosted in its scrollable minimum-size viewport.");
            var minimumViewport = pane.Children.OfType<ScrollViewer>().Single(viewport => ReferenceEquals(viewport.Content, surface));
            Require(browser.Visibility == Visibility.Collapsed && PresentationSource.FromVisual(browser) is null,
                "The native TUI pane initialized or displayed its browser renderer.");

            await Until(() =>
            {
                var snapshot = emulator.GetSnapshot();
                return snapshot.AlternateScreen && snapshot.Columns >= NativeTerminalSurface.MinimumColumns &&
                    snapshot.Rows >= NativeTerminalSurface.MinimumRows && HasReadableText(EntireText(surface));
            }, "alternate-screen OpenCode TUI output", TimeSpan.FromSeconds(45));
            var tuiSnapshot = emulator.GetSnapshot();
            Require(tuiSnapshot.Columns >= NativeTerminalSurface.MinimumColumns && tuiSnapshot.Rows >= NativeTerminalSurface.MinimumRows,
                $"OpenCode TUI grid fell below its safe size: {tuiSnapshot.Columns}x{tuiSnapshot.Rows}.");
            var viewport = pane.Children.OfType<ScrollViewer>().Single(item => ReferenceEquals(item.Content, surface));
            CaptureVisual(viewport, Path.Combine(root, "opencode-tui-initial.png"));
            Console.WriteLine("Native TUI preview: " + Path.Combine(root, "opencode-tui-initial.png"));

            Uri endpoint = (Uri)Property(pane, "NativeOpenCodeEndpoint")!;
            using var client = new OpenCodeConversationClient(endpoint, project);
            int initialMessageCount = MessageCount(await client.ReadMessagesAsync(sessionId));
            Require(initialMessageCount == 0, "Starting OpenCode without a plan must not submit a model request.");
            Console.WriteLine($"PASS: actual OpenCode TUI starts with a {(emptyPlan ? "blank" : "missing")} plan and no submitted messages.");

            // OpenCode's own mark over the empty conversation says the terminal is connected; the first message takes it away.
            await Until(() => (bool)Property(pane, "ShowsOpenCodeMark")!, "OpenCode's mark over the empty session", TimeSpan.FromSeconds(15));
            CaptureVisual(pane, Path.Combine(root, "opencode-tui-mark.png"));
            Console.WriteLine("OpenCode mark preview: " + Path.Combine(root, "opencode-tui-mark.png"));
            Call(pane, "OnOpenCodeTuiRecoverySignal", new OpenCodeRecoverySignal("message.updated", sessionId, "msg_mark_test", null, 1));
            Require(!(bool)Property(pane, "ShowsOpenCodeMark")!, "OpenCode's mark stayed after a message came.");
            Console.WriteLine("PASS: OpenCode's mark shows over the empty session and goes with the first message.");

            using (var selector = new OpenCodeAutomationClient(endpoint, project, useTuiSelection: true))
            {
                string selectedTitle = "TN_TUI_SELECT_" + Guid.NewGuid().ToString("N")[..10];
                string selectedSession = await selector.CreateStartupSessionAsync(selectedTitle);
                await selector.SelectStartupSessionAsync(selectedSession);
                await (Task)Call(pane, "SelectNativeSessionAsync", selectedSession)!;
                sessionId = selectedSession;
                await Until(() => (string?)Property(pane, "OpenCodeTuiSessionId") == sessionId && pane.IsReady,
                    "rebind the pane to the API-selected root session", TimeSpan.FromSeconds(20));
                viewport.ScrollToBottom();
                try
                {
                    await Until(() => emulator.GetSnapshot().Title?.Contains(selectedTitle, StringComparison.Ordinal) == true,
                        "API-selected fixture session title in the TUI terminal-title sequence", TimeSpan.FromSeconds(30));
                }
                catch (TimeoutException exception)
                {
                    string selectedPreview = Path.Combine(root, "opencode-tui-session-selection-timeout.png");
                    CaptureVisual(viewport, selectedPreview);
                    string rendered = EntireText(surface);
                    string bounded = rendered.Length <= 16_000 ? rendered : rendered[^16_000..];
                    throw new TimeoutException(
                        $"Timed out waiting for API-selected title '{selectedTitle}' in the OSC terminal title. Actual title='{emulator.GetSnapshot().Title}'. Grid={emulator.GetSnapshot().Columns}x{emulator.GetSnapshot().Rows}; Alternate={emulator.GetSnapshot().AlternateScreen}; session={sessionId}; screenshot={selectedPreview}; rendered TUI:\n{bounded}",
                        exception);
                }
                Require(MessageCount(await client.ReadMessagesAsync(selectedSession)) == 0,
                    "Selecting the fixture session unexpectedly created a conversation message.");
            }

            grid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            window.Width = 620;
            window.Height = 320;
            window.UpdateLayout();
            await Task.Delay(180);
            void RequireFittedViewport()
            {
                Rect bounds = surface.TransformToAncestor(minimumViewport).TransformBounds(new Rect(surface.RenderSize));
                Require(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= minimumViewport.ActualWidth + 1 && bounds.Bottom <= minimumViewport.ActualHeight + 1,
                    $"The native TUI does not fit its pane: surface={bounds}; viewport={minimumViewport.RenderSize}.");
                Require(minimumViewport.ScrollableWidth < 1 && minimumViewport.ScrollableHeight < 1,
                    "The fitted native TUI still needs outer scrolling.");
            }
            RequireFittedViewport();

            const string draft = "TN_TUI_DRAFT_20261004_NO_SEND";
            surface.FocusSurface();
            var composition = new TextComposition(InputManager.Current, surface, draft);
            surface.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
            {
                RoutedEvent = TextCompositionManager.TextInputEvent
            });
            await Until(() => EntireText(surface).Contains(draft, StringComparison.Ordinal),
                "unsubmitted draft echoed by the TUI", TimeSpan.FromSeconds(20));
            // Korean goes in and comes back intact without PowerShell switching the console to UTF-8.
            const string korean = "한글입력확인";
            surface.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, surface, korean))
            {
                RoutedEvent = TextCompositionManager.TextInputEvent
            });
            try
            {
                await Until(() => EntireText(surface).Contains(draft + korean, StringComparison.Ordinal),
                    "Korean draft echoed by the TUI", TimeSpan.FromSeconds(20));
            }
            catch (TimeoutException exception)
            {
                string rendered = EntireText(surface);
                int at = rendered.IndexOf(draft, StringComparison.Ordinal);
                throw new TimeoutException("Korean did not come back intact: " + (at < 0 ? "(draft missing)" : rendered.Substring(at, Math.Min(80, rendered.Length - at))), exception);
            }
            Console.WriteLine("PASS: Korean typed into the TUI comes back intact.");
            const string fullDraft = draft + korean;
            Require(MessageCount(await client.ReadMessagesAsync(sessionId)) == initialMessageCount,
                "Typing the draft created a server conversation message; the smoke must never submit a prompt.");

            foreach (var size in new[] { (1180d, 800d), (700d, 350d), (1400d, 900d), (760d, 400d), (1000d, 650d), (620d, 320d) })
            {
                window.Width = size.Item1;
                window.Height = size.Item2;
                window.UpdateLayout();
                await Task.Delay(180);
                RequireFittedViewport();
                var snapshot = emulator.GetSnapshot();
                Require(snapshot.Columns >= NativeTerminalSurface.MinimumColumns && snapshot.Rows >= NativeTerminalSurface.MinimumRows,
                    $"Resize changed the TUI to an unsafe {snapshot.Columns}x{snapshot.Rows} grid.");
                Require(EntireText(surface).Contains(fullDraft, StringComparison.Ordinal), "Resize lost the unsubmitted draft.");
                Require(pane.IsReady && pane.IsRunning && (bool)Property(pane, "IsOpenCodeTuiAttached")!,
                    "Resize stopped the OpenCode server or attach process.");
                Require((string?)Property(pane, "OpenCodeTuiSessionId") == sessionId, "Resize changed the attached root session.");
                Require(ProcessExists(serverPid) && ProcessExists(tuiPid), "Resize reaped the server or attached TUI process.");
                Require(MessageCount(await client.ReadMessagesAsync(sessionId)) == initialMessageCount,
                    "The resize smoke unexpectedly submitted a prompt to OpenCode.");
                if (size.Item1 == 1180d)
                {
                    CaptureVisual(viewport, Path.Combine(root, "opencode-tui-large-viewport.png"));
                    Console.WriteLine("Large viewport preview: " + Path.Combine(root, "opencode-tui-large-viewport.png"));
                }
            }

            CaptureVisual(viewport, Path.Combine(root, "opencode-tui-small-viewport.png"));
            Console.WriteLine("Small viewport preview: " + Path.Combine(root, "opencode-tui-small-viewport.png"));
            if (Environment.GetEnvironmentVariable("TERMINALNOTES_TEST_PLAN_WITH_DRAFT") is { Length: > 0 } planCase)
            {
                // Sends one prompt, so only on request. A plan runs with text left in the composer: "1" on the session that text was typed
                // in, "new" with a new session opened for it. The fixture has no model, so a run covers one of them.
                window.Width = 1180;
                window.Height = 800;
                window.UpdateLayout();
                await Until(() => pane.CanExecuteSavedPlan, "the plan gate with text in the composer: " + pane.PlanExecutionBlockReason, TimeSpan.FromSeconds(30));
                await File.WriteAllTextAsync(plan, "# Test\n\n- Answer with one word.\n");
                async Task RunPlan(string where)
                {
                    bool ran = await pane.ExecuteSavedPlanAsync(plan);
                    Require(ran, $"The plan did not run with text in the composer ({where}): {typeof(TerminalPane).GetProperty("LastPlanExecutionFailure", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pane)}");
                    await Until(() => EntireText(surface).Contains("Read and follow the saved", StringComparison.Ordinal), "the plan on the TUI", TimeSpan.FromSeconds(30));
                    await Task.Delay(1500);
                    bool kept = EntireText(surface).Contains(fullDraft, StringComparison.Ordinal);
                    Console.WriteLine($"{(kept ? "PASS" : "LOST")}: the plan ran with text in the composer {where}; the text {(kept ? "stayed" : "is gone")}.");
                    Require(kept, "The plan took the text out of the composer " + where + ".");
                }
                bool newSession = planCase == "new";
                if (newSession) Call(pane, "UseDedicatedAutomationSession");
                await RunPlan(newSession ? "with a new session opened for it" : "on the session it was typed in");
                Require(((string?)Property(pane, "OpenCodeTuiSessionId") != sessionId) == newSession, "The plan ran on the wrong session.");
            }
            passed = true;
            Console.WriteLine($"PASS: renderer=native; session={sessionId}; serverPid={serverPid}; attachPid={tuiPid}; grid={emulator.GetSnapshot().Columns}x{emulator.GetSnapshot().Rows}.");
        }
        finally
        {
            pane?.Dispose();
            if (pane is not null) grid.Children.Remove(pane);
            window.Content = null;
            window.Close();
            try
            {
                if (pane is not null)
                {
                    await Until(() => !ProcessExists(tuiPid) && !ProcessExists(serverPid),
                        "owned OpenCode server and TUI process cleanup", TimeSpan.FromSeconds(20));
                }
            }
            finally { AppContext.SetData("TerminalNotes.DataDirectory", originalDataDirectory); }
            if (passed && !string.Equals(Environment.GetEnvironmentVariable("TERMINALNOTES_TEST_KEEP_FIXTURE"), "1", StringComparison.Ordinal))
            {
                string fixtureRoot = Path.GetFullPath(root);
                string allowedRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!fixtureRoot.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Refusing to clean a fixture outside the temporary directory.");
                for (int attempt = 0; Directory.Exists(fixtureRoot); attempt++)
                {
                    try { Directory.Delete(fixtureRoot, recursive: true); }
                    catch (IOException) when (attempt < 20) { await Task.Delay(250); }
                    catch (UnauthorizedAccessException) when (attempt < 20) { await Task.Delay(250); }
                }
            }
            else Console.Error.WriteLine("Preserved isolated OpenCode TUI fixture for diagnosis: " + root);
        }
    }

    private sealed class Admitted : IDisposable { public void Dispose() { } }

    private static bool HasReadableText(string value) => value.Count(char.IsLetterOrDigit) >= 4;

    private static void CaptureVisual(FrameworkElement visual, string path)
    {
        if (visual.ActualWidth <= 0 || visual.ActualHeight <= 0) throw new InvalidOperationException("Cannot capture an unmeasured native terminal viewport.");
        var dpi = VisualTreeHelper.GetDpi(visual);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(visual.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(visual.ActualHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string EntireText(NativeTerminalSurface surface) => (string)Call(surface, "GetEntireBufferText")!;

    private static int MessageCount(System.Text.Json.JsonElement messages) =>
        messages.ValueKind == System.Text.Json.JsonValueKind.Array ? messages.GetArrayLength() : -1;

    private static async Task Until(Func<bool> condition, string description, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (condition()) return;
            await Task.Delay(75);
        }
        throw new TimeoutException("Timed out waiting for " + description + ".");
    }

    private static bool ProcessExists(uint? id)
    {
        if (id is null) return false;
        try { using var process = Process.GetProcessById(checked((int)id.Value)); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static T Field<T>(object instance, string name) =>
        (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new MissingFieldException(instance.GetType().Name, name));

    private static object? Call(object instance, string name, params object?[] args) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(instance, args);

    private static object? Property(object instance, string name) =>
        instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new MissingMemberException(instance.GetType().Name, name);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class IsolatedEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _original = new(StringComparer.Ordinal);

        public IsolatedEnvironment(string root, string shimDirectory)
        {
            Set("PATH", shimDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
            Set("HOME", root);
            Set("USERPROFILE", root);
            foreach (string kind in new[] { "CONFIG", "DATA", "CACHE", "STATE" })
            {
                string path = Path.Combine(root, "xdg-" + kind.ToLowerInvariant());
                Directory.CreateDirectory(path);
                Set("XDG_" + kind + "_HOME", path);
            }
            foreach (string name in new[] { "APPDATA", "LOCALAPPDATA" })
            {
                string path = Path.Combine(root, name.ToLowerInvariant());
                Directory.CreateDirectory(path);
                Set(name, path);
            }
            Set("OPENCODE_CONFIG_DIR", Path.Combine(root, "opencode-config"));
            Set("OPENCODE_DATA_DIR", Path.Combine(root, "opencode-data"));
            Set("OPENCODE_CONFIG_CONTENT", CreateIsolatedConfig());
            foreach (string name in new[] { "OPENCODE_CONFIG", "OPENCODE_TUI_CONFIG", "OPENCODE_WORKSPACE_ID", "OPENCODE_DB", "OPENCODE_PERMISSION" }) Set(name, null);
            foreach (string name in new[] { "OPENCODE_DISABLE_AUTOUPDATE", "OPENCODE_DISABLE_MODELS_FETCH", "OPENCODE_DISABLE_LSP_DOWNLOAD", "OPENCODE_DISABLE_DEFAULT_PLUGINS", "OPENCODE_DISABLE_PROJECT_CONFIG", "OPENCODE_PURE" }) Set(name, "1");
            Set("OPENCODE_OFFLINE", "true");
            Set("TERMINALNOTES_OPENCODE_RENDERER", null);
            Set("TERMINALNOTES_TEST_OPENCODE", null);
        }

        private void Set(string name, string? value)
        {
            _original.TryAdd(name, Environment.GetEnvironmentVariable(name));
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in _original) Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static string CreateIsolatedConfig() => System.Text.Json.JsonSerializer.Serialize(new
    {
        model = "fixture/test-model",
        small_model = "fixture/test-model",
        enabled_providers = new[] { "fixture" },
        provider = new Dictionary<string, object>
        {
            ["fixture"] = new
            {
                name = "Isolated TUI fixture",
                npm = "@ai-sdk/openai-compatible",
                env = Array.Empty<string>(),
                options = new { apiKey = "fixture-only", baseURL = "http://127.0.0.1:9/v1" },
                models = new Dictionary<string, object>
                {
                    ["test-model"] = new
                    {
                        id = "test-model", name = "TUI fixture", attachment = false,
                        reasoning = false, temperature = false, tool_call = true,
                        release_date = "2025-01-01", limit = new { context = 32_000, output = 4_000 },
                        cost = new { input = 0, output = 0 }, options = new { }
                    }
                }
            }
        }
    });
}
