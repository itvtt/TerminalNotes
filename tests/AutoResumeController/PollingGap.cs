using TerminalNotes.Services;

internal static partial class Program
{
    private static async Task LongPollingGapReconcilesOwnedLoopAsync()
    {
        using var fixture = new Fixture("gap-completed-loop");
        fixture.Controller.SetContinuousImprovement(true, null);
        fixture.Api.NoTodos = true;
        await fixture.BindAsync(observeTodo: false);
        fixture.Controller.SetEnabled(false); // Loop owns its own recovery intent.
        fixture.Api.CompleteTask = true;
        await fixture.Controller.TickAsync();
        fixture.Clock.Advance(TimeSpan.FromHours(6));
        await fixture.Controller.TickAsync();
        Require(fixture.SessionId() == fixture.Session && fixture.Controller.IsContinuousImprovementActive,
            "A six-hour polling gap discarded a confirmed, still-owned completed Loop request.");
        Require(fixture.Api.ContinuePosts.Count == 0 && fixture.Api.AbortPosts.Count == 0,
            "Elapsed unobserved time alone dispatched a completed Loop recovery action.");
        fixture.Clock.Advance(TimeSpan.FromSeconds(19));
        await fixture.Controller.TickAsync();
        Require(fixture.Api.ContinuePosts.Count == 0, "A resumed Loop skipped its fresh native idle interval.");
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Controller.TickAsync();
        await fixture.Controller.TickAsync();
        Require(fixture.Api.ContinuePosts.Count == 1 && fixture.Api.ContinuePosts.Single().SessionId == fixture.Session,
            "A reconciled completed Loop failed to submit exactly one next improvement to its exact session.");

        using var unavailable = new Fixture("gap-unavailable-loop");
        unavailable.Controller.SetContinuousImprovement(true, null);
        await unavailable.BindAsync();
        unavailable.Api.CompleteTask = true;
        await unavailable.Controller.TickAsync();
        unavailable.Api.StatusFailuresRemaining = 4;
        foreach (int seconds in new[] { 0, 10, 20, 40 })
        {
            unavailable.Clock.Advance(TimeSpan.FromSeconds(seconds));
            await unavailable.Controller.TickAsync();
            Require(unavailable.SessionId() == unavailable.Session && unavailable.Api.ContinuePosts.Count == 0,
                "Repeated unavailable native reads discarded the request or dispatched without evidence.");
        }
        unavailable.Clock.Advance(TimeSpan.FromHours(6));
        await unavailable.Controller.TickAsync();
        Require(unavailable.SessionId() == unavailable.Session && unavailable.Api.ContinuePosts.Count == 0,
            "A long gap during native read backoff discarded the pending Loop or replayed immediately.");
        await AdvanceFailureAsync(unavailable, TimeSpan.FromSeconds(20));
        Require(unavailable.Api.ContinuePosts.Count == 1,
            "Loop failed to resume once unavailable reads, long-gap reconciliation and settling recovered.");
    }

    private static async Task LongPollingGapPreservesRetryDeadlinesAsync()
    {
        foreach (bool nativeRetry in new[] { false, true })
        {
            using var fixture = new Fixture("gap-provider-" + nativeRetry);
            fixture.Controller.SetContinuousImprovement(true, null);
            await fixture.BindAsync();
            fixture.Controller.SetEnabled(false);
            fixture.Api.NoTodos = true;
            fixture.Api.SetNativeFailure("APIError", 429, false);
            fixture.Api.ErrorResponseHeaders = new() { ["Retry-After"] = "7200" };
            fixture.Api.SetStatus(nativeRetry ? OpenCodeRecoveryStatus.Retry : OpenCodeRecoveryStatus.Idle);
            DateTimeOffset deadline = fixture.Clock.GetUtcNow().AddHours(2);
            if (nativeRetry) fixture.Api.RetryNext = deadline.ToUnixTimeMilliseconds();
            await fixture.Controller.TickAsync();
            fixture.Clock.Advance(TimeSpan.FromHours(1));
            await fixture.Controller.TickAsync();
            Require(fixture.SessionId() == fixture.Session && fixture.Controller.ErrorRetryAt == deadline,
                "A polling gap discarded or slid an already anchored provider retry deadline.");
            await AdvanceFailureAsync(fixture, TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59));
            Require(fixture.Api.ContinuePosts.Count == 0 && fixture.Api.AbortPosts.Count == 0,
                "Gap reconciliation bypassed a future provider/native retry deadline.");
            fixture.Api.SetStatus(OpenCodeRecoveryStatus.Idle);
            await AdvanceFailureAsync(fixture, TimeSpan.FromSeconds(1));
            await AdvanceFailureAsync(fixture, TimeSpan.FromSeconds(20));
            Require(fixture.Api.ContinuePosts.Count == 1,
                "A still-owned Loop did not recover after its original provider deadline and fresh native settle interval.");
        }
    }

    private static async Task LongPollingGapRequiresFreshStallEvidenceAsync()
    {
        using var fixture = new Fixture("gap-ignored-abort");
        fixture.Controller.SetContinuousImprovement(true, null);
        await fixture.BindAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        await fixture.Controller.TickAsync();
        int restarts = 0;
        fixture.Controller.RestartStalledProcessAsync = async request =>
        {
            Require(request.CanRestart() && await request.VerifyAsync(), "A gap restart lacked exact fresh native verification.");
            restarts++;
            return true;
        };
        await AdvanceFailureAsync(fixture, OpenCodeRecoveryPolicy.StalledAbortPeriod);
        Require(fixture.Api.AbortPosts.Count == 1 && restarts == 0, "The pre-gap stall did not reach its first native abort.");
        fixture.Clock.Advance(TimeSpan.FromHours(6));
        await fixture.Controller.TickAsync();
        Require(fixture.SessionId() == fixture.Session && fixture.Api.AbortPosts.Count == 1 && restarts == 0,
            "A polling gap discarded an owned busy request or reused stale abort continuity to restart it.");
        await AdvanceFailureAsync(fixture, OpenCodeRecoveryPolicy.StalledAbortPeriod - TimeSpan.FromSeconds(1));
        Require(fixture.Api.AbortPosts.Count == 1 && restarts == 0, "Unobserved time counted toward a fresh post-gap stall.");
        await AdvanceFailureAsync(fixture, TimeSpan.FromSeconds(1));
        Require(fixture.Api.AbortPosts.Count == 2 && restarts == 0, "Post-gap inactivity did not require a newly verified abort.");
        await AdvanceFailureAsync(fixture, TimeSpan.FromMinutes(2));
        Require(restarts == 1 && fixture.Api.ContinuePosts.Count == 0,
            "A newly verified post-gap ignored abort failed safe process recovery or blindly resent into busy.");
    }

    private static async Task LongPollingGapPreservesAmbiguousAbortAsync()
    {
        using var fixture = new Fixture("gap-ambiguous-abort");
        fixture.Controller.SetContinuousImprovement(true, null);
        await fixture.BindAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        fixture.Api.LoseNextAbortResponse = true;
        await fixture.Controller.TickAsync();
        int restarts = 0;
        fixture.Controller.RestartStalledProcessAsync = _ => { restarts++; return Task.FromResult(true); };
        await AdvanceFailureAsync(fixture, OpenCodeRecoveryPolicy.StalledAbortPeriod);
        Require(fixture.Api.AbortPosts.Count == 1, "Lost-abort-acknowledgement setup did not dispatch its native abort.");
        fixture.Clock.Advance(TimeSpan.FromHours(6));
        await fixture.Controller.TickAsync();
        await AdvanceFailureAsync(fixture, TimeSpan.FromMinutes(20));
        Require(fixture.SessionId() == fixture.Session && fixture.Api.AbortPosts.Count == 1 &&
            fixture.Api.ContinuePosts.Count == 0 && restarts == 0,
            "A gap forgot an ambiguous abort reservation and repeated abort, continued, or restarted without resolution.");
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Idle);
        await fixture.Controller.TickAsync();
        await AdvanceFailureAsync(fixture, TimeSpan.FromSeconds(20));
        Require(fixture.Api.ContinuePosts.Count == 1 && fixture.Api.AbortPosts.Count == 1,
            "Fresh exact native idle failed to reconcile a lost abort acknowledgement without repeating abort.");
    }

    private static async Task LongPollingGapPreservesSafetyFencesAsync()
    {
        foreach (string blocker in new[] { "stop", "permission", "question", "auth", "tool", "new-request", "changed-plan", "ownership", "ambiguous" })
        {
            using var fixture = new Fixture("gap-blocked-" + blocker);
            fixture.Controller.SetContinuousImprovement(true, null);
            await fixture.BindAsync();
            Fixture? replacement = null;
            try
            {
                if (blocker == "ambiguous")
                {
                    fixture.Api.SetNativeFailure("APIError", 503, true);
                    await fixture.Controller.TickAsync();
                    fixture.Api.LoseNextContinuationResponse = true;
                    Require(!await fixture.Controller.RetryErrorAsync(), "Ambiguous gap setup unexpectedly confirmed delivery.");
                }
                else
                {
                    fixture.Api.CompleteTask = true;
                    await fixture.Controller.TickAsync();
                    switch (blocker)
                    {
                        case "stop": fixture.Controller.ObserveLocalInput("\x03"); break;
                        case "permission": fixture.Api.SetPendingInput(permission: true); break;
                        case "question": fixture.Api.SetPendingInput(question: true); break;
                        case "auth": fixture.Api.SetNativeFailure("APIError", 401, true); break;
                        case "tool": fixture.Api.SetRunningTool(true); break;
                        case "new-request": fixture.Api.AddUserMessage("foreign-user-after-gap", fixture.Clock.GetUtcNow().AddSeconds(1)); break;
                        case "changed-plan": File.AppendAllText(fixture.PlanPath!, "\nNew scope after suspension."); break;
                        case "ownership":
                            replacement = new Fixture(fixture.Session, projectPath: fixture.ProjectPath, clock: fixture.Clock);
                            await replacement.BindAsync();
                            break;
                    }
                }
                int previousPosts = fixture.Api.ContinuePosts.Count;
                fixture.Clock.Advance(TimeSpan.FromHours(6));
                await fixture.Controller.TickAsync();
                await AdvanceFailureAsync(fixture, TimeSpan.FromMinutes(20));
                Require(fixture.Api.ContinuePosts.Count == previousPosts && fixture.Api.AbortPosts.Count == 0,
                    "Long-gap reconciliation bypassed the " + blocker + " safety fence or duplicated an uncertain POST.");
                if (blocker is "stop" or "new-request" or "changed-plan" or "ownership")
                    Require(fixture.SessionId() is null, "Gap recovery retained a stopped, replaced, or stale request: " + blocker);
                else
                    Require(fixture.SessionId() == fixture.Session, "A blocked but still-owned request was silently abandoned after a gap: " + blocker);
            }
            finally { replacement?.Dispose(); }
        }
    }
}
