using System.IO;
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

    private void LearnFrom(TerminalPane pane)
    {
        if (LearnOverride is { } learn) { learn(pane); return; }
        if (_learning is { IsCompleted: false } || pane.LearningSource is not { } source || !MySkills.ReadSettings().AutoLearn) return;
        var now = DateTimeOffset.UtcNow;
        if (_learnedAt.TryGetValue(source.SessionId, out var last) && now - last < LearnGap) return;
        _learnedAt[source.SessionId] = now;
        string project = pane.WorkingDirectory, projectName = ProjectDisplayName(project);
        _learning = Task.Run(async () =>
        {
            try
            {
                var learned = await LearnAsync(source.Client, source.SessionId, project, projectName, CancellationToken.None);
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

    /// <summary>Reads what the session did since it was last read; with enough steps asks the session's model for a skill.</summary>
    internal static async Task<MySkills.Skill?> LearnAsync(OpenCodeConversationClient client, string sessionId, string project, string projectName,
        CancellationToken token)
    {
        var settings = MySkills.ReadSettings();
        var digest = MySkillLearning.Read(await client.ReadMessagesAsync(sessionId, token), settings.LearnedUpTo.GetValueOrDefault(sessionId));
        if (digest.UsedSkills.Count > 0) MySkills.RecordUses(digest.UsedSkills);
        if (digest.LastMessageId is { } lastMessage)
            MySkills.UpdateSettings(current => current with { LearnedUpTo = new(current.LearnedUpTo) { [sessionId] = lastMessage } });
        if (digest.ToolCalls < MySkillLearning.MinimumToolCalls) return null;
        string? model = await client.ReadPlanMakerModelAsync(sessionId, await client.ReadAgentsAsync(token), token);
        string answer = await client.CompleteToolLessPromptAsync("Terminal Notes · 스킬 배우기",
            MySkillLearning.LearnPrompt(digest, MySkills.List(), projectName), model, token, TimeSpan.FromMinutes(6), TimeSpan.FromMinutes(2));
        if (MySkillLearning.ParseLesson(answer) is not { } lesson) return null;
        MySkills.Skill saved;
        if (lesson.Update is { } update && MySkills.Get(update) is { } existing)
        {
            // Same subject as a skill already there: one new version holding both.
            string merged = await client.CompleteToolLessPromptAsync("Terminal Notes · 스킬 합치기",
                MySkillLearning.MergePrompt([(existing.Title, existing.Description, existing.Body), (lesson.Title, lesson.Description, lesson.Body)]),
                model, token, TimeSpan.FromMinutes(6), TimeSpan.FromMinutes(2));
            if (MySkillLearning.ParseLesson(merged) is not { } combined) return null;
            saved = MySkills.Save(existing.Name, combined.Title, combined.Description, combined.Body);
        }
        else saved = MySkills.Save(MySkills.NameFor(lesson.Name, lesson.Title), lesson.Title, lesson.Description, lesson.Body, learned: true, project: project);
        MySkills.UpdateSettings(current => current with { LearnedSinceReview = current.LearnedSinceReview + 1 });
        MySkills.Curate();
        if (MySkills.ReviewDue()) await ReviewMySkillsAsync(client, model, token);
        return saved;
    }

    /// <summary>The curator: which skills say the same, each group with its merged text, kept as proposals for the user.</summary>
    internal static async Task ReviewMySkillsAsync(OpenCodeConversationClient client, string? model, CancellationToken token)
    {
        var skills = MySkills.List();
        string answer = await client.CompleteToolLessPromptAsync("Terminal Notes · 스킬 정리", MySkillLearning.ReviewPrompt(skills), model, token,
            TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(2));
        var proposals = new List<MySkills.MergeProposal>();
        foreach (var (names, reason) in MySkillLearning.ParseReview(answer, skills.Select(skill => skill.Name).ToHashSet(StringComparer.Ordinal)))
        {
            var members = names.Select(MySkills.Get).OfType<MySkills.Skill>().ToList();
            if (members.Count < 2) continue;
            string merged = await client.CompleteToolLessPromptAsync("Terminal Notes · 스킬 합치기",
                MySkillLearning.MergePrompt(members.Select(member => (member.Title, member.Description, member.Body))), model, token,
                TimeSpan.FromMinutes(6), TimeSpan.FromMinutes(2));
            if (MySkillLearning.ParseLesson(merged) is { } lesson && lesson.Body.Length <= MySkills.MaxBodyChars)
                proposals.Add(new MySkills.MergeProposal(members.Select(member => member.Name).ToArray(), lesson.Title, lesson.Description, lesson.Body, reason));
        }
        MySkills.UpdateSettings(current => current with
        {
            LastReview = DateTimeOffset.UtcNow, LearnedSinceReview = 0,
            Proposals = current.Proposals.Where(old => !proposals.Any(proposal => proposal.Names.Intersect(old.Names).Any())).Concat(proposals).TakeLast(5).ToList()
        });
    }
}
