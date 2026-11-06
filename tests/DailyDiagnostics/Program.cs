using System.IO.Compression;
using System.Text.Json;
using TerminalNotes.Services;

string root = Path.Combine(Path.GetTempPath(), "TerminalNotes-daily-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var failures = new List<string>();
int passed = 0;
try
{
    Check("actual file version and test data override", () =>
    {
        Assert(ApplicationDiagnostics.ApplicationVersion == "7.8.9.10", "FileVersion must win over AssemblyVersion");
        Assert(ApplicationDiagnostics.DisplayVersion == "7.8.9", "display must use three components of the actual FileVersion");
        AppContext.SetData("TerminalNotes.DataDirectory", root);
        Assert(ApplicationDiagnostics.GetLogDirectory() == Path.Combine(root, "diagnostics", "daily"), "data override ignored");
    });
    Check("normal lifecycle and metadata", () =>
    {
        string dir = Folder("normal");
        using var session = New(dir);
        session.Record("operation.completed", "automation", "exit_0");
        session.CompleteNormalExit();
        var events = Events(dir);
        JsonElement start = events.Single(e => Event(e) == "session.start");
        Assert(start.GetProperty("appVersion").GetString() == "7.8.9.10", "wrong startup version");
        Assert(start.TryGetProperty("buildId", out _) && start.TryGetProperty("os", out _)
            && start.TryGetProperty("framework", out _) && start.TryGetProperty("architecture", out _), "missing environment metadata");
        Assert(start.GetProperty("timestamp").GetString()!.Contains('+') || start.GetProperty("timestamp").GetString()!.EndsWith("Z"), "missing time offset");
        Assert(events.Any(e => Event(e) == "session.normal_exit"), "missing normal exit");
        Assert(!Directory.EnumerateFiles(dir, "*.session.json").Any(), "normal marker remains");
        using var next = New(dir);
        Assert(next.Flush(TimeSpan.FromSeconds(5)), "flush timeout");
        Assert(!Events(dir).Any(e => Event(e) == "session.previous_abnormal_shutdown"), "normal exit called abnormal");
    });
    Check("stale markers recovered with unknown cause and last observation", () =>
    {
        string dir = Folder("stale");
        string prior;
        using (var first = New(dir)) { prior = first.SessionId; first.Flush(TimeSpan.FromSeconds(5)); }
        using var second = New(dir);
        second.Flush(TimeSpan.FromSeconds(5));
        JsonElement stale = Events(dir).Single(e => Event(e) == "session.previous_abnormal_shutdown");
        Assert(stale.GetProperty("previousSessionId").GetString() == prior, "wrong prior session");
        Assert(stale.GetProperty("cause").GetString() == "unknown" && stale.GetProperty("terminationTime").GetString() == "unknown", "invented termination cause or time");
        Assert(stale.TryGetProperty("lastSeen", out _), "no last observation");
    });
    Check("live concurrent instances are preserved", () =>
    {
        string dir = Folder("live");
        using var first = New(dir);
        using var second = New(dir);
        second.Flush(TimeSpan.FromSeconds(5));
        Assert(!Events(dir).Any(e => Event(e) == "session.previous_abnormal_shutdown"), "live process misclassified");
        Assert(File.Exists(Path.Combine(dir, first.SessionId + ".session.json")), "live marker removed");
        second.CompleteNormalExit();
        first.CompleteNormalExit();
    });
    Check("fatal exception survives a subsequent normal-exit callback", () =>
    {
        string dir = Folder("fatal");
        using (var first = New(dir))
        {
            first.RecordException("app.unhandled", Thrown("password=TOPSECRET full private prompt"), true);
            Assert(first.Flush(TimeSpan.FromSeconds(5)), "fatal flush timeout");
            first.CompleteNormalExit();
        }
        using var next = New(dir);
        next.Flush(TimeSpan.FromSeconds(5));
        Assert(!Events(dir).Any(e => Event(e) == "session.normal_exit"), "fatal relabeled normal");
        JsonElement stale = Events(dir).Single(e => Event(e) == "session.previous_abnormal_shutdown");
        Assert(stale.GetProperty("fatalExceptionObserved").GetBoolean(), "lost fatal evidence");
    });
    Check("no exception messages, paths, prompts or credentials", () =>
    {
        string dir = Folder("privacy");
        using var session = New(dir);
        session.Record("full private prompt with password=TOPSECRET", "C:\\private\\document.md", "Bearer SECRET");
        session.RecordException("io.failure", Thrown("api_key=TOPSECRET C:\\private\\document.md private prompt"));
        session.CompleteNormalExit();
        string logs = string.Concat(Directory.EnumerateFiles(dir, "*.jsonl").Select(File.ReadAllText));
        Assert(!logs.Contains("TOPSECRET") && !logs.Contains("document.md") && !logs.Contains("private prompt") && !logs.Contains("Bearer SECRET"), "sensitive text leaked");
        JsonElement error = Events(dir).Single(e => Event(e) == "io.failure").GetProperty("exceptions")[0];
        Assert(error.GetProperty("type").GetString() == "System.InvalidOperationException", "exception type omitted");
        Assert(error.GetProperty("hResult").GetString()!.StartsWith("0x"), "HResult omitted");
        Assert(error.GetProperty("stack").GetArrayLength() > 0, "useful method stack missing");
    });
    Check("local date boundary is attached to event timestamp", () =>
    {
        string dir = Folder("midnight");
        var current = new DateTimeOffset(2026, 10, 19, 23, 59, 59, TimeSpan.FromHours(9));
        using var session = New(dir, () => current);
        session.Record("before_midnight");
        session.Flush(TimeSpan.FromSeconds(5));
        current = current.AddSeconds(2);
        session.Record("after_midnight");
        session.CompleteNormalExit();
        Assert(Directory.EnumerateFiles(dir, "2026-10-19-*.jsonl").Any() && Directory.EnumerateFiles(dir, "2026-10-20-*.jsonl").Any(), "daily rollover missing");
        foreach (string path in Directory.EnumerateFiles(dir, "*.jsonl"))
            Assert(File.ReadAllLines(path).All(line => JsonDocument.Parse(line).RootElement.GetProperty("timestamp").GetString()!.StartsWith(Path.GetFileName(path)[..10])), "event written to wrong local date");
    });
    Check("rotation and count retention are bounded", () =>
    {
        string dir = Folder("rotation");
        using var session = new DailyDiagnosticSession(new()
        { DirectoryPath = dir, MaxFileBytes = 1024, MaxFiles = 3, MaxTotalBytes = 10000, QueueCapacity = 512 });
        for (int i = 0; i < 100; i++) session.Record("operation.completed", "automation", "safe_operational_code");
        session.CompleteNormalExit();
        FileInfo[] files = Directory.EnumerateFiles(dir, "*.jsonl").Select(path => new FileInfo(path)).ToArray();
        Assert(files.Length == 3, "rotation/count retention failed");
        Assert(files.All(file => file.Length <= 1024), "file size limit exceeded");
        Assert(Events(dir).Any(e => Event(e) == "session.normal_exit"), "retention dropped newest event");
    });
    Check("fatal priority across midnight cannot overflow older rotated parts", () =>
    {
        string dir = Folder("midnight-priority-rotation");
        var current = new DateTimeOffset(2026, 10, 19, 23, 59, 59, TimeSpan.FromHours(9));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int pause = 0;
        const int limit = 2048;
        using var session = new DailyDiagnosticSession(new()
        {
            DirectoryPath = dir, Now = () => current, MaxFileBytes = limit, MaxFiles = 64, MaxTotalBytes = 1024 * 1024,
            BeforeWrite = () =>
            {
                if (Interlocked.CompareExchange(ref pause, 2, 1) == 1)
                { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); }
            }
        });
        for (int i = 0; i < 24; i++) session.Record("rotation.seed");
        Assert(session.Flush(TimeSpan.FromSeconds(5)), "seed records did not drain");
        string[] earlierParts = Directory.EnumerateFiles(dir, "2026-10-19-*.jsonl").OrderBy(path => path).ToArray();
        Assert(earlierParts.Length >= 3, "insufficient pre-midnight rotation");
        // Fill the first two retired parts exactly. Each added line is valid JSON, keeping
        // this fixture compatible with archive/parser behavior and the configured retention.
        foreach (string path in earlierParts.Take(2))
        {
            int remaining = limit - (int)new FileInfo(path).Length;
            if (remaining >= 3) File.AppendAllText(path, new string(' ', remaining - 3) + "{}\n");
        }
        try
        {
            Volatile.Write(ref pause, 1);
            session.Record("paused_before_midnight");
            Assert(entered.Wait(TimeSpan.FromSeconds(5)), "writer did not pause");
            for (int i = 0; i < 8; i++) session.Record("queued_before_midnight");
            current = current.AddSeconds(2);
            session.RecordException("fatal_after_midnight", Thrown("PRIVATE"), true);
        }
        finally { release.Set(); }
        Assert(session.Flush(TimeSpan.FromSeconds(5)), "interleaved records did not drain");
        session.CompleteNormalExit();
        FileInfo[] files = Directory.EnumerateFiles(dir, "*.jsonl").Select(path => new FileInfo(path)).ToArray();
        Assert(files.All(file => file.Length <= limit), "interleaved date reopened an already-full part");
        var observations = new List<(string Day, JsonElement Entry)>();
        foreach (FileInfo file in files)
            foreach (string line in File.ReadAllLines(file.FullName))
            {
                using var document = JsonDocument.Parse(line);
                if (!document.RootElement.TryGetProperty("event", out _)) continue;
                var entry = document.RootElement.Clone();
                Assert(entry.GetProperty("timestamp").GetString()!.StartsWith(file.Name[..10]), "event assigned to wrong date");
                observations.Add((file.Name[..10], entry));
            }
        Assert(observations.Count(item => Event(item.Entry) == "queued_before_midnight" && item.Day == "2026-10-19") == 8,
            "queued pre-midnight evidence lost");
        Assert(observations.Count(item => Event(item.Entry) == "fatal_after_midnight" && item.Day == "2026-10-20") == 1,
            "post-midnight fatal evidence lost");
    });
    Check("heartbeat advances a low-frequency last-seen marker", () =>
    {
        string dir = Folder("heartbeat");
        var current = DateTimeOffset.Now;
        using var session = new DailyDiagnosticSession(new()
        { DirectoryPath = dir, Now = () => current, HeartbeatInterval = TimeSpan.FromMilliseconds(30) });
        current = current.AddMinutes(1);
        string marker = Path.Combine(dir, session.SessionId + ".session.json");
        bool advanced = SpinWait.SpinUntil(() =>
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(marker));
                return document.RootElement.GetProperty("lastSeen").GetDateTimeOffset() == current;
            }
            catch (IOException) { return false; }
        }, TimeSpan.FromSeconds(3));
        Assert(advanced, "heartbeat did not update marker");
        session.CompleteNormalExit();
    });
    Check("age and total-size retention", () =>
    {
        string dir = Folder("retention");
        Directory.CreateDirectory(dir);
        string old = Path.Combine(dir, "2001-01-01-" + Guid.NewGuid().ToString("N") + "-0000.jsonl");
        File.WriteAllText(old, "{}\n");
        using var session = new DailyDiagnosticSession(new() { DirectoryPath = dir, MaxFileBytes = 1024, MaxTotalBytes = 2048 });
        for (int i = 0; i < 100; i++) session.Record("retention.check");
        session.CompleteNormalExit();
        Assert(!File.Exists(old), "old date not pruned");
        Assert(Directory.EnumerateFiles(dir, "*.jsonl").Sum(path => new FileInfo(path).Length) <= 2048, "total retention exceeded");
    });
    Check("export snapshots active logs and excludes settings/documents/partial records", () =>
    {
        string dir = Folder("export");
        using var session = New(dir);
        session.Record("export.check");
        session.Flush(TimeSpan.FromSeconds(5));
        File.WriteAllText(Path.Combine(dir, "settings.json"), "SECRET");
        File.WriteAllText(Path.Combine(dir, "plan.md"), "PRIVATE PROMPT");
        string log = Directory.EnumerateFiles(dir, "*.jsonl").First();
        using var active = new FileStream(log, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        active.Seek(0, SeekOrigin.End);
        active.Write("{\"incomplete\":"u8);
        active.Flush();
        string zipPath = DailyDiagnosticSession.ExportArchive(dir, Path.Combine(root, "support.zip"), 7, DateTimeOffset.Now);
        using var zip = ZipFile.OpenRead(zipPath);
        Assert(zip.Entries.Any(e => e.FullName == "support-metadata.json"), "manifest missing");
        Assert(zip.Entries.All(e => e.FullName == "support-metadata.json" || e.FullName.StartsWith("daily/") && e.FullName.EndsWith(".jsonl")), "unexpected exported artifact");
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".jsonl")))
        {
            using var reader = new StreamReader(entry.Open());
            string text = reader.ReadToEnd();
            Assert(!text.Contains("incomplete") && !text.Contains("SECRET") && !text.Contains("PRIVATE PROMPT"), "partial or sensitive export");
            foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries)) JsonDocument.Parse(line).Dispose();
        }
    });
    Check("logger file failures never propagate", () =>
    {
        string blocked = Path.Combine(root, "blocked");
        File.WriteAllText(blocked, "not a directory");
        using var session = New(blocked);
        session.Record("failure.check");
        session.RecordException("failure.check", new IOException("password=SECRET"));
        Assert(session.Flush(TimeSpan.FromSeconds(5)), "failed writer did not drain");
        Assert(session.LastWriteError == "log_unavailable", "failure status missing");
        session.CompleteNormalExit();
    });
    Check("daily logs appear in bounded default and imported analysis", () =>
    {
        string data = Folder("analyzer-data"), dir = Path.Combine(data, "diagnostics", "daily");
        using (var first = New(dir))
        {
            first.RecordException("io.failure", Thrown("PRIVATE PROMPT password=TOPSECRET"), true);
            first.Flush(TimeSpan.FromSeconds(5));
        }
        using var next = New(dir);
        next.CompleteNormalExit();
        var imported = DiagnosticLogAnalyzer.AnalyzeAsync(Directory.EnumerateFiles(dir, "*.jsonl")).GetAwaiter().GetResult();
        Assert(imported.ParsedEvents >= 5 && imported.Summary.Contains("치명적 예외 관측")
            && imported.Summary.Contains("이전 비정상 종료 관측") && imported.Summary.Contains("7.8.9.10"), "daily imported evidence omitted");
        Assert(!imported.Summary.Contains("TOPSECRET") && !imported.Summary.Contains("PRIVATE PROMPT"), "private text in summary");
        AppContext.SetData("TerminalNotes.DataDirectory", data);
        try
        {
            var defaults = DiagnosticLogAnalyzer.AnalyzeDefaultAsync(Path.Combine(data, "settings.json")).GetAwaiter().GetResult();
            Assert(defaults.ParsedEvents >= 5 && defaults.Summary.Contains("7.8.9.10"), "daily default discovery missing");
        }
        finally { AppContext.SetData("TerminalNotes.DataDirectory", root); }
    });
    Check("saturated queues preserve normal-exit evidence before removing marker", () =>
    {
        string dir = Folder("saturated-normal");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int first = 0;
        using var session = new DailyDiagnosticSession(new()
        {
            DirectoryPath = dir, QueueCapacity = 8,
            BeforeWrite = () => { if (Interlocked.Exchange(ref first, 1) == 0) { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); } }
        });
        try
        {
            Assert(entered.Wait(TimeSpan.FromSeconds(5)), "writer did not pause");
            for (int i = 0; i < 32; i++) session.Record("queue.fill");
            // The writer stays paused for the complete bounded exit attempt, so admission
            // is tested while every ordinary queue slot is occupied.
            session.CompleteNormalExit();
        }
        finally { release.Set(); }
        Assert(session.Completion.Wait(TimeSpan.FromSeconds(5)), "exit did not drain");
        var events = Events(dir);
        Assert(events.Count(e => Event(e) == "session.normal_exit") == 1, "saturated queue lost normal exit");
        Assert(events.Any(e => e.TryGetProperty("droppedEvents", out var drops) && drops.GetInt64() > 0), "queue was not saturated");
        Assert(!File.Exists(Path.Combine(dir, session.SessionId + ".session.json")), "normal marker cleanup missing");
    });
    Check("saturated queues preserve first fatal and independent fatal marker", () =>
    {
        string dir = Folder("saturated-fatal");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int first = 0;
        using var session = new DailyDiagnosticSession(new()
        {
            DirectoryPath = dir, QueueCapacity = 8,
            BeforeWrite = () => { if (Interlocked.Exchange(ref first, 1) == 0) { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); } }
        });
        try
        {
            Assert(entered.Wait(TimeSpan.FromSeconds(5)), "writer did not pause");
            for (int i = 0; i < 32; i++) session.Record("queue.fill");
            session.RecordException("first_fatal", Thrown("PRIVATE FIRST FATAL"), true);
            session.RecordException("second_fatal", Thrown("PRIVATE SECOND FATAL"), true);
            session.CompleteNormalExit();
        }
        finally { release.Set(); }
        Assert(session.Completion.Wait(TimeSpan.FromSeconds(5)), "fatal did not drain");
        var events = Events(dir);
        Assert(events.Count(e => Event(e) == "first_fatal") == 1, "first fatal lost to saturation");
        Assert(events.Count(e => Event(e) == "session.exit_after_fatal") == 1, "fatal exit missing");
        Assert(!events.Any(e => Event(e) == "session.normal_exit"), "fatal mislabeled normal");
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, session.SessionId + ".session.json")));
        Assert(marker.RootElement.GetProperty("fatalObserved").GetBoolean(), "fatal marker not persisted");
    });
    Check("fatal marker persists even if fatal event append fails", () =>
    {
        string dir = Folder("fatal-append-failure");
        int writes = 0;
        using var session = new DailyDiagnosticSession(new()
        {
            DirectoryPath = dir,
            BeforeWrite = () => { if (Interlocked.Increment(ref writes) == 2) throw new IOException("simulated append failure"); }
        });
        Assert(session.Flush(TimeSpan.FromSeconds(5)), "startup did not drain");
        session.RecordException("fatal_append_failure", Thrown("PRIVATE"), true);
        Assert(session.Flush(TimeSpan.FromSeconds(5)), "fatal did not drain");
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, session.SessionId + ".session.json")));
        Assert(marker.RootElement.GetProperty("fatalObserved").GetBoolean(), "append failure erased independent fatal state");
        Assert(session.LastWriteError == "log_unavailable", "append failure hidden");
    });
    Check("warning and fault identifiers preserve daily severity and analysis context", () =>
    {
        string dir = Folder("severity");
        using var session = New(dir);
        session.Record("operation_warning", "ui");
        session.Record("planner_warning", "planner");
        session.Record("terminal_lifecycle_fault", "terminal", "System.InvalidOperationException");
        session.CompleteNormalExit();
        var events = Events(dir);
        Assert(events.Single(e => Event(e) == "operation_warning").GetProperty("severity").GetString() == "warning", "operation warning misclassified");
        Assert(events.Single(e => Event(e) == "planner_warning").GetProperty("severity").GetString() == "warning", "planner warning misclassified");
        Assert(events.Single(e => Event(e) == "terminal_lifecycle_fault").GetProperty("severity").GetString() == "error", "terminal fault misclassified");
        var analysis = DiagnosticLogAnalyzer.AnalyzeAsync(Directory.EnumerateFiles(dir, "*.jsonl")).GetAwaiter().GetResult();
        Assert(analysis.ParsedEvents == events.Count && analysis.Summary.Contains("앱 경고 관측") && analysis.Summary.Contains("앱 오류 관측")
            && analysis.Summary.Contains("7.8.9.10") && analysis.Summary.Contains("빌드 "), "daily severity or build context missing");
    });
}
finally
{
    AppContext.SetData("TerminalNotes.DataDirectory", null);
    Directory.Delete(root, true);
}
Console.WriteLine($"Daily diagnostics: {passed} passed, {failures.Count} failed.");
foreach (string failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;

void Check(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception exception) { failures.Add(name + ": " + exception.Message); }
}
string Folder(string name) => Path.Combine(root, name);
DailyDiagnosticSession New(string dir, Func<DateTimeOffset>? now = null) => new(new()
{ DirectoryPath = dir, Now = now ?? (() => DateTimeOffset.Now) });
static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static string? Event(JsonElement element) => element.GetProperty("event").GetString();
static List<JsonElement> Events(string dir) => Directory.EnumerateFiles(dir, "*.jsonl").SelectMany(File.ReadAllLines)
    .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }).ToList();
static Exception Thrown(string message)
{
    try { throw new InvalidOperationException(message); } catch (Exception exception) { return exception; }
}
