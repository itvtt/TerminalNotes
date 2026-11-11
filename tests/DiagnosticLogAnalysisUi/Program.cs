using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TerminalNotes;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int result = 1;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            TerminalProfilesDialog? settings = null;
            DiagnosticLogAnalysisDialog? analysis = null;
            string folder = Path.Combine(Path.GetTempPath(), "TerminalNotes.DiagnosticLogAnalysisUi", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            AppContext.SetData("TerminalNotes.DataDirectory", folder);
            try
            {
                string settingsPath = Path.Combine(folder, "isolated-settings.json");
                string logPath = settingsPath + ".ui-diagnostics.jsonl";
                WriteLog(logPath, 47);
                WriteResourceLog(settingsPath + ".resource-diagnostics.jsonl");
                File.WriteAllText(settingsPath + ".resource-diagnostics.jsonl.1",
                    File.ReadAllText(settingsPath + ".resource-diagnostics.jsonl")
                        .Replace("sample-runtime.exe", "rotated-runtime.exe", StringComparison.Ordinal)
                        .Replace("Fixture runtime", "Rotated runtime", StringComparison.Ordinal)
                        .Replace("\"systemMemoryLoadPercent\":98", "\"systemMemoryLoadPercent\":99", StringComparison.Ordinal)
                        .Replace("01:02:00Z", "01:01:30Z", StringComparison.Ordinal), new UTF8Encoding(false));
                settings = new TerminalProfilesDialog(folder, false, diagnosticSettingsPath: settingsPath)
                {
                    Width = 480, Height = 440
                };
                settings.Show();
                settings.UpdateLayout();
                var open = (Button)settings.FindName("AnalyzeLogsButton");
                // Problems are sent automatically now: the settings entry is hidden; the dialog itself still works (opened directly).
                Check(!open.IsVisible && ((FrameworkElement)settings.FindName("ProblemReportRow")).Visibility == Visibility.Collapsed,
                    "settings hide the manual problem report entry");
                SaveImage(settings, Path.Combine(folder, "settings.png"));

                _ = settings.Dispatcher.BeginInvoke(new Action(() => open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
                await UntilAsync(() =>
                {
                    analysis = app.Windows.OfType<DiagnosticLogAnalysisDialog>().FirstOrDefault();
                    return analysis is not null;
                });
                var send = (Button)analysis!.FindName("SendButton");
                var description = (TextBox)analysis.FindName("ProblemDescription");
                await UntilAsync(() => send.IsEnabled);
                Check(analysis.FindName("SummaryTextBox") is null && analysis.FindName("RefreshButton") is null &&
                    analysis.FindName("CopyButton") is null && analysis.FindName("OpenLogFolderButton") is null &&
                    ((Button)analysis.FindName("ExportLogsButton")).Visibility == Visibility.Collapsed,
                    "the send dialog keeps only description, send and close; ZIP save is a failure fallback");
                description.Text = "터미널이 멈췄어요";
                string report = (string)typeof(DiagnosticLogAnalysisDialog).GetMethod("BuildReport",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!.Invoke(analysis, null)!;
                Check(report.StartsWith("[Terminal Notes 문제 보고서]", StringComparison.Ordinal) && report.Contains("앱 버전: v", StringComparison.Ordinal) &&
                    report.Contains("터미널이 멈췄어요", StringComparison.Ordinal), "report carries the header and the user's description");
                Check(report.Contains("47", StringComparison.Ordinal) && report.Contains("sample-runtime.exe", StringComparison.Ordinal) &&
                    report.Contains("rotated-runtime.exe", StringComparison.Ordinal), "report includes the background analysis of default logs");
                Check(!report.Contains("private-sentinel", StringComparison.Ordinal), "raw private log content stays out of the report");
                Check(ReferenceEquals(analysis.Owner, settings), "send dialog opens from settings without changing settings");
                SaveImage(analysis, Path.Combine(folder, "send.png"));
                analysis.Width = analysis.MinWidth;
                analysis.Height = analysis.MinHeight;
                analysis.UpdateLayout();
                foreach (string name in new[] { "SendButton" })
                {
                    var action = (Button)analysis.FindName(name);
                    var bottomRight = action.TransformToAncestor(analysis).Transform(new Point(action.ActualWidth, action.ActualHeight));
                    Check(action.IsVisible && action.IsEnabled && bottomRight.X < analysis.ActualWidth && bottomRight.Y < analysis.ActualHeight,
                        name + " is accessible at minimum window size");
                }
                Check(description.ActualHeight >= 60, "the description box stays usable at minimum size");
                SaveImage(analysis, Path.Combine(folder, "send-minimum.png"));
                // Send to a local fake relay and check what arrives.
                int port = FreePort();
                using var listener = new System.Net.HttpListener();
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Start();
                AppContext.SetData("TerminalNotes.ReportRelayUrl", $"http://localhost:{port}");
                AppContext.SetData("TerminalNotes.ReportRelayKey", "test-key");
                var received = listener.GetContextAsync();
                send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var context = await received;
                string body;
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync();
                Check(context.Request.Url!.AbsolutePath == "/api/reports" && context.Request.Headers["X-TerminalNotes-Key"] == "test-key" &&
                    body.Contains("name=report", StringComparison.Ordinal) && body.Contains("터미널이 멈췄어요", StringComparison.Ordinal) &&
                    body.Contains("filename=logs.zip", StringComparison.Ordinal) && !body.Contains("private-sentinel", StringComparison.Ordinal),
                    "send posts the report and log ZIP with the relay key");
                byte[] reply = Encoding.UTF8.GetBytes("{\"id\":\"20261019-120000-abcdef12\"}");
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(reply);
                context.Response.Close();
                var status = (TextBlock)analysis.FindName("LoadStatus");
                await UntilAsync(() => status.Text.Contains("20261019-120000-abcdef12", StringComparison.Ordinal));
                Check(Equals(send.Content, "보냄") && !send.IsEnabled, "a sent report shows its receipt number and cannot be sent twice by accident");
                AppContext.SetData("TerminalNotes.ReportRelayUrl", null);
                Check(!File.Exists(settingsPath), "sending a report never saves or rewrites app settings");
                Console.WriteLine("PASS settings entry, report header/description/background analysis, privacy, minimum layout, relay send");

                // Automatic reports: new errors, an abnormal shutdown and a frozen screen count; information, old events and the
                // reporter's own events do not. The report carries the lines the API server analyzes.
                string daily = Path.Combine(folder, "auto-daily");
                Directory.CreateDirectory(daily);
                var since = DateTimeOffset.UtcNow.AddMinutes(-30);
                string Line(string name, string severity, DateTimeOffset at) =>
                    $"{{\"schemaVersion\":1,\"timestamp\":\"{at:O}\",\"event\":\"{name}\",\"severity\":\"{severity}\"}}";
                File.WriteAllLines(Path.Combine(daily, "today.jsonl"), new[]
                {
                    Line("terminal_lifecycle_fault", "error", since.AddMinutes(5)),
                    Line("terminal_lifecycle_fault", "error", since.AddMinutes(6)),
                    Line("session.previous_abnormal_shutdown", "warning", since.AddMinutes(1)),
                    Line("dispatcher_unresponsive", "information", since.AddMinutes(7)),
                    Line("crash_handler", "fatal", since.AddMinutes(8)),
                    Line("session.heartbeat", "information", since.AddMinutes(9)),
                    Line("report_send_failed", "error", since.AddMinutes(10)),
                    Line("auto_report_failed", "error", since.AddMinutes(11)),
                    Line("old_failure", "error", since.AddMinutes(-60)),
                    "{not json"
                });
                var found = AutomaticProblemReporter.Find(daily, since);
                Check(found is not null && found.Kinds["오류"] == 2 && found.Kinds["비정상 종료"] == 1 && found.Kinds["화면 멈춤"] == 1 &&
                    found.Kinds["치명적 오류"] == 1 && !found.Events.ContainsKey("old_failure") && !found.Events.ContainsKey("report_send_failed") &&
                    !found.Events.ContainsKey("auto_report_failed") && found.Latest == since.AddMinutes(8),
                    "automatic reports count new errors, crashes, abnormal shutdowns and freezes, not old or own events");
                Check(AutomaticProblemReporter.Find(daily, since.AddMinutes(8)) is null, "nothing newer than the last report sends nothing");
                string auto = AutomaticProblemReporter.BuildReport(found!, "(분석)", "고급", DateTimeOffset.Now);
                Check(auto.StartsWith(AutomaticProblemReporter.Title, StringComparison.Ordinal) && auto.Contains("\n문제: 오류 2 · ", StringComparison.Ordinal) &&
                    auto.Contains("문제 이벤트: terminal_lifecycle_fault ×2", StringComparison.Ordinal) &&
                    auto.Contains("[어떤 문제가 있었나요]" + Environment.NewLine + "자동 보고 · 오류 2", StringComparison.Ordinal) && auto.Contains("앱 버전: v", StringComparison.Ordinal),
                    "an automatic report has the title, summary, 문제 and 문제 이벤트 lines the API server reads");
                Console.WriteLine("PASS automatic problem reports: new problems only, own events ignored, server-readable report lines");
                Console.WriteLine("UI screenshots: " + folder);
                result = 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
            }
            finally
            {
                analysis?.Close();
                settings?.Close();
                AppContext.SetData("TerminalNotes.DataDirectory", null);
                app.Shutdown();
            }
        }));
        app.Run();
        return result;
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("UI analysis did not complete.");
            await Task.Delay(25);
        }
    }

    private static void WriteLog(string path, int duration)
    {
        File.WriteAllText(path,
            "{\"timestampUtc\":\"2026-10-13T01:00:00Z\",\"eventName\":\"dispatcher_unresponsive\",\"durationSeconds\":15,\"processId\":1234,\"terminalSlot\":1,\"terminalStage\":\"native_server_started\",\"detail\":\"private-sentinel\"}\n" +
            "{\"timestampUtc\":\"2026-10-13T01:01:00Z\",\"eventName\":\"dispatcher_recovered\",\"durationSeconds\":" + duration + ",\"processId\":1234,\"terminalSlot\":1,\"terminalStage\":\"native_server_started\"}\n",
            new UTF8Encoding(false));
    }

    private static void WriteResourceLog(string path)
    {
        File.WriteAllText(path, """
            {"timestampUtc":"2026-10-13T01:02:00Z","event":"resource_snapshot","appProcessId":4321,"appStartedAtUtc":"2026-10-13T00:59:00Z","systemMemoryLoadPercent":98,"totalPhysicalBytes":8589934592,"availablePhysicalBytes":171798692,"processCount":2,"unreadableProcessCount":0,"isPartial":false,"treePrivateBytes":188743680,"treeWorkingSetBytes":251658240,"processes":[{"processId":4321,"parentProcessId":1,"name":"TerminalNotes.exe","description":"TerminalNotes","productName":"TerminalNotes","workingSetBytes":83886080,"privateBytes":62914560,"startedAtUtc":"2026-10-13T00:59:00Z","isApp":true},{"processId":4322,"parentProcessId":4321,"name":"sample-runtime.exe","description":"Fixture runtime","productName":"Fixture","workingSetBytes":167772160,"privateBytes":125829120,"startedAtUtc":"2026-10-13T01:00:00Z","isApp":false}]}
            """ + Environment.NewLine, new UTF8Encoding(false));
    }

    private static void SaveImage(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }
}
