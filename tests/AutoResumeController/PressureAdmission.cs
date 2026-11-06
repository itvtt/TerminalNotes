using System.Reflection;
using TerminalNotes.Services;

internal static partial class Program
{
    private static async Task ContinuationAdmissionScenariosAsync()
    {
        foreach (string pressure in new[] { "memory", "cpu", "commit" })
        {
            bool cpu = pressure == "cpu";
            using var resources = new ResourceAdmission(cpu, pressure == "commit");
            using var fixture = new Fixture("pressure-recovery-" + pressure, acquireContinuationAdmission: resources.Acquire);
            await fixture.BindAsync();
            await fixture.Controller.TickAsync();
            fixture.Clock.Advance(TimeSpan.FromSeconds(20));
            Task pending = fixture.Controller.TickAsync();
            await UntilAsync(() => fixture.Controller.Status.Contains(pressure == "commit" ? "시스템 커밋" : cpu ? "CPU" : "메모리"), "waiting for resource pressure");
            Require(fixture.Api.ContinuePosts.Count == 0 && fixture.Api.AbortPosts.Count == 0 && !pending.IsCompleted,
                "Resource pressure dispatched continuation or aborted an active model.");
            resources.Recover();
            await pending.WaitAsync(WaitLimit);
            Require(resources.HealthySamples >= 2 && fixture.Api.ContinuePosts.Count == 1 && fixture.Api.AbortPosts.Count == 0,
                "Resource recovery did not require two safe samples before the exact continuation.");
            Require(resources.WaitCodes.Contains(cpu ? "cpu" : "memory") && resources.WaitCodes.Contains("recovery"),
                "Typed pressure and recovery callbacks did not remain within the same pending admission.");
        }

        var reviewAdmission = new HeldAdmission();
        using (var review = new Fixture("pressure-completed-loop", acquireContinuationAdmission: reviewAdmission.AcquireAsync))
        {
            review.Controller.SetContinuousImprovement(true, null);
            await review.BindAsync();
            review.Api.CompleteTask = true;
            await review.Controller.TickAsync();
            review.Clock.Advance(TimeSpan.FromSeconds(20));
            Task pendingReview = review.Controller.TickAsync();
            await reviewAdmission.Entered.Task.WaitAsync(WaitLimit);
            Require(review.Api.ContinuePosts.Count == 0, "A completed Loop review bypassed pressure admission.");
            reviewAdmission.Release();
            await pendingReview.WaitAsync(WaitLimit);
            Require(review.Api.ContinuePosts.Count == 1 && reviewAdmission.Lease.Disposed,
                "A completed Loop did not resume and release admission after recovery.");
        }

        var compactAdmission = new HeldAdmission();
        using (var compact = new ContextScenario("pressure-compaction", compactAdmission.AcquireAsync))
        {
            await compact.BindAsync();
            compact.Fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            Task pendingCompact = compact.Fixture.Controller.TickAsync();
            await compactAdmission.Entered.Task.WaitAsync(WaitLimit);
            Require(compact.Fixture.Api.CompactionPosts == 0 && compact.Fixture.Api.AbortPosts.Count == 0,
                "Native compaction retry bypassed admission or aborted for pressure.");
            compactAdmission.Release();
            await pendingCompact.WaitAsync(WaitLimit);
            Require(compact.Fixture.Api.CompactionPosts == 1 && compactAdmission.Lease.Disposed,
                "Native compaction did not resume and release admission after recovery.");
        }

        foreach (string cancellation in new[] { "stop", "input", "dispose", "deadline" })
        {
            var admission = new HeldAdmission();
            using var fixture = new Fixture("pressure-cancel-" + cancellation, acquireContinuationAdmission: admission.AcquireAsync);
            fixture.Controller.SetContinuousImprovement(true, cancellation == "deadline" ? fixture.Clock.GetUtcNow().AddSeconds(21) : null);
            await fixture.BindAsync();
            await fixture.Controller.TickAsync();
            fixture.Clock.Advance(TimeSpan.FromSeconds(20));
            Task pending = fixture.Controller.TickAsync();
            await admission.Entered.Task.WaitAsync(WaitLimit);
            if (cancellation == "stop") fixture.Controller.Suspend("Stopped");
            else if (cancellation == "input") fixture.Controller.ObserveLocalInput("new request");
            else if (cancellation == "dispose") fixture.Controller.Dispose();
            else fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            try { await pending.WaitAsync(WaitLimit); }
            catch (OperationCanceledException) { }
            Require(admission.Token.IsCancellationRequested && fixture.Api.ContinuePosts.Count == 0 && fixture.Api.AbortPosts.Count == 0,
                "A queued continuation survived " + cancellation + ".");
            string status = fixture.Controller.Status;
            admission.Waiting!("stale resource callback");
            Require(fixture.Controller.Status == status, "A cancelled admission overwrote the current status.");
        }

        foreach (string change in new[] { "permission", "question", "busy", "progress", "user", "plan" })
        {
            var admission = new HeldAdmission();
            using var fixture = new Fixture("pressure-final-" + change, acquireContinuationAdmission: admission.AcquireAsync);
            await fixture.BindAsync();
            await fixture.Controller.TickAsync();
            fixture.Clock.Advance(TimeSpan.FromSeconds(20));
            Task pending = fixture.Controller.TickAsync();
            await admission.Entered.Task.WaitAsync(WaitLimit);
            if (change == "permission") fixture.Api.SetPendingInput(permission: true);
            else if (change == "question") fixture.Api.SetPendingInput(question: true);
            else if (change == "busy") fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
            else if (change == "progress") fixture.Api.SetAssistantText("Fresh current-turn activity.");
            else if (change == "user") fixture.Api.AddUserMessage("intervening-user", fixture.Clock.GetUtcNow());
            else File.AppendAllText(fixture.PlanPath!, "\nChanged plan.");
            admission.Release();
            await pending.WaitAsync(WaitLimit);
            Require(fixture.Api.ContinuePosts.Count == 0 && admission.Lease.Disposed,
                "The final native/plan preflight or admission cleanup failed after " + change + ".");
        }

        var extensionAdmission = new HeldAdmission();
        using (var extension = new Fixture("pressure-deadline-extension", acquireContinuationAdmission: extensionAdmission.AcquireAsync))
        {
            extension.Controller.SetContinuousImprovement(true, extension.Clock.GetUtcNow().AddMinutes(10));
            await extension.BindAsync();
            await extension.Controller.TickAsync();
            extension.Clock.Advance(TimeSpan.FromSeconds(20));
            Task oldWait = extension.Controller.TickAsync();
            await extensionAdmission.Entered.Task.WaitAsync(WaitLimit);
            CancellationToken previousToken = extensionAdmission.Token;
            Action<string> oldCallback = extensionAdmission.Waiting!;
            extension.Controller.SetContinuousImprovement(true, extension.Clock.GetUtcNow().AddHours(1));
            try { await oldWait.WaitAsync(WaitLimit); }
            catch (OperationCanceledException) { }
            Require(previousToken.IsCancellationRequested && extension.Api.ContinuePosts.Count == 0,
                "Changing a queued Loop deadline retained stale admission work.");
            await extension.Controller.TickAsync();
            extension.Clock.Advance(TimeSpan.FromSeconds(20));
            Task newWait = extension.Controller.TickAsync();
            await UntilAsync(() => extensionAdmission.Token != previousToken, "revalidating an extended Loop deadline");
            string status = extension.Controller.Status;
            oldCallback("stale deadline resource status");
            Require(extension.Controller.Status == status, "An old deadline callback overwrote the new admission state.");
            extensionAdmission.Release();
            await newWait.WaitAsync(WaitLimit);
            Require(extension.Api.ContinuePosts.Count == 1 && extensionAdmission.Lease.Disposed,
                "An extended deadline failed to resume exactly once after fresh admission.");
        }

        var retryAdmission = new HeldAdmission();
        using var retry = new Fixture("pressure-provider-retry", acquireContinuationAdmission: retryAdmission.AcquireAsync);
        await retry.BindAsync();
        retry.Api.SetNativeFailure("APIError", 429, false);
        retry.Api.ErrorResponseHeaders = new() { ["Retry-After"] = "120" };
        await retry.Controller.TickAsync();
        await AdvanceFailureAsync(retry, TimeSpan.FromSeconds(119));
        Require(!retryAdmission.Entered.Task.IsCompleted && retry.Api.ContinuePosts.Count == 0,
            "Admission bypassed the provider's retry deadline.");
        retry.Clock.Advance(TimeSpan.FromSeconds(1));
        Task retryPending = retry.Controller.TickAsync();
        await retryAdmission.Entered.Task.WaitAsync(WaitLimit);
        Require(retry.Api.ContinuePosts.Count == 0, "Automatic error retry bypassed pressure admission.");
        Require((int)typeof(OpenCodeAutoResumeController).GetField("_errorRetryAttempts", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(retry.Controller)! == 0, "Waiting for pressure consumed an error retry attempt.");
        retryAdmission.Release();
        await retryPending.WaitAsync(WaitLimit);
        Require(retry.Api.ContinuePosts.Count == 1 && retryAdmission.Lease.Disposed,
            "An elapsed provider retry failed to resume after pressure admission.");
    }

    private sealed class AdmissionLease : IDisposable
    {
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    private sealed class HeldAdmission
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IDisposable> _grant = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly AdmissionLease Lease = new();
        public Action<string>? Waiting;
        public CancellationToken Token;
        public async Task<IDisposable> AcquireAsync(Action<string> waiting, CancellationToken token)
        {
            Waiting = waiting;
            Token = token;
            waiting("메모리 여유를 기다리는 중");
            Entered.TrySetResult();
            return await _grant.Task.WaitAsync(token);
        }
        public void Release() => _grant.TrySetResult(Lease);
    }

    // Exercise the production admission sampler and hysteresis without exposing
    // implementation types publicly or starting any provider/process.
    private sealed class ResourceAdmission : IDisposable
    {
        private readonly bool _cpu, _commit;
        private readonly PressureClock _clock = new();
        private int _healthy, _healthySamples;
        private readonly IDisposable _queue;
        public Func<Action<string>, CancellationToken, Task<IDisposable>> Acquire { get; }
        public int HealthySamples => Volatile.Read(ref _healthySamples);
        public System.Collections.Concurrent.ConcurrentQueue<string> WaitCodes { get; } = new();
        public ResourceAdmission(bool cpu, bool commit = false)
        {
            _cpu = cpu;
            _commit = commit;
            Assembly assembly = typeof(OpenCodeAutoResumeController).Assembly;
            Type queueType = assembly.GetType("TerminalNotes.Services.TerminalStartupAdmission", true)!;
            Type measurementType = assembly.GetType("TerminalNotes.Services.StartupResourceMeasurement", true)!;
            Type waitType = assembly.GetType("TerminalNotes.Services.StartupResourceWait", true)!;
            var pair = ((IDisposable, Func<Action<string>, CancellationToken, Task<IDisposable>>))typeof(ResourceAdmission)
                .GetMethod(nameof(Create), BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(measurementType, waitType).Invoke(this, new object[] { queueType })!;
            _queue = pair.Item1;
            Acquire = pair.Item2;
        }
        private (IDisposable, Func<Action<string>, CancellationToken, Task<IDisposable>>) Create<TSample, TWait>(Type queueType)
        {
            Func<CancellationToken, Task<TSample>> measure = _ =>
            {
                bool healthy = Volatile.Read(ref _healthy) != 0;
                if (healthy) { Interlocked.Increment(ref _healthySamples); _clock.Advance(TimeSpan.FromSeconds(5)); }
                var sample = (TSample)Activator.CreateInstance(typeof(TSample), new object[]
                { healthy || _cpu || _commit ? 60 : 90, 16UL * 1024 * 1024 * 1024, 4UL * 1024 * 1024 * 1024, healthy || !_cpu ? 20d : 95d })!;
                typeof(TSample).GetProperty("CommitTotalBytes", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(sample, (healthy || !_commit ? 8UL : 31UL) * 1024 * 1024 * 1024);
                typeof(TSample).GetProperty("CommitLimitBytes", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(sample, 32UL * 1024 * 1024 * 1024);
                return Task.FromResult(sample);
            };
            var queue = (IDisposable)Activator.CreateInstance(queueType, BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: new object?[] { measure, TimeSpan.FromMilliseconds(5), TimeSpan.Zero, _clock }, culture: null)!;
            MethodInfo acquire = queueType.GetMethod("AcquireAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (queue, (waiting, token) => (Task<IDisposable>)acquire.Invoke(queue, new object[]
            { (Action<TWait>)(wait =>
                {
                    WaitCodes.Enqueue((string)typeof(TWait).GetProperty("Code")!.GetValue(wait)!);
                    waiting((string)typeof(TWait).GetProperty("Reason")!.GetValue(wait)!);
                }), token })!);
        }
        public void Recover() => Volatile.Write(ref _healthy, 1);
        public void Dispose() => _queue.Dispose();
    }
    private sealed class PressureClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }
}
