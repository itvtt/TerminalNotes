using System.Diagnostics;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using TerminalNotes.Controls;
using TerminalNotes.TerminalEngine;

internal static partial class Program
{
    private static async Task VerifyMultiPaneOutputAsync()
    {
        const int paneCount = 8, batches = 16;
        var grid = new UniformGrid { Rows = 2, Columns = 4 };
        var window = new Window { Width = 1280, Height = 640, Content = grid, ShowInTaskbar = false };
        var engines = new TerminalEmulator[paneCount];
        var expected = new TerminalEmulator[paneCount];
        var surfaces = new NativeTerminalSurface[paneCount];
        var parsed = new int[paneCount];
        var suppliers = new Action[paneCount];
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var elapsed = new Stopwatch();
        long allocatedBefore = 0;
        TimeSpan cpuBefore = TimeSpan.Zero;
        int completed = 0, inputAt = -1;
        double inputMilliseconds = -1;
        int inputHeartbeats = 0;
        double maximumHeartbeatMilliseconds = 0;
        using var heartbeatCancellation = new CancellationTokenSource();
        Task heartbeat = Task.CompletedTask;
        // Less than one drain's cap, but long enough for eight continuation
        // timers to overlap. Every pane produces a finite, independent stream.
        string row = "\x1b[32m한🙂\x1b[0m " + new string('x', 70) + "\r\n";
        string body = string.Concat(Enumerable.Repeat(row, 512));
        string Chunk(int pane, int batch) => $"PANE_{pane}_BATCH_{batch:D2}\r\n" + body;
        try
        {
            for (int pane = 0; pane < paneCount; pane++)
            {
                int identity = pane;
                engines[pane] = new TerminalEmulator(80, 24, 10000);
                expected[pane] = new TerminalEmulator(80, 24, 10000);
                surfaces[pane] = new NativeTerminalSurface(engines[pane]);
                grid.Children.Add(surfaces[pane]);
                for (int batch = 0; batch < batches; batch++) expected[pane].Feed(Chunk(pane, batch));
                suppliers[pane] = () =>
                {
                    if (++parsed[identity] < batches) surfaces[identity].Feed(Chunk(identity, parsed[identity]));
                    else if (++completed == paneCount) finished.TrySetResult();
                };
                engines[pane].Changed += suppliers[pane];
            }
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
            elapsed.Start();
            for (int pane = 0; pane < paneCount; pane++) surfaces[pane].Feed(Chunk(pane, 0));
            _ = window.Dispatcher.BeginInvoke(() =>
            {
                inputAt = parsed.Sum();
                inputMilliseconds = elapsed.Elapsed.TotalMilliseconds;
                input.TrySetResult();
            }, DispatcherPriority.Input);
            heartbeat = Task.Run(async () =>
            {
                try
                {
                    while (!heartbeatCancellation.IsCancellationRequested)
                    {
                        await Task.Delay(40, heartbeatCancellation.Token);
                        var wait = Stopwatch.StartNew();
                        await window.Dispatcher.InvokeAsync(() =>
                        {
                            if (completed == paneCount) return;
                            inputHeartbeats++;
                            maximumHeartbeatMilliseconds = Math.Max(maximumHeartbeatMilliseconds, wait.Elapsed.TotalMilliseconds);
                        }, DispatcherPriority.Input).Task.WaitAsync(heartbeatCancellation.Token);
                    }
                }
                catch (OperationCanceledException) when (heartbeatCancellation.IsCancellationRequested) { }
            });
            // The timeout runs on a pool thread, independently of the dispatcher
            // being tested. The producers are bounded even in the RED case.
            await Task.WhenAll(finished.Task, input.Task).WaitAsync(TimeSpan.FromSeconds(30));
            heartbeatCancellation.Cancel();
            await heartbeat;
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            TimeSpan cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
            Console.WriteLine($"MEASURED: eight visible panes: Input={inputMilliseconds:N0} ms at {inputAt}/{paneCount * batches} batches; output={elapsed.Elapsed.TotalMilliseconds:N0} ms.");
            Console.WriteLine($"MEASURED: eight visible panes: managed allocation={allocated / (1024d * 1024):N2} MiB; process CPU={cpu.TotalMilliseconds:N0} ms.");
            Console.WriteLine($"MEASURED: sustained Input heartbeats={inputHeartbeats}, maximum wait={maximumHeartbeatMilliseconds:N0} ms.");
            for (int pane = 0; pane < paneCount; pane++)
                Require(engines[pane].GetAllText() == expected[pane].GetAllText(), $"Pane {pane} lost or reordered ANSI/Unicode output.");
            Require(inputAt < paneCount * batches && inputMilliseconds < 500,
                $"Eight visible output streams starved Input: {inputMilliseconds:N0} ms, {inputAt}/{paneCount * batches} batches.");
            Require(inputHeartbeats >= 3 && maximumHeartbeatMilliseconds < 500,
                $"Input stopped responding during output: heartbeats={inputHeartbeats}, maximum wait={maximumHeartbeatMilliseconds:N0} ms.");
            Console.WriteLine("PASS: eight visible output streams preserve every pane's ordered transcript and service Input promptly.");
        }
        finally
        {
            heartbeatCancellation.Cancel();
            await heartbeat;
            for (int pane = 0; pane < paneCount; pane++)
            {
                if (engines[pane] is not null && suppliers[pane] is not null) engines[pane].Changed -= suppliers[pane];
                surfaces[pane]?.DisposeSurface();
            }
            window.Close();
        }
    }
}
