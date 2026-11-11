using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerminalNotes.Services;

namespace TerminalNotes;

/// <summary>Claude Code's conversation records and skill folder (2026-11-12 사용자: "클로드코드로 연결해서 검증해"): a task run in a Claude
/// Code terminal is learned from and searched like an OpenCode one. Claude Code keeps each conversation as
/// &lt;~/.claude&gt;/projects/&lt;folder with every non-letter-or-digit as -&gt;/&lt;session id&gt;.jsonl and reads skills from
/// &lt;~/.claude&gt;/skills/&lt;name&gt;/SKILL.md (the same SKILL.md as OpenCode's).</summary>
internal static class ClaudeCodeSessions
{
    internal sealed record Session(string Project, string Id, string Path, string Title, DateTimeOffset Updated);

    private static string Home => Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configured
        ? configured : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    internal static string ProjectsRoot => AppContext.GetData("TerminalNotes.ClaudeProjectsDirectory") as string ?? System.IO.Path.Combine(Home, "projects");

    internal static string SkillsDirectory => AppContext.GetData("TerminalNotes.ClaudeSkillsDirectory") as string ?? System.IO.Path.Combine(Home, "skills");

    internal static string FolderFor(string project) =>
        System.IO.Path.Combine(ProjectsRoot, Regex.Replace(System.IO.Path.GetFullPath(project).TrimEnd('\\', '/'), "[^A-Za-z0-9]", "-"));

    /// <summary>A task's Claude Code conversations (sub-agent records, kept in sub folders, are not).</summary>
    internal static IReadOnlyList<Session> List(string project)
    {
        string folder = FolderFor(project);
        if (!Directory.Exists(folder)) return [];
        var sessions = new List<Session>();
        foreach (string path in Directory.EnumerateFiles(folder, "*.jsonl", SearchOption.TopDirectoryOnly))
        {
            try { sessions.Add(new Session(project, System.IO.Path.GetFileNameWithoutExtension(path), path, Title(path), File.GetLastWriteTimeUtc(path))); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return sessions;
    }

    /// <summary>The title Claude Code gave the conversation, else its first request.</summary>
    internal static string Title(string path)
    {
        string? title = null, first = null;
        foreach (string line in ReadShared(path))
        {
            if (line.Contains("\"ai-title\"", StringComparison.Ordinal) && Parse(line) is { } named && Text(named.RootElement, "aiTitle") is { Length: > 0 } ai)
                title = ai;
            else if (first is null && line.Contains("\"type\":\"user\"", StringComparison.Ordinal) && Parse(line) is { } user &&
                user.RootElement.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } ask && !ask.TrimStart().StartsWith('<'))
                first = Regex.Replace(ask, @"\s+", " ").Trim() is var flat && flat.Length > 60 ? flat[..60] + "…" : flat;
        }
        return title ?? first ?? "(제목 없음)";
    }

    /// <summary>The lines of a record Claude Code may be writing right now.</summary>
    internal static IEnumerable<string> ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line) yield return line;
    }

    private static JsonDocument? Parse(string line)
    {
        try { return JsonDocument.Parse(line); }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>One tool-less question to Claude Code's cheapest model, as cheaply as it goes (measured 2026-11-12: about 630 tokens in and
/// $0.0001 for a one-word answer): print mode, no tools, no MCP servers, no settings or hooks, a one-line system prompt instead of the
/// default one, nothing saved as a conversation, run in an empty folder (no project instructions).</summary>
internal static class ClaudeCodeModel
{
    internal const string Model = "haiku";
    private const string SystemPrompt = "요청한 형식으로만 답한다. 도구는 쓰지 않는다.";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>What the last question cost, and the start of its answer (tests and diagnostics).</summary>
    internal static string? LastUsage { get; private set; }
    internal static string? LastAnswer { get; private set; }

    internal static string? Executable()
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(directory.Trim().Trim('"'), "claude.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        return File.Exists(local) ? local : null;
    }

    internal static async Task<string> Ask(string title, string prompt, CancellationToken token)
    {
        string exe = Executable() ?? throw new IOException("Claude Code(claude)를 찾을 수 없습니다.");
        string folder = Path.Combine(Path.GetTempPath(), "TerminalNotes claude ask");
        Directory.CreateDirectory(folder);
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] { "-p", "--model", Model, "--tools", "", "--no-session-persistence", "--strict-mcp-config",
                     "--setting-sources", "", "--system-prompt", SystemPrompt, "--output-format", "json" })
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Claude Code를 시작하지 못했습니다.");
        await process.StandardInput.WriteAsync(prompt);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(Timeout);
        try { await process.WaitForExitAsync(bounded.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new IOException($"모델 응답이 최대 {Timeout.TotalMinutes:0}분 안에 끝나지 않았습니다.");
        }
        string text = await output, problem = (await error).Trim();
        JsonElement result;
        try { result = JsonDocument.Parse(text).RootElement; }
        catch (JsonException) { throw Failure(problem.Length > 0 ? problem : text); }
        if (result.TryGetProperty("usage", out var usage))
            LastUsage = $"in {Number(usage, "input_tokens") + Number(usage, "cache_creation_input_tokens") + Number(usage, "cache_read_input_tokens")}, " +
                        $"out {Number(usage, "output_tokens")}" + (result.TryGetProperty("total_cost_usd", out var cost) ? $", ${cost.GetDouble():0.#####}" : "");
        string answer = result.TryGetProperty("result", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        LastAnswer = answer.Length > 400 ? answer[..400] + "…" : answer;
        if (process.ExitCode != 0 || result.TryGetProperty("is_error", out var failed) && failed.ValueKind == JsonValueKind.True)
            throw Failure(answer.Length > 0 ? answer : problem);
        return answer;
    }

    private static long Number(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt64(out long number) ? number : 0;

    // A usage or rate limit of the user's plan rests learning like OpenCode's (no time is known: the default rest).
    private static IOException Failure(string message)
    {
        message = message.Length > 300 ? message[..300] + "…" : message;
        return ModelUsageLimitException.IsUsageLimitText(message) || message.Contains("limit", StringComparison.OrdinalIgnoreCase)
            ? new ModelUsageLimitException("Claude 사용 한도에 걸렸습니다 · " + message, null)
            : new IOException("Claude Code가 답하지 못했습니다 · " + message);
    }
}
