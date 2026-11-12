using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using TerminalNotes.Services;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TerminalNotes.ShutdownCauseTests")]

namespace TerminalNotes;

/// <summary>Why the PC went down before this boot, from the Windows System log (2026-11-12 사용자: "터미널 노트를 실행하고 다음날
/// 와보면 PC가 꺼져있는 경우가 있어 … 안꺼지게 할수있어?"). Nothing keeps a PC on without knowing what turned it off: an abrupt stop
/// (power or a hang), a blue screen, or a program (Windows Update, a power policy tool) each needs a different answer. The app's own
/// last resource record before the boot tells whether memory was short at the time.</summary>
internal static class ShutdownCause
{
    internal sealed record Entry(int Id, string Provider, DateTime At, IReadOnlyDictionary<string, string> Named, IReadOnlyList<string> Values);
    internal sealed record Snapshot(DateTime At, int MemoryPercent, long AvailableBytes, long CommitTotal, long CommitLimit);

    internal static readonly int[] Ids = [41, 6008, 1001, 1074, 6006, 2004];

    // Shutdowns people start themselves (the Start menu, the desktop): nothing to tell.
    private static readonly string[] UserShells = ["StartMenuExperienceHost.exe", "explorer.exe", "ShellExperienceHost.exe", "ShellHost.exe"];
    private static readonly string[] WindowsUpdate = ["TrustedInstaller.exe", "MoUsoCoreWorker.exe", "usocoreworker.exe", "wuauclt.exe", "musnotification.exe", "UsoClient.exe"];

    /// <summary>What to tell about the shutdown before <paramref name="boot"/> (local time); null for the user's own shutdown or restart.</summary>
    internal static (string Text, bool Unexpected)? Describe(IReadOnlyList<Entry> entries, DateTime boot, Snapshot? last)
    {
        string memory = last is null ? "" :
            $" · 직전 Terminal Notes 기록 {last.At:MM-dd HH:mm}: 메모리 {last.MemoryPercent}%, 남은 메모리 {last.AvailableBytes / 1048576:N0}MB, 커밋 {100.0 * last.CommitTotal / Math.Max(1, last.CommitLimit):N0}%";
        int lowMemory = entries.Count(entry => entry.Id == 2004 && entry.At < boot && entry.At > boot.AddDays(-1));
        string warnings = lowMemory > 0 ? $" · 그 전 하루 동안 Windows 메모리 부족 경고 {lowMemory}회" : "";
        string back = $" · 다시 켜짐 {boot:MM-dd HH:mm}";
        // Logged while this boot came up: the earlier session did not end by itself.
        var atBoot = entries.Where(entry => entry.At >= boot.AddMinutes(-1) && entry.At <= boot.AddMinutes(15)).ToList();
        if (atBoot.FirstOrDefault(entry => entry.Id == 1001 && entry.Provider.Contains("WER-SystemErrorReporting", StringComparison.OrdinalIgnoreCase)) is { } blue)
        {
            string code = blue.Values.FirstOrDefault() is { Length: > 0 } value ? value.Split(' ')[0] : "알 수 없음";
            return ($"블루스크린으로 멈췄습니다(코드 {code}){back}{memory}{warnings}", true);
        }
        if (atBoot.FirstOrDefault(entry => entry.Id is 41 or 6008) is not null)
        {
            var power = atBoot.FirstOrDefault(entry => entry.Id == 41);
            bool button = power is not null && power.Named.TryGetValue("PowerButtonTimestamp", out var pressed) && pressed is { Length: > 0 } && pressed != "0";
            string how = button ? "전원 버튼을 길게 눌러 꺼졌습니다" : "갑자기 꺼졌습니다(전원이 끊겼거나 PC가 멈춤, 블루스크린 아님)";
            return (how + back + memory + warnings, true);
        }
        // A program started the last shutdown: the 1074 just before the event log stopped (6006) for the last time before this boot.
        var stopped = entries.Where(entry => entry.Id == 6006 && entry.At < boot).OrderBy(entry => entry.At).LastOrDefault();
        var initiated = entries.Where(entry => entry.Id == 1074 && entry.At < boot &&
            (stopped is null ? entry.At > boot.AddMinutes(-15) : entry.At <= stopped.At.AddMinutes(1) && entry.At >= stopped.At.AddMinutes(-15)))
            .OrderBy(entry => entry.At).LastOrDefault();
        if (initiated is null || initiated.Values.Count < 5) return null;
        string process = Path.GetFileName(initiated.Values[0].Split(" (", 2)[0].Trim());
        if (UserShells.Contains(process, StringComparer.OrdinalIgnoreCase)) return null;
        string who = WindowsUpdate.Contains(process, StringComparer.OrdinalIgnoreCase) ? $"Windows 업데이트({process})" : process;
        string reason = initiated.Values.Count > 2 && initiated.Values[2] is { Length: > 0 } text ? " · 이유: " + text : "";
        string kind = initiated.Values[4];
        string did = kind.Contains("끄기", StringComparison.Ordinal) || kind.Contains("power off", StringComparison.OrdinalIgnoreCase) ? "껐습니다" :
            kind.Contains("다시 시작", StringComparison.Ordinal) || kind.Contains("restart", StringComparison.OrdinalIgnoreCase) ? "다시 시작했습니다" : kind + "했습니다";
        return ($"{who}가 {initiated.At:MM-dd HH:mm}에 PC를 {did}{reason}{back}{memory}", false);
    }

    internal static DateTime BootTime() => DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    internal static List<Entry> ReadEvents(DateTime since)
    {
        string ids = string.Join(" or ", Ids.Select(id => "EventID=" + id));
        var query = new EventLogQuery("System", PathType.LogName,
            $"*[System[({ids}) and TimeCreated[@SystemTime>='{since.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}']]]");
        XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
        var entries = new List<Entry>();
        using var reader = new EventLogReader(query);
        for (EventRecord? record = reader.ReadEvent(); record is not null && entries.Count < 2000; record = reader.ReadEvent())
            using (record)
            {
                if (record.TimeCreated is not { } at) continue;
                var data = XDocument.Parse(record.ToXml()).Root?.Element(ns + "EventData")?.Elements(ns + "Data").ToList() ?? [];
                var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in data)
                    if (item.Attribute("Name")?.Value is { Length: > 0 } name) named[name] = item.Value;
                entries.Add(new(record.Id, record.ProviderName ?? "", at, named, data.Select(item => item.Value).ToList()));
            }
        return entries;
    }

    /// <summary>The app's last resource record before <paramref name="boot"/> (its log and the rotated one).</summary>
    internal static Snapshot? LastSnapshotBefore(string resourceLog, DateTime boot)
    {
        Snapshot? last = null;
        foreach (string path in new[] { resourceLog + ".1", resourceLog })
        {
            if (!File.Exists(path)) continue;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var lines = new StreamReader(stream);
            for (string? line = lines.ReadLine(); line is not null; line = lines.ReadLine())
            {
                if (!line.Contains("\"resource_snapshot\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    DateTime at = root.GetProperty("timestampUtc").GetDateTimeOffset().LocalDateTime;
                    if (at >= boot || last is not null && at <= last.At) continue;
                    last = new(at, root.GetProperty("systemMemoryLoadPercent").GetInt32(), root.GetProperty("availablePhysicalBytes").GetInt64(),
                        root.TryGetProperty("commitTotalBytes", out var total) ? total.GetInt64() : 0,
                        root.TryGetProperty("commitLimitBytes", out var limit) ? limit.GetInt64() : 0);
                }
                catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
            }
        }
        return last;
    }
}

public partial class MainWindow
{
    private bool _shutdownChecked;
    private DateTimeOffset? _suspendedAt;
    private bool _suspendedDuringWork;
    // What UpdateSleepPrevention last asked for; read when Windows announces a suspend (not on the window's thread).
    private volatile bool _sleepPreventionRequired;

    /// <summary>Once per boot, at start: if the PC went down abnormally or a program turned it off, say so (and why) and keep it in the
    /// diagnostics. Off the window's thread; a log that cannot be read says nothing.</summary>
    private void ReportPreviousShutdown()
    {
        if (_shutdownChecked || !IsTheApp) return;
        _shutdownChecked = true;
        string resourceLog = _settingsPath + ".resource-diagnostics.jsonl";
        string marker = Path.Combine(OpenCodeProjectContext.DataDirectory, "shutdown-check.txt");
        _ = Task.Run(() =>
        {
            try
            {
                DateTime boot = ShutdownCause.BootTime();
                if (File.Exists(marker) && DateTime.TryParse(File.ReadAllText(marker), null, System.Globalization.DateTimeStyles.RoundtripKind, out var checkedBoot) &&
                    Math.Abs((checkedBoot - boot).TotalMinutes) < 2) return;
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                File.WriteAllText(marker, boot.ToString("o"));
                var said = ShutdownCause.Describe(ShutdownCause.ReadEvents(boot.AddDays(-3)), boot, ShutdownCause.LastSnapshotBefore(resourceLog, boot));
                if (said is not { } found) return;
                Dispatcher.BeginInvoke(() =>
                {
                    if (_closing || _resourcesDisposed) return;
                    RecordAutomationDiagnostic("previous_shutdown", found.Text, severity: found.Unexpected ? "error" : "warning");
                    SetAttentionStatus("지난번 PC 종료 · " + found.Text);
                });
            }
            catch (Exception exception) when (exception is EventLogException or IOException or UnauthorizedAccessException or InvalidOperationException or System.Xml.XmlException)
            {
                System.Diagnostics.Trace.WriteLine("Previous shutdown check failed: " + exception.GetType().Name);
            }
        });
    }

    /// <summary>Sleep while work needed the PC awake can only come from outside the app (a power policy, the lid, the user): said on
    /// wake, so a PC found "off" in the morning is not taken for a crash.</summary>
    private void NoteSuspend()
    {
        _suspendedAt = DateTimeOffset.Now;
        _suspendedDuringWork = _sleepPreventionRequired;
    }

    private void NoteResume()
    {
        if (_suspendedAt is not { } since) return;
        _suspendedAt = null;
        var now = DateTimeOffset.Now;
        if (!_suspendedDuringWork || now - since < TimeSpan.FromMinutes(5)) return;
        string text = $"작업 중이었는데 PC가 {since:MM-dd HH:mm}부터 {now:MM-dd HH:mm}까지 절전 상태였습니다 · 앱은 절전을 막고 있었으니 전원 정책·덮개·전원 버튼 같은 앱 밖의 요청입니다.";
        RecordAutomationDiagnostic("sleep_during_work", text, severity: "warning");
        SetAttentionStatus(text);
    }
}
