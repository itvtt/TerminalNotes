using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerminalNotes.Services;

internal static partial class Program
{
    private static string RecommendedPlanHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // The work session's own review-and-improve round (the Loop prompt), not work against a newly planned Full plan.
    private static bool ReviewPrompt(PostRecord post) => PostPrompt(post).StartsWith("Loop 지속 개선", StringComparison.Ordinal);

    private static string PostPrompt(PostRecord post)
    {
        using var body = JsonDocument.Parse(post.Body);
        return body.RootElement.GetProperty("parts")[0].GetProperty("text").GetString()!;
    }

    private static async Task PrepareCompletedFullLoopAsync(Fixture fixture, DateTimeOffset? until = null)
    {
        fixture.Controller.SetContinuousImprovement(true, until, recommendPlan: true);
        fixture.Api.NoTodos = true;
        await fixture.BindAsync(observeTodo: false);
        fixture.Api.CompleteTask = true;
        await fixture.Controller.TickAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
    }

    private static async Task RecommendedPlanLoopScenariosAsync()
    {
        var legacy = JsonSerializer.Deserialize<SingleProjectImprovementPreference>("{\"Enabled\":true,\"Until\":null}")!;
        // Preferences saved before Loop 1 · Loop 2 could be chosen carry no Loop and read as Loop 2 (Loop Full).
        Require(legacy is { Enabled: true, RecommendPlan: true, Loop: null }, "Legacy Loop preferences must read as Loop 2 (Loop Full).");
        using (var unfinished = new Fixture("full-unfinished"))
        {
            int plans = 0;
            unfinished.Controller.SetContinuousImprovement(true, null, recommendPlan: true);
            unfinished.Controller.RecommendPlanAsync = (_, _) =>
            {
                plans++;
                return Task.FromResult<OpenCodeRecommendedPlanResult?>(null);
            };
            await unfinished.BindAsync();
            await unfinished.Controller.TickAsync();
            unfinished.Clock.Advance(TimeSpan.FromSeconds(20));
            await unfinished.Controller.TickAsync();
            Require(plans == 0, "Full Loop recommended a new plan before its current work completed.");
        }
        foreach (string failure in new[] { "none", "read", "preflight", "ambiguous" })
        {
            using var fixture = new Fixture("full-saved-" + failure);
            int plans = 0;
            fixture.Controller.RecommendPlanAsync = async (request, token) =>
            {
                plans++;
                Require(request.ProjectDirectory == fixture.ProjectPath && request.SessionId == fixture.Session &&
                    request.MessageId == fixture.UserMessage && request.StillOwned() && await request.VerifyAsync(token),
                    "Full planning did not pin the exact completed work and original saved plan.");
                Require(fixture.Api.ContinuePosts.Count == 0, "Full work was posted before recommended planning and saving.");
                string text = File.ReadAllText(request.PlanPath) + "\n## Verified improvement\n- Check one concrete condition.\n";
                File.WriteAllText(request.PlanPath, text);
                if (failure == "read") fixture.Api.StatusFailuresRemaining = 1;
                if (failure == "preflight") fixture.Api.FailNextMessageLookup = true;
                if (failure == "ambiguous") fixture.Api.LoseNextContinuationResponse = true;
                return new OpenCodeRecommendedPlanResult(RecommendedPlanHash(text), true);
            };
            await PrepareCompletedFullLoopAsync(fixture);
            await fixture.Controller.TickAsync();
            Require(plans == 1, "Full Loop skipped its first recommended planning cycle.");
            if (failure is "read" or "preflight")
            {
                Require(fixture.Api.ContinuePosts.Count == 0, "An unavailable read or failed preflight still posted work.");
                fixture.Clock.Advance(TimeSpan.FromMinutes(1));
                await fixture.Controller.TickAsync();
            }
            Require(plans == 1 && fixture.Api.ContinuePosts.Count == 1,
                "A saved Full plan was replanned after a read/preflight fault or failed to resume work.");
            using var body = JsonDocument.Parse(fixture.Api.ContinuePosts.Single().Body);
            string prompt = body.RootElement.GetProperty("parts")[0].GetProperty("text").GetString()!;
            Require(prompt.Contains(JsonSerializer.Serialize(fixture.PlanPath!)) && !prompt.Contains("Loop 지속 개선"),
                "Full Loop posted the old review-and-implement prompt instead of work against its saved plan.");
            // Keep the accepted/lost response uncompleted so a new legitimate cycle cannot begin.
            fixture.Api.CompleteTask = false;
            for (int index = 0; index < 3; index++)
            {
                fixture.Clock.Advance(TimeSpan.FromMinutes(1));
                await fixture.Controller.TickAsync();
            }
            Require(plans == 1 && fixture.Api.ContinuePosts.Count == 1,
                "The same Full work delivery was replayed after an accepted or ambiguous POST.");
        }

        using (var unchanged = new Fixture("full-unchanged"))
        {
            int plans = 0;
            unchanged.Controller.RecommendPlanAsync = (request, _) =>
            {
                plans++;
                return Task.FromResult<OpenCodeRecommendedPlanResult?>(new(request.PlanHash, false));
            };
            await PrepareCompletedFullLoopAsync(unchanged);
            await unchanged.Controller.TickAsync();
            // Nothing new to plan never ends the Loop: this round reviews and improves the result in the work session.
            Require(plans == 1 && unchanged.Api.ContinuePosts.Count == 1 && ReviewPrompt(unchanged.Api.ContinuePosts.First()) &&
                !unchanged.Controller.Status.Contains("완료"), "An unchanged Full plan stopped the Loop instead of reviewing the result.");
            for (int tick = 0; tick < 20; tick++) { unchanged.Clock.Advance(TimeSpan.FromMinutes(1)); await unchanged.Controller.TickAsync(); }
            Require(plans >= 2 && unchanged.Api.ContinuePosts.Count >= 2,
                $"An unchanged Full Loop did not plan and review again (plans={plans}, posts={unchanged.Api.ContinuePosts.Count}).");
        }

        foreach (string failure in new[] { "null", "throw", "bad-hash" })
        {
            using var fixture = new Fixture("full-failed-" + failure);
            int plans = 0;
            fixture.Controller.RecommendPlanAsync = (_, _) =>
            {
                plans++;
                if (failure == "throw") throw new InvalidOperationException("fixture planning failure");
                return Task.FromResult<OpenCodeRecommendedPlanResult?>(failure == "null" ? null : new("not-the-saved-hash", true));
            };
            await PrepareCompletedFullLoopAsync(fixture);
            await fixture.Controller.TickAsync();
            Require(plans == 1 && fixture.Api.ContinuePosts.Count == 0,
                "Failed or mismatched Full planning allowed work without a saved plan.");
            // The app checks every 10 seconds; one long jump would read as a polling gap.
            for (int tick = 0; tick < 9; tick++) { fixture.Clock.Advance(TimeSpan.FromSeconds(20)); await fixture.Controller.TickAsync(); }
            if (failure == "bad-hash")
                // A result whose saved plan does not match is never adopted; the app's Loop watchdog reruns the task.
                Require(plans == 1 && fixture.Api.ContinuePosts.Count == 0, "A mismatched Full plan was adopted or replanned.");
            else
                // A failed planner plans once more after a minute, then keeps the Loop going with a result review.
                Require(plans >= 2 && fixture.Api.ContinuePosts.Count >= 1 && fixture.Api.ContinuePosts.All(ReviewPrompt),
                    $"Failed Full planning paused the Loop instead of retrying and then reviewing (plans={plans}, posts={fixture.Api.ContinuePosts.Count}).");
        }

        foreach (string cancellation in new[] { "stop", "input", "mode", "deadline" })
        {
            using var fixture = new Fixture("full-cancel-" + cancellation);
            var entered = new TaskCompletionSource<OpenCodeRecommendedPlanRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<OpenCodeRecommendedPlanResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken observed = default;
            fixture.Controller.RecommendPlanAsync = (request, token) =>
            {
                observed = token;
                entered.TrySetResult(request);
                return release.Task; // Deliberately ignore cancellation to test the controller's await fence.
            };
            await PrepareCompletedFullLoopAsync(fixture, cancellation == "deadline" ? fixture.Clock.GetUtcNow().AddSeconds(21) : null);
            Task pending = fixture.Controller.TickAsync();
            OpenCodeRecommendedPlanRequest request = await entered.Task.WaitAsync(WaitLimit);
            if (cancellation == "stop") fixture.Controller.Suspend("Stopped");
            else if (cancellation == "input") fixture.Controller.ObserveLocalInput("new user text");
            else if (cancellation == "mode") fixture.Controller.SetContinuousImprovement(true, null, recommendPlan: false);
            else fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            await pending.WaitAsync(WaitLimit);
            Require(observed.IsCancellationRequested && !request.StillOwned() && !await request.VerifyAsync(CancellationToken.None),
                "Full planning retained ownership or ignored " + cancellation + ".");
            release.TrySetResult(new(request.PlanHash, false));
            Require(fixture.Api.ContinuePosts.Count == 0, "Cancelled Full planning posted work.");
        }

        foreach (string blocker in new[] { "permission", "question", "user", "external-plan" })
        {
            using var fixture = new Fixture("full-blocker-" + blocker);
            fixture.Controller.RecommendPlanAsync = async (request, token) =>
            {
                if (blocker == "permission") fixture.Api.SetPendingInput(permission: true);
                else if (blocker == "question") fixture.Api.SetPendingInput(question: true);
                else if (blocker == "user") fixture.Api.AddUserMessage("new-native-user", fixture.Clock.GetUtcNow());
                else File.AppendAllText(request.PlanPath, "\nExternal user edit.\n");
                Require(!await request.VerifyAsync(token), "A changed native blocker or external plan still passed Full save verification.");
                return null;
            };
            await PrepareCompletedFullLoopAsync(fixture);
            await fixture.Controller.TickAsync();
            Require(fixture.Api.ContinuePosts.Count == 0, "Full planning bypassed a pending input, new request, or external edit.");
        }
    }
}
