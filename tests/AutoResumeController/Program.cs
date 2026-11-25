using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TerminalNotes.Services;

internal static partial class Program
{
    // Stall cancel interval of the recovery policy (no running tool).
    private static readonly int AbortMinutes = (int)OpenCodeRecoveryPolicy.StalledAbortPeriod.TotalMinutes;
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);
    private static int _assertions;

    private static async Task<int> Main()
    {
        try
        {
            await MissedTodoSignalScenariosAsync();
            await ContinuationAdmissionScenariosAsync();
            await ContextRecoveryScenariosAsync();
            await HistoricalWorkIsNeverBoundAsync();
            await ReadOnlyCompletionMonitoringAsync();
            await VerifiedNotificationStallEvidenceAsync();
            await CompletedContinuousWorkIsNotReimplementedAsync();
            await CompletedLoopWithoutTodoSignalsAsync();
            await RecommendedPlanLoopScenariosAsync();
            await IntermediateToolErrorsAllowVerifiedLoopAndTransientRetryAsync();
            await CompletedLoopReadFailuresRetrySafelyAsync();
            await ConfirmedLoopReceiptSurvivesSlowNativeReadsAsync();
            await CompletedLoopUncertainSendNeverReplaysAsync();
            await CompletedLoopRespectsSafetyAndScopeAsync();
            await BusyQueuedRequestsProtectEarlierWorkAsync();
            await ContinuousImprovementExpiryStopsItsActiveTaskAsync();
            await ContinuousRecoveryKeepsRetryingWithoutCountLimitAsync();
            await TerminalReplyDoesNotInvalidateBoundRecoveryAsync();
            await BlankEnterDoesNotArmForeignUserMessageAsync();
            await ApiSubmissionFiltersUnrelatedNativeMessagesAndReconcilesAsync();
            await LocalInputInvalidatesLateApiSubmissionConfirmationAsync();
            await DuplicateUserMetadataPreservesPostUserTodoSignalAsync();
            await DelayedCandidateExpiresAsync();
            await IdleTodoContinuesExactlyOnceAsync();
            await ProgressDuringFinalContinueReadBlocksPostAsync();
            await AcceptedContinuationIsNotDuplicatedAsync();
            await UnexpectedReadFaultRetainsPinnedRequestAndRetriesAsync();
            await AmbiguousContinuationIsNeverRepeatedAsync();
            await SafetyBlockersPreventRecoveryAsync();
            await StalledGeneratedAppLaunchIsAbortedAndResumedSafelyAsync();
            await MixedAbortAndProviderErrorsRemainBlockedAsync();
            await CancellationInvalidatesPendingReadsAsync();
            await UnknownAndChildSessionsAreRejectedAsync();
            await StalledRequestWithoutTodosRecoversAsync();
            await IgnoredAbortRequestsOneVerifiedProcessRestartAsync();
            await RepeatedRestartsPreserveOriginalInstructionAsync();
            await RestartProtectsFreshActivityAndManualInputAsync();
            await LongBusySessionIsAbortedThenContinuedAfterIdleAsync();
            await ProgressDuringFinalAbortReadBlocksAbortAsync();
            await DifferentSessionsInSameProjectRemainIndependentAsync();
            await TransientFailureBackoffHasNoLimitAsync();
            await AuthenticationFailureRequiresManualRetryWithAutomationOffAsync();
            await FailureRetryProtectsNativeRetryAndNewInputAsync();
            await AmbiguousFailureRetryNeverReplaysAsync();
            await OwnedExitSnapshotsPreserveOnlyConfirmedWorkAsync();
            await FailureRetryRequiresExactAssistantParentAsync();
            await ManualRetryContainsUnexpectedReadFaultAsync();
            await AmbiguousFailureReceiptReconcilesWithoutBusyAsync();
            await ConcurrentProjectsIsolateRecoveryActionsAsync();
            await SameProjectRecoveryActionsRemainSerializedAsync();
            await NativeRetryDeadlineAndStallRecoveryAsync();
            await NativeRetryActivityAndBlockersAsync();
            await ServerNamedWaitHoldsOnlyThisTerminalAsync();
            await DeferredStalledRestartKeepsRetryingAsync();
            await StructuredRateLimitSchedulingAsync();
            await RetryAndCompletedLoopExitSnapshotsAsync();
            await ProviderDeadlineRemainsMandatoryAndFreshAsync();
            await MissingNativeErrorDataRemainsReadableAsync();
            await LongPollingGapReconcilesOwnedLoopAsync();
            await LongPollingGapPreservesRetryDeadlinesAsync();
            await LongPollingGapRequiresFreshStallEvidenceAsync();
            await LongPollingGapPreservesAmbiguousAbortAsync();
            await LongPollingGapPreservesSafetyFencesAsync();
            await OvernightLoopRecoverySimulationAsync();
            Console.WriteLine($"PASS: auto-resume controller/client integration checks ({_assertions} assertions).");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
    }

    private static async Task ReadOnlyCompletionMonitoringAsync()
    {
        using var fixture = new Fixture("s-read-only-completion");
        fixture.Controller.EnableTaskCompletionMonitoring();
        fixture.Controller.ObserveLocalInput("do work\r");
        fixture.Controller.ObserveNativeSignal(UserSignal(fixture, fixture.Session, fixture.UserMessage, fixture.Clock.GetUtcNow(), 1));
        await UntilAsync(() => fixture.SessionId() == fixture.Session, "binding a read-only completion monitor");
        fixture.Controller.ObserveNativeSignal(new("todo.updated", fixture.Session, null, null, 2));
        await fixture.Controller.TickAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Controller.TickAsync();
        Require(fixture.Api.ContinuePosts.Count == 0 && fixture.Api.AbortPosts.Count == 0, "Read-only completion monitoring enabled automatic actions.");
        Require(!fixture.Controller.HasVerifiedCompletion, "An unfinished task was marked completed.");
        fixture.Api.CompleteTask = true;
        await fixture.Controller.TickAsync();
        Require(fixture.Controller.HasVerifiedCompletion, "A verified single task was not completed with automatic progress disabled.");
        fixture.Api.SetPendingInput(question: true);
        await fixture.Controller.TickAsync();
        Require(!fixture.Controller.HasVerifiedCompletion, "A pending question retained completed task status.");
    }

    private static async Task VerifiedNotificationStallEvidenceAsync()
    {
        using (var quiet = new Fixture("s-notification-quiet"))
        {
            quiet.Controller.SetEnabled(true);
            await quiet.BindAsync();
            await quiet.Controller.TickAsync();
            Require(!quiet.Controller.HasVerifiedNotificationStall,
                "Ordinary quiet or idle state was reported as a verified notification stall.");
        }

        using (var stalled = new Fixture("s-notification-stalled"))
        {
            stalled.Controller.SetEnabled(true);
            await stalled.BindAsync();
            stalled.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
            await stalled.Controller.TickAsync();
            for (int minute = 0; minute < 5; minute++)
            {
                stalled.Clock.Advance(TimeSpan.FromMinutes(1));
                await stalled.Controller.TickAsync();
            }
            Require(stalled.Controller.HasVerifiedNotificationStall,
                "A fresh five-minute no-tool native stall was not exported.");
            stalled.Api.SetPendingInput(question: true);
            await stalled.Controller.TickAsync();
            Require(!stalled.Controller.HasVerifiedNotificationStall,
                "A pending question retained verified stall evidence.");
        }

        using (var observed = new Fixture("s-notification-auto-off"))
        {
            observed.Controller.EnableTaskCompletionMonitoring();
            observed.Controller.ObserveLocalInput("do work\r");
            observed.Controller.ObserveNativeSignal(UserSignal(observed, observed.Session, observed.UserMessage, observed.Clock.GetUtcNow(), 1));
            await UntilAsync(() => observed.SessionId() == observed.Session, "binding notification-only stall evidence");
            observed.Controller.ObserveNativeSignal(new("todo.updated", observed.Session, null, null, 2));
            observed.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
            await observed.Controller.TickAsync();
            for (int minute = 0; minute < 5; minute++)
            {
                observed.Clock.Advance(TimeSpan.FromMinutes(1));
                await observed.Controller.TickAsync();
            }
            Require(observed.Controller.HasVerifiedNotificationStall,
                "Notification-only monitoring depended on AutoResume being enabled.");
            Require(observed.Api.ContinuePosts.Count == 0 && observed.Api.AbortPosts.Count == 0,
                "Notification-only stall tracking dispatched an automatic recovery action.");

            observed.Api.SetAssistantText("Streaming assistant output");
            await observed.Controller.TickAsync();
            Require(!observed.Controller.HasVerifiedNotificationStall,
                "Fresh streamed assistant activity did not reset notification stall continuity.");
            for (int minute = 0; minute < 4; minute++)
            {
                observed.Clock.Advance(TimeSpan.FromMinutes(1));
                await observed.Controller.TickAsync();
            }
            Require(!observed.Controller.HasVerifiedNotificationStall,
                "Assistant activity reset expired before five fresh minutes of quiet.");
            observed.Clock.Advance(TimeSpan.FromMinutes(1));
            await observed.Controller.TickAsync();
            Require(observed.Controller.HasVerifiedNotificationStall,
                "A renewed no-progress interval was not exported after five minutes.");

            observed.Api.SetCompletedToolEvidence(true);
            await observed.Controller.TickAsync();
            Require(!observed.Controller.HasVerifiedNotificationStall,
                "Structured completed-tool progress did not reset notification stall continuity.");
            for (int minute = 0; minute < 4; minute++)
            {
                observed.Clock.Advance(TimeSpan.FromMinutes(1));
                await observed.Controller.TickAsync();
            }
            Require(!observed.Controller.HasVerifiedNotificationStall,
                "Structured progress reset expired before five fresh minutes of no progress.");
            observed.Clock.Advance(TimeSpan.FromMinutes(1));
            await observed.Controller.TickAsync();
            Require(observed.Controller.HasVerifiedNotificationStall &&
                observed.Api.ContinuePosts.Count == 0 && observed.Api.AbortPosts.Count == 0,
                "A renewed no-progress stall was not notification-only after five minutes.");
        }
        using (var tool = new Fixture("s-notification-tool"))
        {
            tool.Controller.SetEnabled(true);
            await tool.BindAsync();
            tool.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
            tool.Api.SetRunningTool(true);
            await tool.Controller.TickAsync();
            for (int minute = 0; minute < 14; minute++)
            {
                tool.Clock.Advance(TimeSpan.FromMinutes(1));
                await tool.Controller.TickAsync();
            }
            Require(!tool.Controller.HasVerifiedNotificationStall,
                "A running tool was called stalled before the 15-minute threshold.");
            tool.Clock.Advance(TimeSpan.FromMinutes(1));
            await tool.Controller.TickAsync();
            Require(tool.Controller.HasVerifiedNotificationStall,
                "A 15-minute unchanged running-tool interval was not exported.");

            tool.Clock.Advance(TimeSpan.FromSeconds(31));
            Require(!tool.Controller.HasVerifiedNotificationStall,
                "A cached stall remained authoritative after its fresh-read window.");
            await tool.Controller.TickAsync();
            Require(tool.Controller.HasVerifiedNotificationStall,
                "A new authoritative read did not refresh a still-current stall.");
            tool.Clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
            await tool.Controller.TickAsync();
            Require(!tool.Controller.HasVerifiedNotificationStall,
                "A long native evidence gap retained verified stall state.");
        }
    }
    private static async Task CompletedContinuousWorkIsNotReimplementedAsync()
    {
        using var fixture = new Fixture("s-completed-continuous");
        fixture.Controller.SetEnabled(true);
        fixture.Controller.SetContinuousImprovement(true, until: null);
        await fixture.BindAsync();
        fixture.Api.CompleteTask = true;
        await fixture.Controller.TickAsync();
        Require(fixture.Controller.HasVerifiedCompletion && !fixture.Controller.HasUnfinishedWork &&
            !fixture.Controller.HasContinuousImprovementWork,
            "Completed native todos must remain completed during the Loop review interval.");
        Require(fixture.Controller.IsContinuousImprovementActive && fixture.Controller.Status.Contains("20초 후") && fixture.Api.ContinuePosts.Count == 0,
            "Loop shows its initial review countdown instead of appearing stopped.");
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        await fixture.Controller.TickAsync();
        Require(fixture.Api.ContinuePosts.Count == 1 && fixture.Api.AbortPosts.Count == 0,
            "Completed Loop dispatches exactly one new result review after settling.");
        using JsonDocument reviewBody = JsonDocument.Parse(fixture.Api.ContinuePosts.Single().Body);
        string prompt = reviewBody.RootElement.GetProperty("parts")[0].GetProperty("text").GetString()!;
        Require(prompt.Contains("실제 코드") && prompt.Contains("사용자가 원하는 서비스에 가장 도움이 되는 개선") && prompt.Contains("변경 규모가 커도 주저하지 말고") && prompt.Contains("이미 한 구현을 반복") && prompt.Contains("근거 있는 개선이 없으면"),
            "Loop reviews real evidence, picks the improvement that best serves the user's goal without shrinking it, and forbids invented/repeated work.");
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Controller.TickAsync();
        Require(fixture.Api.ContinuePosts.Count == 1 && fixture.Controller.Status.Contains("60초 후"),
            "A completed review with unchanged todos enters a one-minute backoff without duplicate dispatch.");
        fixture.Clock.Advance(TimeSpan.FromSeconds(60));
        await fixture.Controller.TickAsync();
        Require(fixture.Api.ContinuePosts.Count == 2, "An idle Loop automatically resumes its next result review.");
        // This fake stamps successive user receipts a few seconds ahead; the
        // final assistant must follow its exact parent, just as on the server.
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        await fixture.Controller.TickAsync();
        // Unchanged rounds come further apart (1, 3, then 10 minutes) but never end the Loop (사용자: 멈추면 Loop가 무슨 의미가 있어).
        Require(fixture.Controller.Status.Contains("180초 후") && fixture.Controller.IsContinuousImprovementActive && fixture.Api.ContinuePosts.Count == 2,
            $"A second unchanged review did not wait three minutes and keep the Loop going (status={fixture.Controller.Status}, posts={fixture.Api.ContinuePosts.Count}).");
        var statuses = new List<string>();
        async Task<int> MinutesUntilPostAsync(int posts)
        {
            for (int minute = 1; minute <= 15; minute++)
            {
                fixture.Clock.Advance(TimeSpan.FromMinutes(1));
                await fixture.Controller.TickAsync();
                statuses.Add(fixture.Controller.Status);
                if (fixture.Api.ContinuePosts.Count == posts) return minute;
            }
            return -1;
        }
        int third = await MinutesUntilPostAsync(3);
        Require(third == 3, $"The Loop did not review again three minutes after an unchanged review (minute {third}; {string.Join(" / ", statuses)}).");
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        await fixture.Controller.TickAsync();
        Require(fixture.Controller.Status.Contains("600초 후"), $"Unchanged reviews did not settle at ten minutes apart (status={fixture.Controller.Status}).");
        statuses.Clear();
        int fourth = await MinutesUntilPostAsync(4);
        Require(fourth == 10 && fixture.Api.AbortPosts.Count == 0 && !fixture.Controller.Status.Contains("완료"),
            $"An unchanged Loop did not keep reviewing ten minutes apart (minute {fourth}; {string.Join(" / ", statuses)}).");
    }

    private static async Task IntermediateToolErrorsAllowVerifiedLoopAndTransientRetryAsync()
    {
        using (var completed = new Fixture("s-loop-intermediate-tool-error"))
        {
            completed.Controller.SetContinuousImprovement(true, null);
            await completed.BindAsync();
            completed.Api.IncludeEarlierToolError = true;
            completed.Api.CompleteTask = true;
            await completed.Controller.TickAsync();
            Require(completed.Controller.Status.Contains("20초 후"),
                "A recovered intermediate tool error prevented scheduling the next completed Loop turn.");
            completed.Clock.Advance(TimeSpan.FromSeconds(20));
            await completed.Controller.TickAsync();
            Require(completed.Api.ContinuePosts.Count == 1 && completed.Api.ContinuePosts.Single().SessionId == completed.Session,
                "A recovered intermediate tool error prevented exactly one same-session Loop request.");
        }
        using var failed = new Fixture("s-loop-tool-error-then-503");
        failed.Controller.SetContinuousImprovement(true, null);
        await failed.BindAsync();
        failed.Api.IncludeEarlierToolError = true;
        failed.Api.SetNativeFailure("APIError", 503, true);
        await failed.Controller.TickAsync();
        Require(failed.Controller.ErrorRetryAt == failed.Clock.GetUtcNow().AddMinutes(1),
            "An intermediate tool error prevented scheduling a terminal transient failure retry.");
        await AdvanceFailureAsync(failed, TimeSpan.FromMinutes(1));
        Require(failed.Api.ContinuePosts.Count == 1 && failed.Api.ContinuePosts.Single().SessionId == failed.Session,
            "An intermediate tool error blocked the independently verified terminal 503 retry.");
    }

    private static async Task CompletedLoopWithoutTodoSignalsAsync()
    {
        foreach (bool noTodos in new[] { true, false })
        {
            using var fixture = new Fixture("s-loop-no-signal-" + noTodos);
            fixture.Api.NoTodos = noTodos;
            await fixture.BindAsync(observeTodo: false);
            fixture.Api.CompleteTask = true;
            await fixture.Controller.TickAsync();
            fixture.Controller.SetContinuousImprovement(true, null); // Also covers enabling Loop after completion.
            await fixture.Controller.TickAsync();
            Require(!fixture.Controller.HasVerifiedCompletion,
                "Loop eligibility must not fabricate causally verified native todo completion.");
            Require(fixture.Controller.Status.Contains("20초 후"),
                "An exact successful final without a todo signal did not schedule the next Loop review.");
            fixture.Clock.Advance(TimeSpan.FromSeconds(20));
            await fixture.Controller.TickAsync();
            Require(fixture.Api.ContinuePosts.Count == 1 && fixture.Api.ContinuePosts.Single().SessionId == fixture.Session,
                "Loop without native todos or their SSE event did not send one exact-session improvement request.");
            using var body = JsonDocument.Parse(fixture.Api.ContinuePosts.Single().Body);
            string prompt = body.RootElement.GetProperty("parts")[0].GetProperty("text").GetString()!;
            Require(prompt.Contains("지속 개선") && prompt.Contains("가장 도움이 되는 개선") && prompt.Contains(JsonSerializer.Serialize(fixture.PlanPath!)),
                "The next completed Loop turn must request the most useful improvement in the original plan scope.");
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.Controller.TickAsync();
            Require(fixture.Controller.Status.Contains("60초 후"), "An unchanged fast Loop result did not enter one-minute pacing.");
            fixture.Clock.Advance(TimeSpan.FromSeconds(59));
            await fixture.Controller.TickAsync();
            Require(fixture.Api.ContinuePosts.Count == 1, "Repeated reads duplicated the accepted improvement prompt.");
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.Controller.TickAsync();
            Require(fixture.Api.ContinuePosts.Count == 2 && fixture.Api.ContinuePosts.Select(p => p.MessageId).Distinct().Count() == 2,
                "A fresh exact completed Loop turn did not resume with a distinct request after pacing.");
        }
        foreach (string blocker in new[] { "permission", "question", "busy", "tool", "error", "stop", "new-input", "stale-parent", "unfinished-todo", "cancelled-todo", "interrupted", "changed-plan" })
        {
            using var fixture = new Fixture("s-loop-no-todo-blocked-" + blocker);
            fixture.Api.NoTodos = true;
            fixture.Controller.SetContinuousImprovement(true, null);
            await fixture.BindAsync(observeTodo: false);
            fixture.Api.CompleteTask = true;
            switch (blocker)
            {
                case "permission": fixture.Api.SetPendingInput(permission: true); break;
                case "question": fixture.Api.SetPendingInput(question: true); break;
                case "busy": fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy); break;
                case "tool": fixture.Api.SetRunningTool(true); break;
                case "error": fixture.Api.SetMessageError(true); break;
                case "stop": fixture.Controller.ObserveLocalInput("\x03"); break;
                case "new-input": fixture.Controller.ObserveLocalInput("draft"); break;
                case "stale-parent": fixture.Api.FinalParentOverride = "unrelated-user"; break;
                case "unfinished-todo": fixture.Api.NoTodos = false; fixture.Api.PendingTodoOverride = true; break;
                case "cancelled-todo": fixture.Api.NoTodos = false; fixture.Api.TodoStatusOverride = "cancelled"; break;
                case "interrupted": fixture.Api.FinalFinishOverride = "tool-calls"; break;
                case "changed-plan": File.AppendAllText(fixture.PlanPath!, "\nNew scope."); break;
            }
            await fixture.Controller.TickAsync();
            if (blocker == "error")
            {
                // With Loop on, any failure a person could retry is retried after the 1·3·10-minute delay with the error
                // recovery prompt, never as a completed-result review (사용자: 멈추면 어떻게든 다시 실행해 계속 이어가게).
                for (int i = 0; i < 2; i++) { fixture.Clock.Advance(TimeSpan.FromSeconds(20)); await fixture.Controller.TickAsync(); }
                Require(fixture.Api.ContinuePosts.Count == 0, "A Loop error was retried before its one-minute delay.");
                for (int i = 0; i < 2; i++) { fixture.Clock.Advance(TimeSpan.FromSeconds(20)); await fixture.Controller.TickAsync(); }
                Require(fixture.Api.ContinuePosts.Count == 1 && fixture.Api.AbortPosts.Count == 0 &&
                    PostPrompt(fixture.Api.ContinuePosts.Single()).Contains("이전 요청에서 오류가 발생했습니다") &&
                    !ReviewPrompt(fixture.Api.ContinuePosts.Single()),
                    "A Loop error was not retried with the error recovery prompt after its delay.");
                continue;
            }
            for (int i = 0; i < 3; i++) { fixture.Clock.Advance(TimeSpan.FromSeconds(20)); await fixture.Controller.TickAsync(); }
            if (blocker is "stale-parent" or "interrupted")
                Require(!fixture.Controller.HasVerifiedCompletion && fixture.Api.AbortPosts.Count == 0 &&
                    fixture.Api.ContinuePosts.All(post => !post.Body.Contains("Loop")),
                    $"An unrelated or interrupted final response authorized a completed Loop review ({blocker}).");
            else
                Require(fixture.Api.ContinuePosts.Count == 0 && fixture.Api.AbortPosts.Count == 0,
                    $"A completed no-todo Loop bypassed the {blocker} safety fence.");
        }
    }

    private static async Task CompletedLoopReadFailuresRetrySafelyAsync()
    {
        using var fixture = new Fixture("s-loop-transient-read");
        fixture.Controller.SetContinuousImprovement(true, null);
        await fixture.BindAsync();
        fixture.Api.CompleteTask = true;
        await fixture.Controller.TickAsync();
        fixture.Api.StatusFailuresRemaining = 4;
        foreach (int delay in new[] { 0, 10, 20, 40 })
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(delay));
            await fixture.Controller.TickAsync();
            Require(fixture.SessionId() == fixture.Session && fixture.Api.ContinuePosts.Count == 0,
                "Transient native reads lost the Loop request or posted without fresh evidence.");
            int reads = fixture.Api.ReadCount;
            await fixture.Controller.TickAsync();
            Require(reads == fixture.Api.ReadCount, "Native read failure bypassed its bounded retry delay.");
        }
        fixture.Clock.Advance(TimeSpan.FromSeconds(60));
        await fixture.Controller.TickAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        await fixture.Controller.TickAsync();
        Require(fixture.Api.ContinuePosts.Count == 1, "Loop did not resume after10/20/40/60-second transient read recovery.");

        using var preflight = new Fixture("s-loop-preflight-read");
        preflight.Controller.SetContinuousImprovement(true, null);
        await preflight.BindAsync();
        preflight.Api.CompleteTask = true;
        await preflight.Controller.TickAsync();
        preflight.Api.FailNextMessageLookup = true;
        preflight.Clock.Advance(TimeSpan.FromSeconds(20));
        await preflight.Controller.TickAsync();
        Require(preflight.SessionId() == preflight.Session && preflight.Api.ContinuePosts.Count == 0,
            "A proven-before-POST Loop preflight failure discarded the exact original request.");
        preflight.Clock.Advance(TimeSpan.FromSeconds(59));
        await preflight.Controller.TickAsync();
        Require(preflight.Api.ContinuePosts.Count == 0, "A failed Loop preflight bypassed the one-minute retry delay.");
        preflight.Clock.Advance(TimeSpan.FromSeconds(1));
        await preflight.Controller.TickAsync();
        Require(preflight.Api.ContinuePosts.Count == 1, "Loop never resumed after a transient message-ID preflight recovered.");
    }

    private static async Task ConfirmedLoopReceiptSurvivesSlowNativeReadsAsync()
    {
        foreach (string condition in new[] { "stable", "new-input", "changed-plan" })
        {
            using var fixture = new Fixture("s-loop-slow-receipt-" + condition);
            fixture.Controller.SetContinuousImprovement(true, null);
            fixture.Api.NoTodos = true;
            fixture.Api.CompleteTask = true;
            fixture.Api.StatusFailuresRemaining = 4;
            fixture.Controller.ObserveSubmittedPrompt("original\nmultiline request", fixture.Session, fixture.UserMessage);
            fixture.Controller.ConfirmApiSubmission(fixture.Session, fixture.UserMessage);
            await UntilAsync(() => fixture.Api.StatusFailuresRemaining == 3 && fixture.Controller.Status.Contains("상태 연결 대기"),
                "the initial confirmed-receipt read failure");
            foreach (int seconds in new[] { 10, 20, 40 })
            {
                fixture.Clock.Advance(TimeSpan.FromSeconds(seconds));
                await fixture.Controller.TickAsync();
            }
            if (condition == "new-input") fixture.Controller.ObserveLocalInput("a newer draft");
            if (condition == "changed-plan") File.AppendAllText(fixture.PlanPath!, "\nA different plan.");
            fixture.Clock.Advance(TimeSpan.FromSeconds(60));
            await fixture.Controller.TickAsync();
            fixture.Clock.Advance(TimeSpan.FromSeconds(20));
            await fixture.Controller.TickAsync();
            if (condition == "stable")
                Require(fixture.SessionId() == fixture.Session && fixture.Api.ContinuePosts.Count == 1,
                    "A confirmed exact Loop receipt expired while repeated slow native reads were recovering beyond two minutes.");
            else
                Require(fixture.Api.ContinuePosts.Count == 0 && fixture.SessionId() is null,
                    $"A delayed confirmed Loop receipt ignored {condition} instead of retaining the user's newer state.");
        }
    }

    private static async Task CompletedLoopUncertainSendNeverReplaysAsync()
    {
        foreach (bool lateReceipt in new[] { false, true })
        {
            using var fixture = new Fixture("s-loop-uncertain-send-" + lateReceipt);
            fixture.Controller.SetContinuousImprovement(true, null);
            fixture.Api.NoTodos = true;
            await fixture.BindAsync(observeTodo: false);
            fixture.Api.CompleteTask = true;
            await fixture.Controller.TickAsync();
            fixture.Api.LoseNextContinuationResponse = true;
            fixture.Clock.Advance(TimeSpan.FromSeconds(20));
            await fixture.Controller.TickAsync();
            Require(fixture.Api.ContinuePosts.Count == 1, "The uncertain completed-Loop fixture never reached POST.");
            var original = fixture.Api.ContinuePosts.Single();
            if (lateReceipt) fixture.Api.AddUserMessage(original.MessageId, fixture.Clock.GetUtcNow());
            for (int i = 0; i < 3; i++) { fixture.Clock.Advance(TimeSpan.FromSeconds(20)); await fixture.Controller.TickAsync(); }
            Require(fixture.Api.ContinuePosts.Count == 1,
                "A possibly delivered Loop improvement was replayed before a distinct completed native turn and pacing.");
            fixture.Clock.Advance(TimeSpan.FromSeconds(20));
            await fixture.Controller.TickAsync();
            Require(fixture.Api.ContinuePosts.Count == (lateReceipt ? 2 : 1) &&
                fixture.Api.ContinuePosts.Count(p => p.MessageId == original.MessageId) == 1,
                "Loop lost exact late-receipt reconciliation or replayed an uncertain prompt without receipt.");
            fixture.Controller.ObserveLocalInput("\x03");
            for (int i = 0; i < 4; i++) { fixture.Clock.Advance(TimeSpan.FromSeconds(30)); await fixture.Controller.TickAsync(); }
            Require(fixture.Api.ContinuePosts.Count == (lateReceipt ? 2 : 1), "Stop resumed a previously uncertain Loop request.");
        }
    }

    private static async Task CompletedLoopRespectsSafetyAndScopeAsync()
    {
        foreach (string blocker in new[] { "loop-off", "deadline", "stop", "planner", "permission", "question", "busy", "tool", "error", "changed-plan", "runtime" })
        {
            using var fixture = new Fixture("s-completed-loop-" + blocker);
            if (blocker != "loop-off") fixture.Controller.SetContinuousImprovement(true, blocker == "deadline" ? fixture.Clock.GetUtcNow().AddSeconds(10) : null);
            await fixture.BindAsync();
            fixture.Api.CompleteTask = true;
            await fixture.Controller.TickAsync();
            switch (blocker)
            {
                case "stop": fixture.Controller.ObserveLocalInput("\x03"); break;
                case "planner": fixture.Controller.Suspend("플랜 작성 · 자동 진행 중단"); break;
                case "permission": fixture.Api.SetPendingInput(permission: true); break;
                case "question": fixture.Api.SetPendingInput(question: true); break;
                case "busy": fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy); break;
                case "tool": fixture.Api.SetRunningTool(true); break;
                case "error": fixture.Api.SetMessageError(true); break;
                case "changed-plan": File.AppendAllText(fixture.PlanPath!, "\nChanged scope."); break;
                case "runtime": fixture.CanAct = false; break;
            }
            for (int tick = 0; tick < 3; tick++) { fixture.Clock.Advance(TimeSpan.FromMinutes(1)); await fixture.Controller.TickAsync(); }
            if (blocker == "error")
                // A Loop retries its failed request (error recovery prompt), never reviews it as a completed result.
                Require(fixture.Api.ContinuePosts.Count >= 1 && fixture.Api.AbortPosts.Count == 0 &&
                    fixture.Api.ContinuePosts.All(post => PostPrompt(post).Contains("이전 요청에서 오류가 발생했습니다") && !ReviewPrompt(post)),
                    "A Loop error was reviewed as a completed result or not retried.");
            else
                Require(fixture.Api.ContinuePosts.Count == 0 && fixture.Api.AbortPosts.Count == 0,
                    $"Completed Loop respects {blocker}; ordinary completed work also stays stopped when Loop is off.");
        }
    }

    private static async Task BusyQueuedRequestsProtectEarlierWorkAsync()
    {
        foreach (string oldWork in new[] { "tool", "stream", "tool-calls-running", "tool-calls-pending" })
        {
            bool runningTool = oldWork != "stream";
            using var fixture = new Fixture("s-busy-queued-" + oldWork);
            File.WriteAllText(Path.Combine(fixture.ProjectPath, "app.js"), "// Fake generated app; never executed.");
            await fixture.BindAsync();
            string queuedId = "queued-second-" + fixture.Session;
            DateTimeOffset receiptAt = fixture.Clock.GetUtcNow();
            bool oldFinished = false, newFinished = false;
            int streamingRevision = 0;
            object User(string id, DateTimeOffset created) => new
            {
                info = new { id, role = "user", sessionID = fixture.Session, time = new { created = created.ToUnixTimeMilliseconds() } },
                parts = Array.Empty<object>()
            };
            object Assistant(string id, string parent, bool finished, bool tool, string text) => new
            {
                info = new { id, role = "assistant", sessionID = fixture.Session, parentID = parent,
                    finish = finished ? "stop" : id == "old-root" && oldWork.StartsWith("tool-calls", StringComparison.Ordinal) ? "tool-calls" : null,
                    time = new { created = receiptAt.AddSeconds(id == "old-root" ? -1 : 1).ToUnixTimeMilliseconds(), completed = finished ? (long?)fixture.Clock.GetUtcNow().ToUnixTimeMilliseconds() : null } },
                parts = tool
                    ? new object[] { new { type = "tool", tool = "shell", state = new { status = oldWork == "tool-calls-pending" ? "pending" : "running", input = new { command = "node app.js" } } }, new { type = "text", text } }
                    : new object[] { new { type = "text", text } }
            };
            fixture.Api.MessagesOverride = () => JsonSerializer.Serialize(new object[]
            {
                User(fixture.UserMessage, receiptAt.AddMinutes(-1)),
                Assistant("old-root", fixture.UserMessage, oldFinished, runningTool, "old stream " + streamingRevision),
                User("queued-first", receiptAt.AddMilliseconds(-1)),
                User(queuedId, receiptAt)
            }.Concat(newFinished ? new[] { Assistant("new-final", queuedId, true, false, "New request implemented and verified.") } : Array.Empty<object>()));
            fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
            fixture.Controller.ObserveSubmittedPrompt("Additional work", fixture.Session, queuedId);
            fixture.Controller.ConfirmApiSubmission(fixture.Session, queuedId);
            await UntilAsync(() => typeof(OpenCodeAutoResumeController).GetField("_expectedUserMessageId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(fixture.Controller) as string == queuedId,
                "binding the exact queued request after two busy receipts");
            fixture.Controller.ObserveNativeSignal(new("todo.updated", fixture.Session, null, null, 30));
            for (int minute = 0; minute < 17; minute++)
            {
                streamingRevision++;
                fixture.Clock.Advance(TimeSpan.FromMinutes(1));
                await fixture.Controller.TickAsync();
            }
            Require(fixture.Api.AbortPosts.Count == 0 && fixture.Api.ContinuePosts.Count == 0 && !fixture.Controller.HasVerifiedCompletion,
                $"Queued receipt does not hide older {oldWork} or authorize recovery/completion after 17 minutes.");

            oldFinished = true; // Keep its stale running tool marker deliberately.
            using var http = new HttpClient(fixture.Api, disposeHandler: false);
            using var client = new OpenCodeRecoveryClient(new Uri("http://127.0.0.1:43219/"), fixture.ProjectPath, http);
            var oldDone = await client.ReadAsync(fixture.Session);
            Require(oldDone.Evidence is { HasRunningTools: false, HasSuccessfulFinalResponse: false },
                "Finished old root cannot retain a stale tool lock or complete its newer queued user.");
            fixture.Api.SetStatus(OpenCodeRecoveryStatus.Idle);
            newFinished = fixture.Api.CompleteTask = true;
            await fixture.Controller.TickAsync();
            Require(fixture.Controller.HasVerifiedCompletion && !fixture.Controller.HasUnfinishedWork,
                "Only a successful assistant correlated to the exact new user completes queued work.");
        }
    }

    private static async Task ContinuousImprovementExpiryStopsItsActiveTaskAsync()
    {
        using var fixture = new Fixture("s-continuous-expiry");
        fixture.Controller.SetEnabled(true);
        fixture.Controller.SetContinuousImprovement(true, fixture.Clock.GetUtcNow().AddSeconds(30));
        await fixture.BindAsync();
        await fixture.Controller.TickAsync();
        Require(typeof(OpenCodeAutoResumeController).GetField("_continuousTaskInProgress",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(fixture.Controller) is true,
            "The submitted task was not pinned to its enabled continuous-improvement window.");
        Require(fixture.Controller.HasContinuousImprovementWork,
            "An enabled continuous task with native unfinished todos was not reported as active.");

        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        await fixture.Controller.TickAsync();
        Require(!fixture.Controller.IsContinuousImprovementEnabled && !fixture.Controller.HasContinuousImprovementWork,
            "An expired continuous-improvement deadline remained active.");
        Require(!fixture.Controller.HasUnfinishedWork,
            "Global AutoResume bypassed expiry for a task that started under continuous-improvement ownership.");
        Require(fixture.Api.ContinuePosts.Count == 0,
            "Expiry started another continuous-improvement unit after the current task settled.");
    }

    private static async Task ContinuousRecoveryKeepsRetryingWithoutCountLimitAsync()
    {
        using var fixture = new Fixture("s-continuous-stalled");
        fixture.Controller.SetEnabled(true);
        fixture.Controller.SetContinuousImprovement(true, until: null);
        await fixture.BindAsync();
        await fixture.Controller.TickAsync();
        Require(fixture.Controller.HasContinuousImprovementWork,
            "The continuous recovery case began without native unfinished work.");

        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        await fixture.Controller.TickAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Idle);
        await fixture.Controller.TickAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        await fixture.Controller.TickAsync();
        await WaitForPostAsync(fixture.Api);

        await ResumeWithoutProgressAsync(fixture, TimeSpan.FromMinutes(1));
        await ResumeWithoutProgressAsync(fixture, TimeSpan.FromMinutes(3));
        for (int attempt = 0; attempt < 5; attempt++)
            await ResumeWithoutProgressAsync(fixture, TimeSpan.FromMinutes(10));
        Require(fixture.Api.ContinuePosts.Count == 8 && !fixture.Controller.IsRecoveryPaused,
            $"No-progress retries stopped at an old count limit (posts={fixture.Api.ContinuePosts.Count}, status={fixture.Controller.Status}).");
        Require(fixture.Controller.HasUnfinishedWork && fixture.Controller.HasContinuousImprovementWork,
            "Repeated recovery lost its continuous-work and sleep-prevention ownership.");

        fixture.Api.SetPendingInput(question: true);
        await AdvanceBusyAsync(fixture, 11);
        Require(fixture.Api.ContinuePosts.Count == 8 && fixture.Api.AbortPosts.Count == 0,
            "Removing the retry cap bypassed approval/question protection.");
        fixture.Api.SetPendingInput();
        fixture.Controller.ObserveLocalInput("\x03");
        await AdvanceBusyAsync(fixture, 11);
        Require(fixture.Api.ContinuePosts.Count == 8 && !fixture.Controller.HasUnfinishedWork,
            "Removing the retry cap bypassed a manual stop.");
    }
    private static async Task ResumeWithoutProgressAsync(Fixture fixture, TimeSpan retryDelay)
    {
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Controller.TickAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Idle);
        fixture.Clock.Advance(OpenCodeRecoveryPolicy.IdleSettlePeriod);
        await fixture.Controller.TickAsync();
        TimeSpan remaining = retryDelay - TimeSpan.FromSeconds(21);
        while (remaining > TimeSpan.Zero)
        {
            TimeSpan step = remaining > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : remaining;
            fixture.Clock.Advance(step);
            remaining -= step;
            await fixture.Controller.TickAsync();
        }
        await WaitForPostAsync(fixture.Api);
    }

    private static async Task HistoricalWorkIsNeverBoundAsync()
    {
        using var fixture = new Fixture("s-historical");
        fixture.Controller.SetEnabled(true);
        await fixture.Controller.TickAsync();
        fixture.Controller.ObserveNativeSignal(UserSignal(fixture, "s-historical", "old-user", fixture.Clock.GetUtcNow().AddHours(-1), 1));
        await Task.Delay(25);
        Require(fixture.Api.ReadCount == 0, "Startup and historical native messages did not initiate reads.");

        fixture.Controller.ObserveLocalInput("do work\r");
        fixture.Controller.ObserveNativeSignal(UserSignal(fixture, "s-historical", "old-user", fixture.Clock.GetUtcNow().AddHours(-1), 2));
        await Task.Delay(25);
        Require(fixture.SessionId() is null, "A message created before the local submission did not bind a session.");
        Require(fixture.Api.ContinuePosts.Count == 0, "Historical incomplete work did not trigger a continuation.");
    }

    private static async Task TerminalReplyDoesNotInvalidateBoundRecoveryAsync()
    {
        using var fixture = new Fixture("s-terminal-reply");
        await fixture.BindAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        await fixture.Controller.TickAsync();

        fixture.Controller.ObserveLocalInput("\x1b[?1;2c\x1b[12;40R\x1b[0n\x1b[I\x1b[O");
        Require(fixture.SessionId() == fixture.Session,
            "Terminal-generated device, cursor, status, and focus replies preserve the pinned recovery cycle.");

        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Idle);
        await fixture.Controller.TickAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        await fixture.Controller.TickAsync();
        await WaitForPostAsync(fixture.Api);
        Require(fixture.Api.ContinuePosts.Count == 1,
            "Known xterm protocol replies did not disarm a valid recovery continuation.");
    }

    private static async Task BlankEnterDoesNotArmForeignUserMessageAsync()
    {
        using var fixture = new Fixture("s-blank-enter");
        fixture.Controller.SetEnabled(true);
        fixture.Controller.ObserveLocalInput("\r");
        string foreignMessageId = "foreign-user-after-blank-enter";
        fixture.Api.AddUserMessage(foreignMessageId, fixture.Clock.GetUtcNow());
        fixture.Controller.ObserveNativeSignal(UserSignal(fixture, fixture.Session, foreignMessageId, fixture.Clock.GetUtcNow(), 10));
        await Task.Delay(25);

        Require(fixture.SessionId() is null, "A blank Enter did not arm an unrelated native user-message event.");
        Require(fixture.Api.ReadCount == 0, "A blank Enter did not initiate a recovery read for foreign native work.");
        Require(fixture.Api.ContinuePosts.Count == 0, "A blank Enter did not trigger a continuation.");
    }

    private static async Task ApiSubmissionFiltersUnrelatedNativeMessagesAndReconcilesAsync()
    {
        using var fixture = new Fixture("s-api-submission");
        fixture.Controller.SetEnabled(true);
        const string messageId = "msg_api_submission";
        fixture.Controller.ObserveSubmittedPrompt("initial task prompt", fixture.Session, messageId);

        fixture.Controller.ObserveNativeSignal(UserSignal(fixture, "s-foreign", messageId, fixture.Clock.GetUtcNow(), 1));
        fixture.Controller.ObserveNativeSignal(UserSignal(fixture, fixture.Session, "msg-foreign", fixture.Clock.GetUtcNow(), 2));
        await Task.Delay(25);
        Require(fixture.SessionId() is null && fixture.Api.ReadCount == 0,
            "Unrelated session or message SSE cannot bind an API-submitted request.");

        fixture.Api.AddUserMessage(messageId, fixture.Clock.GetUtcNow());
        fixture.Controller.ConfirmApiSubmission(fixture.Session, messageId);
        await UntilAsync(() => fixture.SessionId() == fixture.Session, "reconciling an API receipt without its SSE event");
        Require(fixture.Api.ContinuePosts.Count == 0,
            "An exact GET-confirmed API receipt binds the request without posting duplicate work.");
    }

    private static async Task LocalInputInvalidatesLateApiSubmissionConfirmationAsync()
    {
        using var fixture = new Fixture("s-api-submission-input");
        fixture.Controller.SetEnabled(true);
        const string messageId = "msg_api_submission_late";
        fixture.Controller.ObserveSubmittedPrompt("initial task prompt", fixture.Session, messageId);
        fixture.Controller.ObserveLocalInput("new user request\r");
        fixture.Api.AddUserMessage(messageId, fixture.Clock.GetUtcNow());

        fixture.Controller.ConfirmApiSubmission(fixture.Session, messageId);
        await Task.Delay(25);
        Require(fixture.SessionId() is null && fixture.Api.ReadCount == 0 && fixture.Api.ContinuePosts.Count == 0,
            "Manual input invalidates the pending API receipt so a late confirmation cannot revive recovery.");
    }

    private static async Task IdleTodoContinuesExactlyOnceAsync()
    {
        using var fixture = new Fixture("s-idle");
        await fixture.BindAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        await fixture.Controller.TickAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Idle);
        await fixture.Controller.TickAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        await fixture.Controller.TickAsync();

        PostRecord post = await WaitForPostAsync(fixture.Api);
        Require(post.SessionId == "s-idle", "Continuation was posted to the exact bound session.");
        Require(!string.IsNullOrWhiteSpace(post.MessageId), "Continuation carries a client-generated message ID.");
        Require(post.Body.Contains(Path.GetFileName(fixture.PlanPath!), StringComparison.Ordinal), "Continuation identifies the linked plan path.");
        using (var payload = JsonDocument.Parse(post.Body))
        {
            string prompt = payload.RootElement.GetProperty("parts")[0].GetProperty("text").GetString()!;
            Require(prompt.Contains("plan.md") && prompt.Contains("다시 읽고") && prompt.Contains("끝난 작업은 반복하지 말고") && prompt.Contains("현재 코드"),
                "Automatic recovery rechecks the plan and current implementation without repeating completed work.");
        }
        Require(fixture.Api.ContinuePosts.Count == 1, "One settled idle interval caused exactly one continuation.");
    }

    private static async Task DuplicateUserMetadataPreservesPostUserTodoSignalAsync()
    {
        using var fixture = new Fixture("s-metadata");
        fixture.Api.BlockNextPath("/session/status");
        fixture.Controller.SetEnabled(true);
        fixture.Controller.ObserveLocalInput("do work\r");
        fixture.Controller.ObserveNativeSignal(UserSignal(fixture, fixture.Session, fixture.UserMessage, fixture.Clock.GetUtcNow(), 10));
        await fixture.Api.WaitForBlockedReadAsync();
        fixture.Controller.ObserveNativeSignal(new("todo.updated", fixture.Session, null, null, 11));
        fixture.Controller.ObserveNativeSignal(UserSignal(fixture, fixture.Session, fixture.UserMessage, fixture.Clock.GetUtcNow(), 12));
        fixture.Api.ReleaseBlockedRead();
        await UntilAsync(() => fixture.SessionId() == fixture.Session, "binding after duplicate user metadata");

        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        await fixture.Controller.TickAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Idle);
        await fixture.Controller.TickAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        await fixture.Controller.TickAsync();
        await Task.Delay(100);
        bool workConfirmed = typeof(OpenCodeAutoResumeController)
            .GetField("_workConfirmed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(fixture.Controller) is true;
        Require(fixture.Api.ContinuePosts.Count == 1,
            $"Repeated metadata changed the causal baseline (bound={fixture.SessionId()}, confirmed={workConfirmed}, status={fixture.Controller.Status}, reads={fixture.Api.ReadCount}).");
    }

    private static async Task DelayedCandidateExpiresAsync()
    {
        using var fixture = new Fixture("s-expired");
        fixture.Api.BlockNextPath("/session/status");
        fixture.Controller.SetEnabled(true);
        fixture.Controller.ObserveLocalInput("do work\r");
        fixture.Controller.ObserveNativeSignal(UserSignal(fixture, fixture.Session, fixture.UserMessage, fixture.Clock.GetUtcNow(), 10));
        await fixture.Api.WaitForBlockedReadAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(1)));
        fixture.Api.ReleaseBlockedRead();
        await Task.Delay(40);
        Require(fixture.SessionId() is null, "A native user-message candidate older than two minutes did not bind after a delayed read.");
        Require(fixture.Api.ContinuePosts.Count == 0, "An expired candidate did not trigger a continuation.");
    }

    private static async Task AcceptedContinuationIsNotDuplicatedAsync()
    {
        using var fixture = new Fixture("s-once");
        await fixture.BindAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        await fixture.Controller.TickAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Idle);
        await fixture.Controller.TickAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        await fixture.Controller.TickAsync();
        await WaitForPostAsync(fixture.Api);

        for (int i = 0; i < 6; i++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(10));
            await fixture.Controller.TickAsync();
        }
        Require(fixture.Api.ContinuePosts.Count == 1, "A 204 response without subsequent model progress was not posted twice.");
    }

    private static async Task UnexpectedReadFaultRetainsPinnedRequestAndRetriesAsync()
    {
        using var fixture = new Fixture("s-unexpected-read-fault");
        fixture.Controller.SetEnabled(true);
        await fixture.BindAsync();
        var diagnostics = new List<OpenCodeAutoResumeDiagnostic>();
        fixture.Controller.DiagnosticEmitted += diagnostics.Add;
        fixture.Controller.StatusChanged += _ => throw new InvalidOperationException("observer failure");
        fixture.Api.ThrowNextStatusRead = true;

        var tick = (Task)typeof(OpenCodeAutoResumeController)
            .GetMethod("TickSafelyAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(fixture.Controller, null)!;
        await tick;

        Require(fixture.SessionId() == fixture.Session && fixture.Controller.ContinuationSessionId == fixture.Session,
            "An unexpected read fault invalidated the pinned session instead of retaining it for retry.");
        Require(diagnostics.Any(item => item.Code == "tick_fault" && item.ExceptionType == "InvalidOperationException"),
            "An unexpected read fault did not emit a safe diagnostic with its exception type.");

        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.Controller.TickAsync();
        Require(fixture.SessionId() == fixture.Session && fixture.Api.ReadCount > 0,
            "A transient read fault did not recover on the next bounded retry.");
    }

    private static async Task AmbiguousContinuationIsNeverRepeatedAsync()
    {
        using var fixture = new Fixture("s-ambiguous-controller");
        await fixture.BindAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        await fixture.Controller.TickAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Idle);
        await fixture.Controller.TickAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        var diagnostics = new List<OpenCodeAutoResumeDiagnostic>();
        fixture.Controller.DiagnosticEmitted += diagnostics.Add;
        fixture.Api.LoseNextContinuationResponse = true;
        await fixture.Controller.TickAsync();

        Require(fixture.Api.ContinuePosts.Count == 1, "The ambiguous continuation was not sent exactly once.");
        Require(fixture.SessionId() == fixture.Session && fixture.Controller.ContinuationSessionId == fixture.Session,
            "An ambiguous continuation discarded the pinned session before native reconciliation.");
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Controller.TickAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Controller.TickAsync();
        Require(fixture.Api.ContinuePosts.Count == 1,
            "A second continuation was sent before native evidence resolved the ambiguous first POST.");
        Require(fixture.SessionId() == fixture.Session && diagnostics.Any(item => item.Code == "action_ambiguous"),
            "Ambiguous delivery did not remain safely latched with an actionable diagnostic.");
    }

    private static async Task ProgressDuringFinalContinueReadBlocksPostAsync()
    {
        using var fixture = new Fixture("s-progress-continue");
        await fixture.BindAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        await fixture.Controller.TickAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Idle);
        await fixture.Controller.TickAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        // The message-ID lookup occurs before the client's final evidence read.
        fixture.Api.CompleteTodoOnMessageIdLookup();
        await fixture.Controller.TickAsync();
        await Task.Delay(50);
        Require(fixture.Api.ContinuePosts.Count == 0,
            "Substantive progress arriving during the client's final read blocked a stale continuation POST.");
    }

    private static async Task SafetyBlockersPreventRecoveryAsync()
    {
        foreach (string blocker in new[] { "running-tool", "retry", "permission", "question", "error", "missing-plan", "unknown" })
        {
            string plan = blocker == "missing-plan" ? "" : "plan.md";
            using var fixture = new Fixture("s-block-" + blocker, plan);
            if (blocker == "missing-plan")
            {
                fixture.Controller.SetEnabled(true);
                fixture.Controller.ObserveLocalInput("do work\r");
                fixture.Controller.ObserveNativeSignal(UserSignal(fixture, fixture.Session, fixture.UserMessage, fixture.Clock.GetUtcNow(), 10));
                await Task.Delay(25);
                Require(fixture.Api.ReadCount == 0, "An unbound or missing Markdown plan prevented native recovery reads.");
                continue;
            }
            await fixture.BindAsync();
            fixture.Api.SetStatus(blocker == "retry" ? OpenCodeRecoveryStatus.Retry : OpenCodeRecoveryStatus.Busy);
            if (blocker == "running-tool") fixture.Api.SetRunningTool(true);
            if (blocker == "permission") fixture.Api.SetPendingInput(permission: true);
            if (blocker == "question") fixture.Api.SetPendingInput(question: true);
            if (blocker == "error") fixture.Api.SetMessageError(true);
            if (blocker == "unknown") fixture.Api.SetUnknownStatus(true);
            await fixture.Controller.TickAsync();

            fixture.Api.SetStatus(blocker == "retry" ? OpenCodeRecoveryStatus.Retry : OpenCodeRecoveryStatus.Idle);
            await fixture.Controller.TickAsync();
            fixture.Clock.Advance(TimeSpan.FromSeconds(20));
            await fixture.Controller.TickAsync();
            Require(fixture.Api.ContinuePosts.Count == 0, $"The {blocker} safety gate blocked continuation.");
            Require(fixture.Api.AbortPosts.Count == 0, $"The {blocker} safety gate blocked abort.");

            if (blocker == "running-tool")
            {
                fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
                for (int minute = 0; minute < 14; minute += 2)
                {
                    fixture.Clock.Advance(TimeSpan.FromMinutes(2));
                    await fixture.Controller.TickAsync();
                }
                fixture.Clock.Advance(TimeSpan.FromMinutes(1));
                await fixture.Controller.TickAsync();
                Require(fixture.Api.AbortPosts.Count == 0, "A running tool vetoed the 15-minute abort path.");
            }
        }

        using var idleTool = new Fixture("s-idle-tool");
        await idleTool.BindAsync();
        idleTool.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        await idleTool.Controller.TickAsync();
        idleTool.Api.SetStatus(OpenCodeRecoveryStatus.Idle);
        idleTool.Api.SetRunningTool(true);
        await idleTool.Controller.TickAsync();
        idleTool.Clock.Advance(TimeSpan.FromSeconds(20));
        await idleTool.Controller.TickAsync();
        Require(idleTool.Api.ContinuePosts.Count == 0, "A running tool blocks continuation even when the root status is idle.");
    }

    private static async Task CancellationInvalidatesPendingReadsAsync()
    {
        foreach (string cancellation in new[] { "typing", "manual-stop", "plan-rebind", "toggle-off", "suspend" })
        {
            using var fixture = new Fixture("s-cancel-" + cancellation);
            fixture.Api.BlockNextPath("/session/status");
            fixture.Controller.SetEnabled(true);
            fixture.Controller.ObserveLocalInput("do work\r");
            fixture.Controller.ObserveNativeSignal(UserSignal(fixture, fixture.Session, fixture.UserMessage, fixture.Clock.GetUtcNow(), 10));
            await fixture.Api.WaitForBlockedReadAsync();

            switch (cancellation)
            {
                case "typing": fixture.Controller.ObserveLocalInput("x"); break;
                case "manual-stop": fixture.Controller.ObserveLocalInput("\x03"); break;
                case "plan-rebind": fixture.Controller.UpdatePlan(fixture.AlternatePlanPath); break;
                case "toggle-off": fixture.Controller.SetEnabled(false); break;
                case "suspend": fixture.Controller.Suspend("manual cancel"); break;
            }

            fixture.Api.ReleaseBlockedRead();
            await Task.Delay(40);
            Require(fixture.Api.ContinuePosts.Count == 0, $"{cancellation} invalidated a queued read before any continuation POST.");
            Require(fixture.Api.AbortPosts.Count == 0, $"{cancellation} invalidated a queued read before any abort POST.");
        }
    }

    private static async Task UnknownAndChildSessionsAreRejectedAsync()
    {
        using (var missing = new Fixture("s-missing"))
        {
            await missing.BindAsync();
            missing.Api.RemoveSessionFromDirectoryList = true;
            await missing.Controller.TickAsync();
            Require(missing.Api.ContinuePosts.Count == 0, "A session absent from the directory-scoped session list was not resumed.");
        }

        using (var child = new Fixture("s-child", parentSessionId: "s-parent"))
        {
            child.Controller.SetEnabled(true);
            child.Controller.ObserveLocalInput("do work\r");
            child.Controller.ObserveNativeSignal(UserSignal(child, child.Session, child.UserMessage, child.Clock.GetUtcNow(), 10));
            await UntilAsync(() => child.Api.ReadCount > 0, "checking a child session identity");
            await Task.Delay(25);
            Require(child.SessionId() is null, "A child session did not become the recovery root binding.");
            Require(child.Api.ContinuePosts.Count == 0, "A child session was not hijacked as a recovery root.");
        }
    }

    private static async Task AdvanceBusyAsync(Fixture fixture, int minutes)
    {
        for (int index = 0; index < minutes; index++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            await fixture.Controller.TickAsync();
        }
    }

    private static async Task StalledRequestWithoutTodosRecoversAsync()
    {
        using var fixture = new Fixture("s-no-todos");
        fixture.Api.NoTodos = true;
        await fixture.BindAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        await fixture.Controller.TickAsync();
        await AdvanceBusyAsync(fixture, AbortMinutes - 1);
        Require(fixture.Controller.IsStalled && fixture.Api.AbortPosts.Count == 0,
            "Unchanged minutes show suspected stall without prematurely interrupting the request.");
        await AdvanceBusyAsync(fixture, 1);
        Require(fixture.Api.AbortPosts.Count == 1,
            "A pinned current request can be aborted even when the model never made a todo list.");
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Idle);
        await fixture.Controller.TickAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        await fixture.Controller.TickAsync();
        Require(fixture.Api.ContinuePosts.Count == 1 && fixture.Api.ContinuePosts.Single().SessionId == fixture.Session,
            "A confirmed idle after abort resumes the same exact unfinished no-todo request once.");
    }

    private static async Task IgnoredAbortRequestsOneVerifiedProcessRestartAsync()
    {
        using var fixture = new Fixture("s-ignored-abort");
        await fixture.BindAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        await fixture.Controller.TickAsync();
        int restarts = 0;
        fixture.Controller.RestartStalledProcessAsync = async request =>
        {
            Require(request.SessionId == fixture.Session && request.UserMessageId == fixture.UserMessage &&
                request.PlanPath == fixture.PlanPath && request.CanRestart() && await request.VerifyAsync(),
                "Process recovery verifies the exact request, plan and current native inactivity before restart.");
            Require(request.Prompt.Contains("do work") && request.Prompt.Contains("현재 파일"),
                "Restart carries the original instruction and tells the model to inspect already applied changes.");
            restarts++;
            return true;
        };
        await AdvanceBusyAsync(fixture, AbortMinutes + 1);
        Require(restarts == 0 && fixture.Api.AbortPosts.Count == 1,
            "An accepted abort gets two minutes to settle before any process restart.");
        await AdvanceBusyAsync(fixture, 1);
        Require(restarts == 1 && fixture.Api.ContinuePosts.Count == 0,
            "Busy remaining unchanged after an ignored abort requests process recovery instead of sending continue into busy.");
        await AdvanceBusyAsync(fixture, 3);
        Require(restarts == 1, "One stuck request cannot enqueue duplicate process restarts.");
    }

    private static async Task RepeatedRestartsPreserveOriginalInstructionAsync()
    {
        using var project = new Fixture("s-repeated-restart-project");
        const string original = "첫 번째 요구사항\n두 번째 요구사항\n마지막 확인 기준";
        string submittedPrompt = original;
        string? previousRecoveryPrompt = null;
        int restarts = 0;
        for (int cycle = 0; cycle < 3; cycle++)
        {
            using var fixture = new Fixture("s-repeated-restart-" + cycle, projectPath: project.ProjectPath);
            fixture.Controller.SetContinuousImprovement(true, until: null);
            fixture.Controller.ObserveSubmittedPrompt(submittedPrompt);
            if (cycle > 0) fixture.Controller.PreserveRestartInstruction(submittedPrompt, original);
            fixture.Controller.ObserveNativeSignal(UserSignal(fixture, fixture.Session, fixture.UserMessage, fixture.Clock.GetUtcNow(), 10));
            await UntilAsync(() => fixture.SessionId() == fixture.Session, "binding the restarted request");
            fixture.Controller.ObserveNativeSignal(new("todo.updated", fixture.Session, null, null, 11));
            fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
            await fixture.Controller.TickAsync();
            fixture.Controller.RestartStalledProcessAsync = async request =>
            {
                Require(request.CanRestart() && await request.VerifyAsync(),
                    "Each replacement session must independently verify its own stall.");
                Require(request.OriginalInstruction == original && request.Prompt.Contains(original),
                    "Repeated restarts must retain the entire original multiline request.");
                Require(previousRecoveryPrompt is null || request.Prompt == previousRecoveryPrompt,
                    "Recovery guidance must not recursively accumulate on each process restart.");
                previousRecoveryPrompt = submittedPrompt = request.Prompt;
                restarts++;
                return true;
            };
            await AdvanceBusyAsync(fixture, AbortMinutes + 2);
            Require(restarts == cycle + 1, "A later replacement session was blocked by an old restart count.");
            await AdvanceBusyAsync(fixture, 3);
            Require(restarts == cycle + 1, "The same controller enqueued duplicate process restarts.");
        }
    }
    private static async Task RestartProtectsFreshActivityAndManualInputAsync()
    {
        using var fixture = new Fixture("s-restart-input-protection");
        await fixture.BindAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        await fixture.Controller.TickAsync();
        int restarts = 0;
        fixture.Controller.RestartStalledProcessAsync = async request =>
        {
            fixture.Api.SetPendingInput(question: true);
            Require(!await request.VerifyAsync(), "An approval/question appearing at the final read blocks process termination.");
            restarts++;
            return false;
        };
        await AdvanceBusyAsync(fixture, AbortMinutes + 2);
        Require(restarts == 1 && fixture.Controller.IsRecoveryPaused,
            "A rejected process restart pauses instead of retrying blindly.");
        using var stopped = new Fixture("s-restart-manual-stop");
        await stopped.BindAsync();
        stopped.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        await stopped.Controller.TickAsync();
        stopped.Controller.RestartStalledProcessAsync = _ => throw new InvalidOperationException("Restarted stopped work.");
        await AdvanceBusyAsync(stopped, AbortMinutes);
        stopped.Controller.ObserveLocalInput("\x03");
        await AdvanceBusyAsync(stopped, 3);
        Require(stopped.Api.ContinuePosts.Count == 0 && stopped.Api.AbortPosts.Count == 1,
            "Manual stop cancels the pending hard-recovery path.");
    }
    private static async Task LongBusySessionIsAbortedThenContinuedAfterIdleAsync()
    {
        using var fixture = new Fixture("s-stalled");
        await fixture.BindAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        await fixture.Controller.TickAsync();

        for (int minute = 0; minute < 14; minute += 2)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(2));
            await fixture.Controller.TickAsync();
        }
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Controller.TickAsync();
        await WaitForPostAsync(fixture.Api, abort: true);
        Require(fixture.Api.AbortPosts.Count == 1, "Fifteen minutes of busy status without tools caused one exact-session abort.");
        Require(fixture.Api.ContinuePosts.Count == 0, "A busy session was not continued immediately after abort.");

        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Idle);
        await fixture.Controller.TickAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        await fixture.Controller.TickAsync();
        await WaitForPostAsync(fixture.Api);
        Require(fixture.Api.AbortPosts.Count == 1 && fixture.Api.ContinuePosts.Count == 1,
            "Continuation followed a fresh idle read after the accepted abort.");
        Require(fixture.Api.ContinuePosts.Single().SessionId == "s-stalled", "Post-abort continuation remained in the pinned session.");
    }

    private static async Task StalledGeneratedAppLaunchIsAbortedAndResumedSafelyAsync()
    {
        using var fixture = new Fixture("s-stalled-app-launch");
        string appProject = Path.Combine(fixture.ProjectPath, "App.csproj");
        await File.WriteAllTextAsync(appProject, "<Project />");
        await fixture.BindAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        fixture.Api.SetRunningTool(true, "dotnet run --project App.csproj");
        await fixture.Controller.TickAsync();
        for (int minute = 1; minute <= 15; minute++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            await fixture.Controller.TickAsync();
        }
        await WaitForPostAsync(fixture.Api, abort: true);
        Require(fixture.Api.AbortPosts.Count == 1 && fixture.Api.ContinuePosts.Count == 0,
            "A single exact generated-app launch received one native abort after 15 unchanged minutes.");

        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Idle);
        fixture.Api.SetRunningTool(false);
        fixture.Api.SetAbortCancellationError();
        await fixture.Controller.TickAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        await fixture.Controller.TickAsync();
        await WaitForPostAsync(fixture.Api);
        Require(fixture.Api.AbortPosts.Count == 1 && fixture.Api.ContinuePosts.Count == 1 &&
            fixture.Api.ContinuePosts.Single().SessionId == fixture.Session,
            "Only fresh idle after the accepted abort allowed one continuation in the pinned session despite its abort cancellation marker.");

        using var permission = new Fixture("s-app-permission");
        await File.WriteAllTextAsync(Path.Combine(permission.ProjectPath, "App.csproj"), "<Project />");
        await permission.BindAsync();
        permission.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        permission.Api.SetRunningTool(true, "dotnet run --project App.csproj");
        permission.Api.SetPendingInput(permission: true);
        await permission.Controller.TickAsync();
        for (int minute = 1; minute <= 16; minute++)
        {
            permission.Clock.Advance(TimeSpan.FromMinutes(1));
            await permission.Controller.TickAsync();
        }
        Require(permission.Api.AbortPosts.Count == 0 && permission.Api.ContinuePosts.Count == 0,
            "A permission blocker preserved a running app tool from abort and continuation.");
    }

    private static async Task MixedAbortAndProviderErrorsRemainBlockedAsync()
    {
        using var fixture = new Fixture("s-mixed-abort-provider-errors");
        string appProject = Path.Combine(fixture.ProjectPath, "App.csproj");
        await File.WriteAllTextAsync(appProject, "<Project />");
        await fixture.BindAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        const string launchCommand = "dotnet run --project App.csproj";
        fixture.Api.SetRunningTool(true, launchCommand);
        await fixture.Controller.TickAsync();
        for (int minute = 1; minute <= 15; minute++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            await fixture.Controller.TickAsync();
        }
        await WaitForPostAsync(fixture.Api, abort: true);
        Require(fixture.Api.AbortPosts.Count == 1, "The generated app launch was not aborted once before mixed-error reconciliation.");

        var diagnostics = new List<OpenCodeAutoResumeDiagnostic>();
        fixture.Controller.DiagnosticEmitted += diagnostics.Add;
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Idle);
        fixture.Api.SetRunningTool(false, launchCommand);
        fixture.Api.SetMixedAbortAndUnrelatedToolErrors();
        await fixture.Controller.TickAsync();
        for (int retry = 0; retry < 4; retry++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            await fixture.Controller.TickAsync();
        }

        Require(diagnostics.Any(item => item.Phase == "Paused" && item.RequiresUserAction),
            "The controller did not surface mixed abort/provider errors as a user-action-required blocked diagnostic.");
        Require(fixture.Api.AbortPosts.Count == 1 && fixture.Api.ContinuePosts.Count == 0,
            "Mixed expected-abort and unrelated errors caused another abort or a continuation attempt.");
    }

    private static async Task ProgressDuringFinalAbortReadBlocksAbortAsync()
    {
        using var fixture = new Fixture("s-progress-abort");
        await fixture.BindAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        await fixture.Controller.TickAsync();
        await AdvanceBusyAsync(fixture, AbortMinutes - 1);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        // Completion evidence arrives before the next controller observation; an
        // already-completed plan todo cannot be aborted as a stalled task.
        fixture.Api.CompleteTask = true;
        await fixture.Controller.TickAsync();
        await Task.Delay(50);
        Require(fixture.Api.AbortPosts.Count == 0,
            "Substantive progress arriving during the client's final read blocked a stale abort POST.");
    }

    private static async Task DifferentSessionsInSameProjectRemainIndependentAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "TerminalNotes AutoResume Shared " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var first = new Fixture("s-pane-a", projectPath: root);
            using var second = new Fixture("s-pane-b", projectPath: root);
            await first.BindAsync();
            await second.BindAsync();
            foreach (Fixture fixture in new[] { first, second })
            {
                fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
                await fixture.Controller.TickAsync();
                fixture.Api.SetStatus(OpenCodeRecoveryStatus.Idle);
                await fixture.Controller.TickAsync();
                fixture.Clock.Advance(TimeSpan.FromSeconds(20));
                await fixture.Controller.TickAsync();
            }
            await WaitForPostAsync(first.Api);
            await WaitForPostAsync(second.Api);
            Require(first.Api.ContinuePosts.Single().SessionId == "s-pane-a", "First same-project pane retained its own session owner.");
            Require(second.Api.ContinuePosts.Single().SessionId == "s-pane-b", "Second same-project pane retained its own session owner.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static OpenCodeRecoverySignal UserSignal(Fixture fixture, string session, string message,
        DateTimeOffset created, long version) => new("message.updated", session, message, created, version);

    private static async Task<PostRecord> WaitForPostAsync(FakeOpenCodeApi api, bool abort = false)
    {
        await UntilAsync(() => abort ? api.AbortPosts.Count > 0 : api.ContinuePosts.Count > 0,
            $"waiting for {(abort ? "abort" : "continuation")} POST");
        return abort ? api.AbortPosts.Last() : api.ContinuePosts.Last();
    }

    private static async Task UntilAsync(Func<bool> condition, string description)
    {
        DateTime deadline = DateTime.UtcNow + WaitLimit;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Timed out while " + description + ".");
            await Task.Delay(5);
        }
    }

    private static void Require(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task AdvanceFailureAsync(Fixture fixture, TimeSpan elapsed)
    {
        while (elapsed > TimeSpan.Zero)
        {
            TimeSpan step = elapsed > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : elapsed;
            fixture.Clock.Advance(step);
            elapsed -= step;
            await fixture.Controller.TickAsync();
        }
    }

    private static async Task TransientFailureBackoffHasNoLimitAsync()
    {
        using var fixture = new Fixture("error-backoff");
        await fixture.BindAsync();
        fixture.Api.SetNativeFailure("APIError", 503, true);
        fixture.Api.NoTodos = true;
        await fixture.Controller.TickAsync();
        Require(fixture.Controller.CanRetryError && fixture.Controller.ErrorRetryAt == fixture.Clock.GetUtcNow().AddMinutes(1),
            "A confirmed pre-todo transient failure did not offer manual retry and a one minute automatic delay.");
        await AdvanceFailureAsync(fixture, TimeSpan.FromSeconds(59));
        Require(fixture.Api.ContinuePosts.Count == 0, "Transient failure was retried before its delay.");
        await AdvanceFailureAsync(fixture, TimeSpan.FromSeconds(1));
        Require(fixture.Api.ContinuePosts.Count == 1, "A transient native failure was not retried once.");
        for (int cycle = 1; cycle < 7; cycle++)
        {
            fixture.Api.SetStatus(OpenCodeRecoveryStatus.Idle);
            await fixture.Controller.TickAsync();
            await AdvanceFailureAsync(fixture, TimeSpan.FromMinutes(cycle == 1 ? 3 : 10));
            Require(fixture.Api.ContinuePosts.Count == cycle + 1,
                $"Repeated transient retry stopped or violated backoff on cycle {cycle} ({fixture.Controller.Status}).");
        }
        var prompts = fixture.Api.ContinuePosts.Select(post => JsonDocument.Parse(post.Body).RootElement.GetProperty("parts")[0].GetProperty("text").GetString()).ToArray();
        Require(prompts.All(prompt => prompt!.EndsWith("do work", StringComparison.Ordinal)),
            "Failure retries lost or recursively expanded the original instruction.");
        Require(fixture.Api.ContinuePosts.Select(post => post.MessageId).Distinct().Count() == 7,
            "Distinct confirmed failed turns did not receive distinct correlated message IDs.");
    }

    private static async Task AuthenticationFailureRequiresManualRetryWithAutomationOffAsync()
    {
        using var fixture = new Fixture("error-auth");
        await fixture.BindAsync();
        fixture.Controller.EnableTaskCompletionMonitoring();
        fixture.Controller.SetEnabled(false);
        fixture.Api.SetNativeFailure("APIError", 401, true);
        await fixture.Controller.TickAsync();
        await AdvanceFailureAsync(fixture, TimeSpan.FromMinutes(11));
        Require(fixture.Api.ContinuePosts.Count == 0 && fixture.Controller.CanRetryError,
            "Authentication failure auto retried or lost explicit retry when automatic progress was off.");
        Require(fixture.Controller.Status.Contains("인증", StringComparison.Ordinal), "Authentication failure status lacked a clear reason.");
        Require(await fixture.Controller.RetryErrorAsync(), "Explicit exact-session authentication retry did not submit once.");
        Require(!await fixture.Controller.RetryErrorAsync() && fixture.Api.ContinuePosts.Count == 1,
            "A repeated manual retry submitted the same failed turn twice.");
    }

    private static async Task FailureRetryProtectsNativeRetryAndNewInputAsync()
    {
        using var fixture = new Fixture("error-safety");
        await fixture.BindAsync();
        fixture.Api.SetNativeFailure("APIError", 429, true);
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Retry);
        await fixture.Controller.TickAsync();
        await AdvanceFailureAsync(fixture, TimeSpan.FromMinutes(4));
        Require(!fixture.Controller.CanRetryError && !await fixture.Controller.RetryErrorAsync() && fixture.Api.ContinuePosts.Count == 0,
            "Failure recovery interrupted the native retry wait.");
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Idle);
        fixture.Api.SetPendingInput(permission: true);
        await fixture.Controller.TickAsync();
        Require(!fixture.Controller.CanRetryError && fixture.Controller.GetConfirmedRecoverySnapshot() is null,
            "Failure retry offered a restart while permission input was pending.");
        fixture.Api.SetPendingInput();
        await fixture.Controller.TickAsync();
        fixture.Api.BlockNextPath("/session/" + fixture.Session + "/message");
        Task<bool> retry = fixture.Controller.RetryErrorAsync();
        await fixture.Api.WaitForBlockedReadAsync();
        Require(!await fixture.Controller.RetryErrorAsync(), "A double-click started a second concurrent error retry.");
        fixture.Controller.ObserveLocalInput("\x03");
        fixture.Api.ReleaseBlockedRead();
        Require(!await retry && fixture.Api.ContinuePosts.Count == 0,
            "A user stop racing final retry validation still sent a prompt.");
    }

    private static async Task AmbiguousFailureRetryNeverReplaysAsync()
    {
        using var fixture = new Fixture("error-ambiguous");
        await fixture.BindAsync();
        fixture.Api.SetNativeFailure("APIError", 503, true);
        await fixture.Controller.TickAsync();
        fixture.Api.LoseNextContinuationResponse = true;
        Require(!await fixture.Controller.RetryErrorAsync(), "Lost error retry response was treated as confirmed acceptance.");
        await AdvanceFailureAsync(fixture, TimeSpan.FromMinutes(12));
        Require(!fixture.Controller.CanRetryError && fixture.Controller.GetConfirmedRecoverySnapshot() is null && fixture.Api.ContinuePosts.Count == 1,
            "An ambiguous failure retry was repeated or offered process replay.");
    }

    private static async Task OwnedExitSnapshotsPreserveOnlyConfirmedWorkAsync()
    {
        using var fixture = new Fixture("error-exit");
        await fixture.BindAsync();
        fixture.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        fixture.Api.SetRunningTool(true);
        await fixture.Controller.TickAsync();
        var snapshot = fixture.Controller.GetConfirmedRecoverySnapshot();
        Require(snapshot is not null && snapshot.UserMessageId == fixture.UserMessage && snapshot.OriginalInstruction == "do work" && snapshot.CanRestart(),
            "An owned process crash during an active tool lost the confirmed exact request.");
        fixture.Controller.Dispose();
        Require(snapshot!.CanRestart(), "Host disposal invalidated the retained crash recovery snapshot.");
        using var changed = new Fixture("error-exit-new-input");
        await changed.BindAsync();
        await changed.Controller.TickAsync();
        var old = changed.Controller.GetConfirmedRecoverySnapshot();
        changed.Controller.ObserveLocalInput("new input");
        Require(old is not null && !old.CanRestart() && changed.Controller.GetConfirmedRecoverySnapshot() is null,
            "New local input failed to invalidate the old crash recovery request.");
        using var immediate = new Fixture("error-exit-immediate");
        immediate.Controller.SetEnabled(true);
        immediate.Controller.ObserveSubmittedPrompt("line one\nline two", immediate.Session, immediate.UserMessage);
        Require(immediate.Controller.GetConfirmedRecoverySnapshot() is null, "Unconfirmed startup POST was offered for process replay.");
        immediate.CanAct = false;
        immediate.Controller.ConfirmApiSubmission(immediate.Session, immediate.UserMessage);
        await UntilAsync(() => immediate.Controller.GetConfirmedRecoverySnapshot() is not null, "retaining an exact accepted receipt before the first live poll");
        var received = immediate.Controller.GetConfirmedRecoverySnapshot();
        Require(received!.OriginalInstruction == "line one\nline two" && received.UserMessageId == immediate.UserMessage,
            "An immediate confirmed receipt crash lost the multiline request identity.");
        immediate.Controller.ObserveLocalInput("\x03");
        Require(!received.CanRestart(), "A manual stop failed to invalidate an immediate receipt crash snapshot.");
    }

    private static async Task FailureRetryRequiresExactAssistantParentAsync()
    {
        foreach (string? parent in new string?[] { null, "older-user-request" })
        {
            using var fixture = new Fixture("error-parent-" + (parent ?? "missing"));
            await fixture.BindAsync();
            fixture.Api.SetNativeFailure("APIError", 503, true);
            fixture.Api.SetFailureParent(parent);
            await fixture.Controller.TickAsync();
            await AdvanceFailureAsync(fixture, TimeSpan.FromMinutes(4));
            Require(!fixture.Controller.CanRetryError && !await fixture.Controller.RetryErrorAsync() && fixture.Api.ContinuePosts.Count == 0,
                "A missing or mismatched assistant parent authorized retry for a different user request.");
        }
    }

    private static async Task ManualRetryContainsUnexpectedReadFaultAsync()
    {
        using var fixture = new Fixture("error-read-fault");
        await fixture.BindAsync();
        fixture.Api.SetNativeFailure("APIError", 503, true);
        await fixture.Controller.TickAsync();
        var diagnostics = new List<OpenCodeAutoResumeDiagnostic>();
        fixture.Controller.DiagnosticEmitted += diagnostics.Add;
        fixture.Api.ThrowNextStatusRead = true;
        Require(!await fixture.Controller.RetryErrorAsync() && fixture.Api.ContinuePosts.Count == 0 && fixture.SessionId() == fixture.Session,
            "An unexpected manual retry read fault escaped or discarded the exact request.");
        Require(diagnostics.Any(value => value.Code == "tick_fault"), "Manual retry read fault lacked its safe diagnostic.");
        await AdvanceFailureAsync(fixture, TimeSpan.FromSeconds(10));
        Require(await fixture.Controller.RetryErrorAsync() && fixture.Api.ContinuePosts.Count == 1,
            "Manual retry did not recover after the bounded read retry interval.");
    }

    private static async Task AmbiguousFailureReceiptReconcilesWithoutBusyAsync()
    {
        using var fixture = new Fixture("error-late-receipt");
        await fixture.BindAsync();
        fixture.Api.SetNativeFailure("APIError", 503, true);
        await fixture.Controller.TickAsync();
        fixture.Api.LoseNextContinuationResponse = true;
        Require(!await fixture.Controller.RetryErrorAsync(), "An uncertain retry was reported as accepted without receipt.");
        PostRecord original = fixture.Api.ContinuePosts.Single();
        fixture.Api.AddUserMessage(original.MessageId, fixture.Clock.GetUtcNow());
        await AdvanceFailureAsync(fixture, TimeSpan.FromSeconds(1));
        Require(fixture.Api.ContinuePosts.Count == 1, "Exact late receipt caused an immediate duplicate send.");
        await AdvanceFailureAsync(fixture, TimeSpan.FromMinutes(3));
        Require(fixture.Api.ContinuePosts.Count == 2 && fixture.Api.ContinuePosts.Last().MessageId != original.MessageId,
            "An exact late retry receipt plus fast failure was not reconciled before the next safe backoff retry.");

        using var finished = new Fixture("error-late-final-receipt");
        await finished.BindAsync();
        finished.Api.SetNativeFailure("APIError", 503, true);
        await finished.Controller.TickAsync();
        finished.Api.LoseNextContinuationResponse = true;
        Require(!await finished.Controller.RetryErrorAsync(), "A lost final retry result was prematurely treated as accepted.");
        finished.Api.AddUserMessage(finished.Api.ContinuePosts.Single().MessageId, finished.Clock.GetUtcNow());
        finished.Api.SetMessageError(false);
        finished.Api.CompleteTask = true;
        await AdvanceFailureAsync(finished, TimeSpan.FromMinutes(4));
        Require(finished.Controller.HasVerifiedCompletion && finished.Api.ContinuePosts.Count == 1,
            "An exact late receipt and fast final response without Busy failed to complete or caused a replay.");
    }

    private static async Task ConcurrentProjectsIsolateRecoveryActionsAsync()
    {
        using var blocked = new Fixture("concurrent-blocked");
        using var second = new Fixture("concurrent-second");
        using var third = new Fixture("concurrent-third");
        await Task.WhenAll(blocked.BindAsync(), second.BindAsync(), third.BindAsync());
        blocked.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        await blocked.Controller.TickAsync();
        var entered = new TaskCompletionSource<OpenCodeStalledRestart>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        blocked.Controller.RestartStalledProcessAsync = request => { entered.TrySetResult(request); return release.Task; };
        await AdvanceBusyAsync(blocked, AbortMinutes + 1);
        Task heldRestart = AdvanceBusyAsync(blocked, 1);
        OpenCodeStalledRestart snapshot = await entered.Task.WaitAsync(WaitLimit);
        Task<bool>? secondRetry = null, thirdRetry = null;
        bool completedBeforeRelease = false, cancelledBeforeRelease = false;
        try
        {
            second.Api.SetNativeFailure("APIError", 503, true);
            third.Api.SetNativeFailure("APIError", 503, true);
            await Task.WhenAll(second.Controller.TickAsync(), third.Controller.TickAsync());
            secondRetry = second.Controller.RetryErrorAsync();
            thirdRetry = third.Controller.RetryErrorAsync();
            Task allRetries = Task.WhenAll(secondRetry, thirdRetry);
            completedBeforeRelease = await Task.WhenAny(allRetries, Task.Delay(TimeSpan.FromSeconds(1))) == allRetries;
            blocked.Controller.ObserveLocalInput("\x03");
            cancelledBeforeRelease = !snapshot.CanRestart();
            // Independent native monitoring and UI-facing reads remain responsive
            // even while another project's replacement is waiting for readiness/resources.
            await Task.WhenAll(second.Controller.TickAsync(), third.Controller.TickAsync()).WaitAsync(WaitLimit);
            _ = second.Controller.Status;
            _ = third.Controller.Status;
        }
        finally
        {
            release.TrySetResult(false);
            await heldRestart.WaitAsync(WaitLimit);
            if (secondRetry is not null && thirdRetry is not null)
                await Task.WhenAll(secondRetry, thirdRetry).WaitAsync(WaitLimit);
        }
        Require(completedBeforeRelease && secondRetry!.Result && thirdRetry!.Result &&
            second.Api.ContinuePosts.Count == 1 && third.Api.ContinuePosts.Count == 1,
            "A blocked process restart in one project starved two unrelated active projects' exact-session error retries.");
        Require(cancelledBeforeRelease && blocked.Api.ContinuePosts.Count == 0,
            "A stop during a blocked restart failed to invalidate its guarded request.");
    }

    private static async Task SameProjectRecoveryActionsRemainSerializedAsync()
    {
        using var blocked = new Fixture("same-project-blocked");
        using var waiting = new Fixture("same-project-waiting", projectPath: blocked.ProjectPath.ToUpperInvariant());
        await Task.WhenAll(blocked.BindAsync(), waiting.BindAsync());
        blocked.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        await blocked.Controller.TickAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        blocked.Controller.RestartStalledProcessAsync = request => { entered.TrySetResult(); return release.Task; };
        await AdvanceBusyAsync(blocked, AbortMinutes + 1);
        Task heldRestart = AdvanceBusyAsync(blocked, 1);
        await entered.Task.WaitAsync(WaitLimit);
        Task<bool>? retry = null;
        bool remainedQueued = false, cancelledPromptly = false;
        try
        {
            waiting.Api.SetNativeFailure("APIError", 503, true);
            await waiting.Controller.TickAsync();
            retry = waiting.Controller.RetryErrorAsync();
            remainedQueued = await Task.WhenAny(retry, Task.Delay(150)) != retry && waiting.Api.ContinuePosts.Count == 0;
            waiting.Controller.ObserveLocalInput("\x03");
            cancelledPromptly = !await retry.WaitAsync(WaitLimit);
        }
        finally
        {
            release.TrySetResult(false);
            await heldRestart.WaitAsync(WaitLimit);
            if (retry is not null) await retry.WaitAsync(WaitLimit);
        }
        Require(remainedQueued && cancelledPromptly && waiting.Api.ContinuePosts.Count == 0,
            "The same canonical project bypassed recovery serialization or its queued action ignored cancellation.");
        var gateType = typeof(OpenCodeAutoResumeController).Assembly.GetType("TerminalNotes.Services.ProjectRecoveryActionGate")!;
        var registry = (System.Collections.IDictionary)gateType.GetField("Entries",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        Require(registry.Count == 0, "Recovery action gates retained idle or canceled project entries.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly bool _ownsProject;
        private readonly HttpClient _http;
        private readonly OpenCodeRecoveryClient _client;

        public Fixture(string session, string planRelativePath = "plan.md", string? parentSessionId = null, string? projectPath = null,
            TestTimeProvider? clock = null, Func<Action<string>, CancellationToken, Task<IDisposable>>? acquireContinuationAdmission = null)
        {
            Clock = clock ?? new();
            Session = session;
            ParentSessionId = parentSessionId;
            ProjectPath = projectPath ?? Path.Combine(Path.GetTempPath(), "TerminalNotes AutoResume " + Guid.NewGuid().ToString("N"));
            _ownsProject = projectPath is null;
            Directory.CreateDirectory(ProjectPath);
            PlanPath = planRelativePath.Length == 0 ? null : Path.Combine(ProjectPath, planRelativePath);
            AlternatePlanPath = Path.Combine(ProjectPath, "alternate-plan.md");
            File.WriteAllText(AlternatePlanPath, "# Alternate plan\nContinue the requested work.\n");
            if (PlanPath is not null) File.WriteAllText(PlanPath, "# Project plan\nContinue the requested work.\n");

            Api = new FakeOpenCodeApi(ProjectPath, session, parentSessionId, UserMessage, () => Clock.GetUtcNow());
            _http = new HttpClient(Api) { Timeout = Timeout.InfiniteTimeSpan };
            _client = new OpenCodeRecoveryClient(new Uri("http://127.0.0.1:43219/"), ProjectPath, _http);
            Controller = new OpenCodeAutoResumeController(ProjectPath, PlanPath, _client, () => CanAct, Clock, acquireContinuationAdmission);
        }

        public string Session { get; }
        public bool CanAct { get; set; } = true;
        public string UserMessage => "user-initial-" + Session;
        public string? ParentSessionId { get; }
        public string ProjectPath { get; }
        public string? PlanPath { get; }
        public string AlternatePlanPath { get; }
        public TestTimeProvider Clock { get; }
        public FakeOpenCodeApi Api { get; }
        public OpenCodeAutoResumeController Controller { get; }
        public string? SessionId() => typeof(OpenCodeAutoResumeController)
            .GetField("_sessionId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(Controller) as string;

        public async Task BindAsync(bool observeTodo = true)
        {
            Controller.SetEnabled(true);
            Controller.ObserveLocalInput("do work\r");
            Controller.ObserveNativeSignal(UserSignal(this, Session, UserMessage, Clock.GetUtcNow(), 10));
            await UntilAsync(() => SessionId() == Session, "binding the native root session");
            if (observeTodo) Controller.ObserveNativeSignal(new("todo.updated", Session, null, null, 11));
        }

        public void Dispose()
        {
            Controller.Dispose();
            _http.Dispose();
            if (_ownsProject)
            {
                try { Directory.Delete(ProjectPath, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 21, 0, 0, 0, TimeSpan.Zero);
        private long _timestamp;

        public override DateTimeOffset GetUtcNow() => _now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed)
        {
            _now += elapsed;
            _timestamp += elapsed.Ticks;
        }
    }

    private sealed record PostRecord(string SessionId, string MessageId, string Body);

    private sealed class FakeOpenCodeApi : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly string _directory;
        private readonly string _session;
        private readonly string? _parentSessionId;
        private readonly string _initialUserMessage;
        private readonly Func<DateTimeOffset> _now;
        private readonly List<(string Id, string Role, DateTimeOffset Created)> _messages = [];
        private readonly ConcurrentQueue<PostRecord> _continuePosts = new();
        private readonly ConcurrentQueue<PostRecord> _abortPosts = new();
        private readonly ConcurrentQueue<string> _calls = new();
        private TaskCompletionSource? _blockedRead;
        private TaskCompletionSource? _blockedEntered;
        private string? _blockPath;
        private OpenCodeRecoveryStatus _status = OpenCodeRecoveryStatus.Idle;
        public long? RetryNext;
        public int RetryAttempt = 1;
        public string RetryMessage = "provider waiting";
        public Dictionary<string, string>? ErrorResponseHeaders;
        private bool _runningTool;
        private string _runningToolCommand = "long task";
        private bool _permission;
        private bool _question;
        private bool _messageError;
        private string _messageErrorType = "fixture_error";
        private bool _mixedAbortAndUnrelatedToolErrors;
        private bool _completedToolEvidence;
        private bool _completeTodoOnMessageIdLookup;
        private bool _unknownStatus;
        private bool _removeSessionFromDirectoryList;
        private long _messageOrdinal;
        private int _messageReadCount;
        private int? _mutateProgressOnMessageRead;
        private string? _assistantText;
        public int CompactionPosts;
        public bool LoseNextCompactionResponse;
        public Action? OnCompaction;
        public RecoveryModelSelection? CompactionModel;
        public Action? BeforeAgentRead;
        public bool CompleteTask;
        public bool NoTodos;
        public bool ThrowNextStatusRead;
        public int StatusFailuresRemaining;
        public bool FailNextMessageLookup;
        public bool PendingTodoOverride;
        public string? TodoStatusOverride;
        public string? FinalParentOverride;
        public string? FinalFinishOverride;
        public bool LoseNextContinuationResponse;
        public bool LoseNextAbortResponse;
        public Func<string>? MessagesOverride;
        public Action? BeforeMessageLookup;
        public bool UniqueAssistantPerRequest;
        public bool IncludeEarlierToolError;

        public FakeOpenCodeApi(string directory, string session, string? parentSessionId,
            string initialUserMessage, Func<DateTimeOffset> now)
        {
            _directory = directory;
            _session = session;
            _parentSessionId = parentSessionId;
            _initialUserMessage = initialUserMessage;
            _now = now;
            _messages.Add((initialUserMessage, "user", now()));
        }

        public IReadOnlyCollection<PostRecord> ContinuePosts => _continuePosts.ToArray();
        public IReadOnlyCollection<PostRecord> AbortPosts => _abortPosts.ToArray();
        public int ReadCount => _calls.Count;
        public bool RemoveSessionFromDirectoryList { get { lock (_gate) return _removeSessionFromDirectoryList; } set { lock (_gate) _removeSessionFromDirectoryList = value; } }

        public void SetStatus(OpenCodeRecoveryStatus status) { lock (_gate) _status = status; }
        public void SetRunningTool(bool value, string command = "long task") { lock (_gate) { _runningTool = value; _runningToolCommand = command; } }
        public void SetPendingInput(bool permission = false, bool question = false) { lock (_gate) { _permission = permission; _question = question; } }
        public void SetMessageError(bool value) { lock (_gate) _messageError = value; }
        private int? _errorStatusCode;
        private bool? _errorRetryable;
        private bool _overrideFailureParent;
        private string? _failureParent;
        public void SetFailureParent(string? parent) { lock (_gate) { _overrideFailureParent = true; _failureParent = parent; } }
        public void SetNativeFailure(string type, int? status = null, bool? retryable = null)
        { lock (_gate) { _messageError = true; _messageErrorType = type; _errorStatusCode = status; _errorRetryable = retryable; } }
        public void SetAbortCancellationError() { lock (_gate) { _messageError = true; _messageErrorType = "MessageAbortedError"; } }
        public void SetMixedAbortAndUnrelatedToolErrors()
        {
            lock (_gate)
            {
                _messageError = true;
                _messageErrorType = "MessageAbortedError";
                _mixedAbortAndUnrelatedToolErrors = true;
            }
        }
        public void SetUnknownStatus(bool value) { lock (_gate) _unknownStatus = value; }
        public void AddUserMessage(string messageId, DateTimeOffset created)
        {
            lock (_gate) _messages.Add((messageId, "user", created));
        }
        public void SetAssistantText(string value) { lock (_gate) _assistantText = value; }
        public void SetCompletedToolEvidence(bool value) { lock (_gate) _completedToolEvidence = value; }

        public void AddAssistantProgressOnMessageRead(int readOffset)
        {
            lock (_gate) _mutateProgressOnMessageRead = _messageReadCount + readOffset;
        }
        public void CompleteTodoOnMessageIdLookup()
        {
            lock (_gate) _completeTodoOnMessageIdLookup = true;
        }

        public void BlockNextPath(string path)
        {
            lock (_gate)
            {
                _blockPath = path;
                _blockedRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _blockedEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public Task WaitForBlockedReadAsync()
        {
            Task? entered;
            lock (_gate) entered = _blockedEntered?.Task;
            return (entered ?? throw new InvalidOperationException("No blocked request was configured.")).WaitAsync(WaitLimit);
        }

        public void ReleaseBlockedRead()
        {
            TaskCompletionSource? blocked;
            lock (_gate) blocked = _blockedRead;
            blocked?.TrySetResult();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri ?? throw new InvalidOperationException("Request URI missing.");
            string path = uri.AbsolutePath;
            _calls.Enqueue(request.Method + " " + path);
            Task? wait = null;
            lock (_gate)
            {
                if (_blockPath == path)
                {
                    _blockPath = null;
                    _blockedEntered!.TrySetResult();
                    wait = _blockedRead!.Task;
                }
            }
            if (wait is not null) await wait.ConfigureAwait(false); // Deliberately ignore cancellation to exercise post-read generation checks.

            if (request.Method == HttpMethod.Post && path.EndsWith("/prompt_async", StringComparison.Ordinal))
            {
                string messageId;
                string body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using (JsonDocument document = JsonDocument.Parse(body))
                    messageId = document.RootElement.GetProperty("messageID").GetString()!;
                var post = new PostRecord(ExtractSession(path), messageId, body);
                _continuePosts.Enqueue(post);
                bool lose;
                lock (_gate) { lose = LoseNextContinuationResponse; LoseNextContinuationResponse = false; }
                if (lose) throw new HttpRequestException("fixture lost response after possible dispatch");
                lock (_gate) _messages.Add((messageId, "user", _now().AddSeconds(++_messageOrdinal)));
                return Response(request, HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/summarize", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref CompactionPosts);
                OnCompaction?.Invoke();
                if (LoseNextCompactionResponse) { LoseNextCompactionResponse = false; throw new HttpRequestException("fixture uncertain compaction dispatch"); }
                return Json(request, "true");
            }
            if (request.Method == HttpMethod.Get && path == "/agent")
            {
                BeforeAgentRead?.Invoke();
                return Json(request, JsonSerializer.Serialize(new[] { new { name = "compaction", model = CompactionModel is { } selected ? new { providerID = selected.ProviderId, modelID = selected.ModelId } : null } }));
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/abort", StringComparison.Ordinal))
            {
                _abortPosts.Enqueue(new(ExtractSession(path), "", ""));
                if (LoseNextAbortResponse)
                {
                    LoseNextAbortResponse = false;
                    throw new HttpRequestException("fixture lost abort response after possible dispatch");
                }
                return Json(request, "true");
            }

            if (request.Method != HttpMethod.Get) return Response(request, HttpStatusCode.MethodNotAllowed);
            if (path == "/session")
            {
                bool omit;
                lock (_gate) omit = _removeSessionFromDirectoryList;
                string json = omit ? "[]" : JsonSerializer.Serialize(new[] { SessionInfo(_session, _parentSessionId) });
                return Json(request, json);
            }
            if (path == "/session/status")
            {
                OpenCodeRecoveryStatus status;
                bool unknown;
                bool fail;
                lock (_gate) { status = _status; unknown = _unknownStatus; fail = ThrowNextStatusRead; ThrowNextStatusRead = false; }
                if (fail) throw new InvalidOperationException("fixture transient read fault");
                if (StatusFailuresRemaining > 0) { StatusFailuresRemaining--; throw new HttpRequestException("fixture transient network read failure"); }
                string kind = unknown ? "mystery" : status switch
                {
                    OpenCodeRecoveryStatus.Busy => "busy",
                    OpenCodeRecoveryStatus.Retry => "retry",
                    _ => "idle"
                };
                return Json(request, JsonSerializer.Serialize(new Dictionary<string, object> { [_session] = new { type = kind, attempt = RetryAttempt, message = RetryMessage, next = RetryNext } }));
            }
            if (path == "/permission" || path == "/question")
            {
                bool include;
                lock (_gate) include = path == "/permission" ? _permission : _question;
                return Json(request, include
                    ? JsonSerializer.Serialize(new[] { new { id = "request-1", sessionID = _session } })
                    : "[]");
            }
            if (path == "/session/" + Uri.EscapeDataString(_session))
                return Json(request, JsonSerializer.Serialize(SessionInfo(_session, _parentSessionId)));
            if (path == "/session/" + Uri.EscapeDataString(_session) + "/children")
                return Json(request, "[]");
            if (NoTodos && path == "/session/" + Uri.EscapeDataString(_session) + "/todo") return Json(request, "[]");
            if (path == "/session/" + Uri.EscapeDataString(_session) + "/todo")
                return Json(request, "[{\"content\":\"Implement requested work\",\"status\":\"" + (TodoStatusOverride ?? (CompleteTask && !PendingTodoOverride ? "completed" : "pending")) + "\",\"priority\":\"high\"}]");
            if (path == "/session/" + Uri.EscapeDataString(_session) + "/message")
            {
                lock (_gate)
                {
                    _messageReadCount++;
                    if (_mutateProgressOnMessageRead == _messageReadCount)
                    {
                        _assistantText = "New native assistant progress during the final recovery read.";
                        _completedToolEvidence = true;
                    }
                }
                return Json(request, MessagesJson());
            }
            if (path.StartsWith("/session/" + Uri.EscapeDataString(_session) + "/message/", StringComparison.Ordinal))
            {
                BeforeMessageLookup?.Invoke();
                if (FailNextMessageLookup) { FailNextMessageLookup = false; throw new HttpRequestException("fixture transient message-ID read failure"); }
                lock (_gate)
                {
                    if (_completeTodoOnMessageIdLookup)
                    {
                        _completeTodoOnMessageIdLookup = false;
                        CompleteTask = true;
                    }
                }
                string messageId = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
                var found = FindMessage(messageId);
                return found is null ? Response(request, HttpStatusCode.NotFound) : Json(request, JsonSerializer.Serialize(new { info = new { id = found.Value.Id, sessionID = _session, role = found.Value.Role } }));
            }
            return Response(request, HttpStatusCode.NotFound);
        }

        private string MessagesJson()
        {
            if (MessagesOverride is { } build) return build();
            List<(string Id, string Role, DateTimeOffset Created)> messages;
            bool tool, error, completedTool, mixedAbortAndUnrelatedToolErrors;
            string? assistantText;
            lock (_gate) { messages = [.. _messages]; tool = _runningTool; error = _messageError; completedTool = _completedToolEvidence; mixedAbortAndUnrelatedToolErrors = _mixedAbortAndUnrelatedToolErrors; assistantText = _assistantText; }
            var rows = messages.Select(message => new
            {
                info = new
                {
                    id = message.Id,
                    role = message.Role,
                    sessionID = _session,
                    time = new { created = message.Created.ToUnixTimeMilliseconds() },
                    error = error && message.Role == "user" ? new { name = "fixture_error" } : null
                },
                parts = message.Role == "assistant" && tool
                    ? new object[] { new { type = "tool", tool = "shell", state = new { status = "running", title = "running fixture tool", input = new { command = _runningToolCommand } } } }
                    : Array.Empty<object>()
            }).ToArray();
            if (CompleteTask) assistantText = "Implemented and verified.";
            if (!error && !tool && !completedTool && assistantText is null) return JsonSerializer.Serialize(rows);
            var assistant = new
            {
                info = new
                {
                    id = UniqueAssistantPerRequest ? "assistant-fixture-" + messages.Last(message => message.Role == "user").Id : "assistant-fixture",
                    role = "assistant",
                    sessionID = _session,
                    parentID = CompleteTask && FinalParentOverride is not null ? FinalParentOverride : error && _overrideFailureParent ? _failureParent : CompleteTask || error ? messages.Last(message => message.Role == "user").Id : null,
                    finish = CompleteTask ? FinalFinishOverride ?? "stop" : null,
                    time = new { created = (CompleteTask ? _now() : _now().AddHours(-1)).ToUnixTimeMilliseconds(), completed = CompleteTask ? (long?)_now().ToUnixTimeMilliseconds() : null },
                    error = error ? new { name = _messageErrorType, data = new { statusCode = _errorStatusCode, isRetryable = _errorRetryable, responseHeaders = ErrorResponseHeaders } } : null
                },
                parts = BuildAssistantParts(tool, completedTool, assistantText, mixedAbortAndUnrelatedToolErrors)
            };
            IEnumerable<object> responseRows = rows.Cast<object>();
            if (IncludeEarlierToolError)
            {
                var user = messages.Last(message => message.Role == "user");
                responseRows = responseRows.Append(new
                {
                    info = new { id = "assistant-tool-error-" + user.Id, role = "assistant", sessionID = _session, parentID = user.Id,
                        finish = "tool-calls", time = new { created = user.Created.ToUnixTimeMilliseconds() } },
                    parts = new[] { new { type = "tool", tool = "read", state = new { status = "error", input = new { filePath = "missing.txt" }, output = "File not found" } } }
                });
            }
            return JsonSerializer.Serialize(responseRows.Append(assistant).ToArray());
        }

        private object[] BuildAssistantParts(bool runningTool, bool completedTool, string? text, bool mixedAbortAndUnrelatedToolErrors)
        {
            var parts = new List<object>();
            if (text is not null) parts.Add(new { type = "text", text });
            if (runningTool)
                parts.Add(new { type = "tool", tool = "shell", state = new { status = "running", title = "running fixture tool", input = new { command = _runningToolCommand } } });
            else if (completedTool)
                parts.Add(new { type = "tool", tool = "shell", state = new { status = "completed", title = "build", input = new { command = "dotnet test" } } });
            if (mixedAbortAndUnrelatedToolErrors)
            {
                parts.Add(new { type = "tool", tool = "shell", state = new { status = "error", input = new { command = _runningToolCommand }, output = "Tool execution aborted" } });
                parts.Add(new { type = "tool", tool = "shell", state = new { status = "error", input = new { command = "unrelated provider task" }, output = "provider failed" } });
            }
            return [.. parts];
        }

        private (string Id, string Role, DateTimeOffset Created)? FindMessage(string id)
        {
            lock (_gate) return _messages.Cast<(string Id, string Role, DateTimeOffset Created)?>().FirstOrDefault(message => message?.Id == id);
        }

        private object SessionInfo(string id, string? parent) => parent is null
            ? new { id, directory = _directory }
            : new { id, directory = _directory, parentID = parent };

        private static string ExtractSession(string path)
        {
            string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return Uri.UnescapeDataString(segments[1]);
        }

        private static HttpResponseMessage Json(HttpRequestMessage request, string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
            RequestMessage = request
        };

        private static HttpResponseMessage Response(HttpRequestMessage request, HttpStatusCode status) => new(status)
        {
            RequestMessage = request
        };
    }
}
