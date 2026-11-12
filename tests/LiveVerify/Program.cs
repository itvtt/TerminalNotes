using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using TerminalNotes;
using TerminalNotes.Services;

// Live verification with a real OpenCode server and its real (free) model: a multi-step task, learning a skill from it, nothing new
// to learn right after, the skill used in a later session (counted), the curator's merge review, and searching the conversation.
// OpenCode's config folder is a temporary one, so the user's skills are not touched.
internal static class Program
{
    private static int _failures;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static void Assert(bool condition, string name)
    {
        Console.WriteLine((condition ? "ok   " : "FAIL ") + name);
        if (!condition) _failures++;
    }

    private static void Log(string text) => Console.WriteLine($"[{Clock.Elapsed:mm\\:ss}] {text}");

    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("claude")) return await ClaudeAsync();
        string root = Path.Combine(Path.GetTempPath(), "TerminalNotes live " + Guid.NewGuid().ToString("N")[..8]);
        string config = Path.Combine(root, "config"), project = Path.Combine(root, "sales-report");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(Path.Combine(config, "opencode", "skills"));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
        AppContext.SetData("TerminalNotes.SkillsDirectory", Path.Combine(config, "opencode", "skills"));
        AppContext.SetData("TerminalNotes.MySkillsDirectory", Path.Combine(root, "my-skills"));
        await File.WriteAllTextAsync(Path.Combine(project, "sales.csv"), "region,month,amount\nseoul,1,120\nbusan,1,80\nseoul,2,150\nbusan,2,95\ndaegu,2,40\n");
        AppContext.SetData("TerminalNotes.ClaudeSkillsDirectory", Path.Combine(root, "claude-skills"));
        int port = FreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/");
        using var host = new OpenCodeHeadlessHost(endpoint);
        try
        {
            await host.StartAsync(project, null, autoMode: true);
            Log($"server {host.ProcessId} on {port}");
            using var client = new OpenCodeConversationClient(endpoint, project);
            string? model = args.FirstOrDefault(arg => arg.Contains('/'));
            if (args.Contains("limit"))
            {
                // What OpenCode reports while the provider refuses (a used-up free limit): its session status and message error.
                var created = await client.CreateSessionAsync("limit probe");
                string probe = created.GetProperty("id").GetString()!;
                await client.SendAsync(probe, "한 단어로 답해: 안녕", "msg_" + Guid.NewGuid().ToString("N"), "opencode/big-pickle");
                for (int i = 0; i < 12; i++)
                {
                    await Task.Delay(2500);
                    var status = await client.ReadStatusAsync();
                    string mine = status.ValueKind == JsonValueKind.Object && status.TryGetProperty(probe, out var own) ? own.GetRawText() : "(none)";
                    Log("status: " + (mine.Length > 400 ? mine[..400] : mine));
                }
                foreach (var message in (await client.ReadMessagesAsync(probe)).EnumerateArray())
                    if (message.GetProperty("info").TryGetProperty("error", out var error)) Log("message error: " + error.GetRawText()[..Math.Min(500, error.GetRawText().Length)]);
                var watch = Stopwatch.StartNew();
                try
                {
                    await client.CompleteToolLessPromptAsync("Terminal Notes · 스킬 배우기", "한 단어로 답해: 안녕", "opencode/big-pickle", CancellationToken.None,
                        TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(60));
                    Log("side session answered (no limit now)");
                }
                catch (ModelUsageLimitException limit)
                {
                    Log($"side session: limit in {watch.Elapsed.TotalSeconds:0} s · {limit.Message}");
                    Assert(watch.Elapsed < TimeSpan.FromSeconds(30) && limit.RetryAt is not null, "at the usage limit a side session stops at once, with when it comes back");
                }
                // The curator at the limit: learning rests until then.
                MySkills.Save(null, "가", "가", "가", learned: true);
                MySkills.Save(null, "나", "나", "나", learned: true);
                var limitReview = await MainWindow.ReviewMySkillsAsync(client, "opencode/big-pickle", CancellationToken.None);
                var rest = MySkillLearning.Resting(MySkills.ReadSettings(), DateTimeOffset.UtcNow);
                Log($"review: {string.Join("; ", limitReview.Dropped)} · resting: {rest}");
                Assert(rest?.Contains("사용 한도", StringComparison.Ordinal) == true, "and learning rests until the limit comes back");
                return _failures;
            }
            if (args.Contains("merges"))
            {
                await MergesAsync(client);
                return _failures;
            }

            // 1. Real multi-step work.
            string work = await RunAsync(client, "월별 매출 보고서", """
                이 폴더의 sales.csv로 월별·지역별 매출 보고서를 만들어라. 단계마다 도구를 써라:
                1) sales.csv를 읽는다. 2) PowerShell로 월별 합계를 계산한다. 3) 지역별 합계를 계산한다.
                4) report.md에 표 두 개(월별, 지역별)로 쓴다. 5) report.md를 다시 읽어 합계가 맞는지 확인한다.
                끝나면 한 줄로 결과만 말해라.
                """, model);
            var messages = await client.ReadMessagesAsync(work);
            var digest = MySkillLearning.Read(messages, null);
            Log($"work: {digest.ToolCalls} tool calls, digest {digest.Text.Length} chars");
            Assert(File.Exists(Path.Combine(project, "report.md")), "the agent did the work (report.md)");
            Assert(digest.ToolCalls >= MySkillLearning.MinimumToolCalls, $"a real multi-step session reads as {digest.ToolCalls} tool calls");

            // 2. Learning from it, with the real model: the raw answer, then the same through LearnAsync.
            string? sessionModel = await client.ReadPlanMakerModelAsync(work, await client.ReadAgentsAsync(CancellationToken.None), CancellationToken.None);
            Log("session model: " + (sessionModel ?? "(default)"));
            string answer = await client.CompleteToolLessPromptAsync("Terminal Notes · 스킬 배우기", MySkillLearning.LearnPrompt(digest, [], "sales-report"),
                sessionModel, CancellationToken.None, TimeSpan.FromMinutes(6), TimeSpan.FromMinutes(2));
            Log("raw learning answer:\n" + Indent(answer));
            bool none = answer.Contains(MySkillLearning.None, StringComparison.Ordinal);
            var lesson = MySkillLearning.ParseLesson(answer);
            Assert(none || lesson is not null, "the real model answers in one of the two formats");
            var learned = await MainWindow.LearnAsync(client, work, project, "sales-report", CancellationToken.None);
            Log(learned is null ? "LearnAsync: nothing learned" : $"LearnAsync: {learned.Name} · {learned.Title} · {learned.Description}\n{Indent(learned.Body)}");
            Assert(learned is not null, "a five-step report task is learned as a skill");
            if (learned is not null)
            {
                string deployed = Path.Combine(config, "opencode", "skills", learned.Name, "SKILL.md");
                Assert(File.Exists(deployed), "the learned skill is in OpenCode's skill folder");
                Assert(!System.Text.RegularExpressions.Regex.IsMatch(learned.Body, @"\b(sk-|ghp_|password\s*[:=])", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "no secrets in it");
            }
            Assert(await MainWindow.LearnAsync(client, work, project, "sales-report", CancellationToken.None) is null, "right after, there is nothing new to learn");

            // 3. The skill used in a later session: OpenCode loads it after the instance reloads; the use is counted.
            if (learned is not null)
            {
                await client.DisposeInstanceAsync();
                string later = await RunAsync(client, "다시 보고서", $"{learned.Name} 스킬을 먼저 불러온 뒤 그 절차대로 sales.csv의 2월 지역별 합계만 report2.md에 써라.", model);
                var laterDigest = MySkillLearning.Read(await client.ReadMessagesAsync(later), null);
                Log($"later session used skills: [{string.Join(", ", laterDigest.UsedSkills)}]");
                Assert(laterDigest.UsedSkills.Contains(learned.Name), "OpenCode loads the learned skill with its skill tool");
                await MainWindow.LearnAsync(client, later, project, "sales-report", CancellationToken.None);
                var counted = MySkills.Get(learned.Name);
                Assert(counted is { Uses: > 0, LastUsed: not null }, $"the use is counted ({counted?.Uses})");
            }

            // 4. The curator's merge review with the real model: two skills on the same subject.
            MySkills.Save(null, "CSV 월별 합계 내기", "CSV에서 월별 합계를 낼 때", "1. Import-Csv로 읽는다.\n2. Group-Object month로 묶는다.\n3. Measure-Object -Sum으로 더한다.", learned: true);
            MySkills.Save(null, "월별 매출 집계", "매출 CSV를 월별로 집계할 때", "- Import-Csv 후 month별 Group-Object\n- amount 합계는 Measure-Object -Sum\n- 결과를 표로 쓴다", learned: true);
            var review = await MainWindow.ReviewMySkillsAsync(client, sessionModel, CancellationToken.None);
            Log($"review: {review.Groups} groups, dropped [{string.Join("; ", review.Dropped)}]\n{Indent(review.Answer)}");
            var proposals = MySkills.ReadSettings().Proposals;
            foreach (var proposal in proposals) Log($"proposal: {string.Join(" + ", proposal.Names)} → {proposal.Title} ({proposal.Reason})\n{Indent(proposal.Body)}");
            Assert(proposals.Count > 0, "the curator proposes merging the two skills on monthly sums");
            if (proposals.FirstOrDefault() is { } first)
            {
                var merged = MySkills.AcceptProposal(first);
                Assert(MySkills.Get(merged.Name) is { Version: 2 } && first.Names.Skip(1).All(name => MySkills.Get(name) is null), "accepting merges them");
            }

            // 5. Searching the conversation through the same server.
            var sessions = ConversationSearchWindow.Sessions(await client.ListSessionsAsync(), project).ToList();
            Log("sessions: " + string.Join(" | ", sessions.Select(session => session.Title)));
            Assert(sessions.Any(session => session.Id == work), "the work session is listed");
            Assert(sessions.All(session => !session.Title.StartsWith("Terminal Notes ·", StringComparison.Ordinal)), "the app's own side sessions are not");
            var hit = ConversationSearchWindow.Match(sessions.First(session => session.Id == work), digest.Text, "report.md");
            Assert(hit is { Snippet.Length: > 0 }, "a word from the work is found with its snippet: " + hit?.Snippet);
        }
        catch (Exception exception) { _failures++; Console.WriteLine("FAIL " + exception); }
        finally
        {
            host.Dispose();
            try { Directory.Delete(root, true); } catch { }
        }
        Console.WriteLine(_failures == 0 ? "PASS live verification" : $"FAIL live verification ({_failures})");
        return _failures;
    }

    // The curator's review and merge, again and again on the same three related skills (one long): how often it proposes.
    private static async Task MergesAsync(OpenCodeConversationClient client)
    {
        string longBody = string.Join("\n", Enumerable.Range(1, 40).Select(i =>
            $"{i}. 단계 {i}: Import-Csv로 읽은 행을 Group-Object로 묶고 Measure-Object -Sum으로 합계를 낸 뒤, 결과 표의 합과 전체 합이 같은지 확인한다. 열 이름이 다르면 먼저 헤더를 본다."));
        MySkills.Save(null, "CSV 범주별 합계 보고서", "CSV를 범주별로 집계해 마크다운 보고서를 만들 때", longBody, learned: true);
        MySkills.Save(null, "CSV 월별 합계 내기", "CSV에서 월별 합계를 낼 때", "1. Import-Csv로 읽는다.\n2. Group-Object month로 묶는다.\n3. Measure-Object -Sum으로 더한다.", learned: true);
        MySkills.Save(null, "월별 매출 집계", "매출 CSV를 월별로 집계할 때", "- Import-Csv 후 month별 Group-Object\n- amount 합계는 Measure-Object -Sum\n- 결과를 표로 쓴다", learned: true);
        int proposed = 0;
        const int rounds = 2;
        for (int round = 1; round <= rounds; round++)
        {
            MySkills.UpdateSettings(settings => settings with { Proposals = [] });
            var review = await MainWindow.ReviewMySkillsAsync(client, "opencode/big-pickle", CancellationToken.None);
            var proposals = MySkills.ReadSettings().Proposals;
            Log($"round {round}: groups {review.Groups}, proposals {proposals.Count}, dropped [{string.Join("; ", review.Dropped)}]" +
                string.Join("", proposals.Select(proposal => $"\n      {string.Join("+", proposal.Names)} → {proposal.Title} ({proposal.Body.Length}자)")));
            if (proposals.Count > 0) proposed++;
        }
        Assert(proposed == rounds, $"the curator proposes the merge every time ({proposed}/{rounds})");
    }

    // ── Claude Code: the same loop with its records and its cheapest model, every call's cost logged.
    private static async Task<int> ClaudeAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "TerminalNotes live claude " + Guid.NewGuid().ToString("N")[..8]);
        string project = Path.Combine(root, "salesreport");
        Directory.CreateDirectory(project);
        AppContext.SetData("TerminalNotes.SkillsDirectory", Path.Combine(root, "opencode-skills"));
        AppContext.SetData("TerminalNotes.ClaudeSkillsDirectory", Path.Combine(root, "claude-skills"));
        AppContext.SetData("TerminalNotes.MySkillsDirectory", Path.Combine(root, "my-skills"));
        await File.WriteAllTextAsync(Path.Combine(project, "sales.csv"), "region,month,amount\nseoul,1,120\nbusan,1,80\nseoul,2,150\nbusan,2,95\n");
        string records = ClaudeCodeSessions.FolderFor(project);
        try
        {
            // 1. Real work in Claude Code (haiku, its Bash tool only): a task it works out itself, a record written.
            var work = await RunClaude(project, ["--tools", "Bash", "--dangerously-skip-permissions", "--setting-sources", ""],
                "이 폴더의 sales.csv로 월별·지역별 매출 합계 보고서를 report.md에 마크다운 표 두 개로 만들어라. 먼저 파일과 헤더를 확인하고, " +
                "월별과 지역별 합계를 따로 계산하고, 두 표의 총합이 원본 총합과 같은지 검산하라. 단계마다 Bash 도구를 따로 써라. 끝나면 한 줄로 결과만 말해라.");
            Log("work: " + work);
            var sessions = ClaudeCodeSessions.List(project);
            Log($"records: {records} · {sessions.Count} · " + string.Join(" | ", sessions.Select(session => session.Title)));
            Assert(sessions.Count == 1, "Claude Code wrote the conversation record where it is looked for");
            var digest = MySkillLearning.ReadClaude(ClaudeCodeSessions.ReadShared(sessions[0].Path), null);
            Log($"digest: {digest.ToolCalls} tool calls, {digest.Text.Length} chars\n{Indent(digest.Text)}");
            Assert(digest.ToolCalls >= MySkillLearning.MinimumToolCalls, $"the record reads as {digest.ToolCalls} tool calls");

            // 2. Learning from it with Claude Code's cheapest model.
            var learned = await MainWindow.LearnFromClaudeAsync(project, "salesreport", CancellationToken.None);
            Log($"learn cost: {ClaudeCodeModel.LastUsage} · " + (learned is null ? "nothing learned · answer: " + ClaudeCodeModel.LastAnswer : $"{learned.Name} · {learned.Title}\n{Indent(learned.Body)}"));
            Assert(learned is not null, "a five-step task is learned as a skill");
            if (learned is not null)
            {
                Assert(File.Exists(Path.Combine(root, "claude-skills", learned.Name, "SKILL.md")) && File.Exists(Path.Combine(root, "opencode-skills", learned.Name, "SKILL.md")),
                    "the skill is in Claude Code's skill folder and OpenCode's");
                Assert(await MainWindow.LearnFromClaudeAsync(project, "salesreport", CancellationToken.None) is null, "right after, nothing new to learn (and no model call)");

                // 3. Claude Code uses it in a later session (the task's own skill folder), and the use is counted.
                string own = Path.Combine(project, ".claude", "skills", learned.Name);
                Directory.CreateDirectory(own);
                File.Copy(Path.Combine(root, "claude-skills", learned.Name, "SKILL.md"), Path.Combine(own, "SKILL.md"));
                var use = await RunClaude(project, ["--tools", "Skill", "--dangerously-skip-permissions", "--setting-sources", "project"],
                    $"Skill 도구로 {learned.Name} 스킬을 불러온 뒤, 그 스킬의 첫 단계만 한 줄로 말해라.");
                Log("use: " + use);
                await MainWindow.LearnFromClaudeAsync(project, "salesreport", CancellationToken.None);
                Assert(MySkills.Get(learned.Name) is { Uses: > 0 }, $"the use of the skill is counted ({MySkills.Get(learned.Name)?.Uses})");
            }

            // 4. The curator with Claude Code: two skills on the same subject.
            MySkills.Save(null, "CSV 월별 합계 내기", "CSV에서 월별 합계를 낼 때", "1. 헤더를 확인한다.\n2. awk로 월별로 더한다.", learned: true);
            MySkills.Save(null, "월별 매출 집계", "매출 CSV를 월별로 집계할 때", "- awk -F, 로 month별 합계\n- 결과를 표로 쓴다", learned: true);
            var review = await MainWindow.ReviewMySkillsAsync(ClaudeCodeModel.Ask, CancellationToken.None);
            var proposals = MySkills.ReadSettings().Proposals;
            Log($"review cost (last call): {ClaudeCodeModel.LastUsage} · groups {review.Groups}, proposals {proposals.Count}, dropped [{string.Join("; ", review.Dropped)}]" +
                string.Join("", proposals.Select(proposal => $"\n      {string.Join(" + ", proposal.Names)} → {proposal.Title}")));
            Assert(proposals.Count > 0, "the curator proposes merging skills on the same subject");

            // 5. Search: the record is found by what was run, and goes on with claude --resume.
            var session = ClaudeCodeSessions.List(project).OrderBy(found => found.Updated).First();
            var hit = ConversationSearchWindow.Match(new ConversationSearchWindow.Session(project, session.Id, session.Title, 0, ConversationSearchWindow.ClaudeAgent, session.Path),
                MySkillLearning.ReadClaude(ClaudeCodeSessions.ReadShared(session.Path), null).Text, "report.md");
            Assert(hit is { Snippet.Length: > 0 }, "the work is found by a word from it: " + hit?.Snippet);
            Assert(hit is { } found && ConversationSearchWindow.ResumeCommand(found.Session) == "claude --resume " + session.Id, "and goes on with claude --resume");
        }
        catch (Exception exception) { _failures++; Console.WriteLine("FAIL " + exception); }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
            try { if (Directory.Exists(records)) Directory.Delete(records, true); } catch { }
        }
        Console.WriteLine(_failures == 0 ? "PASS live verification (Claude Code)" : $"FAIL live verification (Claude Code) ({_failures})");
        return _failures;
    }

    // One Claude Code print-mode run in the task folder with haiku, saved as a conversation; its answer and cost.
    private static async Task<string> RunClaude(string folder, string[] extra, string prompt)
    {
        var info = new ProcessStartInfo(ClaudeCodeModel.Executable()!)
        {
            WorkingDirectory = folder, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        foreach (string argument in new[] { "-p", "--model", "haiku", "--strict-mcp-config", "--system-prompt", "시키는 대로 짧게 일한다.", "--output-format", "json" }.Concat(extra))
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        await process.StandardInput.WriteAsync(prompt);
        process.StandardInput.Close();
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        using var json = JsonDocument.Parse(output);
        var r = json.RootElement;
        var usage = r.GetProperty("usage");
        long input = usage.GetProperty("input_tokens").GetInt64() + usage.GetProperty("cache_creation_input_tokens").GetInt64() + usage.GetProperty("cache_read_input_tokens").GetInt64();
        return $"{r.GetProperty("result").GetString()?.Trim()} (turns {r.GetProperty("num_turns")}, in {input}, out {usage.GetProperty("output_tokens")}, ${r.GetProperty("total_cost_usd").GetDouble():0.####})";
    }

    // Sends one prompt in a new session and waits until the agent is done.
    private static async Task<string> RunAsync(OpenCodeConversationClient client, string title, string prompt, string? model)
    {
        var created = await client.CreateSessionAsync(title);
        string session = created.GetProperty("id").GetString()!;
        await client.SendAsync(session, prompt, "msg_" + Guid.NewGuid().ToString("N"), model);
        Log($"sent '{title}' ({session})");
        var deadline = Stopwatch.StartNew();
        bool seenBusy = false;
        int idle = 0;
        while (deadline.Elapsed < TimeSpan.FromMinutes(10))
        {
            await Task.Delay(2000);
            var status = await client.ReadStatusAsync();
            bool busy = status.ValueKind == JsonValueKind.Object && status.TryGetProperty(session, out var state) &&
                state.TryGetProperty("type", out var type) && type.GetString() is "busy" or "retry";
            if (busy) { seenBusy = true; idle = 0; continue; }
            if (++idle >= 2 && (seenBusy || deadline.Elapsed > TimeSpan.FromSeconds(20))) break;
        }
        Log($"'{title}' finished in {deadline.Elapsed:mm\\:ss}");
        return session;
    }

    private static string Indent(string text) => string.Join("\n", text.Trim().Split('\n').Take(40).Select(line => "      " + line));

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
