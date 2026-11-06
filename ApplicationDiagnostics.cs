using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace TerminalNotes.Services;

/// <summary>Always-on support metadata. Never pass input, output, commands, paths or exception messages.</summary>
public static class ApplicationDiagnostics
{
    private static readonly object Gate = new();
    private static DailyDiagnosticSession? session;
    private static bool started;
    private static string? startupFailure;

    public static string ApplicationVersion => typeof(ApplicationDiagnostics).Assembly
        .GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
        ?? typeof(ApplicationDiagnostics).Assembly.GetName().Version?.ToString(4) ?? "0.0.0.0";
    public static string DisplayVersion => Version.TryParse(ApplicationVersion, out var version) && version.Build >= 0
        ? version.ToString(3) : ApplicationVersion;
    public static string? LastWriteError => Volatile.Read(ref startupFailure) ?? session?.LastWriteError;
    public static bool IsStarted => session != null;

    public static string GetLogDirectory() => Path.Combine(
        AppContext.GetData("TerminalNotes.DataDirectory") is string directory ? directory :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerminalNotes"),
        "diagnostics", "daily");

    public static void BeginSession()
    {
        try
        {
            lock (Gate)
            {
                if (started) return;
                started = true;
                session = new DailyDiagnosticSession(new() { DirectoryPath = GetLogDirectory() });
            }
        }
        catch { Volatile.Write(ref startupFailure, "log_unavailable"); }
    }

    public static void Record(string eventName, string? component = null, string? code = null)
    {
        try { session?.Record(eventName, component, code); } catch { }
    }

    internal static void RecordOperational(string eventName, string severity, string? component = null, string? code = null)
    {
        try { session?.Record(eventName, component, code, severity); } catch { }
    }

    public static void RecordException(string eventName, Exception exception, bool fatal = false)
    {
        try
        {
            session?.RecordException(eventName, exception, fatal);
            if (fatal) Flush(TimeSpan.FromSeconds(2));
        }
        catch { }
    }

    public static void Flush(TimeSpan? timeout = null)
    {
        try { session?.Flush(timeout ?? TimeSpan.FromSeconds(2)); } catch { }
    }

    public static void CompleteNormalExit()
    {
        try { session?.CompleteNormalExit(); } catch { }
    }

    /// <summary>Exports only bounded daily JSONL snapshots and generated support metadata.</summary>
    public static Task<string> ExportArchiveAsync(string destinationPath, int recentDays = 7) => Task.Run(() =>
    {
        Flush();
        return DailyDiagnosticSession.ExportArchive(GetLogDirectory(), destinationPath, recentDays, DateTimeOffset.Now);
    });
}

internal sealed class DailyDiagnosticOptions
{
    public required string DirectoryPath { get; init; }
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;
    public int QueueCapacity { get; init; } = 512;
    public int MaxFileBytes { get; init; } = 2 * 1024 * 1024;
    public int MaxFiles { get; init; } = 128;
    public long MaxTotalBytes { get; init; } = 64L * 1024 * 1024;
    public int RetentionDays { get; init; } = 14;
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(60);
    internal Action? BeforeWrite { get; init; }
}

internal sealed class DailyDiagnosticSession : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Regex LogName = new(@"^\d{4}-\d{2}-\d{2}-[a-f0-9]{32}-\d{4}\.jsonl$", RegexOptions.CultureInvariant);
    private readonly DailyDiagnosticOptions options;
    private readonly Channel<Pending> queue;
    private readonly Timer heartbeat;
    private readonly Task writer;
    private readonly string markerPath;
    private readonly string lockPath;
    private FileStream? lease;
    private string? currentDay;
    private int part;
    private int completed;
    private int fatalObserved;
    private readonly object lifecycleGate = new();
    private Dictionary<string, object?>? pendingFatal;
    private Dictionary<string, object?>? pendingExit;
    private bool fatalCaptured;
    private long dropped;
    private long writeFailures;
    private string? lastWriteError;
    private readonly DateTimeOffset startedAt;
    internal string SessionId { get; } = Guid.NewGuid().ToString("N");
    internal string? LastWriteError => Volatile.Read(ref lastWriteError);
    internal Task Completion => writer;
    private sealed record Pending(Dictionary<string, object?>? Entry = null, TaskCompletionSource<bool>? Barrier = null, bool Heartbeat = false);
    private sealed record Marker(string SessionId, int ProcessId, DateTimeOffset StartedAt, DateTimeOffset LastSeen,
        string State, bool FatalObserved);

    internal DailyDiagnosticSession(DailyDiagnosticOptions options)
    {
        this.options = options;
        startedAt = options.Now();
        markerPath = Path.Combine(options.DirectoryPath, SessionId + ".session.json");
        lockPath = Path.Combine(options.DirectoryPath, SessionId + ".lock");
        queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(Math.Max(8, options.QueueCapacity))
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        // Marker creation is synchronous so immediate termination can still be detected next launch.
        TryIo(() =>
        {
            Directory.CreateDirectory(options.DirectoryPath);
            lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            UpdateMarker("running");
        });
        writer = Task.Run(WriteLoopAsync);
        Enqueue(NewEntry("session.start", "information", new()
        {
            ["appVersion"] = ApplicationDiagnostics.ApplicationVersion,
            ["informationalVersion"] = typeof(ApplicationDiagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            ["buildId"] = typeof(ApplicationDiagnostics).Module.ModuleVersionId.ToString("N"),
            ["os"] = RuntimeInformation.OSDescription,
            ["framework"] = RuntimeInformation.FrameworkDescription,
            ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["heartbeatSeconds"] = options.HeartbeatInterval.TotalSeconds
        }));
        RecoverPreviousSessions();
        TryIo(Prune);
        heartbeat = new Timer(_ =>
        {
            if (Volatile.Read(ref completed) == 0 && !queue.Writer.TryWrite(new(Heartbeat: true)))
                Interlocked.Increment(ref dropped);
        }, null, options.HeartbeatInterval, options.HeartbeatInterval);
    }

    internal void Record(string eventName, string? component = null, string? code = null, string? severity = null)
    {
        if (Volatile.Read(ref completed) != 0) return;
        severity = severity is "information" or "warning" or "error" or "fatal" ? severity : eventName.Contains("warning", StringComparison.OrdinalIgnoreCase)
            ? "warning" : eventName.Contains("error", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("failure", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("fault", StringComparison.OrdinalIgnoreCase) ? "error" : "information";
        Enqueue(NewEntry(Token(eventName), severity, new()
        { ["component"] = OptionalToken(component), ["code"] = OptionalToken(code) }));
    }

    internal void RecordException(string eventName, Exception exception, bool fatal = false)
    {
        if (Volatile.Read(ref completed) != 0) return;
        var chain = new List<object>();
        for (Exception? item = exception; item != null && chain.Count < 6; item = item.InnerException)
        {
            // StackTrace frames deliberately omit file paths, line text and exception messages.
            string[] frames;
            try
            {
                frames = (new StackTrace(item, false).GetFrames() ?? [])
                    .Take(32).Select(frame => frame.GetMethod())
                    .Where(method => method != null)
                    .Select(method => SafeType(method!.DeclaringType) + "." + Token(method.Name)).ToArray();
            }
            catch { frames = []; }
            chain.Add(new { type = SafeType(item.GetType()), hResult = "0x" + item.HResult.ToString("X8"), stack = frames });
        }
        var entry = NewEntry(Token(eventName), fatal ? "fatal" : "error", new() { ["exceptions"] = chain });
        if (!fatal) { Enqueue(entry); return; }
        lock (lifecycleGate)
        {
            if (Volatile.Read(ref completed) != 0) return;
            Interlocked.Exchange(ref fatalObserved, 1);
            if (!fatalCaptured)
            {
                fatalCaptured = true;
                pendingFatal = entry;
            }
            else Enqueue(entry);
        }
        // Wake an idle reader. If the channel is full, it already has work to wake it.
        queue.Writer.TryWrite(new());
    }

    internal bool Flush(TimeSpan timeout)
    {
        try
        {
            if (writer.IsCompleted) return true;
            var barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource(timeout);
            queue.Writer.WriteAsync(new(Barrier: barrier), cancellation.Token).AsTask().GetAwaiter().GetResult();
            return barrier.Task.Wait(timeout);
        }
        catch { return false; }
    }

    internal void CompleteNormalExit()
    {
        lock (lifecycleGate)
        {
            if (Interlocked.Exchange(ref completed, 1) != 0) return;
            // Dedicated constant-size lifecycle slots cannot be displaced by ordinary events.
            pendingExit = NewEntry(Volatile.Read(ref fatalObserved) == 0 ? "session.normal_exit" : "session.exit_after_fatal",
                Volatile.Read(ref fatalObserved) == 0 ? "information" : "fatal");
        }
        heartbeat.Dispose();
        queue.Writer.TryComplete();
        try
        {
            writer.Wait(TimeSpan.FromSeconds(2));
        }
        catch { }
    }

    // Dispose also supports tests simulating abrupt termination without a normal-exit record.
    public void Dispose()
    {
        Interlocked.Exchange(ref completed, 1);
        heartbeat.Dispose();
        queue.Writer.TryComplete();
        try { writer.Wait(TimeSpan.FromSeconds(2)); } catch { }
        ReleaseLease();
    }

    private void ReleaseLease()
    {
        try { lease?.Dispose(); lease = null; File.Delete(lockPath); } catch { }
    }

    private Dictionary<string, object?> NewEntry(string eventName, string severity, Dictionary<string, object?>? fields = null)
    {
        fields ??= new();
        fields["schemaVersion"] = 1;
        fields["timestamp"] = options.Now().ToString("O", CultureInfo.InvariantCulture);
        fields["sessionId"] = SessionId;
        fields["appVersion"] = ApplicationDiagnostics.ApplicationVersion;
        fields["buildId"] = typeof(ApplicationDiagnostics).Module.ModuleVersionId.ToString("N");
        fields["processId"] = Environment.ProcessId;
        fields["event"] = eventName;
        fields["severity"] = severity;
        return fields;
    }

    private void Enqueue(Dictionary<string, object?> entry)
    {
        if (!queue.Writer.TryWrite(new(entry))) Interlocked.Increment(ref dropped);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (Pending pending in queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                DrainFatal();
                if (pending.Heartbeat)
                {
                    TryIo(() => UpdateMarker("running"));
                    TryIo(() => WriteEntry(NewEntry("session.heartbeat", "information")));
                }
                if (pending.Entry is { } entry) TryIo(() => WriteEntry(entry));
                DrainFatal();
                if (pending.Barrier is { } barrier) barrier.TrySetResult(true);
            }
            DrainFatal();
            Dictionary<string, object?>? exit;
            lock (lifecycleGate) { exit = pendingExit; pendingExit = null; }
            if (exit != null)
            {
                bool written = false;
                TryIo(() => { WriteEntry(exit); written = true; });
                // Preserve the marker if the exit record could not be persisted.
                if (written && Volatile.Read(ref fatalObserved) == 0)
                {
                    TryIo(() => UpdateMarker("normal"));
                    TryIo(() => File.Delete(markerPath));
                    ReleaseLease();
                }
            }
        }
        catch { Interlocked.Increment(ref writeFailures); }
    }

    private void DrainFatal()
    {
        Dictionary<string, object?>? fatal;
        lock (lifecycleGate) { fatal = pendingFatal; pendingFatal = null; }
        if (fatal == null) return;
        // Persist fatal state independently: even an append failure leaves evidence in the marker.
        TryIo(() => UpdateMarker("running"));
        TryIo(() => WriteEntry(fatal));
    }

    private void WriteEntry(Dictionary<string, object?> entry)
    {
        options.BeforeWrite?.Invoke();
        if (entry["severity"] is "fatal") TryIo(() => UpdateMarker("running"));
        long lost = Interlocked.Exchange(ref dropped, 0);
        long failures = Interlocked.Exchange(ref writeFailures, 0);
        if (lost > 0) entry["droppedEvents"] = lost;
        if (failures > 0) entry["priorWriteFailures"] = failures;
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, Json) + "\n");
        // Use each event's local date, even if it waited in the queue across midnight.
        string day = ((string)entry["timestamp"]!)[..10];
        if (currentDay != day) { currentDay = day; part = 0; }
        Directory.CreateDirectory(options.DirectoryPath);
        string path = Path.Combine(options.DirectoryPath, $"{day}-{SessionId}-{part:D4}.jsonl");
        // Priority fatal events can cross midnight ahead of queued older events. Returning to
        // that date can encounter several already-full parts, so recheck every candidate.
        // Bound probes even if files were externally precreated; failed writes stay fail-safe.
        for (int probes = 0; File.Exists(path) && new FileInfo(path).Length + bytes.Length > Math.Max(1024, options.MaxFileBytes); probes++)
        {
            if (probes >= 255) throw new IOException("No available daily log part within the bounded probe limit.");
            part++;
            path = Path.Combine(options.DirectoryPath, $"{day}-{SessionId}-{part:D4}.jsonl");
        }
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            stream.Write(bytes);
            // OS flush, including fatal records. No UI thread performs disk writes.
            stream.Flush(entry["severity"] is "fatal");
        }
        // Reserve a full part on creation so its later growth cannot exceed the total budget.
        if (new FileInfo(path).Length == bytes.Length) Prune(path);
        // Retain the most recent failure code even if later writes succeed: marker/retention
        // failures are support-relevant too and must not be hidden by a successful append.
    }

    private void UpdateMarker(string state)
    {
        if (lease == null) return;
        string temporary = markerPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Marker(SessionId, Environment.ProcessId,
            startedAt, options.Now(), state, Volatile.Read(ref fatalObserved) != 0), Json), new UTF8Encoding(false));
        File.Move(temporary, markerPath, true);
    }

    private void RecoverPreviousSessions()
    {
        TryIo(() =>
        {
            foreach (string path in Directory.EnumerateFiles(options.DirectoryPath, "*.session.json").Take(256))
            {
                if (path == markerPath || IsLink(path)) continue;
                string id = Path.GetFileName(path)[..^13];
                if (!Guid.TryParseExact(id, "N", out _)) continue;
                string priorLock = Path.Combine(options.DirectoryPath, id + ".lock");
                // A process keeps its exclusive writer lease throughout the run. Unlike PID checks,
                // this also handles PID reuse and never classifies a live concurrent instance as crashed.
                try
                {
                    using (var claim = new FileStream(priorLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                        if (new FileInfo(path).Length > 16 * 1024) continue;
                        Marker? marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(path), Json);
                        if (marker == null || marker.SessionId != id) continue;
                        if (marker.State != "normal")
                            Enqueue(NewEntry("session.previous_abnormal_shutdown", "warning", new()
                            {
                                ["previousSessionId"] = id,
                                ["previousStartedAt"] = marker.StartedAt.ToString("O"),
                                ["lastSeen"] = marker.LastSeen.ToString("O"),
                                ["detectedAt"] = options.Now().ToString("O"),
                                ["terminationTime"] = "unknown",
                                ["cause"] = "unknown",
                                ["fatalExceptionObserved"] = marker.FatalObserved
                            }));
                        File.Delete(path);
                    }
                    File.Delete(priorLock);
                }
                catch (IOException) { /* Live process, competing recovery or unavailable marker: preserve it. */ }
                catch (UnauthorizedAccessException) { }
                catch (JsonException) { }
            }
        });
    }

    private void Prune() => Prune(null);

    private void Prune(string? growingPath)
    {
        FileInfo[] files = DailyFiles(options.DirectoryPath).OrderByDescending(file => file.LastWriteTimeUtc).ThenByDescending(file => file.Name, StringComparer.Ordinal).ToArray();
        string cutoff = options.Now().AddDays(-Math.Max(1, options.RetentionDays)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        long total = 0;
        int count = 0;
        foreach (FileInfo file in files)
        {
            total += file.FullName == growingPath ? Math.Max(file.Length, Math.Max(1024, options.MaxFileBytes)) : file.Length;
            count++;
            if (string.CompareOrdinal(file.Name[..10], cutoff) < 0 || count > options.MaxFiles || total > options.MaxTotalBytes)
                TryIo(file.Delete);
        }
    }

    internal static string ExportArchive(string directory, string destinationPath, int recentDays, DateTimeOffset now)
    {
        string destination = Path.GetFullPath(destinationPath);
        if (!string.Equals(Path.GetExtension(destination), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Support archives must use the .zip extension.", nameof(destinationPath));
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            {
                string cutoff = now.AddDays(-Math.Clamp(recentDays, 1, 14) + 1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                long remaining = 64L * 1024 * 1024;
                int count = 0;
                foreach (FileInfo file in DailyFiles(directory).Where(file => string.CompareOrdinal(file.Name[..10], cutoff) >= 0)
                    .OrderByDescending(file => file.Name, StringComparer.Ordinal).Take(128))
                {
                    if (remaining <= 0) break;
                    try
                    {
                        using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        int length = (int)Math.Min(Math.Min(input.Length, 2 * 1024 * 1024), remaining);
                        byte[] snapshot = new byte[length];
                        int read = 0;
                        while (read < length)
                        {
                            int received = input.Read(snapshot, read, length - read);
                            if (received == 0) break;
                            read += received;
                        }
                        // An active writer may be mid-record: export complete lines only.
                        while (read > 0 && snapshot[read - 1] != (byte)'\n') read--;
                        if (read == 0) continue;
                        using Stream target = archive.CreateEntry("daily/" + file.Name, CompressionLevel.Fastest).Open();
                        target.Write(snapshot, 0, read);
                        remaining -= read;
                        count++;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                using var metadata = new StreamWriter(archive.CreateEntry("support-metadata.json").Open(), new UTF8Encoding(false));
                metadata.Write(JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, exportedAt = now.ToString("O"), logFiles = count,
                    scope = "Daily operational metadata only. Session identity and build metadata are in session.start records.",
                    abnormalShutdown = "LastSeen is an observation, not an exact termination time. Cause and time may be unknown."
                }, Json));
            }
            File.Move(temporary, destination, true);
            return destination;
        }
        catch
        {
            try { File.Delete(temporary); } catch { }
            throw;
        }
    }

    private static IEnumerable<FileInfo> DailyFiles(string directory) => Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory, "*.jsonl").Where(path => LogName.IsMatch(Path.GetFileName(path)) && !IsLink(path)).Select(path => new FileInfo(path))
        : [];
    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    // Metadata tokens are developer-controlled identifiers, never arbitrary text. Invalid values are omitted whole.
    private static string Token(string? value) => value is { Length: > 0 and <= 120 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '+' or '`') ? value : "omitted";
    private static string? OptionalToken(string? value) => value == null ? null : Token(value);
    private static string SafeType(Type? type) => Token(type?.FullName);
    private void TryIo(Action action)
    {
        try { action(); }
        catch { Interlocked.Increment(ref writeFailures); Volatile.Write(ref lastWriteError, "log_unavailable"); }
    }
}
