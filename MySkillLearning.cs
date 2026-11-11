using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerminalNotes;

/// <summary>Learning 나만의 스킬 from finished work, like Hermes Agent's closed loop: when a terminal's agent finishes a piece of work
/// that took several steps, its session is read back and the same model is asked (in a side session without tools) whether it holds
/// a procedure worth keeping; if so it becomes a skill (or a new version of the one on the same subject). Now and then the curator
/// asks which skills say the same and proposes merging them. Prompts, the session digest and the answer formats live here.</summary>
internal static class MySkillLearning
{
    /// <summary>Work with fewer tool calls than this is not learned from (a question and answer, a one-step change).</summary>
    internal const int MinimumToolCalls = 5;
    internal const int DigestLimit = 24_000;
    internal const string None = "<<<TERMINALNOTES_SKILL_NONE>>>", Start = "<<<TERMINALNOTES_SKILL>>>", End = "<<<END_TERMINALNOTES_SKILL>>>";
    internal const string MergeNone = "<<<TERMINALNOTES_MERGE_NONE>>>", MergeStart = "<<<TERMINALNOTES_MERGE>>>", MergeEnd = "<<<END_TERMINALNOTES_MERGE>>>";

    internal sealed record Digest(string Text, int ToolCalls, IReadOnlyList<string> UsedSkills, string? LastMessageId);

    internal sealed record Lesson(string Name, string? Update, string Title, string Description, string Body);

    /// <summary>What happened in a session after <paramref name="after"/> (its whole history when that message is not there), as text:
    /// what the user asked, what the agent said, and its tool calls with failures. The newest part is kept when it is too long.</summary>
    internal static Digest Read(JsonElement messages, string? after)
    {
        if (messages.ValueKind != JsonValueKind.Array) return new Digest("", 0, [], null);
        var items = messages.EnumerateArray().ToList();
        int from = 0;
        if (after is not null)
        {
            int index = items.FindIndex(item => Id(item) == after);
            if (index >= 0) from = index + 1;
        }
        var lines = new List<string>();
        var used = new List<string>();
        int tools = 0;
        string? firstAsk = null;
        foreach (var item in items.Skip(from))
        {
            string role = item.TryGetProperty("info", out var info) ? Text(info, "role") ?? "" : "";
            if (!item.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array) continue;
            foreach (var part in parts.EnumerateArray())
            {
                switch (Text(part, "type"))
                {
                    case "text" when Text(part, "text") is { Length: > 0 } text && !IsTrue(part, "synthetic"):
                        if (role == "user")
                        {
                            firstAsk ??= Cut(text, 1500);
                            lines.Add("사용자: " + Cut(text, 1200));
                        }
                        else lines.Add("에이전트: " + Cut(text, 1500));
                        break;
                    case "tool":
                        tools++;
                        string tool = Text(part, "tool") ?? "?";
                        part.TryGetProperty("state", out var state);
                        string input = state.ValueKind == JsonValueKind.Object && state.TryGetProperty("input", out var raw) ? Compact(raw) : "";
                        if (tool == "skill" && state.ValueKind == JsonValueKind.Object && state.TryGetProperty("input", out var skillInput) && Text(skillInput, "name") is { } skill)
                            used.Add(skill);
                        string status = state.ValueKind == JsonValueKind.Object ? Text(state, "status") ?? "" : "";
                        string error = status == "error" && state.TryGetProperty("error", out var problem) ? " (실패: " + Cut(problem.ValueKind == JsonValueKind.String ? problem.GetString() ?? "" : problem.ToString(), 240) + ")" : "";
                        lines.Add($"[도구 {tool}] {Cut(input, 260)}{error}");
                        break;
                }
            }
        }
        string text2 = string.Join("\n", lines);
        if (text2.Length > DigestLimit)
        {
            // The ask that started it, then the newest part.
            string head = firstAsk is null ? "" : "처음 요청: " + firstAsk + "\n…\n";
            text2 = head + text2[^(DigestLimit - head.Length)..];
        }
        string? last = items.Count > 0 ? Id(items[^1]) : null;
        return new Digest(text2, tools, used, last);
    }

    internal static string LearnPrompt(Digest digest, IEnumerable<MySkills.Skill> existing, string projectName)
    {
        var known = existing.Select(skill => $"- {skill.Name}: {skill.Title} — {skill.Description}").ToList();
        return $$"""
            너는 Terminal Notes의 스킬 학습 담당이다. 아래는 방금 끝난 OpenCode 작업의 기록이다. 다음에 비슷한 일을 할 때 도움이 될,
            다시 쓸 수 있는 절차가 있으면 스킬 하나로 정리하라. 도구는 쓰지 말고 아래 형식으로만 답한다.

            정리하는 경우: 여러 단계를 거쳐 해결했고, 시행착오 끝에 알게 된 방법·명령·설정·확인 방법·주의점이 있으며, 같은 종류의 일이 다시 올 만한 경우.
            정리하지 않는 경우: 단순한 질문과 답, 한 번뿐인 일, 이 작업에만 해당하는 내용뿐인 경우, 이미 있는 스킬과 같은 내용인 경우.
            이미 있는 나만의 스킬과 주제가 같으면 새로 만들지 말고 update에 그 스킬 이름을 쓴다.

            규칙:
            - 비밀번호, 토큰, 키, 개인 정보, 접속 주소는 쓰지 않는다.
            - 이 작업에만 맞는 파일 이름이나 값 대신 일반적인 절차로 쓴다.
            - 지침은 한국어로, 6000자 이내로, 단계·명령·확인 방법·주의점 중심으로 쓴다. 실패했던 방법과 그 이유도 짧게 남긴다.
            - 지침 본문 첫 줄에 제목(# 제목)을 다시 쓰지 않는다. 제목은 title에만 쓴다.

            정리할 것이 없으면 정확히 이 한 줄만 답한다:
            {{None}}

            정리할 것이 있으면:
            {{Start}}
            name: 영어 소문자와 하이픈 이름 (예: wpf-release-check)
            update: 고칠 기존 스킬 이름, 없으면 비움
            title: 한국어 제목
            description: 언제 이 스킬을 쓰는지 한 줄 (에이전트가 이것을 보고 스킬을 고른다)
            ---
            지침 본문
            {{End}}

            이미 있는 나만의 스킬:
            {{(known.Count == 0 ? "(없음)" : string.Join("\n", known))}}

            작업 폴더: {{projectName}}

            작업 기록:
            {{digest.Text}}
            """;
    }

    /// <summary>A new version of an existing skill that keeps what it said and adds what was learned.</summary>
    internal static string MergePrompt(IEnumerable<(string Title, string Description, string Body)> parts, int limit = 6000) => $$"""
        너는 나만의 스킬 정리 담당이다. 아래 스킬들을 하나의 스킬로 합쳐라. 도구는 쓰지 말고 아래 형식으로만 답한다.
        - 내용은 빠짐없이 합치되 겹치는 것은 한 번만 쓴다. 서로 다르면 더 최근의 것(뒤에 있는 것)을 따른다.
        - 결과는 처음부터 하나였던 것처럼 지침만 쓴다. 합친 과정, 스킬 번호, 원래 스킬들의 차이는 쓰지 않는다.
        - 답의 첫 줄은 {{Start}}, 마지막 줄은 {{End}}로 반드시 감싼다.
        - 한국어로, 반드시 {{limit}}자 이내로, 단계·명령·확인 방법·주의점 중심으로 쓴다. 넘치면 예시와 설명부터 줄인다. 본문 첫 줄에 제목(# 제목)을 다시 쓰지 않는다.
        - 비밀번호, 토큰, 키, 개인 정보, 접속 주소는 쓰지 않는다.

        {{Start}}
        name: 영어 소문자와 하이픈 이름
        update:
        title: 한국어 제목
        description: 언제 이 스킬을 쓰는지 한 줄
        ---
        합친 지침 본문
        {{End}}

        {{string.Join("\n\n", parts.Select((part, index) => $"## 스킬 {index + 1}: {part.Title}\n설명: {part.Description}\n\n{part.Body}"))}}
        """;

    /// <summary>The curator's question: which skills say the same (titles and descriptions only; the merging reads the full text).</summary>
    internal static string ReviewPrompt(IEnumerable<MySkills.Skill> skills) => $$"""
        너는 나만의 스킬 정리 담당이다. 아래 스킬 목록에서 같은 일을 다루어 하나로 합치는 게 나은 묶음을 찾아라.
        확실한 것만, 많아야 3묶음. 도구는 쓰지 말고 아래 형식으로만 답한다.

        없으면 정확히 이 한 줄만: {{MergeNone}}
        있으면 묶음마다 한 줄씩:
        {{MergeStart}} 이름1, 이름2 | 합치는 이유 한 줄

        스킬 목록:
        {{string.Join("\n", skills.Select(skill => $"- {skill.Name}: {skill.Title} — {skill.Description}"))}}
        """;

    /// <summary>The skill in an answer; null when the answer says there is none or does not follow the format.</summary>
    internal static Lesson? ParseLesson(string answer)
    {
        answer = answer.Replace("\r\n", "\n");
        string block;
        int start = answer.LastIndexOf(Start, StringComparison.Ordinal);
        if (start >= 0)
        {
            // A missing end marker (the answer cut short at the end) still has the whole skill above it.
            int end = answer.IndexOf(End, start, StringComparison.Ordinal);
            block = end < 0 ? answer[(start + Start.Length)..] : answer[(start + Start.Length)..end];
        }
        else
        {
            // Measured 2026-11-12: the model often answers in the field format without the markers. Taken only when the fields are
            // there (a name or title line at a line start, then ---); "nothing to keep" stays nothing.
            if (answer.Contains(None, StringComparison.Ordinal)) return null;
            var first = Regex.Match(answer, @"(?m)^[ \t]*(name|title)[ \t]*:");
            if (!first.Success) return null;
            block = answer[first.Index..].Replace(End, "");
        }
        block = block.Trim('\n', ' ');
        int split = block.IndexOf("\n---", StringComparison.Ordinal);
        if (split < 0) return null;
        string head = block[..split], body = block[(split + 4)..].Trim();
        var fields = head.Split('\n').Select(line => line.Split(':', 2)).Where(pair => pair.Length == 2)
            .GroupBy(pair => pair[0].Trim().ToLowerInvariant()).ToDictionary(group => group.Key, group => group.First()[1].Trim());
        string title = fields.GetValueOrDefault("title") ?? "";
        // SKILL.md puts the title on top: a body that opens with it again (models do) loses that line.
        body = Regex.Replace(body, @"\A#\s+[^\n]*\n+", match => Similar(match.Value.Trim('#', ' ', '\n'), title) ? "" : match.Value).Trim();
        if (title.Length == 0 || body.Length == 0) return null;
        string? update = fields.GetValueOrDefault("update") is { Length: > 0 } named && MySkills.IsMine(named) ? named : null;
        return new Lesson(fields.GetValueOrDefault("name") ?? "", update, title, fields.GetValueOrDefault("description") ?? "", body);
    }

    /// <summary>The groups the curator found: existing skill names (two or more each) and why.</summary>
    internal static IReadOnlyList<(string[] Names, string Reason)> ParseReview(string answer, IReadOnlySet<string> known) =>
        answer.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(MergeStart, StringComparison.Ordinal))
            .Select(line => line[MergeStart.Length..].Split('|', 2))
            .Select(pair => (Names: pair[0].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(known.Contains).Distinct().ToArray(), Reason: pair.Length > 1 ? pair[1].Trim() : ""))
            .Where(group => group.Names.Length >= 2)
            .Take(3).ToList();

    private static bool Similar(string heading, string title) =>
        heading.Length > 0 && (heading.Contains(title, StringComparison.OrdinalIgnoreCase) || title.Contains(heading, StringComparison.OrdinalIgnoreCase));

    private static string? Id(JsonElement item) => item.TryGetProperty("info", out var info) ? Text(info, "id") : null;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsTrue(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string Compact(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => string.Join(" ", element.EnumerateObject().Select(property =>
            property.Value.ValueKind == JsonValueKind.String ? $"{property.Name}={property.Value.GetString()}" : $"{property.Name}={property.Value}")),
        _ => element.ToString()
    };

    private static string Cut(string text, int limit)
    {
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= limit ? text : text[..limit] + "…";
    }
}
