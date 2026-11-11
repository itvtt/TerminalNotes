using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using TerminalNotes.Services;

[assembly: InternalsVisibleTo("TerminalNotes.DiagnosticLogAnalysisUiTests")]

namespace TerminalNotes;

// Problems reach the administrator by themselves (2026-11-11 사용자: "문제보내기 버튼도 설정에서 안보이게 해 자동으로 API로 관리자에게
// 보내지게해 그걸 API서비스에서 볼수있고 분석되게"): the 문제 보내기 button is hidden and the app sends what its diagnostic logs show
// to the API server (report-relay.json: RELAY_URL, or RELAY_HOST and RELAY_PORT, from .env).
public partial class MainWindow
{
    private CancellationTokenSource? _problemReportLifetime;

    private void StartAutomaticProblemReports()
    {
        // Test windows (autoStartCodex off) never reach the real server.
        if (!_autoStartCodex || string.IsNullOrWhiteSpace(ReportRelayClient.Endpoint().Url)) return;
        _problemReportLifetime = new CancellationTokenSource();
        CancellationToken token = _problemReportLifetime.Token;
        Closed += (_, _) => _problemReportLifetime?.Cancel();
        string settingsPath = _settingsPath;
        _ = Task.Run(() => AutomaticProblemReporter.RunAsync(settingsPath, token), token);
    }
}

/// <summary>
/// Shortly after start and every 10 minutes, the daily diagnostic logs are checked for problems newer than the last report: errors,
/// fatal crashes, an earlier abnormal shutdown, a frozen screen. New ones go to the API server as one report with the same
/// privacy-safe log archive the manual report used (event names and counts, no typed input or output); at most one report per
/// 30 minutes. A failed send is tried again at the next check. The reporter's own events never count as problems.
/// </summary>
internal static class AutomaticProblemReporter
{
    internal const string Title = "[Terminal Notes 자동 문제 보고]";
    internal static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(90), Interval = TimeSpan.FromMinutes(10), MinimumGap = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FirstRunLookback = TimeSpan.FromDays(1);

    internal sealed record Found(DateTimeOffset Latest, IReadOnlyDictionary<string, int> Kinds, IReadOnlyDictionary<string, int> Events);

    private static string StatePath => Path.Combine(Path.GetDirectoryName(ApplicationDiagnostics.GetLogDirectory())!, "auto-report.json");

    internal static async Task RunAsync(string settingsPath, CancellationToken token)
    {
        try
        {
            await Task.Delay(FirstCheck, token);
            while (!token.IsCancellationRequested)
            {
                try { await CheckOnceAsync(settingsPath, token); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Recorded as information: the reporter's own trouble never becomes a reported problem.
                    ApplicationDiagnostics.RecordOperational("auto_report_failed", "information", "report", exception.GetType().Name);
                }
                await Task.Delay(Interval, token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task CheckOnceAsync(string settingsPath, CancellationToken token)
    {
        var (lastProblem, lastSent) = ReadState();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (lastSent is { } sent && now - sent < MinimumGap) return;
        ApplicationDiagnostics.Flush();
        if (Find(ApplicationDiagnostics.GetLogDirectory(), lastProblem ?? now - FirstRunLookback) is not { } found) return;
        string analysis;
        try { analysis = (await DiagnosticLogAnalyzer.AnalyzeDefaultAsync(settingsPath, token)).Summary; }
        catch (Exception exception) when (exception is not OperationCanceledException) { analysis = "(분석하지 못함)"; }
        string report = BuildReport(found, analysis, ReadWorkMode(settingsPath), DateTimeOffset.Now);
        string zipPath = Path.Combine(Path.GetTempPath(), "TerminalNotes-auto-report-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await ApplicationDiagnostics.ExportArchiveAsync(zipPath, recentDays: 2);
            using (var archive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Update))
            {
                archive.GetEntry("report.txt")?.Delete();
                using var writer = new StreamWriter(archive.CreateEntry("report.txt").Open(), new UTF8Encoding(true));
                writer.Write(report);
            }
            string id = await ReportRelayClient.SendAsync(report, zipPath);
            WriteState(found.Latest, DateTimeOffset.UtcNow);
            ApplicationDiagnostics.RecordOperational("auto_report_sent", "information", "report", id.Length > 0 ? "received" : "sent");
        }
        finally
        {
            try { File.Delete(zipPath); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Problems newer than <paramref name="since"/> in the daily logs, or null when there are none.</summary>
    internal static Found? Find(string directory, DateTimeOffset since)
    {
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        var events = new Dictionary<string, int>(StringComparer.Ordinal);
        DateTimeOffset latest = since;
        if (!Directory.Exists(directory)) return null;
        foreach (string path in Directory.EnumerateFiles(directory, "*.jsonl").Where(path => File.GetLastWriteTimeUtc(path) >= since.UtcDateTime))
        {
            string[] lines;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                lines = reader.ReadToEnd().Split('\n');
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            foreach (string line in lines.TakeLast(20000))
            {
                if (line.Length < 2 || !line.Contains("\"event\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var json = JsonDocument.Parse(line);
                    JsonElement root = json.RootElement;
                    string name = Text(root, "event"), severity = Text(root, "severity");
                    if (!DateTimeOffset.TryParse(Text(root, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) || time <= since) continue;
                    if (name.StartsWith("auto_report", StringComparison.Ordinal) || name.StartsWith("report_", StringComparison.Ordinal)) continue;
                    string? kind = name switch
                    {
                        "session.previous_abnormal_shutdown" => "비정상 종료",
                        "dispatcher_unresponsive" => "화면 멈춤",
                        _ => severity switch { "fatal" => "치명적 오류", "error" => "오류", _ => null }
                    };
                    if (kind is null) continue;
                    kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                    events[name] = events.GetValueOrDefault(name) + 1;
                    if (time > latest) latest = time;
                }
                catch (JsonException) { }
            }
        }
        return kinds.Count == 0 ? null : new Found(latest, kinds, events);
    }

    internal static string BuildReport(Found found, string analysis, string workMode, DateTimeOffset now)
    {
        string kinds = string.Join(" · ", found.Kinds.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key} {pair.Value}"));
        var report = new StringBuilder();
        report.AppendLine(Title);
        report.AppendLine("앱 버전: v" + ApplicationDiagnostics.DisplayVersion);
        report.AppendLine("Windows: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim() +
            " (" + System.Runtime.InteropServices.RuntimeInformation.OSArchitecture + ")");
        report.AppendLine("작업 모드: " + workMode);
        report.AppendLine($"작성 시각: {now:yyyy-MM-dd HH:mm} (UTC{now:zzz})");
        report.AppendLine();
        report.AppendLine("[어떤 문제가 있었나요]");
        report.AppendLine("자동 보고 · " + kinds);
        report.AppendLine();
        report.AppendLine("문제: " + kinds);
        report.AppendLine("문제 이벤트: " + string.Join(", ", found.Events.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(12).Select(pair => $"{pair.Key} ×{pair.Value}")));
        report.AppendLine($"마지막 문제 시각: {found.Latest.ToLocalTime():yyyy-MM-dd HH:mm}");
        report.AppendLine();
        report.AppendLine("[자동 로그 분석]");
        report.AppendLine(analysis);
        return report.ToString();
    }

    private static string ReadWorkMode(string settingsPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            return document.RootElement.TryGetProperty("WorkMode", out var mode) ? mode.GetString() switch
            {
                "Basic" => "일반", "Plan" => "플랜", "Advanced" => "고급", _ => "확인 불가"
            } : "확인 불가";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return "확인 불가"; }
    }

    private static (DateTimeOffset? LastProblem, DateTimeOffset? LastSent) ReadState()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(StatePath));
            DateTimeOffset? Read(string key) => document.RootElement.TryGetProperty(key, out var value) &&
                DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time : null;
            return (Read("lastProblem"), Read("lastSent"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return (null, null); }
    }

    private static void WriteState(DateTimeOffset lastProblem, DateTimeOffset lastSent)
    {
        string temporary = StatePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { lastProblem = lastProblem.ToString("O"), lastSent = lastSent.ToString("O") }));
        File.Move(temporary, StatePath, overwrite: true);
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
