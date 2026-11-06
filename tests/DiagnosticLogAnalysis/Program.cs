using System.Text;
using System.Text.Json;
using TerminalNotes.Services;

string directory = Path.Combine(Path.GetTempPath(), "TerminalNotes.DiagnosticLogAnalysisTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
// AnalyzeDefaultAsync also reads the daily diagnostics folder: point it at this fixture, never at the user's real logs.
AppContext.SetData("TerminalNotes.DataDirectory", directory);
try
{
    string path = Path.Combine(directory, "fixture.jsonl");
    string Event(string name, int second, int slot = 1, uint pid = 42, object? duration = null, string stage = "native_server_started") => JsonSerializer.Serialize(new
    {
        timestampUtc = $"2026-10-13T01:00:{second:00}Z", eventName = name, terminalSlot = slot, processId = pid,
        durationSeconds = duration, terminalStage = stage, prompt = "PRIVATE_PROMPT_日本語", authorization = "Bearer TOP_SECRET",
        project = "C:/SECRET_PATH", exceptionType = "password=SECRET_EXCEPTION", detail = "SECRET_DETAIL"
    });
    await File.WriteAllTextAsync(path, string.Join('\n', new[]
    {
        Event("dispatcher_recovered", 20, duration: 19.5),
        Event("dispatcher_unresponsive", 1),
        Event("dispatcher_unresponsive", 21, pid: 99),
        Event("dispatcher_recovered", 30, pid: 42, duration: 9),
        Event("terminal_lifecycle_fault", 31, stage: "SECRET_STAGE"),
        "{\"timestamp\":\"2026-10-13T01:00:32Z\",\"eventName\":\"auto_resume_stalled\",\"detail\":\"SECRET_AUTO\"}",
        "{\"eventName\":\"SECRET_EVENT\",\"text\":\"SECRET_TEXT\"}",
        "{broken", "{\"eventName\":"
    }), new UTF8Encoding(true));
    using (var activeWriter = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
    {
        var result = await DiagnosticLogAnalyzer.AnalyzeAsync([path]);
        Require(result.ParsedEvents == 6 && result.InvalidLines == 2 && !result.IsPartial, "UTF8, active-file sharing and corrupt-tail tolerance");
        Require(result.Summary.Contains("UI 응답 지연 기록 2개") && result.Summary.Contains("복구 기록 2개") && result.Summary.Contains("19.5초"), "raw event counts and recorded recovery duration");
        Require(result.Summary.Contains("자동 진행 이벤트 1개") && result.Summary.Contains("터미널 수명주기 오류 1회"), "automation schema and lifecycle count");
        Require(!result.Summary.Contains("SECRET") && !result.Summary.Contains("PRIVATE") && !result.Summary.Contains(directory), "privacy allowlist");
        Require(result.Summary.Contains("배제하지 못") && result.Summary.Contains("단정하지"), "facts distinguished from hypotheses");
        Require(result.Summary.Contains("과거 로그에 메모리 관측 기록이 없어"), "old logs disclose missing memory evidence");
    }
    string mixed = Path.Combine(directory, "mixed-sessions.jsonl");
    await File.WriteAllTextAsync(mixed, string.Join('\n', new[]
    {
        Event("dispatcher_recovered", 40, slot: 8, pid: 999, duration: 39),
        Event("dispatcher_unresponsive", 1, slot: 1, pid: 42),
        Event("dispatcher_unresponsive", 50, slot: 3, pid: 333),
        Event("dispatcher_recovered", 55, slot: 4, pid: 444, duration: 4)
    }));
    var mixedResult = await DiagnosticLogAnalyzer.AnalyzeAsync([mixed]);
    Require(mixedResult.Summary.Contains("UI 응답 지연 기록 2개") && mixedResult.Summary.Contains("복구 기록 2개"), "changed child context and mixed lifetimes retain raw counts");
    Require(!mixedResult.Summary.Contains("쌍") && !mixedResult.Summary.Contains("복구 짝이 없는") && !mixedResult.Summary.Contains("현재 멈춤 1"), "legacy records never produce authoritative correlation or unresolved counts");
    Require(mixedResult.Summary.IndexOf("10-13 01:00:01", StringComparison.Ordinal) < mixedResult.Summary.IndexOf("10-13 01:00:40", StringComparison.Ordinal), "mixed contexts remain chronologically sorted");
    string large = Path.Combine(directory, "large.jsonl");
    await File.WriteAllTextAsync(large, new string('x', DiagnosticLogAnalyzer.MaximumBytesPerFile + 50) + "\n" + Event("dispatcher_recovered", 40, duration: 3));
    var largeResult = await DiagnosticLogAnalyzer.AnalyzeAsync([large]);
    Require(largeResult.IsPartial && largeResult.ParsedEvents == 1, "bounded tail skips truncated first line");
    await File.WriteAllTextAsync(large, string.Concat(Enumerable.Repeat(Event("dispatcher_recovered", 40) + "\n", 12000)));
    var linesResult = await DiagnosticLogAnalyzer.AnalyzeAsync([large]);
    Require(linesResult.IsPartial && linesResult.ParsedEvents <= DiagnosticLogAnalyzer.MaximumLinesPerFile, "byte and line limits");
    var missing = await DiagnosticLogAnalyzer.AnalyzeAsync([Path.Combine(directory, "SECRET_MISSING")]);
    Require(missing.IsPartial && missing.Summary.Contains("읽을 수 없습니다") && !missing.Summary.Contains("SECRET_MISSING"), "missing-file guidance without path leak");
    string memoryPath = Path.Combine(directory, "resource.jsonl");
    string Resource(int second, int appPid, int load, bool partial = false) => JsonSerializer.Serialize(new
    {
        timestampUtc = $"2026-10-13T01:01:{second:00}Z", @event = "resource_snapshot", appProcessId = appPid,
        appStartedAtUtc = "2026-10-13T00:00:00Z", systemMemoryLoadPercent = load,
        totalPhysicalBytes = 16L * 1024 * 1024 * 1024, availablePhysicalBytes = 512L * 1024 * 1024,
        processCount = 3, unreadableProcessCount = 1, isPartial = partial,
        treePrivateBytes = 1024L * 1024 * 1024, treeWorkingSetBytes = 2L * 1024 * 1024 * 1024,
        processes = new object[]
        {
            new { processId = appPid, parentProcessId = 10, name = "TerminalNotes.exe", description = "Terminal Notes", productName = "", privateBytes = 128L * 1024 * 1024, workingSetBytes = 256L * 1024 * 1024, isApp = true },
            new { processId = appPid + 1, parentProcessId = appPid, name = "bun.exe", description = "MiniStackScan", productName = "Bun", privateBytes = 768L * 1024 * 1024, workingSetBytes = 900L * 1024 * 1024, isApp = false },
            new { processId = appPid + 2, parentProcessId = appPid, name = "C:/SECRET_PATH.exe", description = "password=SECRET_VALUE\nSECRET_NEXT", productName = "Bearer SECRET_TOKEN", privateBytes = -1L, workingSetBytes = long.MaxValue, isApp = false }
        },
        prompt = "PRIVATE_PROMPT", commandLine = "SECRET_COMMAND", error = "SECRET_ERROR"
    });
    await File.WriteAllTextAsync(memoryPath, Resource(20, 900, 70, true) + "\n" + Resource(10, 700, 98) + "\n{\"event\":\"resource_snapshot\",\"timestampUtc\":");
    var resource = await DiagnosticLogAnalyzer.AnalyzeAsync([memoryPath, Path.Combine(directory, "optional-missing.jsonl")]);
    Require(resource.IsPartial && resource.ParsedEvents == 2 && resource.InvalidLines == 1, "resource schema, partial sample, missing optional file and truncated tail");
    Require(resource.Summary.Contains("최신 앱 PID 900") && !resource.Summary.Contains("최신 앱 PID 700") && resource.Summary.Contains("최대 사용률: 98%") && resource.Summary.Contains("최근 시스템 메모리 사용률: 70%"), "latest chronological sample versus cross-instance maximum observation");
    Require(resource.Summary.Contains("MiniStackScan") && resource.Summary.Contains("bun.exe") && resource.Summary.Contains("전용 메모리 128.0 MiB") && resource.Summary.Contains("부모 900"), "actual executable metadata and app/tree distinction");
    Require(!resource.Summary.Contains("SECRET") && !resource.Summary.Contains("PRIVATE") && !resource.Summary.Contains("922337") && resource.Summary.Contains("공유 페이지"), "metadata privacy and invalid byte values");
    string defaultSettings = Path.Combine(directory, "default-settings.json");
    await File.WriteAllTextAsync(defaultSettings + ".ui-diagnostics.jsonl", "");
    await File.WriteAllTextAsync(defaultSettings + ".automation-diagnostics.jsonl", "");
    string lowRestart = JsonSerializer.Serialize(new { @event = "resource_snapshot", timestampUtc = "2026-10-13T01:05:00Z", appProcessId = 1200, appStartedAtUtc = "2026-10-13T01:04:00Z", systemMemoryLoadPercent = 20, treePrivateBytes = 1048576L, treeWorkingSetBytes = 2097152L, processes = Array.Empty<object>() });
    await File.WriteAllTextAsync(defaultSettings + ".resource-diagnostics.jsonl", lowRestart);
    var withoutBackup = await DiagnosticLogAnalyzer.AnalyzeDefaultAsync(defaultSettings);
    Require(!withoutBackup.IsPartial && !withoutBackup.Summary.Contains("최대 사용률 관측 당시:"), "absent optional backup is not partial and identical maximum/latest sample not duplicated");
    await File.WriteAllTextAsync(defaultSettings + ".resource-diagnostics.jsonl.1", Resource(10, 700, 98));
    var withBackup = await DiagnosticLogAnalyzer.AnalyzeDefaultAsync(defaultSettings);
    Require(withBackup.ParsedEvents == 2 && withBackup.Summary.Contains("최신 앱 PID 1200") && withBackup.Summary.Contains("최대 사용률 관측 당시: 98%"), "existing rotation backup loaded after lower-load restart");
    Require(withBackup.Summary.Contains("MiniStackScan") && withBackup.Summary.Contains("부모 700") && withBackup.Summary.Contains("전용 메모리 128.0 MiB") && withBackup.Summary.Contains("멈춤을 일으켰다는 판정은 아닙니다"), "peak consumer identity and app/tree totals remain visible without causation");
    string malformedMemory = "{\"event\":\"resource_snapshot\",\"timestampUtc\":\"2026-10-13T01:02:00Z\",\"appProcessId\":-5,\"systemMemoryLoadPercent\":101,\"totalPhysicalBytes\":-1,\"availablePhysicalBytes\":\"secret\",\"treePrivateBytes\":1e300,\"treeWorkingSetBytes\":null,\"processCount\":-1,\"unreadableProcessCount\":99999999,\"processes\":[null,{\"processId\":3,\"name\":\"safe.exe\",\"description\":\"private\\u202eSECRET\",\"isApp\":true,\"privateBytes\":null}]}";
    await File.WriteAllTextAsync(memoryPath, malformedMemory);
    var malformedResult = await DiagnosticLogAnalyzer.AnalyzeAsync([memoryPath]);
    Require(malformedResult.ParsedEvents == 1 && malformedResult.Summary.Contains("최근 시스템 메모리 사용률: 확인 불가") && malformedResult.Summary.Contains("최신 앱 PID 확인 불가"), "invalid numeric values remain unavailable rather than zero");
    Require(!malformedResult.Summary.Contains("SECRET") && !malformedResult.Summary.Contains('\u202e') && malformedResult.Summary.Contains("앱 자체: 전용 메모리 확인 불가"), "format-character metadata blocked and unverified isApp flag ignored");
    await File.WriteAllTextAsync(memoryPath, JsonSerializer.Serialize(new { @event = "resource_snapshot", timestampUtc = "2026-10-13T01:03:00Z", processes = Enumerable.Range(1, 100).Select(id => new { processId = id, name = "safe.exe", privateBytes = id * 1024L }).ToArray() }));
    var rowsLimited = await DiagnosticLogAnalyzer.AnalyzeAsync([memoryPath]);
    Require(rowsLimited.IsPartial && rowsLimited.Summary.Contains("프로세스 일부만 측정") && rowsLimited.Summary.Split('•').Length == 6 && !rowsLimited.Summary.Contains("PID 100"), "untrusted resource rows bounded to 32 and report to top five");
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    try { await DiagnosticLogAnalyzer.AnalyzeAsync([path], canceled.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { }
    Console.WriteLine("PASS responsiveness/resource schemas, raw counts, chronological mixed instances, safe memory identity/units, privacy, invalid metrics, UTF8, malformed/truncated logs, active-file sharing, bounded tail/lines, unavailable historical evidence and cancellation");
}
finally { Directory.Delete(directory, true); }

static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
