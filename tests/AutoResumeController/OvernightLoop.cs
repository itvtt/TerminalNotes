using System.Diagnostics;
using System.Text.Json;
using TerminalNotes.Services;

internal static partial class Program
{
    // This drives the real controller/client against fake native API responses.
    // Controller replacement below models a lifecycle owner's confirmed handoff;
    // it never launches a process, contacts a provider, or changes user settings.
    private static async Task OvernightLoopRecoverySimulationAsync()
    {
        DateTimeOffset verificationStartedUtc = DateTimeOffset.UtcNow;
        var elapsed = Stopwatch.StartNew();
        using var project = new Fixture("overnight-project");
        TestTimeProvider clock = project.Clock;
        DateTimeOffset simulatedStart = clock.GetUtcNow();
        const string original = "Overnight verification request: preserve this first line.\nSecond line: verify one scoped improvement at a time.\nUnique acceptance marker: OVERNIGHT-ORIGINAL-7319.";
        string? canonicalRestartPrompt = null;
        var posts = new List<PostRecord>();
        int aborts = 0, restartAttempts = 0, replacements = 0, quotaRetries = 0, improvementRequests = 0;
        Fixture? active = null;

        async Task<Fixture> BindAsync(string session, string prompt)
        {
            var fixture = new Fixture(session, projectPath: project.ProjectPath, clock: clock);
            fixture.Api.NoTodos = true;
            fixture.Api.UniqueAssistantPerRequest = true;
            fixture.Controller.SetContinuousImprovement(true, null);
            fixture.Controller.ObserveSubmittedPrompt(prompt);
            if (prompt != original) fixture.Controller.PreserveRestartInstruction(prompt, original);
            fixture.Controller.ObserveNativeSignal(UserSignal(fixture, fixture.Session, fixture.UserMessage, clock.GetUtcNow(), 10));
            await UntilAsync(() => fixture.SessionId() == fixture.Session, "binding an overnight replacement controller");
            return fixture;
        }

        void Retire(Fixture fixture)
        {
            posts.AddRange(fixture.Api.ContinuePosts);
            aborts += fixture.Api.AbortPosts.Count;
            fixture.Dispose();
        }

        async Task AdvanceUntilAsync(Fixture fixture, Func<bool> completed, TimeSpan limit, string description)
        {
            DateTimeOffset deadline = clock.GetUtcNow() + limit;
            while (!completed() && clock.GetUtcNow() < deadline)
            {
                clock.Advance(TimeSpan.FromSeconds(30));
                await fixture.Controller.TickAsync();
            }
            Require(completed(), description + " at " + clock.GetUtcNow().ToString("O") + ": " + fixture.Controller.Status);
        }

        void ConfigureReplacement(Fixture fixture, int deferrals, Action<string> acceptPrompt)
        {
            int attemptsForOwner = 0;
            DateTimeOffset? previousAttempt = null;
            fixture.Controller.RestartStalledProcessWithOutcomeAsync = async request =>
            {
                Require(request.SessionId == fixture.Session && request.CanRestart() && await request.VerifyAsync(),
                    "An overnight replacement lacked exact-session/final-native verification.");
                Require(request.OriginalInstruction == original && CountOccurrences(request.Prompt, original) == 1,
                    "An overnight replacement lost or duplicated the original multiline instruction.");
                canonicalRestartPrompt ??= request.Prompt;
                Require(request.Prompt == canonicalRestartPrompt, "Recovery guidance recursively accumulated across overnight replacements.");
                if (previousAttempt is { } previous)
                {
                    int delay = attemptsForOwner == 1 ? 1 : attemptsForOwner == 2 ? 3 : 10;
                    Require(clock.GetUtcNow() - previous >= TimeSpan.FromMinutes(delay),
                        "An overnight replacement attempt skipped its no-progress backoff.");
                }
                previousAttempt = clock.GetUtcNow();
                attemptsForOwner++;
                restartAttempts++;
                if (attemptsForOwner <= deferrals) return OpenCodeRestartOutcome.RetryLater;
                replacements++;
                acceptPrompt(request.Prompt);
                return OpenCodeRestartOutcome.Succeeded;
            };
        }

        try
        {
            for (int round = 0; round < 6; round++)
            {
                DateTimeOffset roundEnd = simulatedStart.AddHours((round + 1) * 2);
                active = await BindAsync("overnight-quota-" + round, original);
                active.Api.SetNativeFailure("APIError", 429, false);
                active.Api.ErrorResponseHeaders = new() { ["Retry-After"] = "300" };
                active.Api.SetStatus(OpenCodeRecoveryStatus.Idle);
                await active.Controller.TickAsync();
                for (int quota = 0; quota < 3; quota++)
                {
                    int before = active.Api.ContinuePosts.Count;
                    DateTimeOffset requiredWait = active.Controller.ErrorRetryAt ?? throw new InvalidOperationException("Missing overnight provider retry deadline.");
                    await AdvanceUntilAsync(active, () => active.Api.ContinuePosts.Count > before, TimeSpan.FromMinutes(12),
                        "An overnight quota retry failed to resume");
                    Require(clock.GetUtcNow() >= requiredWait && active.Api.ContinuePosts.Count == before + 1,
                        "An overnight quota retry bypassed its provider wait or duplicated the request.");
                    PostRecord sent = active.Api.ContinuePosts.Last();
                    using var body = JsonDocument.Parse(sent.Body);
                    string prompt = body.RootElement.GetProperty("parts")[0].GetProperty("text").GetString()!;
                    Require(sent.SessionId == active.Session && prompt.EndsWith(original, StringComparison.Ordinal) && CountOccurrences(prompt, original) == 1,
                        "An overnight rate retry corrupted its exact-session/original-request scope.");
                    quotaRetries++;
                    clock.Advance(TimeSpan.FromSeconds(30));
                    await active.Controller.TickAsync(); // Observe this new exact failed turn, independently of its predecessor.
                }

                string? replacementPrompt = null;
                ConfigureReplacement(active, deferrals: 6, prompt => replacementPrompt = prompt);
                active.Api.SetMessageError(false);
                active.Api.SetStatus(OpenCodeRecoveryStatus.Retry);
                active.Api.RetryNext = clock.GetUtcNow().AddMinutes(10).ToUnixTimeMilliseconds();
                DateTimeOffset nativeDeadline = clock.GetUtcNow().AddMinutes(10);
                await active.Controller.TickAsync();
                while (clock.GetUtcNow() < nativeDeadline)
                {
                    active.Api.RetryAttempt++;
                    active.Api.RetryMessage = "Structured native retry status " + active.Api.RetryAttempt;
                    clock.Advance(TimeSpan.FromSeconds(30));
                    await active.Controller.TickAsync();
                    Require(active.Api.AbortPosts.Count == 0 && replacementPrompt is null,
                        "An overnight native wait was interrupted before its deadline expired.");
                }
                await AdvanceUntilAsync(active, () => active.Api.AbortPosts.Count > 0, TimeSpan.FromMinutes(16),
                    "An expired native retry never reached abort despite unchanged activity");
                Require(clock.GetUtcNow() >= nativeDeadline.AddMinutes(AbortMinutes) && active.Api.AbortPosts.Count == 1,
                    "An overnight native retry skipped the full stall interval or repeated abort.");
                await AdvanceUntilAsync(active, () => replacementPrompt is not null, TimeSpan.FromMinutes(48),
                    "An ignored overnight abort never reached a confirmed deferred replacement");
                Require(!active.Controller.IsRecoveryPaused && active.Api.ContinuePosts.Count == 3,
                    "Safe restart deferrals paused overnight recovery or resent into a native retry.");
                int attemptsAfterSuccess = restartAttempts;
                await active.Controller.TickAsync();
                await active.Controller.TickAsync();
                Require(restartAttempts == attemptsAfterSuccess, "The same overnight owner replaced its process twice.");
                Retire(active);

                active = await BindAsync("overnight-busy-" + round, replacementPrompt!);
                replacementPrompt = null;
                ConfigureReplacement(active, deferrals: 0, prompt => replacementPrompt = prompt);
                active.Api.SetStatus(OpenCodeRecoveryStatus.Busy);
                await active.Controller.TickAsync();
                DateTimeOffset busyStart = clock.GetUtcNow();
                await AdvanceUntilAsync(active, () => replacementPrompt is not null, TimeSpan.FromMinutes(18),
                    "A later replacement hit a restart cap or failed its busy stall recovery");
                Require(clock.GetUtcNow() >= busyStart.AddMinutes(AbortMinutes + 2) && active.Api.AbortPosts.Count == 1 && active.Api.ContinuePosts.Count == 0,
                    "A busy replacement skipped abort settling or blindly continued a live request.");
                Retire(active);

                active = await BindAsync("overnight-improve-" + round, replacementPrompt!);
                active.Api.CompleteTask = true;
                await active.Controller.TickAsync();
                while (clock.GetUtcNow() < roundEnd)
                {
                    TimeSpan step = roundEnd - clock.GetUtcNow();
                    if (step > TimeSpan.FromSeconds(30)) step = TimeSpan.FromSeconds(30);
                    clock.Advance(step);
                    await active.Controller.TickAsync();
                }
                // The fake's reviews never change anything: the Loop still keeps reviewing, further apart, until its end.
                Require(active.Api.ContinuePosts.Count >= 3 && !active.Controller.Status.Contains("완료"),
                    "Recovered overnight Loop stopped instead of submitting further next-improvement requests.");
                foreach (PostRecord post in active.Api.ContinuePosts)
                {
                    using var body = JsonDocument.Parse(post.Body);
                    string prompt = body.RootElement.GetProperty("parts")[0].GetProperty("text").GetString()!;
                    Require(post.SessionId == active.Session && prompt.Contains("지속 개선", StringComparison.Ordinal) &&
                        prompt.Contains("가장 도움이 되는 개선", StringComparison.Ordinal) && prompt.Contains(JsonSerializer.Serialize(active.PlanPath!)),
                        "A recovered next-improvement request lost the saved plan or the improvement instruction.");
                }
                improvementRequests += active.Api.ContinuePosts.Count;
                if (round < 5) { Retire(active); active = null; }
            }

            Require(clock.GetUtcNow() - simulatedStart == TimeSpan.FromHours(12), "Overnight active simulation did not cover twelve hours.");
            active!.Api.CompleteTask = false;
            active.Api.SetNativeFailure("APIError", 429, false);
            active.Api.ErrorResponseHeaders = new() { ["Retry-After"] = "3600" };
            active.Api.SetStatus(OpenCodeRecoveryStatus.Retry);
            active.Api.RetryNext = clock.GetUtcNow().AddHours(1).ToUnixTimeMilliseconds();
            await active.Controller.TickAsync();
            int postsBeforeStop = active.Api.ContinuePosts.Count, abortsBeforeStop = active.Api.AbortPosts.Count, restartsBeforeStop = restartAttempts;
            active.Controller.ObserveLocalInput("\x03");
            for (int minute = 0; minute < 60; minute++)
            {
                clock.Advance(TimeSpan.FromMinutes(1));
                await active.Controller.TickAsync();
            }
            Require(active.Api.ContinuePosts.Count == postsBeforeStop && active.Api.AbortPosts.Count == abortsBeforeStop && restartAttempts == restartsBeforeStop,
                "Stop allowed a queued overnight rate retry, abort, or replacement to resume.");
            Retire(active);
            active = null;
            Require(quotaRetries == 18 && aborts == 12 && replacements == 12 && restartAttempts == 48,
                "The sustained scenario did not execute all quota, stall, deferred-restart and replacement cycles.");
            Require(posts.Count == quotaRetries + improvementRequests && posts.Select(post => post.MessageId).Distinct(StringComparer.Ordinal).Count() == posts.Count,
                "The sustained scenario duplicated a correlated submission or emitted an unexpected POST.");
            elapsed.Stop();
            Console.WriteLine($"PASS: fake API/time overnight simulation; utc_start={verificationStartedUtc:O}; utc_end={DateTimeOffset.UtcNow:O}; wall_seconds={elapsed.Elapsed.TotalSeconds:F3}; simulated_active_hours=12; simulated_stop_hours=1; rounds=6; quota_retries={quotaRetries}; native_aborts=6; busy_aborts=6; replacements={replacements}; restart_callbacks={restartAttempts}; next_improvement_posts={improvementRequests}; total_correlated_posts={posts.Count}; duplicate_message_ids=0; post_stop_actions=0.");
        }
        finally { active?.Dispose(); }
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        for (int index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length) count++;
        return count;
    }
}
