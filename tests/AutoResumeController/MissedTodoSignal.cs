using System.Text.Json;
using TerminalNotes.Services;

internal static partial class Program
{
    private static async Task MissedTodoSignalScenariosAsync()
    {
        foreach (string variant in new[] { "exact", "exact-no-loop", "none", "prior-user", "foreign-session", "missing-session", "summary", "mismatch", "running", "error", "before-user", "later-conflict" })
        {
            using var fixture = new Fixture("missed-todo-" + variant);
            await fixture.BindAsync(observeTodo: false);
            if (variant != "exact-no-loop") fixture.Controller.SetContinuousImprovement(true, null);
            fixture.Api.CompleteTask = true;
            fixture.Api.PendingTodoOverride = true;
            DateTimeOffset created = fixture.Clock.GetUtcNow();
            fixture.Api.MessagesOverride = () => TodoWriteMessages(fixture, created, variant);
            await fixture.Controller.TickAsync();
            fixture.Clock.Advance(TimeSpan.FromSeconds(20));
            await fixture.Controller.TickAsync();
            var current = (RecoverySessionEvidence?)typeof(OpenCodeAutoResumeController).GetField("_lastVerifiedEvidence", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(fixture.Controller);
            Require(fixture.Api.ContinuePosts.Count == (variant.StartsWith("exact", StringComparison.Ordinal) ? 1 : 0),
                "Missed todo SSE provenance failed for " + variant + "; posts=" + fixture.Api.ContinuePosts.Count + "; proof=" + current?.HasCurrentUserTodoWrite + "; final=" + current?.HasSuccessfulFinalResponse + "; status=" + fixture.Controller.Status + ".");
            Require(fixture.Api.AbortPosts.Count == 0, "Missed todo SSE recovery aborted active work.");
        }

        using var finalRace = new Fixture("missed-todo-final-parent-race");
        await finalRace.BindAsync(observeTodo: false);
        finalRace.Api.CompleteTask = finalRace.Api.PendingTodoOverride = true;
        DateTimeOffset raceCreated = finalRace.Clock.GetUtcNow();
        string raceVariant = "exact";
        finalRace.Api.MessagesOverride = () => TodoWriteMessages(finalRace, raceCreated, raceVariant);
        await finalRace.Controller.TickAsync();
        finalRace.Api.BeforeMessageLookup = () => raceVariant = "prior-user";
        finalRace.Clock.Advance(TimeSpan.FromSeconds(20));
        await finalRace.Controller.TickAsync();
        Require(finalRace.Api.ContinuePosts.Count == 0 && !finalRace.Controller.IsRecoveryPaused,
            "A todowrite reparented during final native preflight was sent or permanently paused.");

        var admission = new HeldAdmission();
        using var changed = new Fixture("missed-todo-changed-in-queue", acquireContinuationAdmission: admission.AcquireAsync);
        await changed.BindAsync(observeTodo: false);
        changed.Controller.SetContinuousImprovement(true, null);
        changed.Api.CompleteTask = changed.Api.PendingTodoOverride = true;
        DateTimeOffset original = changed.Clock.GetUtcNow();
        changed.Api.MessagesOverride = () => TodoWriteMessages(changed, original, "exact");
        await changed.Controller.TickAsync();
        changed.Clock.Advance(TimeSpan.FromSeconds(20));
        Task pending = changed.Controller.TickAsync();
        await admission.Entered.Task.WaitAsync(WaitLimit);
        changed.Api.TodoStatusOverride = "in_progress";
        admission.Release();
        await pending.WaitAsync(WaitLimit);
        await changed.Controller.TickAsync();
        changed.Clock.Advance(TimeSpan.FromSeconds(20));
        await changed.Controller.TickAsync();
        Require(changed.Api.ContinuePosts.Count == 0, "A previous todowrite proof authorized a different native todo snapshot.");
    }

    private static string TodoWriteMessages(Fixture fixture, DateTimeOffset created, string variant)
    {
        long at = created.ToUnixTimeMilliseconds();
        var rows = new List<object>
        {
            new { info = new { id = fixture.UserMessage, sessionID = fixture.Session, role = "user", time = new { created = at } }, parts = Array.Empty<object>() }
        };
        if (variant != "none")
            rows.Add(new
            {
                info = new { id = "current-todowrite", sessionID = variant == "missing-session" ? null : variant == "foreign-session" ? "another-session" : fixture.Session,
                    role = "assistant", parentID = variant == "prior-user" ? "older-user" : fixture.UserMessage, summary = variant == "summary",
                    finish = "tool-calls", time = new { created = variant == "before-user" ? at - 1 : at + 1 } },
                parts = new[] { new { type = "tool", tool = "todowrite", state = new { status = variant is "running" or "error" ? variant : "completed",
                    input = new { todos = new[] { new { content = variant == "mismatch" ? "Foreign task" : "Implement requested work", status = "pending", priority = "high" } } } } } }
            });
        if (variant == "later-conflict")
            rows.Add(new
            {
                info = new { id = "later-todowrite", sessionID = fixture.Session, role = "assistant", parentID = fixture.UserMessage,
                    finish = "tool-calls", time = new { created = at + 2 } },
                parts = new[] { new { type = "tool", tool = "todowrite", state = new { status = "completed",
                    input = new { todos = new[] { new { content = "Later conflicting task", status = "pending", priority = "high" } } } } } }
            });
        rows.Add(new
        {
            info = new { id = "current-final", sessionID = fixture.Session, role = "assistant", parentID = fixture.UserMessage,
                finish = "stop", time = new { created = at + 3, completed = at + 4 } },
            parts = new[] { new { type = "text", text = "One unit is verified; the recorded next task remains." } }
        });
        return JsonSerializer.Serialize(rows);
    }
}
