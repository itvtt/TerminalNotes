using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TerminalNotes.Services;

internal static class Program
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "terminalnotes-resource-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UtcNow.AddMinutes(-10);
    private static int _file;

    private static async Task<int> Main()
    {
        Directory.CreateDirectory(Root);
        try
        {
            await Ownership();
            await Bounds();
            MetadataCache();
            await Heaviest();
            await MemoryGuard();
            await LifecycleAndRotation();
            await WindowsSmoke();
            Console.WriteLine("RESOURCE DIAGNOSTICS TESTS PASSED");
            return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + error); return 1; }
        finally { Directory.Delete(Root, recursive: true); }
    }

    private static async Task Ownership()
    {
        var source = new FakeSource();
        source.Edges = [new(1, 0), new(2, 1), new(3, 2), new(4, 1), new(5, 1), new(6, 5), new(7, 1), new(10, 1), new(99, 98)];
        source.Samples[1] = Sample(1, Epoch, 10, 11) with { Name = @"C:\private\app.exe", Description = @"C:\private\secret", ProductName = "App\nProduct" };
        source.Samples[2] = Sample(2, Epoch.AddMinutes(1), 20, 22) with { Description = "Bun", ProductName = "Bun" };
        source.Samples[3] = Sample(3, Epoch.AddSeconds(30), 300, 300); // Old child of a reused parent PID.
        source.Samples[4] = Sample(4, source.Captured.AddSeconds(1), 400, 400); // Child PID reused after graph capture.
        source.Samples[6] = Sample(6, Epoch.AddMinutes(2), 600, 600); // Inaccessible intermediate 5 must stop traversal.
        source.Samples[7] = Sample(7, Epoch, 700, 700) with { StartedAtUtc = null };
        source.Samples[10] = Sample(10, Epoch.AddMinutes(2), 30, null);
        var (monitor, snapshot) = await First(source);
        try
        {
            Assert(snapshot.ProcessCount == 3 && snapshot.UnreadableProcessCount == 5 && snapshot.IsPartial, "PID reuse/exits/inaccessible timestamps remain partial");
            Assert(snapshot.TreePrivateBytes == 60 && snapshot.TreeWorkingSetBytes == 33, "totals include only trustworthy, available counters");
            Assert(!source.ReadIds.Contains(6) && !source.ReadIds.Contains(99), "no inspection below unknown parents or unrelated processes");
            Assert(snapshot.Processes[0].Name == "app.exe" && snapshot.Processes[0].Description is null && snapshot.Processes[0].ProductName == "AppProduct", "metadata excludes paths and controls");
            Assert(snapshot.Processes[1].PrivateBytes == 30 && snapshot.Processes.Single(row => row.ProcessId == 2).Description == "Bun", "rows sort by private memory and retain runtime display alias");
            source.Samples[1] = Sample(1, Epoch.AddMinutes(3), 1000, 1000);
            var reused = monitor.Capture(source, CancellationToken.None);
            Assert(reused.ProcessCount == 0 && reused.TreePrivateBytes is null && reused.UnreadableProcessCount == 1 && reused.IsPartial,
                "root PID reuse cannot attribute a replacement process to this app");
            Console.WriteLine("PASS ownership, PID reuse, exits, nullable metrics, aliases, and privacy");
        }
        finally { monitor.Dispose(); }
    }

    // A pressure notice names what under the app uses the memory: the agent's Python processes counted together, then the rest.
    // A program under the tasks' memory budget that asks for more than one program may use is refused (its own out-of-memory
    // error) while smaller allocations work, and the guard names it.
    private static async Task MemoryGuard()
    {
        AppContext.SetSwitch("TerminalNotes.TaskMemoryGuard", true);
        AppContext.SetData("TerminalNotes.TaskMemoryGuard.ProcessLimitMB", "300");
        var hits = new ConcurrentQueue<TaskMemoryGuard.LimitHit>();
        TaskMemoryGuard.LimitReached += hits.Enqueue;
        if (TaskMemoryGuard.Job == IntPtr.Zero && TaskMemoryGuard.Unavailable == "app_in_job")
        {
            // `dotnet run` keeps the test in a job its children cannot leave; the app started normally is in none.
            Console.WriteLine("SKIP memory guard: this run is inside a job that keeps its children (dotnet run); run the test exe directly");
            TaskMemoryGuard.LimitReached -= hits.Enqueue;
            return;
        }
        Assert(TaskMemoryGuard.Job != IntPtr.Zero && TaskMemoryGuard.ProcessLimit == 300L * 1024 * 1024 && TaskMemoryGuard.JobLimit >= 256L * 1024 * 1024,
            $"The memory guard did not start ({TaskMemoryGuard.Unavailable}): process {TaskMemoryGuard.ProcessLimit}, all {TaskMemoryGuard.JobLimit}.");
        // Started the way the app starts a task's OpenCode server (its own job nested in the budget's), in a task folder.
        string task = Path.Combine(Root, "memory-task");
        Directory.CreateDirectory(task);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = task
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
            "$small = [byte[]]::new(100MB); 'small ok'; try { $big = [byte[]]::new(600MB); 'big allocated' } catch { 'big refused' }" })
            start.ArgumentList.Add(argument);
        var output = new StringBuilder();
        using (var program = OwnedWindowsProcess.Start(start, line => { lock (output) output.AppendLine(line); }))
        {
            await program.Process.WaitForExitAsync();
            await program.OutputDrained;
            for (int wait = 0; wait < 50 && hits.IsEmpty; wait++) await Task.Delay(100);
            string printed;
            lock (output) printed = output.ToString();
            Assert(printed.Contains("small ok", StringComparison.Ordinal) && printed.Contains("big refused", StringComparison.Ordinal),
                "The per-program memory cap did not refuse the large allocation: " + printed.Trim());
            Assert(hits.TryDequeue(out var hit) && hit is { OneProgram: true, Program: "powershell.exe", Task: "memory-task", LimitBytes: 300L * 1024 * 1024 } &&
                hit.ProcessId == program.Process.Id, "The memory guard did not name the refused program and its task: " + (hit?.ToString() ?? "no notice"));
        }
        TaskMemoryGuard.LimitReached -= hits.Enqueue;
        Console.WriteLine($"PASS memory guard refuses one program past its cap and names it (budget for all tasks now {TaskMemoryGuard.JobLimit / (1024 * 1024)} MB)");
    }

    private static async Task Heaviest()
    {
        const long MiB = 1024 * 1024;
        var source = new FakeSource();
        source.Edges = [new(1, 0), new(2, 1), new(3, 2), new(4, 2), new(5, 3), new(6, 1), new(7, 1)];
        source.Samples[1] = Sample(1, Epoch, 150 * MiB, 150 * MiB) with { Name = "TerminalNotes.exe" };
        source.Samples[2] = Sample(2, Epoch.AddMinutes(1), 300 * MiB, 300 * MiB) with { Name = "opencode.exe" };
        foreach (int id in new[] { 3, 4, 5 })
            source.Samples[id] = Sample(id, Epoch.AddMinutes(id), 700 * MiB, 700 * MiB) with { Name = @"C:\Users\someone\AppData\Local\Programs\Python\python.exe" };
        source.Samples[6] = Sample(6, Epoch.AddMinutes(2), 120 * MiB, 120 * MiB) with { Name = "msedgewebview2.exe" };
        source.Samples[7] = Sample(7, Epoch.AddMinutes(2), 5 * MiB, 5 * MiB) with { Name = "conhost.exe" };
        var (monitor, _) = await First(source);
        try
        {
            // Kept in memory for the notices, not in the log (its rows already name the heaviest processes).
            var snapshot = monitor.Capture(source, CancellationToken.None);
            Assert(snapshot.Heaviest.Select(group => $"{group.Name}×{group.Count}:{group.PrivateBytes / MiB}")
                    .SequenceEqual(["python.exe×3:2100", "opencode.exe×1:300", "msedgewebview2.exe×1:120"]),
                "the three heaviest program names under the app, the app itself left out: " + string.Join(", ", snapshot.Heaviest));
            Assert(TerminalResourcePressure.DescribeHeaviest(snapshot.Heaviest) == "많이 쓰는 프로그램: python.exe 3개 2.1GB · opencode.exe 1개 300MB · msedgewebview2.exe 1개 120MB",
                "the notice line: " + TerminalResourcePressure.DescribeHeaviest(snapshot.Heaviest));
            Assert(TerminalResourcePressure.DescribeHeaviest([]) is null, "no line without programs under the app");
            Console.WriteLine("PASS heaviest programs under the app for pressure notices");
        }
        finally { monitor.Dispose(); }
    }

    private static async Task Bounds()
    {
        var source = new FakeSource();
        source.Edges = Enumerable.Range(1, 201).Select(id => new ResourceProcessEdge(id, id == 1 ? 0 : 1)).ToArray();
        string unicode = string.Concat(Enumerable.Repeat("\U0001f9ea", 300));
        for (int id = 1; id <= 201; id++) source.Samples[id] = Sample(id, Epoch.AddSeconds(id), id, id) with
        { Name = unicode, Description = unicode, ProductName = unicode };
        var (monitor, snapshot) = await First(source);
        try
        {
            Assert(snapshot.ProcessCount == 129 && source.ReadIds.Count == 129 && snapshot.IsPartial, "descendant inspection capped at 128");
            Assert(snapshot.Processes.Count == 32 && snapshot.Processes[0].IsApp && snapshot.Processes[1].ProcessId == 129, "app plus top31 rows, sorted by private memory");
            Assert(snapshot.Processes.All(row => (row.Description?.EnumerateRunes().Count() ?? 0) <= 96), "metadata limited by Unicode rune");
            byte[] serialized = ResourceDiagnosticsMonitor.SerializeBounded(snapshot);
            Assert(serialized.Length + 1 <= 32768, "serialized Unicode-heavy JSON line capped at32KiB");
            using var json = JsonDocument.Parse(serialized);
            Assert(json.RootElement.GetProperty("event").GetString() == "resource_snapshot", "bounded serialization preserves valid schema");
            Assert(json.RootElement.GetProperty("commitTotalBytes").GetUInt64() == 31000 &&
                json.RootElement.GetProperty("commitLimitBytes").GetUInt64() == 32000 &&
                json.RootElement.GetProperty("commitHeadroomBytes").GetUInt64() == 1000,
                "bounded diagnostics preserve system commit and headroom metadata");
        }
        finally { monitor.Dispose(); }

        source = new FakeSource();
        source.Edges = Enumerable.Range(1, 25).Select(id => new ResourceProcessEdge(id, id - 1)).ToArray();
        for (int id = 1; id <= 25; id++) source.Samples[id] = Sample(id, Epoch.AddSeconds(id), id, id);
        (monitor, snapshot) = await First(source);
        try { Assert(snapshot.ProcessCount == 17 && snapshot.IsPartial && !source.ReadIds.Contains(18), "ancestry depth capped at16"); }
        finally { monitor.Dispose(); }
        Console.WriteLine("PASS process/depth/row/rune/serialized-byte bounds");
    }

    private static async Task LifecycleAndRotation()
    {
        var source = Basic();
        source.Gate = new ManualResetEventSlim(false);
        string path = NextPath();
        int callerThread = Environment.CurrentManagedThreadId;
        var monitor = new ResourceDiagnosticsMonitor(path, source, 1, TimeSpan.FromMilliseconds(5));
        await Until(() => source.ReadIds.Count > 0);
        await Task.Delay(80);
        Assert(source.GraphCalls == 1 && source.MaximumConcurrentReads == 1 && source.SourceThread != callerThread, "immediate single off-thread capture cannot overlap while blocked");
        var timer = Stopwatch.StartNew();
        monitor.Dispose();
        Assert(timer.Elapsed < TimeSpan.FromMilliseconds(200), "dispose never waits for a filesystem/process probe");
        source.Gate.Set();
        await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(!File.Exists(path) && source.GraphCalls == 1, "cancellation suppresses publication and future capture");
        source.Gate.Dispose();

        source = Basic();
        path = NextPath();
        monitor = new ResourceDiagnosticsMonitor(path, source, 1, TimeSpan.FromMilliseconds(5), maximumLogBytes: 4096);
        await Until(() => source.GraphCalls >= 15 && File.Exists(path + ".1"));
        monitor.Dispose();
        await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(new FileInfo(path).Length <= 4096 && new FileInfo(path + ".1").Length <= 4096 && !File.Exists(path + ".2"), "rotation retains only bounded current and one backup");
        foreach (string file in new[] { path, path + ".1" })
        foreach (string line in File.ReadLines(file))
        {
            using var json = JsonDocument.Parse(line);
            Assert(Encoding.UTF8.GetByteCount(line) + 1 <= 32768, "rotated files contain complete bounded JSON lines");
        }
        Console.WriteLine("PASS immediate off-thread/no-overlap sampling, nonblocking cancellation, and rotation");
    }

    private static void MetadataCache()
    {
        int loads = 0;
        var source = new WindowsResourceDiagnosticsSource(_ =>
        {
            loads++;
            return (new string('a', 300), @"C:\secret\product");
        });
        for (int id = 0; id < 129; id++) source.ReadMetadata("fixture-" + id);
        Assert(source.MetadataCacheCount == 128 && loads == 129, "metadata cache evicts at128 entries");
        var metadata = source.ReadMetadata("fixture-128");
        Assert(loads == 129 && metadata.Description?.Length == 96 && metadata.Product is null, "cached aliases bounded and sanitized without re-reading");
        source.ReadMetadata("fixture-0");
        Assert(loads == 130 && source.MetadataCacheCount == 128, "evicted metadata refreshes within fixed capacity");
        Console.WriteLine("PASS bounded sanitized PE metadata cache and reuse/eviction");
    }

    private static async Task WindowsSmoke()
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = new WindowsResourceDiagnosticsSource();
        var graph = source.ReadProcessGraph(CancellationToken.None);
        var sample = source.ReadProcess(Environment.ProcessId, CancellationToken.None);
        var memory = source.ReadSystemMemory();
        Assert(graph.Edges.Any(edge => edge.ProcessId == Environment.ProcessId) && sample?.StartedAtUtc is not null,
            "real Toolhelp snapshot and current-process creation time are readable");
        Assert(memory.LoadPercent is >= 0 and <= 100 && memory.TotalPhysicalBytes > 0 && sample?.PrivateBytes > 0,
            "real GlobalMemoryStatusEx/current-process counters are readable");
        Assert(memory.CommitLimitBytes > 0 && memory.CommitTotalBytes >= 0 && memory.CommitTotalBytes <= memory.CommitLimitBytes,
            "GetPerformanceInfo reports valid system commit bytes");
        string path = NextPath();
        using var monitor = new ResourceDiagnosticsMonitor(path);
        await Until(() => File.Exists(path) && new FileInfo(path).Length > 0);
        monitor.Dispose();
        await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        using var json = JsonDocument.Parse(File.ReadLines(path).First());
        Assert(json.RootElement.GetProperty("appProcessId").GetInt32() == Environment.ProcessId &&
            json.RootElement.GetProperty("processes")[0].GetProperty("isApp").GetBoolean(), "real collector logs only this isolated test app/tree");
        Assert(source.MetadataCacheCount <= 128, "metadata cache remains bounded");
        Console.WriteLine("PASS real Windows collector smoke (no CLI/model/app launch)");
    }

    private static ResourceProcessSample Sample(int id, DateTimeOffset started, long? privateBytes, long? working) =>
        new(id, started, "process" + id + ".exe", null, null, working, privateBytes);
    private static FakeSource Basic()
    {
        var source = new FakeSource { Edges = [new(1, 0)] };
        source.Samples[1] = Sample(1, Epoch, 10, 10);
        return source;
    }
    private static async Task<(ResourceDiagnosticsMonitor Monitor, ResourceDiagnosticSnapshot Snapshot)> First(FakeSource source)
    {
        string path = NextPath();
        var monitor = new ResourceDiagnosticsMonitor(path, source, 1, TimeSpan.FromHours(1));
        await Until(() => File.Exists(path) && new FileInfo(path).Length > 0);
        monitor.Dispose();
        await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        return (monitor, JsonSerializer.Deserialize<ResourceDiagnosticSnapshot>(File.ReadLines(path).First(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);
    }
    private static string NextPath() => Path.Combine(Root, Interlocked.Increment(ref _file) + ".jsonl");
    private static async Task Until(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("isolated resource test condition");
            await Task.Delay(10);
        }
    }
    private static void Assert(bool value, string description) { if (!value) throw new Exception(description); }

    private sealed class FakeSource : IResourceDiagnosticsSource
    {
        internal DateTimeOffset Captured = DateTimeOffset.UtcNow;
        internal IReadOnlyList<ResourceProcessEdge> Edges = [];
        internal Dictionary<int, ResourceProcessSample> Samples = [];
        internal ConcurrentBag<int> ReadIds = [];
        internal ManualResetEventSlim? Gate;
        internal int GraphCalls, SourceThread, MaximumConcurrentReads;
        private int _activeReads;
        public ResourceProcessGraph ReadProcessGraph(CancellationToken token)
        {
            Interlocked.Increment(ref GraphCalls);
            SourceThread = Environment.CurrentManagedThreadId;
            return new(Captured, Edges);
        }
        public ResourceProcessSample? ReadProcess(int processId, CancellationToken token)
        {
            int active = Interlocked.Increment(ref _activeReads);
            MaximumConcurrentReads = Math.Max(MaximumConcurrentReads, active);
            try
            {
                ReadIds.Add(processId);
                Gate?.Wait();
                return Samples.GetValueOrDefault(processId);
            }
            finally { Interlocked.Decrement(ref _activeReads); }
        }
        public ResourceSystemMemory ReadSystemMemory() => new(98, 16000, 320)
            { CommitTotalBytes = 31000, CommitLimitBytes = 32000 };
    }
}
