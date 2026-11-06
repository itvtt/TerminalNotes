using System.Windows;
using System.Windows.Threading;
using TerminalNotes.Services;

internal static class Program
{
    [STAThread]
    public static int Main()
    {
        int exitCode = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                await DemonstrateInlineProbeBlocksDispatcherAsync(app.Dispatcher);
                Console.WriteLine("PASS delayed inline filesystem probe blocks queued UI progress");
                await VerifyBackgroundProbeAsync(app.Dispatcher);
                Console.WriteLine("PASS delayed background probes keep dispatcher responsive, coalesce refreshes, and retain conservative status");
            }
            catch (Exception exception)
            {
                exitCode = 1;
                Console.Error.WriteLine("FAIL " + exception.Message);
            }
            finally { app.Shutdown(); }
        };
        app.Run();
        return exitCode;
    }

    private static async Task DemonstrateInlineProbeBlocksDispatcherAsync(Dispatcher dispatcher)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await Task.Run(async () =>
        {
            var blockedRender = dispatcher.InvokeAsync(() =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Probe was not released.");
            });
            try
            {
                if (!entered.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Probe did not enter the UI thread.");
                var progress = dispatcher.InvokeAsync(() => { }, DispatcherPriority.Send);
                await Task.Delay(100);
                if (progress.Task.IsCompleted) throw new InvalidOperationException("Queued progress unexpectedly passed the inline probe.");
                release.Set();
                await progress.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await blockedRender.Task;
            }
            finally { release.Set(); }
        });
    }

    private static async Task VerifyBackgroundProbeAsync(Dispatcher dispatcher)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int calls = 0;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var cache = new ExternalTerminalStatusCache(project =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Background probe was not released.");
            return project == "active";
        }, () => now);
        if (cache.Read("idle") != ExternalTerminalStatus.Unknown) throw new InvalidOperationException("Unknown project was trusted.");
        cache.RequestRefresh(["idle", "active"]);
        try
        {
            await Task.Run(() =>
            {
                if (!entered.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Background probe did not start.");
            });
            await Task.Run(async () =>
            {
                await dispatcher.InvokeAsync(() =>
                {
                    for (int tick = 0; tick < 100; tick++)
                    {
                        cache.RequestRefresh(["idle", "active"]);
                        if (cache.Read("idle") != ExternalTerminalStatus.Unknown) throw new InvalidOperationException("Pending probe was trusted.");
                    }
                }).Task.WaitAsync(TimeSpan.FromSeconds(1));
            });
            if (calls != 1) throw new InvalidOperationException("Delayed refreshes spawned extra probes.");
            release.Set();
            await WaitUntilAsync(() => !cache.IsRefreshing);
            if (calls != 2 || cache.Read("idle") != ExternalTerminalStatus.Inactive || cache.Read("active") != ExternalTerminalStatus.Active)
                throw new InvalidOperationException("Exact probe observations were not retained.");
            now = now.AddSeconds(6);
            if (cache.Read("idle") != ExternalTerminalStatus.Unknown) throw new InvalidOperationException("Stale inactive lease was trusted.");
            using var failed = new ExternalTerminalStatusCache(_ => throw new System.IO.IOException("fixture failure"));
            failed.RequestRefresh(["idle"]);
            await WaitUntilAsync(() => !failed.IsRefreshing);
            if (failed.Read("idle") != ExternalTerminalStatus.Unknown) throw new InvalidOperationException("Failed probe was trusted.");
            cache.Dispose();
            cache.RequestRefresh(["idle"]);
            if (cache.IsRefreshing || cache.Read("active") != ExternalTerminalStatus.Unknown) throw new InvalidOperationException("Disposed cache accepted work.");
            using var disposalEntered = new ManualResetEventSlim();
            using var disposalRelease = new ManualResetEventSlim();
            int disposalCalls = 0;
            using var disposing = new ExternalTerminalStatusCache(_ =>
            {
                Interlocked.Increment(ref disposalCalls);
                disposalEntered.Set();
                if (!disposalRelease.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Disposal fixture was not released.");
                return false;
            });
            disposing.RequestRefresh(["first", "second"]);
            try
            {
                await Task.Run(() =>
                {
                    if (!disposalEntered.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Disposal fixture did not start.");
                });
                disposing.Dispose();
                disposalRelease.Set();
                await WaitUntilAsync(() => !disposing.IsRefreshing);
                if (disposalCalls != 1 || disposing.Read("first") != ExternalTerminalStatus.Unknown)
                    throw new InvalidOperationException("Disposed cache continued probing or published a result.");
            }
            finally { disposalRelease.Set(); }
        }
        finally { release.Set(); }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
