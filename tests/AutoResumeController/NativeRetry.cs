using TerminalNotes.Services;

internal static partial class Program
{
    private static async Task NativeRetryDeadlineAndStallRecoveryAsync()
    {
        using var fixture = new Fixture("native-retry-deadline");
        await fixture.BindAsync(observeTodo: false);
        fixture.Api.NoTodos = true;
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Retry);
        fixture.Api.RetryNext = fixture.Clock.GetUtcNow().AddMinutes(30).ToUnixTimeMilliseconds();
        int restarts = 0;
        fixture.Controller.RestartStalledProcessWithOutcomeAsync = async request =>
        {
            Require(request.CanRestart() && await request.VerifyAsync(), "Expired retry restart lacked final native verification.");
            restarts++;
            return OpenCodeRestartOutcome.Succeeded;
        };
        await fixture.Controller.TickAsync();
        await AdvanceBusyAsync(fixture, 20);
        fixture.Api.RetryNext = fixture.Clock.GetUtcNow().AddMinutes(30).ToUnixTimeMilliseconds();
        await AdvanceBusyAsync(fixture, 30);
        Require(fixture.Api.AbortPosts.Count == 0 && restarts == 0, "An advancing native provider deadline was interrupted.");
        for (int minute = 0; minute < AbortMinutes - 1; minute++)
        {
            fixture.Api.RetryAttempt++;
            fixture.Api.RetryMessage = "provider status update " + minute;
            await AdvanceBusyAsync(fixture, 1);
        }
        Require(fixture.Api.AbortPosts.Count == 0, "Retry expiry did not get a fresh full inactivity interval.");
        await AdvanceBusyAsync(fixture, 1);
        Require(fixture.Api.AbortPosts.Count == 1, "Expired retry status churn kept the exact unfinished request stuck forever.");
        await AdvanceBusyAsync(fixture, 2);
        Require(restarts == 1 && fixture.Api.ContinuePosts.Count == 0, "An unchanged ignored retry abort did not request one process restart.");
        await AdvanceBusyAsync(fixture, 10);
        Require(restarts == 1, "A single retry stall requested duplicate process replacements.");
    }

    // The server asks for 60-120 seconds but OpenCode would retry in 30: this terminal stops that retry, waits the
    // server's time, then continues. Another terminal is not held.
    private static async Task ServerNamedWaitHoldsOnlyThisTerminalAsync()
    {
        using var fixture = new Fixture("server-wait");
        using var other = new Fixture("server-wait-other");
        await fixture.BindAsync();
        await other.BindAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        await fixture.Controller.TickAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Retry);
        fixture.Api.RetryMessage = "요청이 많습니다. 60-120초 후 다시 시도하세요.";
        fixture.Api.RetryNext = fixture.Clock.GetUtcNow().AddSeconds(30).ToUnixTimeMilliseconds();
        DateTimeOffset limitedAt = fixture.Clock.GetUtcNow();
        for (int tick = 0; tick < 4 && fixture.Api.AbortPosts.Count == 0; tick++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            await fixture.Controller.TickAsync();
        }
        await WaitForPostAsync(fixture.Api, abort: true);
        Require(fixture.Api.AbortPosts.Count == 1, "A native retry earlier than the server's wait was not stopped.");
        Require(fixture.Controller.ServerWaitUntil is { } until && until >= limitedAt.AddSeconds(120) && until <= limitedAt.AddSeconds(140),
            "The terminal did not hold for the upper end of the server's 60-120 seconds.");
        Require(other.Controller.ServerWaitUntil is null, "Another terminal was held by this terminal's limit.");

        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Idle);
        fixture.Api.RetryNext = null;
        while (fixture.Clock.GetUtcNow() < limitedAt.AddSeconds(115))
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(10));
            await fixture.Controller.TickAsync();
        }
        Require(fixture.Api.ContinuePosts.Count == 0 && fixture.Controller.Status.Contains("서버 요청 제한"),
            "The terminal sent a request before the server's wait or did not show it was waiting.");
        for (int tick = 0; tick < 12 && fixture.Api.ContinuePosts.Count == 0; tick++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(5));
            await fixture.Controller.TickAsync();
        }
        await WaitForPostAsync(fixture.Api);
        Require(fixture.Api.ContinuePosts.Count == 1 && fixture.Api.AbortPosts.Count == 1 && fixture.Controller.ServerWaitUntil is null,
            "The terminal did not continue once after the server's wait.");
    }

    private static async Task NativeRetryActivityAndBlockersAsync()
    {
        using var fixture = new Fixture("native-retry-activity");
        await fixture.BindAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Retry);
        await fixture.Controller.TickAsync();
        await AdvanceBusyAsync(fixture, AbortMinutes - 1);
        fixture.Api.SetAssistantText("Current reasoning is still streaming.");
        await fixture.Controller.TickAsync();
        await AdvanceBusyAsync(fixture, AbortMinutes - 1);
        Require(fixture.Api.AbortPosts.Count == 0, "Genuine current-turn activity did not reset expired retry inactivity.");
        fixture.Api.SetPendingInput(permission: true);
        await AdvanceBusyAsync(fixture, 20);
        Require(fixture.Api.AbortPosts.Count == 0, "Retry recovery bypassed native permission waiting.");
        fixture.Api.SetPendingInput();
        await fixture.Controller.TickAsync();
        await AdvanceBusyAsync(fixture, AbortMinutes);
        Require(fixture.Api.AbortPosts.Count == 1, "Cleared permission did not permit a fresh retry stall recovery.");
        // A provider can publish a new wait after accepting abort. That wait must
        // disarm the old abort rather than terminate the host when it expires.
        fixture.Api.RetryNext = fixture.Clock.GetUtcNow().AddMinutes(10).ToUnixTimeMilliseconds();
        fixture.Controller.RestartStalledProcessWithOutcomeAsync = _ => throw new InvalidOperationException("Premature retry restart.");
        await AdvanceBusyAsync(fixture, 10);
        Require(fixture.Api.AbortPosts.Count == 1, "A renewed provider wait reused the stale abort interval.");
        fixture.Controller.ObserveLocalInput("\x03");
        await AdvanceBusyAsync(fixture, 30);
        Require(fixture.Api.AbortPosts.Count == 1, "Manual stop failed to latch during native retry recovery.");

        using var tool = new Fixture("native-retry-tool");
        await tool.BindAsync();
        tool.Api.SetStatus(OpenCodeRecoveryStatus.Retry);
        tool.Api.SetRunningTool(true);
        await tool.Controller.TickAsync();
        await AdvanceBusyAsync(tool, 40);
        Require(tool.Api.AbortPosts.Count == 0, "Retry recovery interrupted an active native tool.");
    }

    private static async Task DeferredStalledRestartKeepsRetryingAsync()
    {
        using var fixture = new Fixture("restart-deferred");
        await fixture.BindAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        await fixture.Controller.TickAsync();
        int attempts = 0;
        fixture.Controller.RestartStalledProcessWithOutcomeAsync = async request =>
        {
            Require(request.CanRestart() && await request.VerifyAsync(), "A deferred restart failed exact native revalidation.");
            attempts++;
            return attempts < 7 ? OpenCodeRestartOutcome.RetryLater : OpenCodeRestartOutcome.Succeeded;
        };
        await AdvanceBusyAsync(fixture, AbortMinutes + 2);
        Require(attempts == 1 && !fixture.Controller.IsRecoveryPaused, "A proven pre-dispatch deferral paused the Loop.");
        for (int retry = 1; retry <= 6; retry++)
        {
            int delay = retry == 1 ? 1 : retry == 2 ? 3 : 10;
            if (delay > 1) await AdvanceBusyAsync(fixture, delay - 1);
            Require(attempts == retry, "A deferred restart ignored its escalating backoff.");
            await AdvanceBusyAsync(fixture, 1);
            Require(attempts == retry + 1, "A deferred restart hit an arbitrary attempt limit.");
        }
        await AdvanceBusyAsync(fixture, 20);
        Require(attempts == 7, "A successful replacement kept rescheduling old-owner recovery.");
    }

    private static async Task StructuredRateLimitSchedulingAsync()
    {
        foreach (bool relative in new[] { true, false })
        {
            using var fixture = new Fixture("rate-reset-" + relative);
            await fixture.BindAsync();
            fixture.Api.NoTodos = true;
            fixture.Api.SetNativeFailure("APIError", 429, false);
            var deadline = fixture.Clock.GetUtcNow().AddMinutes(90);
            fixture.Api.ErrorResponseHeaders = relative
                ? new() { ["Retry-After"] = "5400" }
                : new() { ["x-ratelimit-reset"] = deadline.ToUnixTimeSeconds().ToString(global::System.Globalization.CultureInfo.InvariantCulture) };
            await fixture.Controller.TickAsync();
            Require(fixture.Controller.ErrorRetryAt == deadline, "Structured provider reset time was not retained for a non-immediate 429.");
            await AdvanceFailureAsync(fixture, TimeSpan.FromMinutes(89));
            Require(fixture.Api.ContinuePosts.Count == 0 && fixture.Controller.ErrorRetryAt == deadline,
                "Provider retry timing drifted on polls or sent before reset.");
            await AdvanceFailureAsync(fixture, TimeSpan.FromMinutes(1));
            Require(fixture.Api.ContinuePosts.Count == 1, "The scheduled quota probe never resumed after the provider reset.");
        }
        using var auth = new Fixture("retry-auth-gate");
        await auth.BindAsync();
        auth.Api.SetNativeFailure("ProviderAuthError", 429, true);
        auth.Api.SetStatus(OpenCodeRecoveryStatus.Retry);
        await auth.Controller.TickAsync();
        await AdvanceBusyAsync(auth, 40);
        Require(auth.Api.ContinuePosts.Count == 0 && auth.Api.AbortPosts.Count == 0,
            "An authentication error was recovered because it carried rate-limit metadata.");
    }

    private static async Task RetryAndCompletedLoopExitSnapshotsAsync()
    {
        using var retry = new Fixture("retry-exit-snapshot");
        await retry.BindAsync();
        retry.Controller.SetContinuousImprovement(true, null);
        retry.Api.SetStatus(OpenCodeRecoveryStatus.Retry);
        var deadline = retry.Clock.GetUtcNow().AddHours(2);
        retry.Api.RetryNext = deadline.ToUnixTimeMilliseconds();
        await retry.Controller.TickAsync();
        var snapshot = retry.Controller.GetConfirmedRecoverySnapshot();
        Require(snapshot is { AutomaticRetryEnabled: true } && snapshot.NotBeforeUtc == deadline,
            "A confirmed owned-host exit during provider retry lost its exact request or wait deadline.");
        retry.Api.SetPendingInput(question: true);
        await retry.Controller.TickAsync();
        Require(retry.Controller.GetConfirmedRecoverySnapshot() is null, "A retry exit snapshot bypassed a pending question.");

        using var complete = new Fixture("completed-loop-exit-snapshot");
        await complete.BindAsync();
        complete.Controller.SetContinuousImprovement(true, null);
        complete.Api.CompleteTask = true;
        await complete.Controller.TickAsync();
        Require(complete.Controller.GetConfirmedRecoverySnapshot() is { AutomaticRetryEnabled: true },
            "A completed Loop lost its next review when its owned host exited.");
        complete.Controller.SetContinuousImprovement(false, null);
        Require(complete.Controller.GetConfirmedRecoverySnapshot() is null, "Completed non-Loop work was offered for replay.");
    }

    private static async Task ProviderDeadlineRemainsMandatoryAndFreshAsync()
    {
        using var retry = new Fixture("retry-combined-deadlines");
        await retry.BindAsync();
        retry.Api.SetNativeFailure("APIError", 429, false);
        retry.Api.ErrorResponseHeaders = new() { ["Retry-After"] = "3600" };
        retry.Api.SetStatus(OpenCodeRecoveryStatus.Retry);
        retry.Api.RetryNext = retry.Clock.GetUtcNow().AddSeconds(5).ToUnixTimeMilliseconds();
        await retry.Controller.TickAsync();
        var originalDeadline = retry.Clock.GetUtcNow().AddHours(1);
        Require(retry.Controller.GetConfirmedRecoverySnapshot()?.NotBeforeUtc == originalDeadline,
            "A shorter native retry.next overrode the provider reset deadline in an exit snapshot.");
        await AdvanceBusyAsync(retry, 30);
        Require(retry.Api.AbortPosts.Count == 0, "Expired native retry.next bypassed a later provider reset.");
        retry.Api.ErrorResponseHeaders = new() { ["Retry-After"] = "7200" };
        await retry.Controller.TickAsync();
        var extendedDeadline = retry.Clock.GetUtcNow().AddHours(2);
        Require(retry.Controller.ErrorRetryAt == extendedDeadline, "Changed structured error timing on the same request was ignored.");
        await AdvanceBusyAsync(retry, 30);
        Require(retry.Api.AbortPosts.Count == 0 && retry.Controller.ErrorRetryAt == extendedDeadline,
            "An extended provider wait drifted on polls or permitted an early abort.");

        using var manual = new Fixture("manual-provider-deadline");
        await manual.BindAsync();
        manual.Controller.EnableTaskCompletionMonitoring();
        manual.Controller.SetEnabled(false);
        manual.Api.SetNativeFailure("APIError", 429, false);
        var reset = manual.Clock.GetUtcNow().AddMinutes(10);
        // Property order cannot allow a shorter absolute Retry-After to replace
        // the later structured reset deadline.
        manual.Api.ErrorResponseHeaders = new()
        {
            ["x-ratelimit-reset"] = reset.ToUnixTimeSeconds().ToString(global::System.Globalization.CultureInfo.InvariantCulture),
            ["Retry-After"] = manual.Clock.GetUtcNow().AddMinutes(5).ToString("R")
        };
        await manual.Controller.TickAsync();
        Require(manual.Controller.GetConfirmedRecoverySnapshot()?.NotBeforeUtc == reset,
            "Disabled automation or header property order lost the mandatory provider wait.");
        Require(!await manual.Controller.RetryErrorAsync() && manual.Api.ContinuePosts.Count == 0,
            "Explicit live retry bypassed the provider reset deadline.");
        await AdvanceFailureAsync(manual, TimeSpan.FromMinutes(10));
        Require(await manual.Controller.RetryErrorAsync() && manual.Api.ContinuePosts.Count == 1,
            "Manual retry remained blocked after the verified provider wait expired.");

        using var race = new Fixture("provider-final-reread");
        await race.BindAsync();
        race.Api.SetNativeFailure("APIError", 429, false);
        await race.Controller.TickAsync();
        race.Api.BeforeMessageLookup = () =>
        {
            race.Api.BeforeMessageLookup = null;
            race.Api.ErrorResponseHeaders = new() { ["Retry-After"] = "3600" };
        };
        await AdvanceFailureAsync(race, TimeSpan.FromMinutes(1));
        Require(race.Api.ContinuePosts.Count == 0 && !race.Controller.IsRecoveryPaused,
            "A provider deadline appearing during final request preflight was bypassed or permanently paused.");
        await race.Controller.TickAsync();
        Require(race.Controller.ErrorRetryAt >= race.Clock.GetUtcNow().AddHours(1),
            "A newly published final-preflight provider wait was not anchored on the next verified observation.");
    }

    private static async Task MissingNativeErrorDataRemainsReadableAsync()
    {
        using var fixture = new Fixture("error-without-data");
        await fixture.BindAsync();
        fixture.Api.MessagesOverride = () => global::System.Text.Json.JsonSerializer.Serialize(new object[]
        {
            new { info = new { id = fixture.UserMessage, sessionID = fixture.Session, role = "user", time = new { created = fixture.Clock.GetUtcNow().ToUnixTimeMilliseconds() } }, parts = Array.Empty<object>() },
            new { info = new { id = "native-network-error", sessionID = fixture.Session, role = "assistant", parentID = fixture.UserMessage,
                time = new { created = fixture.Clock.GetUtcNow().ToUnixTimeMilliseconds() }, error = new { name = "NetworkError" } }, parts = Array.Empty<object>() }
        });
        await fixture.Controller.TickAsync();
        Require(fixture.Controller.CanRetryError && fixture.Controller.ErrorRetryAt is not null,
            "A structured error without an optional data object became unreadable.");
    }
}
