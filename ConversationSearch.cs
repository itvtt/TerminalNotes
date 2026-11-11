using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using TerminalNotes.Services;

namespace TerminalNotes;

/// <summary>지난 대화 검색, like Hermes Agent's session search (2026-11-11 사용자 선택): the tasks' earlier OpenCode conversations,
/// newest first, by title and by what was said and run. It asks a running OpenCode server of the app (a terminal must be open: no
/// server is started just to search, this PC is short of memory). 보기 shows the conversation; 이어가기 opens it in a PowerShell window.</summary>
internal sealed class ConversationSearchWindow : Window
{
    internal const int SessionLimit = 80, HitLimit = 30;
    private static readonly Dictionary<string, (long Updated, string Text)> Cache = [];
    private readonly Func<Uri?> _endpoint;
    private readonly IReadOnlyList<string> _projects;
    private readonly TextBox _query = new() { Tag = "지난 대화 검색 · 두 글자 이상", MaxLength = 100, Padding = new Thickness(11, 6, 11, 6), FontSize = 13 };
    private readonly StackPanel _results = new() { Margin = new Thickness(18, 0, 18, 12) };
    private readonly TextBlock _state = new() { FontSize = 12.5, Margin = new Thickness(24, 0, 24, 8), TextWrapping = TextWrapping.Wrap };
    private readonly System.Windows.Threading.DispatcherTimer _pause = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private CancellationTokenSource? _search;

    internal const string ClaudeAgent = "Claude Code";

    /// <summary>A conversation: OpenCode's (through its server) or Claude Code's (<paramref name="File"/>, its record).</summary>
    internal sealed record Session(string Project, string Id, string Title, long Updated, string Agent = "OpenCode", string? File = null);

    internal sealed record Hit(Session Session, string Snippet, string Text);

    internal ConversationSearchWindow(Func<Uri?> endpoint, IReadOnlyList<string> projects)
    {
        _endpoint = endpoint;
        _projects = projects;
        Title = "지난 대화 검색";
        var screen = SystemParameters.WorkArea;
        Width = Math.Min(820, screen.Width - 40); Height = Math.Min(640, screen.Height - 40); MinWidth = 480; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        _state.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        AutomationProperties.SetName(_query, "지난 대화 검색");
        var body = new DockPanel();
        var top = new Border { Margin = new Thickness(24, 14, 24, 10), Child = _query };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_state, Dock.Top);
        body.Children.Add(top);
        body.Children.Add(_state);
        body.Children.Add(new ScrollViewer { Content = _results, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        StoreLook.Dress(this, Title, body);
        _query.TextChanged += (_, _) => { _pause.Stop(); _pause.Start(); };
        _query.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _pause.Stop(); _ = SearchAsync(); } };
        _pause.Tick += (_, _) => { _pause.Stop(); _ = SearchAsync(); };
        Loaded += (_, _) => _query.Focus();
        Closed += (_, _) => _search?.Cancel();
        _state.Text = $"태스크 {projects.Count}개의 OpenCode·Claude Code 대화를 최근 것부터 찾습니다.";
    }

    private async Task SearchAsync()
    {
        _search?.Cancel();
        string query = _query.Text.Trim();
        _results.Children.Clear();
        if (query.Length < 2) { _state.Text = "두 글자 이상 쓰세요."; return; }
        // Claude Code conversations are read from their records (always); OpenCode ones through a running OpenCode terminal.
        var endpoint = _endpoint();
        var search = _search = new CancellationTokenSource();
        int hits = 0;
        try
        {
            _state.Text = "찾는 중…";
            var sessions = new List<Session>();
            foreach (string project in _projects)
            {
                sessions.AddRange(await Task.Run(() => ClaudeCodeSessions.List(project)
                    .Select(claude => new Session(project, claude.Id, claude.Title, claude.Updated.ToUnixTimeMilliseconds(), ClaudeAgent, claude.Path)).ToList(), search.Token));
                if (endpoint is null) continue;
                using var client = new OpenCodeConversationClient(endpoint, project);
                sessions.AddRange(Sessions(await client.ListSessionsAsync(search.Token), project));
            }
            string missing = endpoint is null ? " · OpenCode 대화는 OpenCode 터미널이 열려 있을 때 함께 찾습니다" : "";
            if (sessions.Count == 0) { _state.Text = "찾을 대화가 없습니다" + missing; return; }
            int read = 0;
            foreach (var session in sessions.OrderByDescending(session => session.Updated).Take(SessionLimit))
            {
                string text;
                if (Cache.TryGetValue(session.Id, out var cached) && cached.Updated == session.Updated) text = cached.Text;
                else
                {
                    if (session.File is { } file) text = await Task.Run(() => MySkillLearning.ReadClaude(ClaudeCodeSessions.ReadShared(file), null).Text, search.Token);
                    else
                    {
                        using var client = new OpenCodeConversationClient(endpoint!, session.Project);
                        text = MySkillLearning.Read(await client.ReadMessagesAsync(session.Id, search.Token), null).Text;
                    }
                    Cache[session.Id] = (session.Updated, text);
                }
                read++;
                if (Match(session, text, query) is { } hit)
                {
                    _results.Children.Add(Row(hit, query));
                    if (++hits >= HitLimit) break;
                }
                if (search.IsCancellationRequested) return;
            }
            _state.Text = (hits == 0 ? $"대화 {read}개에서 찾지 못했습니다" : $"대화 {read}개 중 {hits}개" + (hits >= HitLimit ? " (더 있으면 검색어를 좁히세요)" : "")) + missing;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or JsonException or IOException or UnauthorizedAccessException)
        {
            if (!search.IsCancellationRequested) _state.Text = "검색하지 못했습니다 · " + exception.Message;
        }
    }

    /// <summary>A task's sessions as OpenCode lists them, without sub-agent sessions and the app's own side sessions.</summary>
    internal static IEnumerable<Session> Sessions(JsonElement list, string project)
    {
        if (list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.TryGetProperty("parentID", out var parent) && parent.ValueKind == JsonValueKind.String) continue;
            string? id = item.TryGetProperty("id", out var value) ? value.GetString() : null;
            string title = item.TryGetProperty("title", out var named) && named.ValueKind == JsonValueKind.String ? named.GetString() ?? "" : "";
            if (id is null || title.StartsWith("Terminal Notes ·", StringComparison.Ordinal)) continue;
            long updated = item.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Object && time.TryGetProperty("updated", out var at) &&
                at.TryGetInt64(out long ms) ? ms : 0;
            yield return new Session(project, id, title.Length > 0 ? title : "(제목 없음)", updated);
        }
    }

    /// <summary>A session that has the words: in its title, or in what was said (the snippet around the first place).</summary>
    internal static Hit? Match(Session session, string text, string query)
    {
        int at = text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (at < 0) return session.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ? new Hit(session, "", text) : null;
        int start = Math.Max(0, at - 70), end = Math.Min(text.Length, at + query.Length + 90);
        string snippet = (start > 0 ? "…" : "") + Regex.Replace(text[start..end], @"\s+", " ") + (end < text.Length ? "…" : "");
        return new Hit(session, snippet, text);
    }

    private Border Row(Hit hit, string query)
    {
        var title = new TextBlock { Text = hit.Session.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var meta = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
            Text = Path.GetFileName(hit.Session.Project.TrimEnd('\\', '/')) + " · " + hit.Session.Agent + " · " +
                DateTimeOffset.FromUnixTimeMilliseconds(hit.Session.Updated).ToLocalTime().ToString("yyyy-MM-dd HH:mm") };
        meta.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var text = new StackPanel();
        text.Children.Add(title);
        text.Children.Add(meta);
        if (hit.Snippet.Length > 0)
        {
            var snippet = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), MaxHeight = 54, TextTrimming = TextTrimming.CharacterEllipsis };
            snippet.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            int at = hit.Snippet.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
            if (at < 0) snippet.Inlines.Add(hit.Snippet);
            else
            {
                snippet.Inlines.Add(hit.Snippet[..at]);
                var found = new Run(hit.Snippet.Substring(at, query.Length)) { FontWeight = FontWeights.SemiBold };
                found.SetResourceReference(TextElement.ForegroundProperty, "TextPrimary");
                snippet.Inlines.Add(found);
                snippet.Inlines.Add(hit.Snippet[(at + query.Length)..]);
            }
            text.Children.Add(snippet);
        }
        var look = new Button { Content = "보기", Margin = new Thickness(8, 0, 0, 0) };
        var resume = new Button { Content = "이어가기", Margin = new Thickness(8, 0, 0, 0), ToolTip = "PowerShell 창에서 이 대화를 이어 엽니다." };
        look.SetResourceReference(StyleProperty, "QuietButton");
        resume.SetResourceReference(StyleProperty, "AccentButton");
        look.Click += (_, _) => SkillTextWindow.Show(this, "대화", hit.Session.Title, hit.Text);
        resume.Click += (_, _) => Resume(hit.Session);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        buttons.Children.Add(look);
        buttons.Children.Add(resume);
        var row = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(text);
        var frame = new Border { Child = row, CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 10, 10), Margin = new Thickness(6, 0, 6, 8), BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BackgroundProperty, "Surface");
        frame.SetResourceReference(Border.BorderBrushProperty, "ButtonLine");
        AutomationProperties.SetAutomationId(frame, "ConversationHit");
        return frame;
    }

    /// <summary>Replaces opening the PowerShell window (tests): the session and the command it would run.</summary>
    internal static Action<Session, string>? ResumeOverride { get; set; }

    /// <summary>What continues a conversation in its task folder.</summary>
    internal static string ResumeCommand(Session session) =>
        session.Agent == ClaudeAgent ? "claude --resume " + session.Id : "opencode --session " + session.Id;

    private static void Resume(Session session)
    {
        if (!Regex.IsMatch(session.Id, "^[A-Za-z0-9_-]+$") || !Directory.Exists(session.Project)) return;
        string command = ResumeCommand(session);
        if (ResumeOverride is { } resume) { resume(session, command); return; }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true, WorkingDirectory = session.Project, ArgumentList = { "-NoExit", "-Command", command }
        });
    }
}
