using System.Collections.Concurrent;
using System.Diagnostics;
using TerminalNotes.Services;

internal static class Program
{
    private const ulong GiB = 1024UL * 1024 * 1024;
    private static readonly StartupResourceMeasurement Safe = new(40, 16 * GiB, 8 * GiB, 30)
        { CommitTotalBytes = 8 * GiB, CommitLimitBytes = 32 * GiB };
    private static async Task Main()
    {
        await FifoAndSpacing();
        await PressureAndRecovery();
        await CommitAndElapsedRecovery();
        await MonitorPressureCarriesIntoNextRequest();
        await CancelledRecoveryDoesNotCountIdleTime();
        PressureMonitorTransitions();
        await LowMemoryAndMeasurement();
        await CancelAndDispose();
        await TailCancellationReclaimsCapacity();
        await CancelledPaneIsReplaceable();
        await SamplerDoesNotOverlap();
        await RunOncePreservesPressureAndOwnership();
        await RunOncePrioritizesOnlyItsTicket();
        await RunOnceRejectsStaleAndCancelledRequests();
        await RunOnceDoesNotBypassUnknownMeasurements();
        await PressureDelaysAtMostMaximumWait();
        PaneRunOnceRequiresOptIn();
        Console.WriteLine("PASS: FIFO, spacing, memory/CPU hysteresis, measurement retries, cancellation, tail capacity reclamation, cancelled-pane replacement, disposal, no overlapping sampling.");
        Console.WriteLine("PASS: run-once exact-ticket authorization, blocked-head priority, spacing/lease ownership, pressure hysteresis retained, stale/cancelled/disposed callbacks, unknown measurements, fresh-ticket isolation, pane opt-in.");
        Console.WriteLine("PASS: system commit headroom, ten-second monotonic recovery, rapid-sample rejection, unknown-commit reset, monitor transition deduplication, next-request pressure, fresh-pressure restart, and cancelled sampling gaps.");
    }
    private static TerminalStartupAdmission Gate(Func<CancellationToken, Task<StartupResourceMeasurement>> measure,
        int spacing = 0) => new(measure, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(spacing))
            { RecoveryDuration = TimeSpan.FromMilliseconds(30) };
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Eventually(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        { if (watch.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("Test condition timed out."); await Task.Delay(2); }
    }
    private static async Task FifoAndSpacing()
    {
        using var gate = Gate(_ => Task.FromResult(Safe), 60);
        IDisposable first = await gate.AcquireAsync(_ => { }, default).WaitAsync(TimeSpan.FromSeconds(3));
        var watch = Stopwatch.StartNew();
        var second = gate.AcquireAsync(_ => { }, default);
        var third = gate.AcquireAsync(_ => { }, default);
        await Task.Delay(25);
        Check(!second.IsCompleted && !third.IsCompleted, "Concurrent startup admitted before first release.");
        first.Dispose(); first.Dispose();
        IDisposable secondLease = await second.WaitAsync(TimeSpan.FromSeconds(3));
        Check(watch.ElapsedMilliseconds >= 50, "Minimum spacing was skipped.");
        Check(!third.IsCompleted, "FIFO third admitted with second lease held.");
        secondLease.Dispose();
        (await third.WaitAsync(TimeSpan.FromSeconds(3))).Dispose();
    }
    private static async Task PressureAndRecovery()
    {
        foreach (var high in new[] { Safe with { MemoryLoadPercent = 85 }, Safe with { AvailablePhysicalBytes = GiB - 1 }, Safe with { CpuLoadPercent = 90 } })
        {
            var source = new ControlledSource(high);
            using var gate = Gate(source.Sample);
            var waits = new ConcurrentQueue<StartupResourceWait>();
            var pending = gate.AcquireAsync(waits.Enqueue, default);
            await Eventually(() => waits.Any(w => w.Code is "memory" or "cpu"));
            source.Set(Safe with { MemoryLoadPercent = 81 });
            await Eventually(() => waits.Any(w => w.Code == "recovery"));
            Check(!pending.IsCompleted, "Recovery admitted above 80% memory.");
            source.HoldAfterOneSafeSample();
            await Eventually(() => waits.Any(w => w.Reason.Contains("안정 확인")));
            Check(!pending.IsCompleted, "One healthy recovery sample admitted startup.");
            source.Set(Safe);
            (await pending.WaitAsync(TimeSpan.FromSeconds(3))).Dispose();
        }
    }
    private static async Task LowMemoryAndMeasurement()
    {
        // 2GiB system uses 10%/15% free thresholds rather than impossible fixed free requirements.
        var source = new ControlledSource(Safe with { TotalPhysicalBytes = 2 * GiB, AvailablePhysicalBytes = GiB / 2, CpuLoadPercent = null });
        using var gate = Gate(source.Sample);
        var waits = new ConcurrentQueue<StartupResourceWait>();
        var pending = gate.AcquireAsync(waits.Enqueue, default);
        await Eventually(() => waits.Any(w => w.Code == "measurement_unavailable"));
        Check(!pending.IsCompleted, "Unknown CPU was silently treated as safe.");
        source.Set(Safe with { TotalPhysicalBytes = 2 * GiB, AvailablePhysicalBytes = GiB / 2, CpuLoadPercent = 20 });
        (await pending.WaitAsync(TimeSpan.FromSeconds(3))).Dispose();
        source.Set(Safe with { TotalPhysicalBytes = 2 * GiB, AvailablePhysicalBytes = GiB / 10, CpuLoadPercent = 20 });
        var blocked = gate.AcquireAsync(waits.Enqueue, default);
        await Eventually(() => waits.Any(w => w.Code == "memory"));
        source.Set(Safe with { TotalPhysicalBytes = 2 * GiB, AvailablePhysicalBytes = GiB / 3, CpuLoadPercent = 20 });
        (await blocked.WaitAsync(TimeSpan.FromSeconds(3))).Dispose();
        var failures = 0;
        using var failing = Gate(_ => Interlocked.Increment(ref failures) == 1
            ? Task.FromException<StartupResourceMeasurement>(new InvalidOperationException("private diagnostic must not be reported"))
            : Task.FromResult(Safe));
        var reasons = new ConcurrentQueue<StartupResourceWait>();
        (await failing.AcquireAsync(reasons.Enqueue, default).WaitAsync(TimeSpan.FromSeconds(3))).Dispose();
        Check(reasons.Any(w => w.Code == "measurement_unavailable") && !reasons.Any(w => w.Reason.Contains("private diagnostic")), "Measurement exception handling/privacy failed.");
    }
    private static async Task CancelAndDispose()
    {
        var source = new ControlledSource(Safe with { CpuLoadPercent = 95 });
        using var gate = Gate(source.Sample, 30);
        using var headCancel = new CancellationTokenSource();
        var head = gate.AcquireAsync(_ => { }, headCancel.Token);
        using var queuedCancel = new CancellationTokenSource();
        var queued = gate.AcquireAsync(_ => { }, queuedCancel.Token);
        var following = gate.AcquireAsync(_ => { }, default);
        queuedCancel.Cancel(); headCancel.Cancel();
        await Cancelled(head); await Cancelled(queued);
        source.Set(Safe);
        IDisposable lease = await following.WaitAsync(TimeSpan.FromSeconds(3));
        using var activeCancel = new CancellationTokenSource();
        lease.Dispose();
        IDisposable active = await gate.AcquireAsync(_ => { }, activeCancel.Token).WaitAsync(TimeSpan.FromSeconds(3));
        var next = gate.AcquireAsync(_ => { }, default);
        activeCancel.Cancel();
        await Task.Delay(40);
        Check(!next.IsCompleted, "Owner cancellation released a still-active startup lease.");
        active.Dispose();
        (await next.WaitAsync(TimeSpan.FromSeconds(3))).Dispose();
        using var disposed = Gate(_ => Task.FromResult(Safe with { CpuLoadPercent = null }));
        var waiting = disposed.AcquireAsync(_ => { }, default);
        var waitingBehind = disposed.AcquireAsync(_ => { }, default);
        var watch = Stopwatch.StartNew(); disposed.Dispose();
        Check(watch.ElapsedMilliseconds < 100, "Dispose blocked.");
        await Cancelled(waiting); await Cancelled(waitingBehind);
        await disposed.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        try { await disposed.AcquireAsync(_ => { }, default); throw new Exception("Disposed gate accepted ticket."); }
        catch (ObjectDisposedException) { }
    }
    private static async Task Cancelled(Task<IDisposable> task)
    { try { (await task.WaitAsync(TimeSpan.FromSeconds(3))).Dispose(); throw new Exception("Expected cancellation."); } catch (OperationCanceledException) { } }
    private static async Task TailCancellationReclaimsCapacity()
    {
        foreach (bool pressureBlocked in new[] { true, false })
        {
            var source = new ControlledSource(pressureBlocked ? Safe with { CpuLoadPercent = 95 } : Safe);
            using var gate = Gate(source.Sample);
            var waits = new ConcurrentQueue<StartupResourceWait>();
            var head = gate.AcquireAsync(waits.Enqueue, default);
            IDisposable? held = null;
            if (pressureBlocked) await Eventually(() => waits.Any(w => w.Code == "cpu"));
            else held = await head.WaitAsync(TimeSpan.FromSeconds(3));
            // Head never releases while tails are repeatedly stopped and replaced.
            for (int index = 0; index < 600; index++)
            {
                using var cancel = new CancellationTokenSource();
                var tail = gate.AcquireAsync(_ => { }, cancel.Token);
                Check(gate.PendingCount == 1, "Tail was not queued behind held head.");
                cancel.Cancel();
                Check(gate.PendingCount == 0, "Cancelled tail retained queue node/capacity.");
                await Cancelled(tail);
                Check(!pressureBlocked || !head.IsCompleted, "Tail cancellation disturbed pressure-blocked head.");
            }
            var following = gate.AcquireAsync(_ => { }, default);
            if (pressureBlocked)
            { source.Set(Safe); held = await head.WaitAsync(TimeSpan.FromSeconds(3)); }
            Check(!following.IsCompleted, "Follow-up admitted while head still held lease.");
            held!.Dispose();
            (await following.WaitAsync(TimeSpan.FromSeconds(3))).Dispose();
        }
    }
    private static async Task SamplerDoesNotOverlap()
    {
        int active = 0, maximum = 0;
        using var gate = Gate(async token =>
        {
            int now = Interlocked.Increment(ref active); maximum = Math.Max(maximum, now);
            try { await Task.Delay(15, token); return Safe; } finally { Interlocked.Decrement(ref active); }
        });
        var requests = Enumerable.Range(0, 12).Select(_ => gate.AcquireAsync(_ => { }, default)).ToArray();
        foreach (var request in requests) (await request.WaitAsync(TimeSpan.FromSeconds(3))).Dispose();
        Check(maximum == 1, "Resource measurements overlapped.");
    }
    private static async Task CancelledPaneIsReplaceable()
    {
        foreach (bool queueStarted in new[] { false, true })
        {
            var pane = new TerminalNotes.TerminalPane();
            if (queueStarted) pane.FixtureQueue();
            using var cancel = new CancellationTokenSource();
            Task first = pane.WaitForResourceAdmissionAsync(cancel.Token);
            Task second = pane.WaitForResourceAdmissionAsync();
            cancel.Cancel();
            Check(pane.FixtureFailed && pane.FixtureLifetimeCancelled && !pane.IsWaitingForResources,
                "Stop did not synchronously make a preloaded/queued pane replaceable.");
            await CancelledWait(first); await CancelledWait(second);
        }
        var starting = new TerminalNotes.TerminalPane();
        starting.FixtureQueue(); starting.FixtureAdmit();
        await starting.WaitForResourceAdmissionAsync();
        starting.FixtureNativeCancellation();
        Check(starting.FixtureFailed, "Cancellation after admission left a nonready pane reusable.");
        var ready = new TerminalNotes.TerminalPane(); ready.FixtureReady();
        using var alreadyCancelled = new CancellationTokenSource(); alreadyCancelled.Cancel();
        await ready.WaitForResourceAdmissionAsync(alreadyCancelled.Token);
        ready.FixtureNativeCancellation();
        Check(!ready.FixtureFailed && !ready.FixtureLifetimeCancelled,
            "Admission cancellation changed an already-ready pane's stop policy.");
    }
    private static async Task CancelledWait(Task task)
    { try { await task.WaitAsync(TimeSpan.FromSeconds(3)); throw new Exception("Expected cancelled pane admission."); } catch (OperationCanceledException) { } }

    private static async Task RunOncePreservesPressureAndOwnership()
    {
        foreach (var high in new[] { Safe with { MemoryLoadPercent = 95 }, Safe with { AvailablePhysicalBytes = GiB - 1 }, Safe with { CpuLoadPercent = 95 } })
        {
            var source = new ControlledSource(high);
            using var gate = Gate(source.Sample);
            using var ownerCancellation = new CancellationTokenSource();
            var headWaits = new ConcurrentQueue<StartupResourceWait>();
            var head = gate.AcquireAsync(headWaits.Enqueue, ownerCancellation.Token);
            await Eventually(() => headWaits.Any(w => w.Code is "memory" or "cpu"));
            Func<bool> authorize = headWaits.Last(w => w.TryRunOnce is not null).TryRunOnce!;
            var tailWaits = new ConcurrentQueue<StartupResourceWait>();
            var tail = gate.AcquireAsync(tailWaits.Enqueue, default);
            Check(authorize() && !authorize(), "A ticket must accept exactly one authorization.");
            IDisposable lease = await head.WaitAsync(TimeSpan.FromSeconds(3));
            ownerCancellation.Cancel();
            await Task.Delay(25);
            Check(!tail.IsCompleted && !authorize(), "Run-once bypass released an owned lease or reused its permission.");
            lease.Dispose();
            await Eventually(() => tailWaits.Any(w => w.Code is "memory" or "cpu"));
            Check(!tail.IsCompleted, "Run-once permission leaked to another ticket.");
            source.Set(Safe with { MemoryLoadPercent = 81 });
            await Eventually(() => tailWaits.Any(w => w.Code == "recovery"));
            Check(!tail.IsCompleted, "Run-once reset shared pressure hysteresis.");
            source.Set(Safe);
            (await tail.WaitAsync(TimeSpan.FromSeconds(3))).Dispose();
        }
    }

    private static async Task RunOncePrioritizesOnlyItsTicket()
    {
        var source = new ControlledSource(Safe);
        using var gate = Gate(source.Sample, 120);
        IDisposable active = await gate.AcquireAsync(_ => { }, default).WaitAsync(TimeSpan.FromSeconds(3));
        var watch = Stopwatch.StartNew();
        source.Set(Safe with { MemoryLoadPercent = 95 });
        var headWaits = new ConcurrentQueue<StartupResourceWait>();
        var head = gate.AcquireAsync(headWaits.Enqueue, default);
        var approvedWaits = new ConcurrentQueue<StartupResourceWait>();
        var approved = gate.AcquireAsync(approvedWaits.Enqueue, default);
        Func<bool> authorize = approvedWaits.Last(w => w.TryRunOnce is not null).TryRunOnce!;
        Check(authorize(), "Queued target authorization failed.");
        await Task.Delay(20);
        Check(!head.IsCompleted && !approved.IsCompleted, "Authorization preempted a held lease.");
        active.Dispose();
        IDisposable approvedLease = await approved.WaitAsync(TimeSpan.FromSeconds(3));
        Check(watch.ElapsedMilliseconds >= 110 && approvedWaits.Any(w => w.Code == "spacing"), "Authorized queued target skipped start spacing.");
        Check(!head.IsCompleted && !authorize(), "Queued authorization admitted another ticket or was reusable.");
        var freshWaits = new ConcurrentQueue<StartupResourceWait>();
        var fresh = gate.AcquireAsync(freshWaits.Enqueue, default);
        approvedLease.Dispose();
        await Eventually(() => headWaits.Any(w => w.Code == "memory"));
        Check(!head.IsCompleted && !fresh.IsCompleted, "Permission propagated to a subsequent retry/start.");
        gate.Dispose();
        await Cancelled(head); await Cancelled(fresh);

        // Also expedite a tail after the head has already entered a resource wait.
        using var blockedGate = Gate(source.Sample);
        var blockedWaits = new ConcurrentQueue<StartupResourceWait>();
        var blocked = blockedGate.AcquireAsync(blockedWaits.Enqueue, default);
        await Eventually(() => blockedWaits.Any(w => w.Code == "memory"));
        var targetWaits = new ConcurrentQueue<StartupResourceWait>();
        var target = blockedGate.AcquireAsync(targetWaits.Enqueue, default);
        Check(targetWaits.Last().TryRunOnce!(), "Tail behind blocked head could not authorize.");
        (await target.WaitAsync(TimeSpan.FromSeconds(3))).Dispose();
        Check(!blocked.IsCompleted, "Expedited target bypassed the blocked head's own admission.");
        blockedGate.Dispose(); await Cancelled(blocked);
    }

    private static async Task RunOnceRejectsStaleAndCancelledRequests()
    {
        var source = new ControlledSource(Safe);
        using var gate = Gate(source.Sample);
        IDisposable held = await gate.AcquireAsync(_ => { }, default).WaitAsync(TimeSpan.FromSeconds(3));
        source.Set(Safe with { CpuLoadPercent = 95 });
        foreach (bool authorizeBeforeCancellation in new[] { false, true })
        {
            using var cancel = new CancellationTokenSource();
            var waits = new ConcurrentQueue<StartupResourceWait>();
            var pending = gate.AcquireAsync(waits.Enqueue, cancel.Token);
            Func<bool> stale = waits.Last().TryRunOnce!;
            if (authorizeBeforeCancellation) Check(stale(), "Queued authorization was not accepted.");
            cancel.Cancel();
            await Cancelled(pending);
            Check(!stale() && gate.PendingCount == 0, "Cancelled authorization retained its ticket or remained callable.");
        }
        var freshWaits = new ConcurrentQueue<StartupResourceWait>();
        var fresh = gate.AcquireAsync(freshWaits.Enqueue, default);
        held.Dispose();
        await Eventually(() => freshWaits.Any(w => w.Code == "cpu"));
        Check(!fresh.IsCompleted, "A cancelled ticket's authorization transferred to a replacement.");
        Func<bool> disposed = freshWaits.Last(w => w.TryRunOnce is not null).TryRunOnce!;
        gate.Dispose();
        Check(!disposed(), "Disposed gate accepted stale authorization.");
        await Cancelled(fresh);
    }

    private static async Task RunOnceDoesNotBypassUnknownMeasurements()
    {
        var source = new ControlledSource(Safe with { CpuLoadPercent = null });
        using var gate = Gate(source.Sample);
        var waits = new ConcurrentQueue<StartupResourceWait>();
        var pending = gate.AcquireAsync(waits.Enqueue, default);
        // Authorization may be queued before the first sample, but never grants with unknown measurements.
        Func<bool> queued = waits.First(w => w.Code == "queue").TryRunOnce!;
        bool accepted = queued();
        await Eventually(() => waits.Any(w => w.Code == "measurement_unavailable"));
        Check(!pending.IsCompleted && waits.Last(w => w.Code == "measurement_unavailable").TryRunOnce is null,
            "Unknown measurements were bypassed or exposed an authorization callback.");
        if (!accepted) Check(!queued(), "Stale callback authorized an unknown measurement wait.");
        source.Set(Safe with { CpuLoadPercent = 95 });
        if (!accepted)
        {
            await Eventually(() => waits.Any(w => w.Code == "cpu"));
            Check(waits.Last(w => w.Code == "cpu").TryRunOnce!(), "Valid CPU pressure could not be authorized after measurement recovery.");
        }
        (await pending.WaitAsync(TimeSpan.FromSeconds(3))).Dispose();
        Check(!queued(), "Granted ticket accepted a stale queue callback.");
    }

    private static void PaneRunOnceRequiresOptIn()
    {
        var pane = new TerminalNotes.TerminalPane();
        int authorizations = 0;
        pane.FixtureWait(new("memory", "memory wait") { TryRunOnce = () => { authorizations++; return true; } });
        Check(!pane.CanRunResourceAdmissionOnce && !pane.TryRunResourceAdmissionOnce() && authorizations == 0,
            "An ordinary pane exposed run-once permission without PlanMaker opt-in.");
        pane.EnableResourceAdmissionRunOnce();
        Check(pane.CanRunResourceAdmissionOnce && pane.TryRunResourceAdmissionOnce() && authorizations == 1,
            "Opted-in pane did not forward permission to its pending ticket.");
        Check(!pane.TryRunResourceAdmissionOnce() && authorizations == 1, "Pane forwarded a consumed action twice.");
        pane.FixtureWait(new("cpu", "new wait") { TryRunOnce = () => { authorizations++; return true; } });
        pane.FixtureNativeCancellation();
        Check(!pane.CanRunResourceAdmissionOnce && !pane.TryRunResourceAdmissionOnce() && authorizations == 1,
            "Cancelled pane forwarded a stale permission.");
    }
    // 사용자: Loop가 멈추면 안 됨. An office PC at 85–95% memory delays new work at most MaximumPressureWait; a critical
    // shortage keeps it waiting until it eases.
    private static async Task PressureDelaysAtMostMaximumWait()
    {
        var clock = new ManualClock();
        var source = new ControlledSource(Safe with { MemoryLoadPercent = 90 });
        using var gate = new TerminalStartupAdmission(source.Sample, TimeSpan.FromMilliseconds(5), TimeSpan.Zero, clock);
        Check(gate.MaximumPressureWait == TimeSpan.FromMinutes(3), "The bounded pressure wait is not three minutes.");
        var waits = new ConcurrentQueue<StartupResourceWait>();
        var busy = gate.AcquireAsync(waits.Enqueue, default);
        await Eventually(() => waits.Any(w => w.Code == "memory" && !w.Critical));
        clock.Advance(TimeSpan.FromMinutes(2));
        await Task.Delay(25);
        Check(!busy.IsCompleted, "Ordinary memory pressure admitted work before its maximum wait.");
        clock.Advance(TimeSpan.FromMinutes(1));
        (await busy.WaitAsync(TimeSpan.FromSeconds(3))).Dispose();

        source.Set(Safe with { MemoryLoadPercent = 97 });
        var criticalWaits = new ConcurrentQueue<StartupResourceWait>();
        var critical = gate.AcquireAsync(criticalWaits.Enqueue, default);
        await Eventually(() => criticalWaits.Any(w => w.Code == "memory" && w.Critical));
        for (int minute = 0; minute < 30; minute++) { clock.Advance(TimeSpan.FromMinutes(1)); await Task.Delay(5); }
        Check(!critical.IsCompleted, "A critical memory shortage admitted new work.");
        // Eased to ordinary pressure after a long wait: it goes on.
        source.Set(Safe with { MemoryLoadPercent = 90 });
        clock.Advance(TimeSpan.FromSeconds(1));
        (await critical.WaitAsync(TimeSpan.FromSeconds(3))).Dispose();
    }

    private static async Task CommitAndElapsedRecovery()
    {
        var clock = new ManualClock();
        var source = new ControlledSource(Safe);
        using var gate = new TerminalStartupAdmission(source.Sample, TimeSpan.FromMilliseconds(5), TimeSpan.Zero, clock);
        // Healthy startup must require neither a recovery period nor a second sample.
        (await gate.AcquireAsync(_ => { }, default).WaitAsync(TimeSpan.FromSeconds(3))).Dispose();
        source.Set(Safe with { CommitTotalBytes = 31 * GiB });
        var waits = new ConcurrentQueue<StartupResourceWait>();
        var pending = gate.AcquireAsync(waits.Enqueue, default);
        await Eventually(() => waits.Any(w => w.Code == "memory" && w.Reason.Contains("시스템 커밋") && w.Reason.Contains("커밋 여유")));
        Check(!pending.IsCompleted, "Healthy RAM hid exhausted system commit.");
        source.Set(Safe);
        await Eventually(() => waits.Any(w => w.Code == "recovery"));
        await Task.Delay(50);
        Check(!pending.IsCompleted, "Many rapid safe samples bypassed elapsed recovery.");
        clock.Advance(TimeSpan.FromSeconds(9));
        await Task.Delay(25);
        Check(!pending.IsCompleted, "Recovery resumed before ten healthy seconds.");
        source.Set(Safe with { CommitTotalBytes = null });
        await Eventually(() => waits.Any(w => w.Code == "measurement_unavailable"));
        source.Set(Safe);
        int before = waits.Count;
        await Eventually(() => waits.Count > before && waits.Last().Code == "recovery");
        clock.Advance(TimeSpan.FromSeconds(9));
        await Task.Delay(25);
        Check(!pending.IsCompleted, "Unavailable commit did not reset sustained recovery.");
        clock.Advance(TimeSpan.FromSeconds(1));
        (await pending.WaitAsync(TimeSpan.FromSeconds(3))).Dispose();

        source.Set(Safe with { CommitTotalBytes = 31 * GiB });
        var onceWaits = new ConcurrentQueue<StartupResourceWait>();
        var once = gate.AcquireAsync(onceWaits.Enqueue, default);
        await Eventually(() => onceWaits.Any(w => w.Code == "memory"));
        Check(onceWaits.Last().TryRunOnce!(), "Commit pressure lost explicit run-once.");
        (await once.WaitAsync(TimeSpan.FromSeconds(3))).Dispose();
    }
    private static async Task MonitorPressureCarriesIntoNextRequest()
    {
        var clock = new ManualClock();
        var state = new TerminalResourcePressureState(clock);
        state.Observe(Safe with { MemoryLoadPercent = 96 });
        var waits = new ConcurrentQueue<StartupResourceWait>();
        using var gate = new TerminalStartupAdmission(_ => Task.FromResult(Safe), TimeSpan.FromMilliseconds(5), TimeSpan.Zero, clock)
            { ObservedPressure = () => state.Current };
        var pending = gate.AcquireAsync(waits.Enqueue, default);
        await Eventually(() => waits.Any(w => w.Code == "recovery"));
        Check(!pending.IsCompleted, "Pressure observed between automatic requests lost sustained recovery.");
        clock.Advance(TimeSpan.FromSeconds(9));
        await Task.Delay(25);
        Check(!pending.IsCompleted, "Shared monitor pressure resumed before ten healthy seconds.");
        state.Observe(Safe with { CommitTotalBytes = 31 * GiB });
        int before = waits.Count;
        await Eventually(() => waits.Count > before);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(25);
        Check(!pending.IsCompleted, "Fresh monitor pressure did not restart the recovery interval.");
        clock.Advance(TimeSpan.FromSeconds(9));
        (await pending.WaitAsync(TimeSpan.FromSeconds(3))).Dispose();
        // The same retained observation has already completed recovery; it must
        // not impose a new ten-second delay on a fresh, healthy request.
        (await gate.AcquireAsync(_ => { }, default).WaitAsync(TimeSpan.FromSeconds(3))).Dispose();
    }
    private static async Task CancelledRecoveryDoesNotCountIdleTime()
    {
        foreach (bool cancelInsideCallback in new[] { false, true })
        {
            var clock = new ManualClock();
            var source = new ControlledSource(Safe with { CpuLoadPercent = 95 });
            using var gate = new TerminalStartupAdmission(source.Sample, TimeSpan.FromMilliseconds(5), TimeSpan.Zero, clock);
            using var cancellation = new CancellationTokenSource();
            var firstWaits = new ConcurrentQueue<StartupResourceWait>();
            var first = gate.AcquireAsync(wait =>
            {
                firstWaits.Enqueue(wait);
                if (cancelInsideCallback && wait.Code == "recovery") cancellation.Cancel();
            }, cancellation.Token);
            await Eventually(() => firstWaits.Any(w => w.Code == "cpu"));
            source.Set(Safe);
            await Eventually(() => firstWaits.Any(w => w.Code == "recovery"));
            if (!cancelInsideCallback) cancellation.Cancel();
            await Cancelled(first);
            clock.Advance(TimeSpan.FromHours(1));
            var nextWaits = new ConcurrentQueue<StartupResourceWait>();
            var next = gate.AcquireAsync(nextWaits.Enqueue, default);
            await Eventually(() => nextWaits.Any(w => w.Code == "recovery"));
            Check(!next.IsCompleted, "Cancelled admission counted an unsampled idle gap as healthy recovery.");
            clock.Advance(TimeSpan.FromSeconds(10));
            (await next.WaitAsync(TimeSpan.FromSeconds(3))).Dispose();
        }
    }
    private static void PressureMonitorTransitions()
    {
        var clock = new ManualClock();
        var state = new TerminalResourcePressureState(clock);
        var changes = new List<TerminalResourcePressureNotice?>();
        state.Changed += _ => throw new InvalidOperationException("observer failure");
        state.Changed += changes.Add;
        state.Observe(Safe);
        Check(changes.Count == 0, "Healthy monitor emitted an alert.");
        state.Observe(Safe with { CommitTotalBytes = 29 * GiB });
        Check(changes.Count == 1 && state.Current is { Code: "memory", IsCriticalMemory: false }, "Commit pressure was not observed.");
        state.Observe(Safe with { CommitTotalBytes = 30 * GiB });
        Check(changes.Count == 1, "Repeated measured pressure spammed transitions.");
        state.Observe(Safe with { CommitTotalBytes = 31 * GiB });
        Check(changes.Count == 2 && state.Current?.IsCriticalMemory == true, "Critical commit escalation was hidden.");
        state.Observe(Safe);
        clock.Advance(TimeSpan.FromSeconds(9)); state.Observe(Safe);
        Check(changes.Count == 2, "Monitor cleared pressure before sustained recovery.");
        state.Observe(Safe with { CommitLimitBytes = null });
        clock.Advance(TimeSpan.FromSeconds(20)); state.Observe(Safe);
        Check(changes.Count == 2, "Unknown measurement silently cleared active pressure.");
        clock.Advance(TimeSpan.FromSeconds(10)); state.Observe(Safe);
        Check(changes.Count == 3 && changes[^1] is null && state.Current is null, "Recovered monitor did not clear once.");
        state.Observe(Safe with { MemoryLoadPercent = 96 });
        Check(state.Current?.IsCriticalMemory == true, "Critical physical RAM was not observed.");
    }
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = DateTimeOffset.UnixEpoch.Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }
    private sealed class ControlledSource(StartupResourceMeasurement initial)
    {
        private StartupResourceMeasurement _current = initial;
        private int _singleSafe;
        internal void Set(StartupResourceMeasurement sample) { Interlocked.Exchange(ref _singleSafe, 0); Volatile.Write(ref _current, sample); }
        internal void HoldAfterOneSafeSample() { Volatile.Write(ref _current, Safe with { MemoryLoadPercent = 81 }); Interlocked.Exchange(ref _singleSafe, 1); }
        internal Task<StartupResourceMeasurement> Sample(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref _singleSafe, 2, 1) == 1) return Task.FromResult(Safe);
            // Hold subsequent sampling until test permits recovery so the 1/2 assertion is deterministic.
            if (Volatile.Read(ref _singleSafe) == 2) return Hold(token);
            return Task.FromResult(Volatile.Read(ref _current));
        }
        private async Task<StartupResourceMeasurement> Hold(CancellationToken token)
        { while (Volatile.Read(ref _singleSafe) == 2) await Task.Delay(2, token); return Volatile.Read(ref _current); }
    }
}
