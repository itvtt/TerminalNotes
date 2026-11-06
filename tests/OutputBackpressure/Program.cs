using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using TerminalNotes.Services;

internal static class Program
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static readonly MethodInfo Queue = typeof(ConPtySession).GetMethod("QueueOutput", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static async Task Main()
    {
        await SlowConsumerIsLossless();
        await ShutdownAndCancellationWakeProducer();
        await FailedConsumerDoesNotStallDrainage();
        CompareHealthyHandoff();
        await CompareProducerConsumer();
        Console.WriteLine("PASS output backpressure: bounded UTF-16 characters, lossless FIFO, full-only waiting, cancellation, nonblocking shutdown, callback failure; no native processes.");
    }
    private static async Task SlowConsumerIsLossless()
    {
        var buffer = new TerminalOutputBuffer(256);
        var expected = Enumerable.Range(0, 200).Select(i => $"\u001b[32m{i:D4} 한글🙂\u001b[0m\r\n").ToArray();
        var actual = new List<string>();
        buffer.Enqueue(new string('x', 256));
        var blocked = Task.Run(() => buffer.Enqueue(expected[0]));
        await Task.Delay(40);
        Check(!blocked.IsCompleted && buffer.BufferedCharacters == 256, "Full buffer did not backpressure producer.");
        buffer.TryDequeue(out _);
        Check(await blocked.WaitAsync(Limit), "Capacity release did not wake producer.");
        var producer = Task.Run(() =>
        {
            foreach (string text in expected.Skip(1))
            {
                Check(buffer.Enqueue(text), "Open output was rejected.");
                Check(buffer.BufferedCharacters <= 256, "Buffered character budget exceeded.");
            }
        });
        while (!producer.IsCompleted || !buffer.IsEmpty)
        {
            if (buffer.TryDequeue(out string? text)) actual.Add(text!);
            await Task.Delay(1);
        }
        await producer.WaitAsync(Limit);
        Check(expected.SequenceEqual(actual), "Slow consumer lost/reordered ANSI or Unicode output.");
    }
    private static async Task ShutdownAndCancellationWakeProducer()
    {
        var buffer = new TerminalOutputBuffer(8);
        buffer.Enqueue("12345678");
        using var cancellation = new CancellationTokenSource();
        var waiting = Task.Run(() => buffer.Enqueue("x", cancellation.Token));
        await Task.Delay(25);
        cancellation.Cancel();
        try { await waiting.WaitAsync(Limit); throw new Exception("Full writer ignored cancellation."); }
        catch (OperationCanceledException) { }
        var closing = Task.Run(() => buffer.Enqueue("x"));
        await Task.Delay(25);
        var watch = Stopwatch.StartNew(); buffer.Complete();
        Check(watch.Elapsed < TimeSpan.FromMilliseconds(100), "Shutdown waited for the producer.");
        Check(!await closing.WaitAsync(Limit) && buffer.IsEmpty && !buffer.Enqueue("after close"[..8]), "Shutdown did not wake/reject output.");

        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        var session = new ConPtySession();
        session.OutputReceived += _ => { callbackEntered.Set(); releaseCallback.Wait(Limit); };
        Queue.Invoke(session, ["initial"]);
        Check(callbackEntered.Wait(Limit), "Callback did not run off the producer.");
        string chunk = new('x', 8192);
        var reader = Task.Run(() => { for (int i = 0; i < 256; i++) Queue.Invoke(session, [chunk]); });
        var sessionBuffer = (TerminalOutputBuffer)typeof(ConPtySession).GetField("_pendingOutput", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        await Until(() => sessionBuffer.BufferedCharacters == 1024 * 1024);
        Check(!reader.IsCompleted, "Session backlog was unbounded while its callback stalled.");
        try
        {
            await session.CloseAsync().WaitAsync(Limit);
            await reader.WaitAsync(Limit);
            Check(sessionBuffer.IsEmpty, "Close retained output after waking the native reader.");
        }
        finally { releaseCallback.Set(); }
    }
    private static async Task FailedConsumerDoesNotStallDrainage()
    {
        var received = new ConcurrentQueue<string>();
        var session = new ConPtySession();
        int calls = 0;
        session.OutputReceived += text =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("synthetic consumer failure");
            received.Enqueue(text);
        };
        for (int i = 0; i < 500; i++) Queue.Invoke(session, [i.ToString()]);
        await Until(() => received.Count == 499);
        Check(received.SequenceEqual(Enumerable.Range(1, 499).Select(i => i.ToString())), "Callback failure stalled/reordered later output.");
        await session.CloseAsync().WaitAsync(Limit);
    }
    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed > Limit) throw new TimeoutException(); await Task.Delay(2); }
    }
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void CompareHealthyHandoff()
    {
        const int count = 250_000;
        string chunk = new('x', 8192);
        var baseline = new ConcurrentQueue<string>();
        var bounded = new TerminalOutputBuffer();
        for (int i = 0; i < 1000; i++) { baseline.Enqueue(chunk); baseline.TryDequeue(out _); bounded.Enqueue(chunk); bounded.TryDequeue(out _); }
        (double Ms, long Bytes) Run(Action handoff)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < count; i++) handoff();
            watch.Stop();
            return (watch.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        Action oldHandoff = () => { baseline.Enqueue(chunk); baseline.TryDequeue(out _); };
        Action newHandoff = () => { bounded.Enqueue(chunk); bounded.TryDequeue(out _); };
        Run(oldHandoff); Run(newHandoff);
        var old = new List<(double Ms, long Bytes)>();
        var current = new List<(double Ms, long Bytes)>();
        for (int round = 0; round < 7; round++)
        {
            if ((round & 1) == 0) { old.Add(Run(oldHandoff)); current.Add(Run(newHandoff)); }
            else { current.Add(Run(newHandoff)); old.Add(Run(oldHandoff)); }
        }
        Console.WriteLine($"Synthetic healthy handoff {count:N0} x 8192 chars, median7: ConcurrentQueue {old.OrderBy(x => x.Ms).ElementAt(3).Ms:0.00} ms/{old.Min(x => x.Bytes)} bytes; bounded {current.OrderBy(x => x.Ms).ElementAt(3).Ms:0.00} ms/{current.Min(x => x.Bytes)} bytes; no full-buffer waits.");
    }
    private static async Task CompareProducerConsumer()
    {
        // The complete workload fits the budget, so producer and consumer perform
        // real character scanning concurrently without full-buffer backpressure.
        const int count = 4096;
        string chunk = string.Concat(Enumerable.Repeat("\u001b[32m한글 output 0123456789\u001b[0m\r\n", 4));
        Check(count * chunk.Length < 1024 * 1024, "Healthy benchmark unexpectedly exceeds capacity.");
        async Task<double> Run(bool bounded)
        {
            var old = new ConcurrentQueue<string>();
            var current = new TerminalOutputBuffer();
            int finished = 0, consumed = 0;
            long checksum = 0;
            var watch = Stopwatch.StartNew();
            var producer = Task.Run(() =>
            {
                for (int i = 0; i < count; i++) { if (bounded) current.Enqueue(chunk); else old.Enqueue(chunk); }
                Volatile.Write(ref finished, 1);
            });
            var consumer = Task.Run(() =>
            {
                while (Volatile.Read(ref finished) == 0 || consumed < count)
                {
                    string? text;
                    bool available = bounded ? current.TryDequeue(out text) : old.TryDequeue(out text);
                    if (!available) { Thread.Yield(); continue; }
                    foreach (char character in text!) checksum += character;
                    consumed++;
                }
            });
            await Task.WhenAll(producer, consumer).WaitAsync(Limit);
            watch.Stop();
            Check(consumed == count && checksum > 0, "Concurrent benchmark lost output.");
            return watch.Elapsed.TotalMilliseconds;
        }
        await Run(false); await Run(true);
        var oldTimes = new List<double>(); var newTimes = new List<double>();
        for (int round = 0; round < 7; round++)
        {
            if ((round & 1) == 0) { oldTimes.Add(await Run(false)); newTimes.Add(await Run(true)); }
            else { newTimes.Add(await Run(true)); oldTimes.Add(await Run(false)); }
        }
        Console.WriteLine($"Concurrent producer/consumer ANSI+Unicode scan {count:N0} x {chunk.Length} chars, median7: ConcurrentQueue {oldTimes.Order().ElementAt(3):0.00} ms; bounded {newTimes.Order().ElementAt(3):0.00} ms; no sleeps/full-buffer waits.");
    }
}
