using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TerminalNotes;
using TerminalNotes.Services;

internal static partial class Program
{
    private static async Task VerifyPlanMakerTuiSelectionAsync(TerminalPane pane, OpenCodeHeadlessHost backend, string fixture)
    {
        object? oldHooks = Field<object?>(pane, "_tuiConnectionHooks");
        object? oldClient = Field<object?>(pane, "_headlessOpenCodeClient");
        object? oldStatus = Field<object?>(pane, "_openCodeStatus");
        string oldDraft = Field<string>(pane, "_nativeComposerDraft");
        using var transport = new FakePlannerTransport(fixture);
        using var http = new HttpClient(transport);
        using var client = new OpenCodeConversationClient(new Uri("http://127.0.0.1:43129/"), fixture, http);
        int attaches = 0, selections = 0;
        bool blockSelection = false, replaceOwner = false, failAttach = false, silentAttach = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSelection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<string, CancellationToken, Task> select = async (id, token) =>
        {
            selections++;
            Require(id is "ses_exact_existing" or FakePlannerTransport.PlanningSession, "Planner selected another pane's session.");
            if (id != FakePlannerTransport.PlanningSession) return;
            if (replaceOwner) Set(pane, "_headlessSessionId", "ses_new_user_selection");
            if (blockSelection) { entered.TrySetResult(); await releaseSelection.Task.WaitAsync(token); }
        };
        Func<string, CancellationToken, Task<string>> command = (id, token) =>
        {
            Require(id == FakePlannerTransport.PlanningSession && transport.Prompts == 0,
                "Planner attached the base session or submitted before exact-session readiness.");
            attaches++;
            // A launch rejection must exit before any fake display can satisfy readiness.
            return Task.FromResult(failAttach ? "[Environment]::Exit(7)" : silentAttach ? "Start-Sleep -Seconds 90" :
                "Write-Output 'fixture exact planner frame'; Start-Sleep -Seconds 90");
        };
        Type hooksType = typeof(TerminalPane).Assembly.GetType("TerminalNotes.OpenCodeTuiConnectionHooks")!;
        Set(pane, "_tuiConnectionHooks", Activator.CreateInstance(hooksType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [select, command, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(30)], null));
        Set(pane, "_headlessOpenCodeClient", client);
        Set(pane, "_nativeComposerDraft", "");
        Set(pane, "_headlessSessionId", "ses_exact_existing");
        Task<string> Plan(Func<bool> owner, CancellationToken token) => (Task<string>)Invoke(pane,
            "CreatePlanWithQuestionsAsync", "Fixture planner request", owner, (Action<string>)(_ => { }), token, (Action<string>)(_ => { }), null, false)!;
        async Task Rejected(Task task)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("Unowned or cancelled planning unexpectedly succeeded."); }
            catch (OperationCanceledException) { }
        }
        async Task ReconnectOriginalAsync(string message)
        {
            object plannerHooks = Field<object>(pane, "_tuiConnectionHooks");
            Set(pane, "_tuiConnectionHooks", oldHooks);
            try { Require(await RetryAsync(pane), message); }
            finally { Set(pane, "_tuiConnectionHooks", plannerHooks); }
        }
        bool verified = false;
        try
        {
            // A true initial broadcast does not prove a subscriber. The replacement
            // command must target B before the one and only model submission.
            Task<string> planning = Plan(() => true, CancellationToken.None);
            await UntilAsync(() =>
            {
                if (planning.IsCompleted && transport.Prompts == 0) planning.GetAwaiter().GetResult();
                return transport.Prompts == 1;
            });
            Require(attaches == 1 && pane.IsReady && backend.IsRunning, "Exact planner attach did not preserve server readiness.");
            Console.WriteLine("PlanMaker fixture: exact attach and single submission verified.");
            int atQuestion = selections;
            await Task.Delay(700);
            Require(!planning.IsCompleted && selections == atQuestion && attaches == 1 && transport.Prompts == 1,
                "Pending question triggered display reselection, process replacement, or another prompt.");
            transport.Complete = true;
            Require((await planning.WaitAsync(TimeSpan.FromSeconds(10))).Contains("Fixture plan", StringComparison.Ordinal) && transport.Prompts == 1 &&
                Field<string>(pane, "_headlessSessionId") == "ses_exact_existing" && backend.IsRunning,
                "Planner completion replayed submission or replaced its original session/server.");
            Console.WriteLine("PlanMaker fixture: question wait and completion verified.");

            await VerifyContinuousPlanMakerAsync(pane, transport, () => attaches, () => selections);
            await VerifyRecommendedPlanMakerAnswersAsync(pane, transport);

            transport.Reset();
            Task<string> disconnected = Plan(() => true, CancellationToken.None);
            await UntilAsync(() =>
            {
                if (disconnected.IsCompleted && transport.Prompts == 0) disconnected.GetAwaiter().GetResult();
                return transport.Prompts == 1;
            });
            ConPtySession lostAttachment = Field<ConPtySession>(pane, "_session");
            Require(lostAttachment.ProcessId is not null, "Planner attachment has no owned process to terminate.");
            // Graceful disposal suppresses Exited. Kill the owned fixture process so
            // the real unexpected-exit path exposes manual reconnection; hold off
            // automatic retries while this fixture checks the failed planning task.
            Set(pane, "_tuiReconnectAttempts", 2);
            using (var attachProcess = System.Diagnostics.Process.GetProcessById(checked((int)lostAttachment.ProcessId!.Value)))
            {
                attachProcess.Kill(entireProcessTree: true);
                await attachProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            try
            {
                await disconnected.WaitAsync(TimeSpan.FromSeconds(10));
                throw new InvalidOperationException("A disconnected planner kept waiting for an unreachable question.");
            }
            catch (IOException exception) when (exception.Message.Contains("연결이 끊겼습니다", StringComparison.Ordinal)) { }
            await UntilAsync(() => !lostAttachment.IsRunning && Field<bool>(pane, "_tuiConnectionFailed"));
            Require(transport.Prompts == 1 && backend.IsRunning &&
                Field<string>(pane, "_headlessSessionId") == "ses_exact_existing" &&
                Field<string?>(pane, "_planMakerSessionId") is null,
                "Late attach failure replayed a prompt, stopped the backend, or retained its temporary selection.");
            await ReconnectOriginalAsync("Late attach failure did not allow manual reconnection.");
            Console.WriteLine("PlanMaker fixture: post-submission TUI exit fails promptly and permits reconnection.");

            transport.Reset();
            silentAttach = true;
            Task<string> silentAttachment = Plan(() => true, CancellationToken.None);
            try
            {
                await silentAttachment.WaitAsync(TimeSpan.FromSeconds(10));
                throw new InvalidOperationException("A silent attachment was accepted as a ready planning view.");
            }
            catch (TimeoutException) when (silentAttachment.IsFaulted) { }
            Require(transport.Prompts == 0 && !pane.IsReady && backend.IsRunning &&
                Field<bool>(pane, "_tuiConnectionFailed") && Field<ConPtySession>(pane, "_session").IsRunning &&
                Field<string>(pane, "_headlessSessionId") == "ses_exact_existing" &&
                Field<string?>(pane, "_planMakerSessionId") is null,
                "Silent-output timeout submitted a prompt, lost retained state, or omitted manual retry.");
            silentAttach = false;
            await ReconnectOriginalAsync("Silent-output timeout did not permit manual reconnection.");
            Console.WriteLine("PlanMaker fixture: silent live attach times out without submission and permits reconnection.");

            transport.Reset();
            int before = attaches;
            await Rejected(Plan(() => false, CancellationToken.None));
            Require(attaches == before && transport.Prompts == 0, "Missing modal ownership allowed a new TUI or prompt.");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Rejected(Plan(() => true, cancelled.Token));
            }
            Require(attaches == before && transport.Prompts == 0, "Cancelled planning reached its attach or prompt.");

            Set(pane, "_nativeComposerDraft", "user answer remains local");
            try { await Plan(() => true, CancellationToken.None); throw new InvalidOperationException("Local input was discarded."); }
            catch (InvalidOperationException) when (Field<string>(pane, "_nativeComposerDraft") == "user answer remains local" &&
                attaches == before && transport.Prompts == 0) { }
            Set(pane, "_nativeComposerDraft", "");

            blockSelection = true;
            using (var cancellation = new CancellationTokenSource())
            {
                Task<string> waiting = Plan(() => true, cancellation.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                await Rejected(waiting);
            }
            blockSelection = false;
            Require(attaches == before && transport.Prompts == 0 && backend.IsRunning,
                "Cancellation while selecting launched a TUI, submitted a prompt, or stopped the backend.");
            Console.WriteLine("PlanMaker fixture: preflight and selection cancellation verified.");

            foreach (bool changeInput in new[] { true, false })
            {
                Set(pane, "_ready", true); Set(pane, "_failed", false);
                entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                releaseSelection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                blockSelection = true;
                bool owned = true;
                object previousSession = Field<object>(pane, "_session");
                Task<string> waiting = Plan(() => owned, CancellationToken.None);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (changeInput) Set(pane, "_nativeComposerDraft", "answer typed during selection");
                else owned = false;
                releaseSelection.TrySetResult();
                await Rejected(waiting);
                blockSelection = false;
                Require(ReferenceEquals(previousSession, Field<object>(pane, "_session")) &&
                    (bool)previousSession.GetType().GetProperty("IsRunning")!.GetValue(previousSession)! &&
                    attaches == before && transport.Prompts == 0,
                    "Input or modal ownership changed during selection, but its original attach was stopped/replaced.");
                Set(pane, "_nativeComposerDraft", "");
            }
            Console.WriteLine("PlanMaker fixture: delayed-selection input and ownership guards verified.");

            Set(pane, "_ready", true); Set(pane, "_failed", false);
            replaceOwner = true;
            await Rejected(Plan(() => true, CancellationToken.None));
            replaceOwner = false;
            Require(attaches == before && transport.Prompts == 0 &&
                Field<string>(pane, "_headlessSessionId") == "ses_new_user_selection",
                "Stale planner attachment overrode a newer user-selected session.");
            Set(pane, "_headlessSessionId", "ses_exact_existing");
            Set(pane, "_ready", true); Set(pane, "_failed", false);

            failAttach = true;
            Task<string> failedAttachment = Plan(() => true, CancellationToken.None);
            try { await failedAttachment.WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("Failed attach was accepted."); }
            catch (TimeoutException) when (!failedAttachment.IsCompleted)
            {
                object session = Field<object>(pane, "_session");
                Console.Error.WriteLine($"Failed-attach timeout: prompts={transport.Prompts}, ready={pane.IsReady}, attaches={attaches}, " +
                    $"stage={Field<string>(pane, "_tuiConnectionStage")}, task={failedAttachment.Status}, backend={backend.IsRunning}, " +
                    $"attachRunning={session.GetType().GetProperty("IsRunning")!.GetValue(session)}, " +
                    $"attachPid={session.GetType().GetField("_processId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)}, " +
                    $"lastExit={Field<int?>(pane, "_tuiLastExitCode")}");
                throw;
            }
            catch (Exception) when (failedAttachment.IsFaulted && transport.Prompts == 0 && !pane.IsReady && backend.IsRunning) { }
            Require(attaches == before + 1 && transport.Prompts == 0 && backend.IsRunning,
                "Attach failure did not reach its single attach or still submitted/stopped the planner backend.");
            Console.WriteLine("PASS PlanMaker TUI: exact session attach before sole prompt, no reselection during questions, original session/server retained, ownership/input/cancellation/early-exit guards; fake API only.");
            verified = true;
        }
        finally
        {
            Set(pane, "_tuiConnectionHooks", oldHooks);
            Set(pane, "_headlessOpenCodeClient", oldClient);
            Set(pane, "_openCodeStatus", oldStatus);
            Set(pane, "_nativeComposerDraft", oldDraft);
            Set(pane, "_headlessSessionId", "ses_exact_existing");
            if (verified) Require(await RetryAsync(pane), "Focused planner fixture failed to restore its original ready TUI.");
        }
    }

    private static async Task VerifyContinuousPlanMakerAsync(TerminalPane pane, FakePlannerTransport transport,
        Func<int> attaches, Func<int> selections)
    {
        Type controlType = typeof(TerminalPane).Assembly.GetType("TerminalNotes.Services.PlanMakerInterviewControl")!;
        object Control() => Activator.CreateInstance(controlType, nonPublic: true)!;
        bool Paused(object control) => (bool)controlType.GetProperty("IsAwaitingContinuation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(control)!;
        var drafts = new List<string>();
        Task<string> Start(object control, CancellationToken token = default) => (Task<string>)Invoke(pane,
            "CreatePlanWithQuestionsAsync", "Fixture rough idea", (Func<bool>)(() => true), (Action<string>)(_ => { }), token,
            (Action<string>)(draft => drafts.Add(draft)), control, false)!;
        transport.Reset(); transport.InterviewMode = true;
        object interview = Control();
        int oldAttaches = attaches();
        Task<string> planning = Start(interview);
        await UntilAsync(() => transport.Prompts == 2);
        int questionSelections = selections();
        Require(!planning.IsCompleted && transport.MessageIds.Distinct().Count() == 2 && attaches() == oldAttaches + 1,
            "Early model stop did not continue in one owned session with a fresh request ID.");
        await Task.Delay(650);
        transport.Answer("## Second answer\nConfirmed second detail.");
        await UntilAsync(() => transport.Prompts == 3);
        await Task.Delay(650);
        Set(pane, "_nativeComposerDraft", "pending user text");
        transport.Answer("## Third answer\nConfirmed third detail.");
        await Task.Delay(700);
        Require(transport.Prompts == 3 && selections() == questionSelections && !planning.IsCompleted,
            "Pending local input or a third answer triggered unsafe submission or TUI reselection.");
        Set(pane, "_nativeComposerDraft", "");
        await UntilAsync(() => Paused(interview));
        Require(transport.Prompts == 5 && drafts[^1].Contains("First detail", StringComparison.Ordinal) &&
            drafts[^1].Contains("Confirmed second detail", StringComparison.Ordinal) && drafts[^1].Contains("Confirmed third detail", StringComparison.Ordinal),
            "No-question pause lost earlier omitted drafts or counted a questioned turn as questionless.");
        int pausedPrompts = transport.Prompts;
        await Task.Delay(700);
        Require(transport.Prompts == pausedPrompts, "A questionless model produced an unattended prompt loop.");
        transport.PendingPermission = true;
        Invoke(interview, "RequestContinuation");
        await Task.Delay(700);
        Require(transport.Prompts == pausedPrompts && Paused(interview), "Pending native permission allowed a continuation submission.");
        transport.PendingPermission = false;
        await UntilAsync(() => transport.Prompts == 6 && !Paused(interview));
        Require(transport.MessageIds.Distinct().Count() == 6 && selections() == questionSelections && attaches() == oldAttaches + 1,
            "Explicit continuation replaced the session, reused a request, or reattached its native TUI.");
        Invoke(interview, "RequestFinish");
        string plan = await planning.WaitAsync(TimeSpan.FromSeconds(10));
        Require(plan.Contains("Confirmed third detail", StringComparison.Ordinal) && plan.Contains("Keep existing answers", StringComparison.Ordinal) &&
            transport.Rejects == 1 && transport.Aborts == 2 && transport.Prompts == 7 && transport.LastPrompt.Contains("finished this planning interview", StringComparison.Ordinal) &&
            Field<string>(pane, "_headlessSessionId") == "ses_exact_existing",
            "Finishing a pending question lost confirmed draft, skipped the final plan request, failed cleanup, or submitted again.");

        transport.Reset(); transport.InterviewMode = true; transport.StartWithQuestion = true;
        drafts.Clear(); interview = Control();
        planning = Start(interview);
        await UntilAsync(() => drafts.Count > 0);
        transport.BeginAnswer();
        Invoke(interview, "RequestFinish");
        await Task.Delay(700);
        Require(!planning.IsCompleted && transport.Prompts == 1, "Finish discarded an answer still processing or sent a follow-up.");
        transport.CompleteAnswerOnNextStatus("# First detail\nConfirmed purpose.\n\n## Latest answer\nSubmitted answer retained.");
        plan = await planning.WaitAsync(TimeSpan.FromSeconds(10));
        Require(plan.Contains("Submitted answer retained", StringComparison.Ordinal) && transport.Aborts == 1 && transport.Rejects == 0 && transport.Prompts == 2,
            "Finish returned a stale draft before the submitted answer drained, or skipped the final plan request.");

        // The user types their own message into the planner's terminal mid-interview: the interview goes on with it.
        transport.Reset(); transport.InterviewMode = true; transport.StartWithQuestion = true;
        drafts.Clear(); interview = Control();
        planning = Start(interview);
        await UntilAsync(() => drafts.Count > 0);
        transport.BeginAnswer();
        transport.UserTypes("msg_user_typed", "Also keep a dark theme.");
        await Task.Delay(700);
        Require(!planning.IsCompleted && transport.Prompts == 1 && transport.Aborts == 0 && !Paused(interview),
            "A message typed into the PlanMaker terminal stopped the interview or got another prompt on top.");
        transport.AnswerTypedWithQuestion("## Typed request\nKeep a dark theme.");
        await UntilAsync(() => drafts.Count > 0 && drafts[^1].Contains("Keep a dark theme", StringComparison.Ordinal));
        Require(drafts[^1].Contains("Confirmed purpose", StringComparison.Ordinal) && !planning.IsCompleted && transport.Prompts == 1,
            "The typed request's answer replaced earlier confirmed drafts or ended the interview.");
        Invoke(interview, "RequestFinish");
        plan = await planning.WaitAsync(TimeSpan.FromSeconds(10));
        Require(plan.Contains("Keep a dark theme", StringComparison.Ordinal) && plan.Contains("Confirmed purpose", StringComparison.Ordinal) &&
            transport.Aborts == 2 && transport.Prompts == 2 && Field<string>(pane, "_headlessSessionId") == "ses_exact_existing",
            "Finishing after a typed request lost its draft, submitted again, or did not restore the original session.");

        transport.Reset(); transport.InterviewMode = true;
        interview = Control();
        using var cancellation = new CancellationTokenSource();
        planning = Start(interview, cancellation.Token);
        await UntilAsync(() => transport.Prompts == 2);
        cancellation.Cancel();
        try { await planning.WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("Cancelled interview returned successfully."); }
        catch (OperationCanceledException) { }
        Require(transport.Aborts == 1 && !Paused(interview) && Field<string>(pane, "_headlessSessionId") == "ses_exact_existing",
            "Cancellation did not abort only the owned interview and restore its original session.");
        Console.WriteLine("PASS PlanMaker interview: early-stop follow-up, fresh IDs, third answer, omitted-draft retention, local-input guard, pause/resume, finish draining, pending-question finish, typed mid-interview request, cancellation; fake API only.");
    }

    private static async Task VerifyRecommendedPlanMakerAnswersAsync(TerminalPane pane, FakePlannerTransport transport)
    {
        const string exactAnswer = "  Preserve confirmed scope (Recommended)  ";
        Type controlType = typeof(TerminalPane).Assembly.GetType("TerminalNotes.Services.PlanMakerInterviewControl")!;
        object Control() => Activator.CreateInstance(controlType, nonPublic: true)!;
        void Enable(object control, bool enabled) => Invoke(control, "SetRecommendedAnswersEnabled", enabled);
        bool Enabled(object control) => (bool)controlType.GetProperty("RecommendedAnswersEnabled", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(control)!;
        Task<string> Start(object control, CancellationToken token = default) => (Task<string>)Invoke(pane,
            "CreatePlanWithQuestionsAsync", "Fixture recommendation request", (Func<bool>)(() => true), (Action<string>)(_ => { }), token,
            (Action<string>)(_ => { }), control, false)!;
        void Reset()
        {
            transport.Reset(); transport.InterviewMode = transport.StartWithQuestion = true;
            transport.RecommendationLabels = ["Choose manually", exactAnswer];
        }
        async Task Stop(object control, Task<string> task)
        {
            Invoke(control, "RequestFinish");
            Require((await task.WaitAsync(TimeSpan.FromSeconds(10))).Contains("Confirmed purpose", StringComparison.Ordinal),
                "Recommendation mode lost the confirmed draft at explicit finish.");
        }
        Reset();
        object interview = Control();
        Task<string> planning = Start(interview);
        await UntilAsync(() => transport.Prompts == 1 && transport.QuestionReads >= 2);
        await Task.Delay(700);
        Require(!Enabled(interview) && transport.Replies == 0 && !planning.IsCompleted, "Default-off planner answered a marked choice automatically.");
        void QuestionDraft(string requestId, bool hasPendingInput) => Invoke(pane, "HandleNativeWebMessage", JsonSerializer.SerializeToElement(new
        { type = "nativeQuestionDraft", requestID = requestId, hasPendingInput }));
        QuestionDraft("que_fixture_0", true);
        Require((bool)Invoke(pane, "HasLocalPendingInput")! && Field<string>(pane, "_nativeComposerDraft") == "",
            "Question input did not independently block recommendation admission.");
        Invoke(pane, "HandleNativeWebMessage", JsonSerializer.SerializeToElement(new { type = "nativeDraft", text = "independent composer" }));
        QuestionDraft("que_fixture_0", false);
        Require((bool)Invoke(pane, "HasLocalPendingInput")! && Field<string>(pane, "_nativeComposerDraft") == "independent composer",
            "Clearing a question selection discarded the independent composer guard.");
        Invoke(pane, "HandleNativeWebMessage", JsonSerializer.SerializeToElement(new { type = "nativeDraft", text = "" }));
        QuestionDraft("que_fixture_0", true);
        await (Task)Invoke(pane, "ReplyNativeQuestionAsync", JsonSerializer.SerializeToElement(new
        { requestID = "que_fixture_0", answers = new[] { new[] { "Manual exact choice" } } }))!;
        Require(transport.Replies == 1 && transport.SubmittedAnswers[0] == "Manual exact choice", "Default-off planner changed or blocked a manual answer.");
        Require(!(bool)Invoke(pane, "HasLocalPendingInput")!, "Acknowledged manual answer retained its question draft input block.");
        QuestionDraft("que_fixture_0", true);
        await (Task)Invoke(pane, "RefreshNativeStateAsync")!;
        await Task.Delay(650);
        Require((bool)Invoke(pane, "HasLocalPendingInput")!, "A question GET snapshot cleared a newer pending question edit.");
        QuestionDraft("que_fixture_0", false);
        await Stop(interview, planning);

        Reset(); interview = Control(); Enable(interview, true);
        planning = Start(interview);
        await UntilAsync(() => transport.Replies == 2);
        await Task.Delay(1200);
        Require(transport.SubmittedAnswers.SequenceEqual(new[] { exactAnswer, exactAnswer }) && transport.Prompts == 1 && !planning.IsCompleted,
            "Opt-in failed to answer consecutive unique questions with exact labels, resent a repeated ID, or completed the interview.");
        await Stop(interview, planning);
        Require(!Enabled(interview), "Finishing did not disable recommended answers.");

        foreach (string[] labels in new[] { new[] { "First", "Second" }, new[] { "A (Recommended)", "B (추천)" } })
        {
            Reset(); transport.RecommendationLabels = labels;
            interview = Control(); Enable(interview, true); planning = Start(interview);
            await UntilAsync(() => transport.QuestionReads >= 2);
            await Task.Delay(700);
            Require(transport.Replies == 0 && !planning.IsCompleted, "Missing or ambiguous recommendation did not wait for a manual answer.");
            await Stop(interview, planning);
        }

        foreach (string guard in new[] { "off", "input", "question-input", "manual", "permission", "stale-content", "other-session", "finish", "cancel", "new-user" })
        {
            Reset(); interview = Control();
            using var cancellation = new CancellationTokenSource();
            planning = Start(interview, cancellation.Token);
            await UntilAsync(() => transport.QuestionReads >= 2);
            var operationGate = Field<SemaphoreSlim>(pane, "_nativeOperationGate");
            await operationGate.WaitAsync();
            try
            {
                int reads = transport.QuestionReads;
                Enable(interview, true);
                await UntilAsync(() => transport.QuestionReads > reads);
                // The candidate was read while this fixture owns the submission gate.
                switch (guard)
                {
                    case "off": Enable(interview, false); break;
                    case "input": Set(pane, "_nativeComposerDraft", "user is typing"); break;
                    case "question-input": QuestionDraft("que_fixture_0", true); break;
                    case "manual": Set(pane, "_manualInputOverride", true); break;
                    case "permission": transport.PendingPermission = true; break;
                    case "stale-content": transport.RecommendationLabels = ["Manually changed question"]; break;
                    case "other-session": transport.QuestionSession = "ses_unrelated"; break;
                    case "finish":
                        // Finish is refused while a recommended answer is being submitted (the dialog disables it).
                        Invoke(interview, "RequestFinish");
                        Require(!(bool)interview.GetType().GetProperty("IsFinishRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(interview)!,
                            "Finish bypassed an active recommendation submission.");
                        break;
                    case "cancel": cancellation.Cancel(); break;
                    case "new-user": transport.NewerUserRequest = true; break;
                }
            }
            finally { operationGate.Release(); }
            await Task.Delay(700);
            Require(guard == "finish" ? transport.Replies >= 1 : transport.Replies == 0, guard == "finish"
                ? "A refused finish blocked the in-flight recommended answer."
                : "PlanMaker sent an answer after its " + guard + " guard changed before submission.");
            cancellation.Cancel();
            try { await planning.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
            Set(pane, "_nativeComposerDraft", ""); Set(pane, "_manualInputOverride", false);
        }

        Reset(); transport.ReplyUncertain = true;
        interview = Control(); Enable(interview, true); planning = Start(interview);
        await UntilAsync(() => transport.Replies == 1 && !Enabled(interview));
        Enable(interview, true);
        await Task.Delay(1200);
        Require(transport.Replies == 1 && !planning.IsCompleted, "An uncertain acknowledgment was automatically resent after poll or re-enabling.");
        await Stop(interview, planning);
        Console.WriteLine("PASS PlanMaker recommended answers: default-off/manual exact answer, opt-in consecutive exact labels, duplicate-ID suppression, no/ambiguous marker waits, off/input/manual/permission/stale/session/finish/cancel/new-turn guards, uncertain POST no resend; fake transport only.");
    }

    private sealed class FakePlannerTransport(string project) : HttpMessageHandler
    {
        internal const string PlanningSession = "ses_exact_planner";
        internal const string AliasProvider = "enterprise-gateway";
        internal const string AliasModel = "deployment/lantern-v7";
        private string _configuredModel = AliasProvider + "/" + AliasModel;
        internal int Prompts;
        internal volatile bool Complete;
        internal bool InterviewMode, StartWithQuestion;
        internal volatile bool CurrentQuestion, Busy, PendingPermission;
        internal int Aborts;
        internal int Replies, QuestionReads, QuestionGeneration;
        internal string[]? RecommendationLabels;
        internal string QuestionSession = PlanningSession;
        internal bool RepeatQuestion, ReplyUncertain, NewerUserRequest;
        internal int Rejects;
        internal string LastPrompt => _prompt ?? "";
        internal List<string> SubmittedAnswers = new();
        internal List<string> MessageIds = new();
        private readonly List<object> _history = new();
        private string _response = "# First detail\nConfirmed purpose.";
        private string? _answerOnStatus;
        private string? _messageId;
        private string? _prompt;
        internal void Reset()
        {
            Prompts = 0; Complete = false; _messageId = null; _prompt = null;
            InterviewMode = StartWithQuestion = CurrentQuestion = Busy = PendingPermission = false; Aborts = 0;
            _history.Clear(); MessageIds.Clear(); _response = "# First detail\nConfirmed purpose."; _answerOnStatus = null;
            _typedPending = false; _typed = 0;
            _configuredModel = AliasProvider + "/" + AliasModel;
            Replies = QuestionReads = QuestionGeneration = 0; RecommendationLabels = null;
            QuestionSession = PlanningSession; RepeatQuestion = ReplyUncertain = NewerUserRequest = false; SubmittedAnswers.Clear(); Rejects = 0;
        }
        internal void BeginAnswer() { Busy = true; CurrentQuestion = false; Complete = false; }
        internal void Answer(string draft) { _response = draft; Busy = false; CurrentQuestion = false; Complete = true; _typedPending = false; }
        internal void CompleteAnswerOnNextStatus(string draft) => _answerOnStatus = draft;
        // A message the user typed into the planner's own TUI: OpenCode stores it in the planning session and answers it later.
        private bool _typedPending;
        private int _typed;
        internal void UserTypes(string messageId, string text)
        {
            if (_messageId is not null) _history.Add(Assistant());
            _messageId = messageId; _prompt = text; _typed++; _typedPending = true;
            Busy = true; CurrentQuestion = false; Complete = false;
        }
        internal void AnswerTypedWithQuestion(string draft) { _response = draft; _typedPending = false; Busy = false; CurrentQuestion = true; Complete = false; }
        private object Assistant() => new { info = new { id = "msg_fixture_assistant_" + Prompts + "_" + _typed, sessionID = PlanningSession,
            parentID = _messageId, role = "assistant", time = new { created = 1, completed = Complete ? (long?)2 : null },
            finish = Complete ? "stop" : null },
            parts = new[] { new { type = "text", text = "<<<TERMINALNOTES_PLAN>>>\n" + _response + "\n<<<END_TERMINALNOTES_PLAN>>>" } } };
        private object Session(string id) => new
        {
            id, directory = project, title = id == PlanningSession ? "Terminal Notes · 플랜 만들기" : "Terminal Notes",
            permission = new[] { new { permission = "*", pattern = "*", action = "deny" },
                new { permission = "question", pattern = "*", action = "allow" } }
        };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string path = request.RequestUri!.AbsolutePath;
            object payload;
            HttpStatusCode status = HttpStatusCode.OK;
            if (request.Method == HttpMethod.Post && path == "/session") payload = Session(PlanningSession);
            else if (request.Method == HttpMethod.Post && path == "/session/" + PlanningSession + "/prompt_async")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Require(body.RootElement.GetProperty("agent").GetString() == "plan" &&
                    body.RootElement.GetProperty("model").GetProperty("providerID").GetString() == AliasProvider &&
                    body.RootElement.GetProperty("model").GetProperty("modelID").GetString() == AliasModel &&
                    !body.RootElement.TryGetProperty("tools", out _),
                    "Initial or follow-up request changed the exact configured alias or question-only permissions.");
                _configuredModel = "enterprise-gateway/changed-after-first-request";
                if (InterviewMode && _messageId is not null) _history.Add(Assistant());
                _messageId = body.RootElement.GetProperty("messageID").GetString();
                _prompt = body.RootElement.GetProperty("parts")[0].GetProperty("text").GetString();
                MessageIds.Add(_messageId!);
                Prompts++;
                if (InterviewMode)
                {
                    CurrentQuestion = Prompts is 2 or 3 or 6 || Prompts == 1 && StartWithQuestion;
                    Complete = !CurrentQuestion; Busy = false;
                    if (Prompts >= 4) _response = "## Confirmed scope\nKeep existing answers.";
                }
                payload = true; status = HttpStatusCode.NoContent;
            }
            else if (request.Method == HttpMethod.Post && path.EndsWith("/abort", StringComparison.Ordinal)) { Aborts++; Busy = false; payload = true; }
            else if (request.Method == HttpMethod.Post && path.StartsWith("/question/", StringComparison.Ordinal) && path.EndsWith("/reject", StringComparison.Ordinal))
            { Rejects++; CurrentQuestion = false; payload = true; }
            else if (request.Method == HttpMethod.Post && path.StartsWith("/question/", StringComparison.Ordinal) && path.EndsWith("/reply", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Require(body.RootElement.GetProperty("answers").GetArrayLength() == 1 && body.RootElement.GetProperty("answers")[0].GetArrayLength() == 1,
                    "A recommended answer changed the native single-question answer shape.");
                SubmittedAnswers.Add(body.RootElement.GetProperty("answers")[0][0].GetString()!);
                Replies++;
                if (ReplyUncertain) throw new HttpRequestException("Fixture acknowledgment was lost.");
                if (!RepeatQuestion && Replies < 2) QuestionGeneration++;
                payload = true;
            }
            else if (request.Method != HttpMethod.Get) throw new InvalidOperationException("Unexpected fake planner mutation: " + path);
            else if (path == "/experimental/tool/ids") payload = new[] { "question" };
            else if (path == "/agent") payload = new[] { new { name = "plan" } };
            else if (path == "/config") payload = new { model = _configuredModel };
            else if (path == "/provider") payload = new { all = Array.Empty<object>(), connected = Array.Empty<string>() };
            else if (path == "/session/status")
            {
                if (_answerOnStatus is { } answer) { _answerOnStatus = null; Answer(answer); }
                payload = Busy ? new Dictionary<string, object> { [PlanningSession] = new { type = "busy" } } : new Dictionary<string, object>();
            }
            else if (path == "/permission") payload = PendingPermission ? new[] { new { id = "per_fixture", sessionID = PlanningSession } } : Array.Empty<object>();
            else if (path.EndsWith("/children", StringComparison.Ordinal)) payload = Array.Empty<object>();
            else if (path == "/question")
            {
                QuestionReads++;
                payload = Prompts == 0 || (InterviewMode ? !CurrentQuestion : Complete) ? Array.Empty<object>() : new object[]
                {
                    new { id = "que_fixture" + (RecommendationLabels is null ? "" : "_" + QuestionGeneration), sessionID = QuestionSession,
                        questions = new[] { new { question = "Fixture native question", options = (RecommendationLabels ?? []).Select(label => new { label, description = "Fixture reason." }).ToArray() } } }
                };
            }
            else if (path == "/session") payload = new[] { Session("ses_exact_existing"), Session(PlanningSession) };
            else if (path == "/session/" + PlanningSession + "/message")
            {
                long created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var messages = new List<object>(_history);
                if (_messageId is not null) messages.Add(new { info = new { id = _messageId, sessionID = PlanningSession, role = "user", time = new { created } }, parts = new[] { new { type = "text", text = _prompt } } });
                if (InterviewMode && Prompts > 0 && !_typedPending) messages.Add(Assistant());
                else if (Complete) messages.Add(new { info = new { id = "msg_fixture_assistant", sessionID = PlanningSession, parentID = _messageId, role = "assistant", time = new { created, completed = created + 1 }, finish = "stop" },
                    parts = new[] { new { type = "text", text = "<<<TERMINALNOTES_PLAN>>>\n# Fixture plan\n<<<END_TERMINALNOTES_PLAN>>>" } } });
                if (NewerUserRequest) messages.Add(new { info = new { id = "msg_newer_user", sessionID = PlanningSession, role = "user", time = new { created = created + 1000 } }, parts = new[] { new { type = "text", text = "A later user request" } } });
                payload = messages;
            }
            else if (path.EndsWith("/message", StringComparison.Ordinal)) payload = Array.Empty<object>();
            else if (path.StartsWith("/session/", StringComparison.Ordinal)) payload = Session(path.Split('/')[2]);
            else throw new InvalidOperationException("Unexpected fake planner read: " + path);
            return new HttpResponseMessage(status) { RequestMessage = request,
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
        }
    }
}
