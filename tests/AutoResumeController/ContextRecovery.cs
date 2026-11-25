using System.Text.Json;
using TerminalNotes.Services;

internal static partial class Program
{
    private static readonly RecoveryModelSelection ContextWork = new("shared-local", "work-model");
    private static readonly RecoveryModelSelection ContextSummary = new("shared-local", "configured-summary-model");

    private sealed class ContextScenario : IDisposable
    {
        public Fixture Fixture { get; }
        public List<object> Rows { get; } = [];
        private int _ordinal;
        private readonly Dictionary<string, long> _userCreated = [];
        private long UserCreated(string id) => _userCreated.TryGetValue(id, out long created) ? created : (_userCreated[id] = Fixture.Clock.GetUtcNow().ToUnixTimeMilliseconds() + ++_ordinal);
        public string Marker => "context-original-" + Fixture.Session;
        public ContextScenario(string name, Func<Action<string>, CancellationToken, Task<IDisposable>>? acquireContinuationAdmission = null)
        {
            Fixture = new("context-" + name, acquireContinuationAdmission: acquireContinuationAdmission);
            Fixture.Api.NoTodos = true;
            Fixture.Api.CompactionModel = ContextSummary;
            Rows.Add(User(Fixture.UserMessage, Marker));
            Rows.Add(Assistant("overflow-original", Fixture.UserMessage, error: "ContextOverflowError"));
            Fixture.Api.MessagesOverride = () => JsonSerializer.Serialize(Rows.Concat(Fixture.Api.ContinuePosts.Select(post =>
            {
                using var body = JsonDocument.Parse(post.Body);
                string prompt = body.RootElement.GetProperty("parts")[0].GetProperty("text").GetString()!;
                return User(post.MessageId, post.MessageId, prompt);
            })));
        }

        public object User(string id, string? marker = null, string text = "do work", bool compaction = false,
            bool auto = false, bool synthetic = false, RecoveryModelSelection? model = null) => new
        {
            info = new { id, role = "user", sessionID = Fixture.Session, model = new { providerID = (model ?? ContextWork).ProviderId,
                modelID = (model ?? ContextWork).ModelId }, agent = "build", variant = "precise", time = new { created = UserCreated(id) } },
            parts = compaction ? new object[] { new { type = "compaction", auto, overflow = auto } } : new object[] {
                new { type = "text", text, synthetic, metadata = new { terminalnotes = marker is null ? null : new { requestID = marker }, compaction_continue = synthetic } } }
        };

        public object Assistant(string id, string parent, string? error = null, bool summary = false,
            string finish = "stop", string? text = "A complete useful native summary.", RecoveryModelSelection? model = null,
            Dictionary<string, string>? headers = null, int? status = null, string? responseBody = null,
            string message = "structured test error") => new
        {
            info = new { id, role = "assistant", sessionID = Fixture.Session, parentID = parent, summary,
                providerID = (model ?? (summary ? ContextSummary : ContextWork)).ProviderId,
                modelID = (model ?? (summary ? ContextSummary : ContextWork)).ModelId,
                finish = error is null ? finish : "error", time = new { created = Fixture.Clock.GetUtcNow().ToUnixTimeMilliseconds() + ++_ordinal,
                    completed = Fixture.Clock.GetUtcNow().ToUnixTimeMilliseconds() + _ordinal },
                error = error is null ? null : new { name = error, data = new { message, statusCode = status,
                    isRetryable = false, responseHeaders = headers, responseBody } } },
            parts = text is null ? Array.Empty<object>() : new object[] { new { type = "text", text } }
        };

        public string AddSummary(string? error = null, bool auto = false, string finish = "stop", string? text = "Compacted original request and plan.", RecoveryModelSelection? summaryModel = null, int? status = null)
        {
            string id = "compact-" + (_ordinal + 1);
            Rows.Add(User(id, compaction: true, auto: auto));
            Rows.Add(Assistant("summary-" + id, id, error, summary: true, finish: finish, text: text, model: summaryModel, status: status));
            return id;
        }

        public async Task BindAsync()
        {
            await Fixture.BindAsync(observeTodo: false);
            await UntilAsync(() => ((SemaphoreSlim)typeof(OpenCodeAutoResumeController).GetField("_tickGate",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(Fixture.Controller)!).CurrentCount == 1,
                "draining the binding poll before the context scenario");
            await Fixture.Controller.TickAsync();
        }
        public async Task AdvanceAsync(TimeSpan delay)
        {
            Fixture.Clock.Advance(delay);
            await Fixture.Controller.TickAsync();
        }
        public void Dispose() => Fixture.Dispose();
    }

    private static async Task ContextRecoveryScenariosAsync()
    {
        await ContextOverflowCompactsAndResumesOriginalAsync();
        await ContextOverflowInWordsCompactsAsync();
        await NativeCompactionChainsAreReconciledWithoutDispatchAsync();
        await FailedCompactionRetriesWithNoCapAsync();
        await UncertainCompactionNeverDuplicatesAsync();
        await ContextRecoveryHonorsGuardsAsync();
        await CompactFreshProviderDeadlinePreflightAsync();
        await ContextRecoveryRejectsUnprovenSummariesAsync();
        await ContextRecoveryRestoresTerminatedHostAsync();
        await ContextContinuationLostAcknowledgementReturnsToLoopAsync();
        await NativeCompactionBeforeFirstBindAsync();
        await NativeUserEventFencesFinalDispatchAsync();
        await ServiceUnavailableStreamRecoveryAsync();
        await ContextWaitingNativeStallsRemainRecoverableAsync();
        Console.WriteLine("PASS: structured context recovery; fake API/time, configured summary model, native replay markers, unlimited backoff and mutation guards.");
    }

    private static async Task ContextOverflowCompactsAndResumesOriginalAsync()
    {
        using var scenario = new ContextScenario("manual");
        var f = scenario.Fixture;
        f.Api.OnCompaction = () => scenario.AddSummary();
        await scenario.BindAsync();
        Require(f.Api.CompactionPosts == 0 && f.Api.ContinuePosts.Count == 0, "Overflow was blindly resent before compaction/backoff.");
        await scenario.AdvanceAsync(TimeSpan.FromSeconds(59));
        Require(f.Api.CompactionPosts == 0, "Context recovery skipped its first one-minute backoff.");
        await scenario.AdvanceAsync(TimeSpan.FromSeconds(1));
        Require(f.Api.CompactionPosts == 1, "Exact structured overflow did not request native compaction.");
        for (int i = 0; i < 6; i++) await scenario.AdvanceAsync(TimeSpan.FromSeconds(10));
        Require(f.Api.ContinuePosts.Count == 1, "Completed native summary did not cause exactly one correlated continuation.");
        using var body = JsonDocument.Parse(f.Api.ContinuePosts.Single().Body);
        Require(body.RootElement.GetProperty("model").GetProperty("modelID").GetString() == ContextWork.ModelId,
            "Configured summary override leaked into the resumed work model.");
        Require(body.RootElement.GetProperty("agent").GetString() == "build" && body.RootElement.GetProperty("variant").GetString() == "precise",
            "Context continuation lost the original request agent or variant.");
        Require(body.RootElement.GetProperty("parts")[0].GetProperty("metadata").GetProperty("terminalnotes").GetProperty("requestID").GetString() == f.Api.ContinuePosts.Single().MessageId,
            "Recovery continuation did not carry its own native replay correlation marker.");
        Require(body.RootElement.GetProperty("parts")[0].GetProperty("text").GetString()!.Contains("do work", StringComparison.Ordinal),
            "Compaction continuation lost the original user instruction.");
        for (int i = 0; i < 4; i++) await scenario.AdvanceAsync(TimeSpan.FromMinutes(10));
        Require(f.Api.CompactionPosts == 1 && f.Api.ContinuePosts.Count == 1, "Accepted compaction/continuation was duplicated after long polling gaps.");
    }

    // A server that says the context overflowed only in words (400, no structured code) is compacted and the work
    // continues; before, Loop took it for a problem only a person can fix and stopped for good.
    private static async Task ContextOverflowInWordsCompactsAsync()
    {
        using var scenario = new ContextScenario("overflow-words");
        var f = scenario.Fixture;
        scenario.Rows[1] = scenario.Assistant("overflow-words", f.UserMessage, error: "APIError", status: 400,
            message: "This model's maximum context length is 32768 tokens. However, you requested 40000 tokens.");
        f.Api.OnCompaction = () => scenario.AddSummary();
        await scenario.BindAsync();
        Require(!f.Controller.WaitsForPersonAfterError, "A context overflow reported in words waited for a person.");
        await scenario.AdvanceAsync(TimeSpan.FromSeconds(59));
        await scenario.AdvanceAsync(TimeSpan.FromSeconds(1));
        Require(f.Api.CompactionPosts == 1, "A context overflow reported in words was not compacted.");
        for (int i = 0; i < 6; i++) await scenario.AdvanceAsync(TimeSpan.FromSeconds(10));
        Require(f.Api.ContinuePosts.Count == 1, "Work did not continue after compacting an overflow reported in words.");
    }

    private static async Task NativeCompactionChainsAreReconciledWithoutDispatchAsync()
    {
        foreach (bool replay in new[] { false, true })
        {
            using var scenario = new ContextScenario("native-" + replay);
            var f = scenario.Fixture;
            await scenario.BindAsync();
            scenario.AddSummary(auto: true);
            string resumed = "native-resumed";
            scenario.Rows.Add(scenario.User(resumed, replay ? scenario.Marker : null, synthetic: !replay));
            scenario.Rows.Add(scenario.Assistant("native-final", resumed));
            f.Controller.ObserveNativeSignal(UserSignal(f, f.Session, resumed, f.Clock.GetUtcNow(), 50));
            await UntilAsync(() => ((SemaphoreSlim)typeof(OpenCodeAutoResumeController).GetField("_tickGate",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(f.Controller)!).CurrentCount == 1,
                "draining the native compaction event poll");
            await f.Controller.TickAsync();
            Require(f.SessionId() == f.Session, "Verified native compaction/replay was mistaken for user takeover.");
            Require(f.Api.CompactionPosts == 0 && f.Api.ContinuePosts.Count == 0, "App duplicated native automatic compaction or continuation.");
            Require(f.Controller.GetConfirmedRecoverySnapshot() is null, "Successful native work without Loop was reported as unfinished recovery.");
        }
        using var foreign = new ContextScenario("human");
        await foreign.BindAsync();
        foreign.AddSummary(auto: true);
        foreign.Rows.Add(foreign.User("human-identical-text"));
        await foreign.Fixture.Controller.TickAsync();
        await foreign.AdvanceAsync(TimeSpan.FromHours(12));
        Require(foreign.Fixture.Api.CompactionPosts == 0 && foreign.Fixture.Api.ContinuePosts.Count == 0,
            "Human text equality was accepted as proof of native replay.");
        Require(foreign.Fixture.Controller.GetConfirmedRecoverySnapshot() is { ReconcileOnly: true },
            "An ambiguous unmarked replay authorized process prompt replay.");
    }

    private static async Task FailedCompactionRetriesWithNoCapAsync()
    {
        using var scenario = new ContextScenario("repeated-overflow");
        var f = scenario.Fixture;
        f.Api.OnCompaction = () => scenario.AddSummary(error: f.Api.CompactionPosts <= 5 ? (f.Api.CompactionPosts % 2 == 0 ? "APIError" : "ContextOverflowError") : null, status: f.Api.CompactionPosts % 2 == 0 ? 503 : null);
        await scenario.BindAsync();
        for (int expected = 1; expected <= 6; expected++)
        {
            await scenario.AdvanceAsync(TimeSpan.FromMinutes(expected == 1 ? 1 : expected == 2 ? 3 : 10));
            Require(f.Api.CompactionPosts == expected, "Failed native summaries hit a retry cap or lost the pinned request.");
            await f.Controller.TickAsync();
            Require(f.Api.ContinuePosts.Count == 0, "A failed native summary authorized continuation.");
            await scenario.AdvanceAsync(TimeSpan.FromSeconds(20));
        }
        for (int i = 0; i < 6; i++) await scenario.AdvanceAsync(TimeSpan.FromSeconds(10));
        Require(f.Api.ContinuePosts.Count == 1, "Summary recovery did not resume after repeated context failures.");
    }

    private static async Task UncertainCompactionNeverDuplicatesAsync()
    {
        using var uncertain = new ContextScenario("uncertain");
        var f = uncertain.Fixture;
        f.Api.LoseNextCompactionResponse = true;
        await uncertain.BindAsync();
        await uncertain.AdvanceAsync(TimeSpan.FromMinutes(1));
        for (int i = 0; i < 12; i++) await uncertain.AdvanceAsync(TimeSpan.FromHours(1));
        Require(f.Api.CompactionPosts == 1 && f.Api.ContinuePosts.Count == 0, "Lost compaction acknowledgement caused a duplicate mutating POST.");
        Require(f.Controller.GetConfirmedRecoverySnapshot() is { ReconcileOnly: true }, "Uncertain compaction delivery authorized startup prompt replay.");
        uncertain.AddSummary();
        for (int i = 0; i < 5; i++) await uncertain.AdvanceAsync(TimeSpan.FromSeconds(10));
        Require(f.Api.CompactionPosts == 1 && f.Api.ContinuePosts.Count == 1, "Persisted exact summary did not reconcile uncertain compaction delivery.");
        using var stopped = new ContextScenario("stop");
        await stopped.BindAsync();
        stopped.Fixture.Controller.ObserveLocalInput("\x03");
        await stopped.AdvanceAsync(TimeSpan.FromHours(12));
        Require(stopped.Fixture.Api.CompactionPosts == 0 && stopped.Fixture.Api.ContinuePosts.Count == 0, "Manual Stop did not cancel context recovery.");
    }

    private static async Task ContextRecoveryHonorsGuardsAsync()
    {
        foreach (string blocker in new[] { "permission", "question", "tool", "plan", "takeover", "auth", "ordinary400", "aborted" })
        {
            using var scenario = new ContextScenario("guard-" + blocker);
            var f = scenario.Fixture;
            await scenario.BindAsync();
            if (blocker == "permission") f.Api.SetPendingInput(permission: true);
            if (blocker == "question") f.Api.SetPendingInput(question: true);
            if (blocker == "plan") File.AppendAllText(f.PlanPath!, "changed");
            if (blocker == "takeover") scenario.Rows.Add(scenario.User("new-real-user"));
            if (blocker is "auth" or "ordinary400")
            {
                scenario.Rows[1] = scenario.Assistant("new-error", f.UserMessage, error: "APIError", status: blocker == "auth" ? 401 : 400);
            }
            if (blocker == "aborted") scenario.Rows[1] = scenario.Assistant("new-error", f.UserMessage, error: "MessageAbortedError");
            if (blocker is "auth" or "aborted")
            {
                await scenario.AdvanceAsync(TimeSpan.FromSeconds(5));
                // A failed request is Loop's to start over in a new session; a person's own stop is never redone.
                Require(f.Controller.WaitsForPersonAfterError && f.Controller.FailedNeedingPerson == (blocker == "auth"),
                    "A failed request and a person's stop were not told apart (" + blocker + ").");
            }
            if (blocker == "tool")
            {
                scenario.Rows.Add(new { info = new { id = "active-tool", role = "assistant", parentID = f.UserMessage, sessionID = f.Session },
                    parts = new[] { new { type = "tool", tool = "shell", state = new { status = "running", input = new { command = "owned work" } } } } });
            }
            await scenario.AdvanceAsync(TimeSpan.FromHours(12));
            Require(f.Api.CompactionPosts == 0 && f.Api.ContinuePosts.Count == 0, "Context recovery ignored blocker " + blocker + ".");
        }
        using var deadline = new ContextScenario("deadline");
        var d = deadline.Fixture;
        deadline.Rows[1] = deadline.Assistant("rate-context", d.UserMessage, error: "APIError", status: 400,
            responseBody: "{\"error\":{\"code\":\"context_length_exceeded\"}}", headers: new() { ["Retry-After"] = "7200" });
        d.Api.OnCompaction = () => deadline.AddSummary();
        await deadline.BindAsync();
        await deadline.AdvanceAsync(TimeSpan.FromHours(1));
        Require(d.Api.CompactionPosts == 0, "Context compaction bypassed a provider Retry-After deadline after a long polling gap.");
        await deadline.AdvanceAsync(TimeSpan.FromHours(1));
        Require(d.Api.CompactionPosts == 1, "Anchored context Retry-After moved on every poll and never became eligible.");

        using var changedModel = new ContextScenario("model-race");
        await changedModel.BindAsync();
        changedModel.Fixture.Api.BeforeAgentRead = () => changedModel.Fixture.Api.CompactionModel = ContextWork;
        await changedModel.AdvanceAsync(TimeSpan.FromMinutes(1));
        Require(changedModel.Fixture.Api.CompactionPosts == 0, "A compaction model change during preflight was ignored.");

        foreach (string race in new[] { "stop", "plan", "approval" })
        {
            using var during = new ContextScenario("preflight-" + race);
            await during.BindAsync();
            during.Fixture.Api.BeforeAgentRead = () =>
            {
                if (race == "stop") during.Fixture.Controller.ObserveLocalInput("\x03");
                if (race == "plan") File.AppendAllText(during.Fixture.PlanPath!, "changed during preflight");
                if (race == "approval") during.Fixture.Api.SetPendingInput(permission: true);
            };
            try { await during.AdvanceAsync(TimeSpan.FromMinutes(1)); }
            catch (OperationCanceledException) when (race == "stop") { }
            Require(during.Fixture.Api.CompactionPosts == 0, "Compaction dispatched after preflight race " + race + ".");
        }
    }

    private static async Task CompactFreshProviderDeadlinePreflightAsync()
    {
        foreach (string timing in new[] { "direct", "initial-changed", "final-changed" })
        {
            using var scenario = new ContextScenario("fresh-provider-" + timing);
            var f = scenario.Fixture;
            await scenario.BindAsync();
            using var http = new HttpClient(f.Api, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            using var client = new OpenCodeRecoveryClient(new Uri("http://127.0.0.1:43219/"), f.ProjectPath, http);
            var before = (await client.ReadAsync(f.Session)).Evidence!;
            string originalError = string.Join("|", before.CurrentErrors.Select(error => error.RetryEvidenceId));
            void PublishDeadline() => scenario.Rows[1] = scenario.Assistant("new-overflow", f.UserMessage,
                error: "ContextOverflowError", headers: new() { ["Retry-After"] = "3600" });
            if (timing == "final-changed") f.Api.BeforeAgentRead = PublishDeadline;
            else PublishDeadline();
            if (timing == "direct") before = (await client.ReadAsync(f.Session)).Evidence!;
            var result = await client.CompactAsync(f.Session, f.UserMessage, ContextWork, ContextSummary,
                before.ActivityFingerprint, before.ProgressFingerprint, before.TodoFingerprint, () => true,
                timing == "direct" ? null : f.Clock.GetUtcNow(), f.Clock.GetUtcNow,
                expectedRetryEvidence: timing == "direct" ? null : originalError);
            Require(result.Outcome == OpenCodeRecoveryOutcome.Rejected && f.Api.CompactionPosts == 0,
                "Native compaction bypassed fresh mandatory provider/error evidence at " + timing + " preflight.");
        }
    }

    private static async Task ContextRecoveryRejectsUnprovenSummariesAsync()
    {
        foreach (string invalid in new[] { "empty", "truncated", "wrong-model" })
        {
            using var scenario = new ContextScenario("summary-" + invalid);
            var f = scenario.Fixture;
            f.Api.OnCompaction = () => scenario.AddSummary(finish: invalid == "truncated" ? "length" : "stop",
                text: invalid == "empty" ? "" : "summary", summaryModel: invalid == "wrong-model" ? ContextWork : null);
            await scenario.BindAsync();
            await scenario.AdvanceAsync(TimeSpan.FromMinutes(1));
            for (int i = 0; i < 6; i++) await scenario.AdvanceAsync(TimeSpan.FromMinutes(10));
            Require(f.Api.ContinuePosts.Count == 0, "Unverified native summary authorized continuation: " + invalid + ".");
        }
    }

    private static async Task ContextRecoveryRestoresTerminatedHostAsync()
    {
        foreach (int persistedState in new[] { 0, 1, 2 })
        {
            using var scenario = new ContextScenario("restore-" + persistedState);
            var f = scenario.Fixture;
            f.Api.LoseNextCompactionResponse = true;
            f.Api.OnCompaction = () =>
            {
                if (persistedState > 0)
                {
                    string id = "interrupted-compaction";
                    scenario.Rows.Add(scenario.User(id, compaction: true));
                    if (persistedState == 2) scenario.Rows.Add(new { info = new { id = "unfinished-summary", role = "assistant", sessionID = f.Session,
                        parentID = id, summary = true, providerID = ContextSummary.ProviderId, modelID = ContextSummary.ModelId,
                        time = new { created = f.Clock.GetUtcNow().ToUnixTimeMilliseconds() + 10 } }, parts = Array.Empty<object>() });
                }
            };
            await scenario.BindAsync();
            await scenario.AdvanceAsync(TimeSpan.FromMinutes(1));
            await f.Controller.TickAsync();
            var snapshot = f.Controller.GetConfirmedRecoverySnapshot();
            Require(snapshot is { ReconcileOnly: true, ContextRecovery: not null }, "Context transaction was not exported for exact-session-only reconnect.");
            f.Controller.Dispose();
            using var http = new HttpClient(f.Api, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            using var client = new OpenCodeRecoveryClient(new Uri("http://127.0.0.1:43219/"), f.ProjectPath, http);
            using var restored = new OpenCodeAutoResumeController(f.ProjectPath, f.PlanPath, client, () => true, f.Clock);
            restored.SetEnabled(true);
            Require(restored.RestoreContextRecovery(snapshot!, previousOwnedHostStopped: true), "Owned terminated host context restoration failed.");
            await restored.TickAsync();
            await restored.TickAsync();
            f.Clock.Advance(TimeSpan.FromSeconds(30));
            await restored.TickAsync();
            Require(f.Api.CompactionPosts == 1, "Restored transaction ignored its retained retry delay.");
            f.Api.OnCompaction = () => scenario.AddSummary();
            f.Clock.Advance(TimeSpan.FromMinutes(4));
            await restored.TickAsync();
            Require(f.Api.CompactionPosts == 2, "Confirmed terminated host compaction lost retry authorization after later polls/gap.");
            for (int i = 0; i < 6; i++) { f.Clock.Advance(TimeSpan.FromSeconds(10)); await restored.TickAsync(); }
            Require(f.Api.ContinuePosts.Count == 1, "Terminated-host context reconciliation did not resume exactly once.");
        }
    }

    private static async Task ContextContinuationLostAcknowledgementReturnsToLoopAsync()
    {
        using var scenario = new ContextScenario("lost-continue-loop");
        var f = scenario.Fixture;
        f.Api.OnCompaction = () => scenario.AddSummary();
        f.Api.LoseNextContinuationResponse = true;
        await scenario.BindAsync();
        await scenario.AdvanceAsync(TimeSpan.FromMinutes(1));
        for (int i = 0; i < 6; i++) await scenario.AdvanceAsync(TimeSpan.FromSeconds(10));
        Require(f.Api.ContinuePosts.Count == 1, "Lost context continuation acknowledgement caused a duplicate dispatch.");
        string continued = f.Api.ContinuePosts.Single().MessageId;
        scenario.Rows.Add(scenario.Assistant("continued-final", continued));
        f.Controller.SetContinuousImprovement(true, null);
        await f.Controller.TickAsync();
        for (int i = 0; i < 12 && f.Api.ContinuePosts.Count == 1; i++) await scenario.AdvanceAsync(TimeSpan.FromSeconds(10));
        Require(f.Api.ContinuePosts.Count == 2, "Reconciled context continuation failed to return to completed Loop review.");
        using var next = JsonDocument.Parse(f.Api.ContinuePosts.Last().Body);
        string text = next.RootElement.GetProperty("parts")[0].GetProperty("text").GetString()!;
        Require(text.StartsWith("Loop \uC9C0\uC18D \uAC1C\uC120:", StringComparison.Ordinal), "A stale context reservation replaced the next actual Loop improvement request: " + text[..Math.Min(text.Length, 160)] + " state=" + f.Controller.Status);
        Require(f.Api.CompactionPosts == 1, "Successful context recovery was compacted again during completed Loop review.");
    }

    private static async Task NativeCompactionBeforeFirstBindAsync()
    {
        foreach (bool replay in new[] { false, true })
        {
            using var scenario = new ContextScenario("early-native-" + replay);
            var f = scenario.Fixture;
            scenario.AddSummary(auto: true);
            scenario.Rows.Add(scenario.User("early-resumed", replay ? scenario.Marker : null, synthetic: !replay));
            scenario.Rows.Add(scenario.Assistant("early-final", "early-resumed"));
            f.Controller.SetEnabled(true);
            f.Controller.ObserveSubmittedPrompt("do work", f.Session, f.UserMessage);
            f.Controller.ConfirmApiSubmission(f.Session, f.UserMessage);
            await UntilAsync(() => f.SessionId() == f.Session, "binding a confirmed receipt after native auto recovery");
            await f.Controller.TickAsync();
            Require(f.SessionId() == f.Session && f.Api.ContinuePosts.Count == 0 && f.Api.CompactionPosts == 0,
                "Confirmed native recovery before the first monitor read lost its original receipt or duplicated work.");
            Require(f.Controller.GetConfirmedRecoverySnapshot() is null, "An early native completed final was mistaken for unfinished compaction.");
        }
    }

    private static async Task NativeUserEventFencesFinalDispatchAsync()
    {
        using var scenario = new ContextScenario("event-final-dispatch");
        var f = scenario.Fixture;
        await scenario.BindAsync();
        var snapshot = f.Controller.GetConfirmedRecoverySnapshot();
        Require(snapshot is not null, "Missing pre-event context snapshot.");
        Func<string> read = f.Api.MessagesOverride!;
        int reads = 0;
        f.Api.MessagesOverride = () =>
        {
            string json = read();
            if (++reads == 3)
            {
                f.Controller.ObserveNativeSignal(UserSignal(f, f.Session, "unverified-new-user", f.Clock.GetUtcNow(), 99));
                Require(!snapshot!.CanRestart() && f.Controller.GetConfirmedRecoverySnapshot() is null,
                    "Unverified new-user event left an old or new process restart snapshot actionable.");
            }
            return json; // The final read still contains the old native snapshot.
        };
        await scenario.AdvanceAsync(TimeSpan.FromMinutes(1));
        Require(reads >= 3 && f.Api.CompactionPosts == 0 && f.Api.ContinuePosts.Count == 0,
            "A user event after the final native snapshot failed to fence mutating recovery dispatch.");
        await scenario.AdvanceAsync(TimeSpan.FromMinutes(10));
        Require(f.Api.CompactionPosts == 0, "A polling gap cleared an unverified user-event dispatch fence.");
        f.Controller.ObserveLocalInput("\x03");
    }

    private static async Task ContextWaitingNativeStallsRemainRecoverableAsync()
    {
        foreach (bool completedAutoSummary in new[] { true, false })
        foreach (var status in new[] { OpenCodeRecoveryStatus.Busy, OpenCodeRecoveryStatus.Retry })
        {
            using var scenario = new ContextScenario("waiting-stall-" + completedAutoSummary + "-" + status);
            var f = scenario.Fixture;
            await scenario.BindAsync();
            if (completedAutoSummary) scenario.AddSummary(auto: true);
            else
            {
                f.Api.LoseNextCompactionResponse = true;
                await scenario.AdvanceAsync(TimeSpan.FromMinutes(1));
            }
            f.Api.SetStatus(status);
            if (status == OpenCodeRecoveryStatus.Retry)
                f.Api.RetryNext = f.Clock.GetUtcNow().AddMinutes(30).ToUnixTimeMilliseconds();
            int restarts = 0;
            OpenCodeErrorRestart? restartSnapshot = null;
            f.Controller.RestartStalledProcessWithOutcomeAsync = async request =>
            {
                Require(request.CanRestart() && await request.VerifyAsync(), "Waiting context stall failed exact final restart verification.");
                Require(request.ReconcileRecovery is { ReconcileOnly: true, ContextRecovery: not null } snapshot && snapshot.CanRestart() &&
                    snapshot.SessionId == f.Session && snapshot.OriginalInstruction == "do work",
                    "Context watchdog restart would blindly replay a prompt instead of restoring its exact transaction.");
                restartSnapshot = request.ReconcileRecovery;
                restarts++;
                return OpenCodeRestartOutcome.Succeeded;
            };
            await f.Controller.TickAsync();
            if (status == OpenCodeRecoveryStatus.Retry)
            {
                await AdvanceBusyAsync(f, 29);
                Require(f.Api.AbortPosts.Count == 0 && restarts == 0, "Context waiting watchdog bypassed a future native retry deadline.");
                await AdvanceBusyAsync(f, 1);
            }
            await AdvanceBusyAsync(f, AbortMinutes - 1);
            Require(f.Api.AbortPosts.Count == 0, "A context waiting state was aborted before a fresh inactivity interval.");
            await AdvanceBusyAsync(f, 1);
            Require(f.Api.AbortPosts.Count == 1, "An exact native context waiting state bypassed the existing stall watchdog forever.");
            await AdvanceBusyAsync(f, 2);
            Require(restarts == 1 && f.Api.ContinuePosts.Count == 0 && f.Api.CompactionPosts == (completedAutoSummary ? 0 : 1),
                "Context waiting recovery duplicated summarize/continuation or did not request a guarded replacement.");
            f.Controller.Dispose();
            f.Api.SetStatus(OpenCodeRecoveryStatus.Idle);
            f.Api.RetryNext = null;
            using var http = new HttpClient(f.Api, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            using var client = new OpenCodeRecoveryClient(new Uri("http://127.0.0.1:43219/"), f.ProjectPath, http);
            if (completedAutoSummary)
            {
                using var probeHttp = new HttpClient(f.Api, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
                using var probeClient = new OpenCodeRecoveryClient(new Uri("http://127.0.0.1:43219/"), f.ProjectPath, probeHttp);
                using var unproven = new OpenCodeAutoResumeController(f.ProjectPath, f.PlanPath, probeClient, () => true, f.Clock);
                unproven.SetEnabled(true);
                Require(unproven.RestoreContextRecovery(restartSnapshot!, previousOwnedHostStopped: false), "Reconciliation-only restoration failed.");
                for (int i = 0; i < 4; i++) { await unproven.TickAsync(); f.Clock.Advance(TimeSpan.FromMinutes(10)); }
                Require(f.Api.ContinuePosts.Count == 0, "Stable Idle without owned host termination proof invented a native continuation.");
            }
            using var restored = new OpenCodeAutoResumeController(f.ProjectPath, f.PlanPath, client, () => true, f.Clock);
            restored.SetEnabled(true);
            Require(restored.RestoreContextRecovery(restartSnapshot!, previousOwnedHostStopped: true), "Stall replacement lost its exact context transaction.");
            if (!completedAutoSummary) f.Api.OnCompaction = () => scenario.AddSummary();
            for (int i = 0; i < 30 && f.Api.ContinuePosts.Count == 0; i++) { await restored.TickAsync(); f.Clock.Advance(TimeSpan.FromSeconds(10)); }
            Require(f.Api.ContinuePosts.Count == 1 && f.Api.CompactionPosts == (completedAutoSummary ? 0 : 2),
                "Restored context watchdog recovery became permanently idle or duplicated the mutation.");
            for (int i = 0; i < 4; i++) { await restored.TickAsync(); f.Clock.Advance(TimeSpan.FromSeconds(10)); }
            Require(f.Api.ContinuePosts.Count == 1, "A confirmed dead-host continuation was dispatched more than once.");
            string continued = f.Api.ContinuePosts.Single().MessageId;
            scenario.Rows.Add(scenario.Assistant("after-stall-final", continued));
            restored.SetContinuousImprovement(true, null);
            for (int i = 0; i < 12 && f.Api.ContinuePosts.Count == 1; i++) { await restored.TickAsync(); f.Clock.Advance(TimeSpan.FromSeconds(10)); }
            Require(f.Api.ContinuePosts.Count == 2, "Context stall replacement did not reach the next actual Loop improvement request.");
            using var next = JsonDocument.Parse(f.Api.ContinuePosts.Last().Body);
            Require(next.RootElement.GetProperty("parts")[0].GetProperty("text").GetString()!.StartsWith("Loop \uC9C0\uC18D \uAC1C\uC120:", StringComparison.Ordinal),
                "Context stall replacement kept resuming the old summary instead of improving the original plan.");
            restored.ObserveLocalInput("\x03");
        }
        foreach (string blocker in new[] { "idle", "permission", "tool", "stop" })
        {
            using var scenario = new ContextScenario("waiting-blocked-" + blocker);
            var f = scenario.Fixture;
            await scenario.BindAsync();
            string marker = scenario.AddSummary(auto: true);
            f.Api.SetStatus(blocker == "idle" ? OpenCodeRecoveryStatus.Idle : OpenCodeRecoveryStatus.Busy);
            if (blocker == "permission") f.Api.SetPendingInput(permission: true);
            if (blocker == "tool") scenario.Rows.Add(new { info = new { id = "waiting-active-tool", role = "assistant", parentID = marker, sessionID = f.Session },
                parts = new[] { new { type = "tool", tool = "shell", state = new { status = "running", input = new { command = "owned active work" } } } } });
            if (blocker == "stop") f.Controller.ObserveLocalInput("\x03");
            int restarts = 0;
            f.Controller.RestartStalledProcessWithOutcomeAsync = request => { restarts++; return Task.FromResult(OpenCodeRestartOutcome.Succeeded); };
            await f.Controller.TickAsync();
            await AdvanceBusyAsync(f, 50);
            Require(restarts == 0 && f.Api.AbortPosts.Count == 0 && f.Api.ContinuePosts.Count == 0 && f.Api.CompactionPosts == 0,
                "An awaiting-native-auto-continuation state bypassed the " + blocker + " guard.");
        }
    }

    private static async Task ServiceUnavailableStreamRecoveryAsync()
    {
        using var f = new Fixture("partial-stream-503");
        f.Api.NoTodos = true;
        f.Api.UniqueAssistantPerRequest = true;
        await f.BindAsync(observeTodo: false);
        await UntilAsync(() => ((SemaphoreSlim)typeof(OpenCodeAutoResumeController).GetField("_tickGate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(f.Controller)!).CurrentCount == 1, "draining stream fixture binding");
        f.Api.SetAssistantText("Partial response before the local inference server interrupted the stream.");
        f.Api.SetNativeFailure("APIError", 503, false);
        await f.Controller.TickAsync();
        for (int expected = 1; expected <= 6; expected++)
        {
            await AdvanceFailureAsync(f, TimeSpan.FromMinutes(expected == 1 ? 1 : expected == 2 ? 3 : 10));
            Require(f.Api.ContinuePosts.Count == expected, "Structured overload with native isRetryable=false stalled or reached a retry cap.");
            Require(f.Api.CompactionPosts == 0, "Server overload incorrectly triggered context compaction.");
            await f.Controller.TickAsync();
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            await f.Controller.TickAsync();
        }
        f.Controller.ObserveLocalInput("\x03");
        f.Clock.Advance(TimeSpan.FromHours(12));
        await f.Controller.TickAsync();
        Require(f.Api.ContinuePosts.Count == 6, "Manual Stop did not cancel stream overload recovery.");

        foreach (var status in new[] { OpenCodeRecoveryStatus.Busy, OpenCodeRecoveryStatus.Retry })
        {
            using var stalled = new Fixture("503-stalled-" + status);
            await stalled.BindAsync();
            stalled.Api.SetAssistantText("Partial streamed response.");
            stalled.Api.SetNativeFailure("APIError", 503, false);
            stalled.Api.SetStatus(status);
            stalled.Api.ErrorResponseHeaders = new() { ["Retry-After"] = "1800" };
            int restarts = 0;
            stalled.Controller.RestartStalledProcessWithOutcomeAsync = async request =>
            {
                Require(request.CanRestart() && await request.VerifyAsync(), "Overload stall restart lost its exact final guards.");
                restarts++;
                return OpenCodeRestartOutcome.Succeeded;
            };
            await stalled.Controller.TickAsync();
            await AdvanceBusyAsync(stalled, 29);
            Require(stalled.Api.AbortPosts.Count == 0, "503 provider deadline was bypassed by the stall watchdog.");
            await AdvanceBusyAsync(stalled, 1);
            await AdvanceBusyAsync(stalled, AbortMinutes);
            Require(stalled.Api.AbortPosts.Count == 1, "Stale native " + status + " after stream503 never reached safe abort.");
            await AdvanceBusyAsync(stalled, 2);
            Require(restarts == 1 && stalled.Api.ContinuePosts.Count == 0, "Stream503 ignored-abort did not request exactly one verified process replacement.");
        }
        using var running = new Fixture("503-running-tool");
        await running.BindAsync();
        running.Api.SetNativeFailure("APIError", 503, false);
        running.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
        running.Api.SetRunningTool(true);
        await running.Controller.TickAsync();
        await AdvanceBusyAsync(running, 50);
        Require(running.Api.AbortPosts.Count == 0 && running.Api.ContinuePosts.Count == 0,
            "A partial stream503 caused active native tools to be interrupted or duplicated.");
    }
}
