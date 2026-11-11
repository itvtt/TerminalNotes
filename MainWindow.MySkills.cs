using System.IO;
using System.Net.Http;
using System.Text.Json;
using TerminalNotes.Services;

namespace TerminalNotes;

// 나만의 스킬 learned from finished work (see MySkillLearning): a terminal whose agent finishes a piece of work is read back once
// (at most every 10 minutes a session, one at a time), and a procedure worth keeping becomes a skill. The library is tidied at start.
public partial class MainWindow
{
    private static readonly TimeSpan LearnGap = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, DateTimeOffset> _learnedAt = new(StringComparer.Ordinal);
    private Task? _learning;

    /// <summary>Replaces learning (tests): called with the terminal that finished.</summary>
    internal Action<TerminalPane>? LearnOverride { get; set; }

    private void StartMySkills() => _ = Task.Run(() =>
    {
        try { MySkills.Curate(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            System.Diagnostics.Trace.WriteLine("My skills curation failed: " + exception.GetType().Name);
        }
    });

    /// <summary>Asks a model one tool-less question (OpenCode's side session, or Claude Code's print mode).</summary>
    internal delegate Task<string> AskModel(string title, string prompt, CancellationToken token);

    internal static AskModel OpenCodeAsk(OpenCodeConversationClient client, string? model) =>
        (title, prompt, token) => client.CompleteToolLessPromptAsync(title, prompt, model, token, TimeSpan.FromMinutes(6), TimeSpan.FromMinutes(2));

    private void LearnFrom(TerminalPane pane)
    {
        if (LearnOverride is { } learn) { learn(pane); return; }
        var settings = MySkills.ReadSettings();
        if (_learning is { IsCompleted: false } || !settings.AutoLearn || MySkillLearning.Resting(settings, DateTimeOffset.UtcNow) is not null) return;
        // OpenCode: its session through its server. Claude Code: its conversation record, and its print mode (the cheapest model).
        bool claude = pane.IsTypedAgent && AgentCli.FromCommand(pane.StartupCommand) == TerminalTool.ClaudeCode;
        string key = claude ? "claude:" + pane.WorkingDirectory : pane.LearningSource?.SessionId ?? "";
        if (!claude && pane.LearningSource is null || key.Length == 0) return;
        var now = DateTimeOffset.UtcNow;
        if (_learnedAt.TryGetValue(key, out var last) && now - last < LearnGap) return;
        _learnedAt[key] = now;
        string project = pane.WorkingDirectory, projectName = ProjectDisplayName(project);
        var source = pane.LearningSource;
        _learning = Task.Run(async () =>
        {
            try
            {
                var learned = claude
                    ? await LearnFromClaudeAsync(project, projectName, CancellationToken.None)
                    : await LearnAsync(source!.Value.Client, source.Value.SessionId, project, projectName, CancellationToken.None);
                if (learned is { } skill)
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (_closing || !_taskNotificationPreferences.CanNotify(DateTimeOffset.UtcNow)) return;
                        _tray?.ShowTaskNotification("나만의 스킬", (skill.Version > 1 ? "고침 · " : "새로 배움 · ") + skill.Title, false, () => !_closing);
                    });
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Learning never gets in the way of the work.
                System.Diagnostics.Trace.WriteLine("Skill learning failed: " + exception.GetType().Name + " " + exception.Message);
            }
        });
    }

    /// <summary>Reads what the OpenCode session did since it was last read; with enough steps asks the session's model for a skill.</summary>
    internal static async Task<MySkills.Skill?> LearnAsync(OpenCodeConversationClient client, string sessionId, string project, string projectName,
        CancellationToken token)
    {
        var digest = MySkillLearning.Read(await client.ReadMessagesAsync(sessionId, token), MySkills.ReadSettings().LearnedUpTo.GetValueOrDefault(sessionId));
        if (!Note(sessionId, digest)) return null;
        string? model = await client.ReadPlanMakerModelAsync(sessionId, await client.ReadAgentsAsync(token), token);
        return await LearnFromDigestAsync(OpenCodeAsk(client, model), digest, project, projectName, token);
    }

    /// <summary>The same from a Claude Code task: its latest conversation record in the task folder, and Claude Code's cheapest model.</summary>
    internal static async Task<MySkills.Skill?> LearnFromClaudeAsync(string project, string projectName, CancellationToken token, AskModel? ask = null)
    {
        if (ClaudeCodeSessions.List(project).OrderByDescending(session => session.Updated).FirstOrDefault() is not { } session) return null;
        var digest = MySkillLearning.ReadClaude(File.ReadLines(session.Path), MySkills.ReadSettings().LearnedUpTo.GetValueOrDefault(session.Id));
        if (!Note(session.Id, digest)) return null;
        return await LearnFromDigestAsync(ask ?? ClaudeCodeModel.Ask, digest, project, projectName, token);
    }

    // Counts the skills used and remembers how far the session was read; whether there is enough to ask about (and learning may ask).
    private static bool Note(string sessionId, MySkillLearning.Digest digest)
    {
        if (digest.UsedSkills.Count > 0) MySkills.RecordUses(digest.UsedSkills);
        if (digest.LastMessageId is { } lastMessage)
            MySkills.UpdateSettings(current => current with { LearnedUpTo = new(current.LearnedUpTo) { [sessionId] = lastMessage } });
        return digest.ToolCalls >= MySkillLearning.MinimumToolCalls && MySkillLearning.Resting(MySkills.ReadSettings(), DateTimeOffset.UtcNow) is null;
    }

    /// <summary>The model's usage limit was reached: learning rests until it comes back (or <see cref="MySkillLearning.LimitRest"/>).</summary>
    internal static void RestAtLimit(ModelUsageLimitException limit) =>
        MySkills.UpdateSettings(current => current with { PausedUntil = limit.RetryAt is { } at && at > DateTimeOffset.UtcNow ? at : DateTimeOffset.UtcNow + MySkillLearning.LimitRest });

    internal static async Task<MySkills.Skill?> LearnFromDigestAsync(AskModel ask, MySkillLearning.Digest digest, string project, string projectName, CancellationToken token)
    {
        try
        {
            MySkills.UpdateSettings(current => MySkillLearning.Count(current, DateTimeOffset.UtcNow));
            string answer = await ask("Terminal Notes · 스킬 배우기", MySkillLearning.LearnPrompt(digest, MySkills.List(), projectName), token);
            if (MySkillLearning.ParseLesson(answer) is not { } lesson) return null;
            MySkills.Skill saved;
            if (lesson.Update is { } update && MySkills.Get(update) is { } existing)
            {
                // Same subject as a skill already there: one new version holding both.
                if (await MergeAsync(ask, [(existing.Title, existing.Description, existing.Body), (lesson.Title, lesson.Description, lesson.Body)], token)
                    is not { } combined) return null;
                saved = MySkills.Save(existing.Name, combined.Title, combined.Description, combined.Body);
            }
            else saved = MySkills.Save(MySkills.NameFor(lesson.Name, lesson.Title), lesson.Title, lesson.Description, lesson.Body, learned: true, project: project);
            MySkills.UpdateSettings(current => current with { LearnedSinceReview = current.LearnedSinceReview + 1 });
            MySkills.Curate();
            // The skill is learned whatever the review does: its failure must not hide that (the notification).
            if (MySkills.ReviewDue())
                try { await ReviewMySkillsAsync(ask, token); }
                catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException or JsonException)
                {
                    System.Diagnostics.Trace.WriteLine("Skill review failed: " + exception.Message);
                }
            return saved;
        }
        catch (ModelUsageLimitException limit)
        {
            RestAtLimit(limit);
            return null;
        }
    }

    internal sealed record Review(string Answer, int Groups, IReadOnlyList<string> Dropped);

    internal static Task<Review> ReviewMySkillsAsync(OpenCodeConversationClient client, string? model, CancellationToken token) =>
        ReviewMySkillsAsync(OpenCodeAsk(client, model), token);

    /// <summary>The curator: which skills say the same, each group with its merged text, kept as proposals for the user. Returns what
    /// it did (for the diagnostics and tests).</summary>
    internal static async Task<Review> ReviewMySkillsAsync(AskModel ask, CancellationToken token)
    {
        var skills = MySkills.List();
        string answer;
        try { answer = await ask("Terminal Notes · 스킬 정리", MySkillLearning.ReviewPrompt(skills), token); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException)
        {
            // Not reviewed (a slow or busy model, measured 2026-11-12): tried again after the next learned skill. At the usage
            // limit learning rests too.
            if (exception is ModelUsageLimitException limit) RestAtLimit(limit);
            return new Review("", 0, ["검토하지 못함 · " + exception.Message]);
        }
        var proposals = new List<MySkills.MergeProposal>();
        var dropped = new List<string>();
        var groups = MySkillLearning.ParseReview(answer, skills.Select(skill => skill.Name).ToHashSet(StringComparer.Ordinal));
        foreach (var (names, reason) in groups)
        {
            var members = names.Select(MySkills.Get).OfType<MySkills.Skill>().ToList();
            if (members.Count < 2) continue;
            var parts = members.Select(member => (member.Title, member.Description, member.Body)).ToList();
            var problems = new List<string>();
            var lesson = await MergeAsync(ask, parts, token, problems);
            if (lesson is null) { dropped.Add(string.Join("+", names) + ": " + string.Join(" / ", problems)); continue; }
            proposals.Add(new MySkills.MergeProposal(members.Select(member => member.Name).ToArray(), lesson.Title, lesson.Description, lesson.Body, reason));
        }
        MySkills.UpdateSettings(current => current with
        {
            LastReview = DateTimeOffset.UtcNow, LearnedSinceReview = 0,
            Proposals = current.Proposals.Where(old => !proposals.Any(proposal => proposal.Names.Intersect(old.Names).Any())).Concat(proposals).TakeLast(5).ToList()
        });
        return new Review(answer, groups.Count, dropped);
    }

    /// <summary>One skill from several: asked again, shorter, when the first answer is over the length or out of the format (measured
    /// 2026-11-12: three related skills, one of them long, came back over the limit and the proposal was lost).</summary>
    internal static async Task<MySkillLearning.Lesson?> MergeAsync(AskModel ask, IReadOnlyList<(string Title, string Description, string Body)> parts,
        CancellationToken token, List<string>? problems = null)
    {
        foreach (int limit in new[] { 6000, 3500 })
        {
            string merged;
            try { merged = await ask("Terminal Notes · 스킬 합치기", MySkillLearning.MergePrompt(parts, limit), token); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException)
            {
                // A slow or failed answer loses this merge only, not the rest of the review.
                if (exception is ModelUsageLimitException usage) RestAtLimit(usage);
                problems?.Add(exception.Message);
                return null;
            }
            var lesson = MySkillLearning.ParseLesson(merged);
            if (lesson is not null && lesson.Body.Length <= MySkills.MaxBodyChars) return lesson;
            problems?.Add(lesson is null
                ? $"형식 아님 ({merged.Length}자: {merged.Trim()[..Math.Min(160, merged.Trim().Length)].ReplaceLineEndings(" ")})"
                : $"너무 김 ({lesson.Body.Length}자)");
        }
        return null;
    }
}
