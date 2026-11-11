using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Documents = System.Windows.Documents;
using Media = System.Windows.Media;

[assembly: InternalsVisibleTo("TerminalNotes.SkillStoreTests")]

namespace TerminalNotes;

// 스킬 스토어 (사용자 지시: 누구나 지침을 스킬로 공유하고 받아 적용하며, 클릭 한 번으로 적용·해제. 공유한 사람은 계속 고쳐
// 올리고 버전별로 이력이 남으며, 스토어는 최신 버전만 보여 준다. 원하는 버전을 골라 적용하고 그 위에 내 지침을 로컬로 덧붙여
// 기본 + 커스텀으로 쓰며, 새 버전을 적용해도 내가 적은 것은 사라지지 않는다). The store lives on the relay (relay\relay.py,
// /api/skills) and uses the app's relay address and key.
//
// An applied skill is OpenCode's global skill folder ~/.config/opencode/skills/<name>/SKILL.md. OpenCode reads it once per
// instance, so every change raises InstalledSkills.Changed and the open terminals reset their instance when idle (바로 반영).
// The app keeps each store skill's own layers next to them in skills\.terminal-notes\<name>\ (no SKILL.md there, so
// OpenCode ignores it): base.md (the applied store version), custom.md (my additions), local.md (my own full text when I
// manage it myself) and state.json. SKILL.md is base + my additions, or my own text. Nothing the user wrote is ever lost:
// a new version keeps my additions, removing keeps them for the next apply, and a SKILL.md edited by hand is taken over —
// edits below the marker become my additions, other edits switch the skill to "내가 직접 관리".

internal sealed record StoreSkill(string Name, string Title, string Description, string Author, DateTimeOffset? UpdatedAt, string Sha256)
{
    public int Version { get; init; } = 1;
    public int VersionCount { get; init; } = 1;
    public int Downloads { get; init; }
    public string? Note { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    /// <summary>Only its author sees and uses it (relay 2.5 and later); PCs that applied it lose it (SkillWithdrawals).</summary>
    public bool Private { get; init; }
    /// <summary>The GitHub file it follows, and why the relay last could not read it.</summary>
    public string? Source { get; init; }
    public string? SourceError { get; init; }
    /// <summary>Where it came from, as the sharer wrote it: a user id or a link (relay 2.11 and later).</summary>
    public string? Origin { get; init; }
    public Uri? OriginLink => LinkIn(Origin);
    public string? Category { get; init; }
    /// <summary>The category the store files it under: one it was shared without is 기타.</summary>
    public string CategoryName => string.IsNullOrWhiteSpace(Category) ? OtherCategory : Category.Trim();
    internal const string OtherCategory = "기타";
    /// <summary>When it was shared, or last changed once it has newer versions.</summary>
    public DateTimeOffset? ShownDate => Version > 1 ? UpdatedAt ?? CreatedAt : CreatedAt ?? UpdatedAt;
    /// <summary>The first line of its instructions (relay 2.3 and later), for a card when the description only repeats the title.</summary>
    public string? Summary { get; init; }

    /// <summary>How the store shows it: the title and the Windows account that shared it ("Impala 조회-alice").</summary>
    public string DisplayTitle => Shown(Title, Author);

    internal static string Shown(string title, string? author) =>
        string.IsNullOrEmpty(author) || title.EndsWith("-" + author, StringComparison.OrdinalIgnoreCase) ? title : title + "-" + author;

    /// <summary>The web page a 출처 names ("https://…", or "github.com/…" and "www.…" without the scheme, alone or beside a user id), or
    /// null when it names only a user. Anyone writes it and a click opens it, so only http(s) counts: file:, ms-…: and the like stay text.</summary>
    internal static Uri? LinkIn(string? origin)
    {
        string text = origin?.Trim() ?? "";
        string address = Regex.Match(text, @"\bhttps?://[^\s<>""]+", RegexOptions.IgnoreCase).Value.TrimEnd(')', '.', ',');
        if (address.Length == 0 && Regex.IsMatch(text, @"^(?:www\.\S+|[a-z0-9-]+(?:\.[a-z0-9-]+)+/\S*)$", RegexOptions.IgnoreCase)) address = "https://" + text;
        return Uri.TryCreate(address, UriKind.Absolute, out var link) && link.Scheme is "http" or "https" ? link : null;
    }
}

internal sealed record StoreSkillVersion(int Version, string Title, string Description, string Author, DateTimeOffset? PublishedAt,
    string Sha256, string? Note);

/// <summary>One version's SKILL.md as the relay composed it, and the instructions the author wrote.</summary>
internal sealed record StoreSkillContent(string Name, StoreSkillVersion Version, int LatestVersion, string Content, string Body);

internal sealed class SkillStoreException(string message, HttpStatusCode? status = null) : Exception(message)
{
    internal HttpStatusCode? Status { get; } = status;
}

/// <summary>The relay's skill store: list, versions, download and share.</summary>
internal sealed class SkillStoreClient
{
    // The relay is a PC on the local network: reached directly, never through the system proxy (which may not reach it and
    // answers 502 instead).
    private static readonly HttpClient SharedHttp = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Uri _base;
    private readonly string _key;
    private readonly HttpClient _http;
    private readonly string _user;

    internal SkillStoreClient(Uri address, string key, HttpClient? http = null, string? user = null)
    {
        _base = new Uri(address.AbsoluteUri.TrimEnd('/') + "/");
        _key = key;
        _http = http ?? SharedHttp;
        _user = user ?? Environment.UserName;
    }

    /// <summary>The store of this build's relay, or null when the build has no relay address.</summary>
    internal static SkillStoreClient? CreateDefault()
    {
        var (url, key) = ReportRelayClient.Endpoint();
        return Uri.TryCreate(url, UriKind.Absolute, out var address) ? new SkillStoreClient(address, key ?? "") : null;
    }

    /// <summary>Every shared skill at its latest version.</summary>
    internal async Task<IReadOnlyList<StoreSkill>> ListAsync(CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/skills", null, token);
        return await response.Content.ReadFromJsonAsync<List<StoreSkill>>(Json, token) ?? [];
    }

    /// <summary>Every kept version of one skill, newest first.</summary>
    internal async Task<IReadOnlyList<StoreSkillVersion>> VersionsAsync(string name, CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/skills/" + Uri.EscapeDataString(name) + "/versions", null, token);
        return await response.Content.ReadFromJsonAsync<List<StoreSkillVersion>>(Json, token) ?? [];
    }

    /// <summary>One version (the latest when <paramref name="version"/> is null); <paramref name="apply"/> counts a download.</summary>
    internal async Task<StoreSkillContent> GetVersionAsync(string name, int? version, bool apply, CancellationToken token)
    {
        string path = "api/skills/" + Uri.EscapeDataString(name) + (version is { } wanted ? $"/versions/{wanted}" + (apply ? "?apply=1" : "") : "");
        using var response = await SendAsync(HttpMethod.Get, path, null, token);
        var body = await response.Content.ReadFromJsonAsync<VersionDownload>(Json, token);
        if (body is null || string.IsNullOrEmpty(body.Content)) throw new SkillStoreException("스킬 내용을 받지 못했습니다.");
        int number = body.Version is > 0 ? body.Version.Value : 1;
        return new StoreSkillContent(body.Name ?? name, new StoreSkillVersion(number, body.Title ?? name, body.Description ?? "", body.Author ?? "",
            body.PublishedAt ?? body.UpdatedAt, body.Sha256 ?? "", body.Note), body.LatestVersion is > 0 ? body.LatestVersion.Value : number,
            body.Content, body.Body ?? "");
    }

    /// <summary>The latest version (kept for callers that need only the content).</summary>
    internal async Task<(StoreSkill Skill, string Content)> GetAsync(string name, CancellationToken token)
    {
        var latest = await GetVersionAsync(name, null, apply: false, token);
        return (new StoreSkill(latest.Name, latest.Version.Title, latest.Version.Description, latest.Version.Author,
            latest.Version.PublishedAt, latest.Version.Sha256) { Version = latest.Version.Version }, latest.Content);
    }

    /// <summary>Shares a skill, or a new version of the author's own (numbered <paramref name="version"/> when given, which must
    /// be above the latest); returns the version number, the latest one when the content did not change. <paramref name="edit"/>
    /// says it is a new version of an existing skill, which a store admin may add to someone else's (it stays theirs).</summary>
    internal async Task<int> PublishAsync(string name, string title, string description, string body, string author,
        CancellationToken token, string note = "", int? version = null, bool? isPrivate = null, string? source = null, string? category = null,
        bool edit = false, string? origin = null)
    {
        // Null leaves what the skill had: being private, the GitHub link and the 출처 ("" removes them) and the category.
        using var content = new StringContent(JsonSerializer.Serialize(new { name, title, description, body, author, note, version, @private = isPrivate,
            source, category, edit, origin }, Json), Encoding.UTF8, "application/json");
        using var response = await SendAsync(HttpMethod.Post, "api/skills", content, token);
        var result = await response.Content.ReadFromJsonAsync<PublishResult>(Json, token);
        return result?.Version is > 0 ? result.Version.Value : 1;
    }

    /// <summary>Takes a skill out of the store (the relay keeps it 30 days in its .trash).</summary>
    internal async Task DeleteAsync(string name, CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Delete, "api/skills/" + Uri.EscapeDataString(name), null, token);
    }

    /// <summary>Its author shows a skill to everyone or keeps it private (relay 2.10); no new version.</summary>
    internal async Task SetPrivateAsync(string name, bool isPrivate, CancellationToken token)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { @private = isPrivate }, Json), Encoding.UTF8, "application/json");
        using var response = await SendAsync(HttpMethod.Put, "api/skills/" + Uri.EscapeDataString(name) + "/private", content, token);
    }

    /// <summary>Of these skill names, the ones this account may no longer use: private to their author (relay 2.10).</summary>
    internal async Task<IReadOnlyList<string>> WithdrawnAsync(IReadOnlyCollection<string> names, CancellationToken token)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { names }, Json), Encoding.UTF8, "application/json");
        using var response = await SendAsync(HttpMethod.Post, "api/skills/withdrawn", content, token);
        return (await response.Content.ReadFromJsonAsync<WithdrawnSkills>(Json, token))?.Withdrawn ?? [];
    }

    private sealed record WithdrawnSkills(string[]? Withdrawn);

    /// <summary>The text of the file a GitHub (or GitHub Enterprise) link points at, read by the relay.</summary>
    internal async Task<string> FetchSourceAsync(string link, CancellationToken token)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { source = link }, Json), Encoding.UTF8, "application/json");
        using var response = await SendAsync(HttpMethod.Post, "api/skills/source", content, token);
        var body = await response.Content.ReadFromJsonAsync<SourceText>(Json, token);
        return body?.Content is { Length: > 0 } text ? text : throw new SkillStoreException("깃허브 파일을 받지 못했습니다.");
    }

    private sealed record SourceText(string? Content);

    /// <summary>Every plugin in the store, at its latest version (relay 2.8 and later).</summary>
    internal async Task<IReadOnlyList<StorePlugin>> PluginsAsync(CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/plugins", null, token);
        return await response.Content.ReadFromJsonAsync<List<StorePlugin>>(Json, token) ?? [];
    }

    /// <summary>A plugin's latest version with its code; <paramref name="install"/> counts an install.</summary>
    internal async Task<StorePlugin> GetPluginAsync(string name, bool install, CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/plugins/" + Uri.EscapeDataString(name) + (install ? "?install=1" : ""), null, token);
        var plugin = await response.Content.ReadFromJsonAsync<StorePlugin>(Json, token);
        return plugin is { Code.Length: > 0 } ? plugin : throw new SkillStoreException("플러그인 코드를 받지 못했습니다.");
    }

    /// <summary>Shares a plugin (master accounts), or a new version of one (<paramref name="edit"/>); returns its version, the
    /// latest one when the code did not change.</summary>
    internal async Task<int> PublishPluginAsync(string name, string title, string description, string extension, string code, string author,
        string note, bool edit, CancellationToken token, string? source = null)
    {
        // A GitHub link makes the plugin follow that file; "" stops it; null keeps what it had.
        using var content = new StringContent(JsonSerializer.Serialize(new { name, title, description, extension, code, author, note, edit, source }, Json),
            Encoding.UTF8, "application/json");
        using var response = await SendAsync(HttpMethod.Post, "api/plugins", content, token);
        var result = await response.Content.ReadFromJsonAsync<PublishResult>(Json, token);
        return result?.Version is > 0 ? result.Version.Value : 1;
    }

    /// <summary>The code of the .ts/.js file a GitHub (or GitHub Enterprise) link points at, read by the relay.</summary>
    internal async Task<(string Code, string Extension)> FetchPluginSourceAsync(string link, CancellationToken token)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { source = link }, Json), Encoding.UTF8, "application/json");
        using var response = await SendAsync(HttpMethod.Post, "api/plugins/source", content, token);
        var body = await response.Content.ReadFromJsonAsync<PluginSourceText>(Json, token);
        return body?.Content is { Length: > 0 } code ? (code, body.Extension is ".js" ? ".js" : ".ts") : throw new SkillStoreException("깃허브 파일을 받지 못했습니다.");
    }

    private sealed record PluginSourceText(string? Content, string? Extension);

    /// <summary>Takes a plugin out of the store (the relay keeps it 30 days in its .trash).</summary>
    internal async Task DeletePluginAsync(string name, CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Delete, "api/plugins/" + Uri.EscapeDataString(name), null, token);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, new Uri(_base, path)) { Content = content };
        request.Headers.Add("X-TerminalNotes-Key", _key);
        // Private skills are only for their author: the relay lists and serves them by this account.
        request.Headers.TryAddWithoutValidation("X-TerminalNotes-User", Uri.EscapeDataString(_user));
        request.Headers.TryAddWithoutValidation("User-Agent", Services.AnnouncementRelay.UserAgent);
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, token); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
        { throw new SkillStoreException($"스토어({_base.Authority})에 연결하지 못했습니다. 서버 PC에서 중계 서버가 켜져 있는지(서버 시작) 확인하세요."); }
        if (response.IsSuccessStatusCode) return response;
        string detail = "";
        try
        {
            string text = await response.Content.ReadAsStringAsync(token);
            detail = text.StartsWith('"') ? JsonSerializer.Deserialize<string>(text) ?? "" : text;
        }
        catch (Exception exception) when (exception is JsonException or HttpRequestException) { }
        var status = response.StatusCode;
        response.Dispose();
        throw new SkillStoreException(status switch
        {
            HttpStatusCode.Unauthorized => "스토어 키가 맞지 않습니다. 앱을 다시 설치하거나 관리자에게 문의하세요.",
            HttpStatusCode.TooManyRequests => "너무 자주 공유했습니다. 잠시 후 다시 시도하세요.",
            HttpStatusCode.NotFound => "스토어에서 찾지 못했습니다. 목록을 새로 고친 뒤 다시 시도하세요.",
            HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.Forbidden when detail.Length is > 0 and < 300 => detail,
            // A gateway between the app and the relay answered: the relay itself was not reached.
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.ProxyAuthenticationRequired =>
                $"중계 서버({_base.Authority})에 닿지 못했습니다(응답 {(int)status}). 서버 PC에서 '서버 시작'으로 켜져 있는지, 주소가 맞는지 확인하세요.",
            _ => $"스토어 응답 오류({(int)status})."
        }, status);
    }

    private sealed record VersionDownload(string? Name, int? Version, int? LatestVersion, string? Title, string? Description, string? Author,
        DateTimeOffset? PublishedAt, DateTimeOffset? UpdatedAt, string? Sha256, string? Note, string Content, string? Body);
    private sealed record PublishResult(string? Name, string? Sha256, int? Version, bool? Changed);
}

internal enum SkillMode { Store, Local }

/// <summary>Skills in OpenCode's global skills folder; the ones from the store carry its marker and their layers.</summary>
internal static class InstalledSkills
{
    private const string MarkerFile = ".terminal-notes-store.json";
    private const string StateFolder = ".terminal-notes";
    private const int BackupsKept = 20;
    internal const int CustomMaxLength = 50_000;
    internal const string CustomMarker = "<!-- terminal-notes: 아래는 이 PC에서 덧붙인 지침입니다 -->";
    internal const string CustomHeading = "## 이 PC에서 덧붙인 지침";
    internal const string CustomLead = "위 내용과 다르면 아래 지침을 따른다.";
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    internal sealed record Entry(string Name, string? Title, string? Sha256, bool FromStore)
    {
        /// <summary>The applied store version; 0 when an earlier app applied it and the version is not known yet.</summary>
        public int Version { get; init; }
        public SkillMode Mode { get; init; }
        public bool Applied { get; init; } = true;
        public int CustomLines { get; init; }
        public bool HasLocalText { get; init; }
        /// <summary>Why the skill became "내가 직접 관리" on its own (SKILL.md edited by hand).</summary>
        public string? Notice { get; init; }
    }

    private sealed record Marker(string Name, string Title, string Sha256, DateTimeOffset InstalledAt, int Version = 0);

    private sealed record State(string Name, string Title, string Author, int Version, string BaseSha256, SkillMode Mode,
        bool Applied, string WrittenSha256, DateTimeOffset UpdatedAt, string? Notice = null);

    // Where skills go: OpenCode's global folder, or a task's own while that task's skills are open (one window at a time: the
    // one that opens them is modal).
    private static string? _scope;
    internal static string DirectoryPath => _scope ?? GlobalDirectory;

    /// <summary>A task's own skills: OpenCode reads &lt;project&gt;\.opencode\skills\&lt;name&gt;\SKILL.md for sessions in that folder.</summary>
    internal static string ProjectDirectory(string project) => Path.Combine(project, ".opencode", "skills");

    internal static IDisposable Scope(string directory)
    {
        string? previous = _scope;
        _scope = Path.GetFullPath(directory);
        return new ScopeExit(() => _scope = previous);
    }

    /// <summary>The global skills, while a task's are open.</summary>
    internal static Dictionary<string, Entry> ListGlobal()
    {
        string? previous = _scope;
        _scope = null;
        try { return List(); }
        finally { _scope = previous; }
    }

    private sealed class ScopeExit(Action exit) : IDisposable
    {
        public void Dispose() => exit();
    }

    // OpenCode reads <XDG_CONFIG_HOME or ~/.config>/opencode/skills/<name>/SKILL.md when it starts; tests point this elsewhere.
    internal static string GlobalDirectory => AppContext.GetData("TerminalNotes.SkillsDirectory") is string test
        ? Path.GetFullPath(test)
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
            ? config : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "opencode", "skills");

    // OpenCode's skill name rule, which is also the folder name.
    internal static bool ValidName(string name) => name.Length is > 0 and <= 64 && Regex.IsMatch(name, "^[a-z0-9]+(?:-[a-z0-9]+)*$");

    private static string SkillFolder(string name) => Path.Combine(DirectoryPath, name);
    private static string StateDirectory(string name) => Path.Combine(DirectoryPath, StateFolder, name);
    private static string LayerPath(string name, string file) => Path.Combine(StateDirectory(name), file);

    internal static Dictionary<string, Entry> List()
    {
        var installed = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (!Directory.Exists(DirectoryPath)) return installed;
        foreach (string folder in Directory.EnumerateDirectories(DirectoryPath))
        {
            string name = Path.GetFileName(folder);
            if (!ValidName(name) || !File.Exists(Path.Combine(folder, "SKILL.md"))) continue;
            if (ReadMarker(folder) is not { } marker)
            {
                installed[name] = new Entry(name, null, null, FromStore: false);
                continue;
            }
            if (Sync(name, marker) is { } state) installed[name] = ToEntry(state);
        }
        // Removed store skills whose additions (or own text) are kept for the next apply.
        string states = Path.Combine(DirectoryPath, StateFolder);
        if (Directory.Exists(states))
            foreach (string folder in Directory.EnumerateDirectories(states))
            {
                string name = Path.GetFileName(folder);
                if (!ValidName(name) || installed.ContainsKey(name) || ReadState(name) is not { } kept) continue;
                if (kept.Applied) kept = WriteState(kept with { Applied = false });
                var entry = ToEntry(kept);
                if (entry.CustomLines > 0 || entry.HasLocalText) installed[name] = entry;
            }
        return installed;
    }

    private static Entry ToEntry(State state)
    {
        string custom = ReadLayer(state.Name, "custom.md");
        return new Entry(state.Name, state.Title, state.BaseSha256, FromStore: true)
        {
            Version = state.Version, Mode = state.Mode, Applied = state.Applied, Notice = state.Notice,
            CustomLines = custom.Length == 0 ? 0 : custom.Split('\n').Length,
            HasLocalText = ReadLayer(state.Name, "local.md").Length > 0
        };
    }

    /// <summary>Applies one store version: SKILL.md becomes that version plus my additions (which stay as they were).</summary>
    internal static void Apply(StoreSkillContent content)
    {
        string name = content.Name;
        if (!ValidName(name)) throw new InvalidDataException("스킬 이름이 올바르지 않습니다.");
        string folder = SkillFolder(name);
        var marker = Directory.Exists(folder) ? ReadMarker(folder) : null;
        if (Directory.Exists(folder) && File.Exists(Path.Combine(folder, "SKILL.md")) && marker is null)
            throw new IOException("같은 이름의 스킬이 이 PC에 이미 있습니다(직접 만든 스킬). 그 스킬은 그대로 두었습니다.");
        // Take over hand edits first, so applying never overwrites what the user wrote.
        var previous = marker is not null ? Sync(name, marker) : ReadState(name);
        if (previous is { Mode: SkillMode.Local, Applied: true } && File.Exists(Path.Combine(folder, "SKILL.md")))
            Backup(name, File.ReadAllText(Path.Combine(folder, "SKILL.md"), Utf8));
        string baseText = Normalize(content.Content);
        WriteLayer(name, "base.md", baseText);
        string composed = Compose(baseText, ReadLayer(name, "custom.md"));
        Directory.CreateDirectory(folder);
        WriteAtomically(Path.Combine(folder, "SKILL.md"), composed);
        WriteAtomically(Path.Combine(folder, MarkerFile), JsonSerializer.Serialize(
            new Marker(name, content.Version.Title, content.Version.Sha256, DateTimeOffset.UtcNow, content.Version.Version)));
        WriteState(new State(name, content.Version.Title, content.Version.Author, content.Version.Version, Sha(baseText),
            SkillMode.Store, Applied: true, Sha(composed), DateTimeOffset.UtcNow));
        RaiseChanged();
    }

    /// <summary>Earlier callers: applies content of an unknown version.</summary>
    internal static void Install(StoreSkill skill, string content) => Apply(new StoreSkillContent(skill.Name,
        new StoreSkillVersion(skill.Version, skill.Title, skill.Description, skill.Author, skill.UpdatedAt, skill.Sha256, null),
        skill.Version, content, ""));

    /// <summary>Saves my additions; an applied store-mode skill is rewritten at once (OpenCode reads it at the next start).</summary>
    internal static void SaveCustom(string name, string text)
    {
        var state = RequireState(name);
        text = Normalize(text).Trim('\n');
        if (text.Length > CustomMaxLength) throw new InvalidDataException($"내 지침은 {CustomMaxLength:N0}자까지 씁니다.");
        WriteLayer(name, "custom.md", text);
        if (!state.Applied || state.Mode != SkillMode.Store) return;
        WriteSkill(state, Compose(ReadLayer(name, "base.md"), text), SkillMode.Store);
    }

    /// <summary>"내가 전부 직접 관리": SKILL.md becomes my own text, and store versions no longer change it.</summary>
    internal static void SaveLocal(string name, string text)
    {
        var state = RequireState(name);
        text = Normalize(text).TrimEnd('\n') + "\n";
        if (!HasFrontmatter(text, name))
            throw new InvalidDataException($"맨 위 머리말(---, name: {name}, description: …, ---)은 지우지 마세요. OpenCode가 스킬을 읽지 못합니다.");
        if (text.Length > CustomMaxLength * 4) throw new InvalidDataException("내용이 너무 깁니다.");
        WriteLayer(name, "local.md", text);
        if (state.Applied) WriteSkill(state, text, SkillMode.Local);
        else WriteState(state with { Mode = SkillMode.Local, Notice = null });
    }

    /// <summary>Back from "내가 직접 관리" to the applied store version plus my additions; my own text stays kept.</summary>
    internal static void UseStoreVersion(string name)
    {
        var state = RequireState(name);
        string baseText = ReadLayer(name, "base.md");
        if (baseText.Length == 0) throw new IOException("적용한 스토어 버전을 찾지 못했습니다. 스토어에서 버전을 골라 적용하세요.");
        if (state.Applied && File.Exists(Path.Combine(SkillFolder(name), "SKILL.md")))
            Backup(name, File.ReadAllText(Path.Combine(SkillFolder(name), "SKILL.md"), Utf8));
        string composed = Compose(baseText, ReadLayer(name, "custom.md"));
        if (state.Applied) WriteSkill(state, composed, SkillMode.Store);
        else WriteState(state with { Mode = SkillMode.Store, Notice = null });
    }

    internal static void Remove(string name)
    {
        string folder = SkillFolder(name);
        if (!ValidName(name) || !Directory.Exists(folder)) return;
        if (ReadMarker(folder) is not { } marker) throw new IOException("스토어에서 적용한 스킬이 아니어서 지우지 않았습니다.");
        // Hand edits become my additions or my own text before the folder goes; both stay for the next apply.
        var state = Sync(name, marker);
        Directory.Delete(folder, recursive: true);
        if (state is not null) WriteState(state with { Applied = false });
        RaiseChanged();
    }

    internal static string? ReadContent(string name)
    {
        string path = Path.Combine(SkillFolder(name), "SKILL.md");
        return ValidName(name) && File.Exists(path) ? File.ReadAllText(path, Utf8) : null;
    }

    internal static string ReadCustom(string name) => ValidName(name) ? ReadLayer(name, "custom.md") : "";
    internal static string ReadLocal(string name) => ValidName(name) ? ReadLayer(name, "local.md") : "";
    internal static string ReadBase(string name) => ValidName(name) ? ReadLayer(name, "base.md") : "";
    internal static string BackupDirectory(string name) => Path.Combine(StateDirectory(name), "backups");

    /// <summary>SKILL.md for a store version with my additions below it.</summary>
    internal static string Compose(string baseText, string custom)
    {
        string body = Normalize(baseText).TrimEnd('\n');
        string mine = Normalize(custom).Trim('\n');
        return mine.Length == 0 ? body + "\n"
            : body + "\n\n" + CustomMarker + "\n" + CustomHeading + "\n\n" + CustomLead + "\n\n" + mine + "\n";
    }

    /// <summary>My additions read back from a SKILL.md, or null when the store part itself was changed.</summary>
    internal static string? ReadCustomFrom(string skill, string baseText)
    {
        string text = Normalize(skill), body = Normalize(baseText).TrimEnd('\n');
        if (!text.StartsWith(body, StringComparison.Ordinal)) return null;
        string rest = text[body.Length..];
        int marker = rest.IndexOf(CustomMarker, StringComparison.Ordinal);
        if (marker < 0) return rest.Trim('\n', ' ', '\t').Length == 0 ? "" : null;
        if (rest[..marker].Trim('\n', ' ', '\t').Length != 0) return null;
        string mine = rest[(marker + CustomMarker.Length)..].TrimStart('\n');
        if (mine.StartsWith(CustomHeading, StringComparison.Ordinal)) mine = mine[CustomHeading.Length..].TrimStart('\n');
        if (mine.StartsWith(CustomLead, StringComparison.Ordinal)) mine = mine[CustomLead.Length..].TrimStart('\n');
        return mine.Trim('\n');
    }

    internal static bool HasFrontmatter(string text, string name)
    {
        text = Normalize(text);
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) return false;
        int end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return false;
        string[] lines = text[4..end].Split('\n');
        return lines.Any(line => line.Trim() == "name: " + name) && lines.Any(line => line.TrimStart().StartsWith("description:", StringComparison.Ordinal));
    }

    // The store skill's state, after taking over any change made to its SKILL.md outside the app.
    private static State? Sync(string name, Marker marker)
    {
        string path = Path.Combine(SkillFolder(name), "SKILL.md");
        var state = ReadState(name);
        if (!File.Exists(path))
            return state is { Applied: true } ? WriteState(state with { Applied = false }) : state;
        string file = Normalize(File.ReadAllText(path, Utf8));
        if (state is null)
        {
            // Applied by the app before it kept layers: the file is that store version as it was.
            WriteLayer(name, "base.md", file);
            return WriteState(new State(name, marker.Title, "", marker.Version, Sha(file), SkillMode.Store, Applied: true, Sha(file),
                DateTimeOffset.UtcNow));
        }
        if (Sha(file) == state.WrittenSha256)
            return state.Applied ? state : WriteState(state with { Applied = true });
        if (state.Mode == SkillMode.Store && ReadCustomFrom(file, ReadLayer(name, "base.md")) is { } custom)
        {
            WriteLayer(name, "custom.md", custom);
            return WriteState(state with { Applied = true, WrittenSha256 = Sha(file) });
        }
        // The store part itself was edited (or the skill is already mine): the file is now my own text.
        WriteLayer(name, "local.md", file);
        return WriteState(state with
        {
            Mode = SkillMode.Local, Applied = true, WrittenSha256 = Sha(file),
            Notice = state.Mode == SkillMode.Store ? "SKILL.md를 직접 고쳐서 내가 직접 관리로 바꿨습니다" : state.Notice
        });
    }

    private static void WriteSkill(State state, string text, SkillMode mode)
    {
        string folder = SkillFolder(state.Name);
        Directory.CreateDirectory(folder);
        WriteAtomically(Path.Combine(folder, "SKILL.md"), text);
        WriteState(state with { Mode = mode, WrittenSha256 = Sha(text), UpdatedAt = DateTimeOffset.UtcNow, Notice = null });
        RaiseChanged();
    }

    /// <summary>A SKILL.md in this skills folder was written or removed: running OpenCode terminals reload their skills
    /// (the app resets each terminal's OpenCode instance once it is idle). The argument is the skills folder.</summary>
    internal static event Action<string>? Changed;
    private static void RaiseChanged() => Changed?.Invoke(DirectoryPath);
    /// <summary>A change that is not a SKILL.md write (a global skill turned off for one task) in this skills folder.</summary>
    internal static void NotifyChanged(string folder) => Changed?.Invoke(folder);
    internal const string AppliedNow = "열린 OpenCode 터미널에도 바로 반영됩니다(작업 중인 터미널은 그 작업이 끝나는 대로).";

    private static State RequireState(string name)
    {
        if (!ValidName(name)) throw new InvalidDataException("스킬 이름이 올바르지 않습니다.");
        string folder = SkillFolder(name);
        if (Directory.Exists(folder) && ReadMarker(folder) is { } marker) return Sync(name, marker) ?? throw new IOException("스킬 상태를 읽지 못했습니다.");
        return ReadState(name) ?? throw new IOException("스토어에서 적용한 스킬이 아닙니다.");
    }

    private static void Backup(string name, string text)
    {
        string folder = BackupDirectory(name);
        Directory.CreateDirectory(folder);
        WriteAtomically(Path.Combine(folder, $"SKILL-{DateTime.Now:yyyyMMdd-HHmmss-fff}.md"), Normalize(text));
        foreach (string old in Directory.GetFiles(folder, "SKILL-*.md").OrderByDescending(path => path, StringComparer.Ordinal).Skip(BackupsKept))
            File.Delete(old);
    }

    private static State? ReadState(string name)
    {
        string path = LayerPath(name, "state.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<State>(File.ReadAllText(path, Utf8)); }
        catch (Exception exception) when (exception is JsonException or IOException) { return null; }
    }

    private static State WriteState(State state)
    {
        Directory.CreateDirectory(StateDirectory(state.Name));
        TryHide(Path.Combine(DirectoryPath, StateFolder));
        WriteAtomically(LayerPath(state.Name, "state.json"), JsonSerializer.Serialize(state));
        return state;
    }

    private static string ReadLayer(string name, string file)
    {
        string path = LayerPath(name, file);
        try { return File.Exists(path) ? Normalize(File.ReadAllText(path, Utf8)) : ""; }
        catch (IOException) { return ""; }
    }

    private static void WriteLayer(string name, string file, string text)
    {
        Directory.CreateDirectory(StateDirectory(name));
        WriteAtomically(LayerPath(name, file), Normalize(text));
    }

    private static void TryHide(string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            if (info.Exists && (info.Attributes & FileAttributes.Hidden) == 0) info.Attributes |= FileAttributes.Hidden;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static string Normalize(string text) => text.TrimStart('﻿').ReplaceLineEndings("\n");

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(Normalize(text))));

    private static Marker? ReadMarker(string folder)
    {
        string path = Path.Combine(folder, MarkerFile);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<Marker>(File.ReadAllText(path, Utf8)); }
        catch (Exception exception) when (exception is JsonException or IOException) { return null; }
    }

    private static void WriteAtomically(string path, string text)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, Utf8);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>Browse the store as cards and turn a skill on or off with its switch: for every task (OpenCode's global skills), or,
/// opened from a task, for that task alone (its own .opencode\skills). A search box and category tabs narrow the cards, a star
/// keeps a skill at the top, and the rest (its content, versions, my own instructions, a new version of my own skill, deleting
/// for the store's keepers) is in the card's ⋯ menu, which a right click also opens.</summary>
internal sealed partial class SkillStoreDialog : Window
{
    private const string NoStore = "이 빌드에는 스토어 주소가 없습니다(.env의 RELAY_HOST).";
    private readonly SkillStoreClient? _store;
    // A task's folder when its own skills are open; null for the global ones.
    private readonly string? _project;
    // The tasks whose own skills are brought up to date with the store when the window opens.
    private readonly IReadOnlyList<string> _tasks;
    private readonly IDisposable? _scope;
    // Cards in parts by category (2026-11-11 사용자: "스킬이 너무 많아지니 구분도 안되고 카테고리도 검색창하고 같은행이라 보기가
    // 불편해"): the search box alone on top, 전체 · 사용 중 · each category with its count on the left, and the cards under a heading
    // per category, three a row in the usual window (more when wider, fewer when narrow), all as tall as the tallest. In a part the
    // applied ones come first, then the starred, then the latest change.
    private readonly StackPanel _sections = new() { Margin = new Thickness(6, 0, 18, 10) };
    private int _columns = 3;
    private readonly TextBox _search = new() { Tag = "검색", MaxLength = 80, Padding = new Thickness(11, 6, 11, 6), FontSize = 13 };
    private readonly StackPanel _categories = new() { Orientation = Orientation.Vertical };
    private readonly Dictionary<string, TextBlock> _categoryCounts = new(StringComparer.OrdinalIgnoreCase);
    internal const string AllCategory = "전체", AppliedCategory = "사용 중";
    // One line kept for what just happened, so the list does not jump when it comes and goes.
    private readonly TextBlock _status = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MinHeight = 18, Margin = new Thickness(24, 0, 24, 14) };
    private readonly StackPanel _empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(24, 0, 24, 40), Visibility = Visibility.Collapsed };
    private readonly TextBlock _emptyText = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 420 };
    private readonly Button _retry = new() { Content = "다시 시도", MinWidth = 104, HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 18, 0, 0) };
    private readonly Button _refresh = new() { Content = StoreLook.Glyph(StoreLook.RefreshGlyph, 14), ToolTip = "새로 고침 (F5)", Margin = new Thickness(0, 0, 4, 0) };
    private readonly Button _share = new() { Content = "+ 스킬 공유", MinHeight = 30, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0) };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _favorites = SkillFavorites.Load();
    private IReadOnlyList<StoreSkill> _skills = [];
    private string _category = "";
    private string? _failure;
    private bool _busy;

    /// <summary>Asks before replacing my own text with a store version, or before deleting; tests answer it.</summary>
    internal Func<string, bool> Confirm { get; set; }
    /// <summary>Opens a 출처 link in the browser; tests take it instead.</summary>
    internal Action<Uri> OpenLink { get; set; } = link =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });

    internal SkillStoreDialog(SkillStoreClient? store, string? project = null, string? projectName = null, IReadOnlyList<string>? tasks = null)
    {
        _store = store;
        _project = project;
        _tasks = tasks ?? [];
        // A task's skills live in its own folder: every skill operation of this window goes there while it is open.
        if (project is not null) _scope = InstalledSkills.Scope(InstalledSkills.ProjectDirectory(project));
        Confirm = message => AppMessageDialog.Confirm(this, "스킬", message, "확인");
        // Named like its title bar button (스킬, beside 플러그인).
        string caption = project is null ? "스킬" : "태스크 스킬";
        Title = project is null ? caption : $"{caption} · {projectName ?? Path.GetFileName(project)}";
        var screen = SystemParameters.WorkArea;
        Width = Math.Min(1080, screen.Width - 40); Height = Math.Min(740, screen.Height - 40); MinWidth = 520; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        _share.SetResourceReference(StyleProperty, "OutlineButton");
        _share.Visibility = store is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(_share, "스킬 공유하기");
        _refresh.SetResourceReference(StyleProperty, "IconButton");
        _refresh.Visibility = _share.Visibility;
        AutomationProperties.SetName(_refresh, "새로 고침");
        AutomationProperties.SetName(_search, "스킬 검색");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _emptyText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        _empty.Children.Add(_emptyText);
        _empty.Children.Add(_retry);
        // The search box alone on top; the categories on the left; the cards in parts by category.
        var tools = new Border { Margin = new Thickness(24, 14, 24, 10), Child = _search };
        var side = new ScrollViewer { Content = _categories, Width = 184, Margin = new Thickness(24, 2, 6, 14),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var center = new Grid();
        var scroll = new ScrollViewer { Content = _sections, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        // About 330 wide a card: three in the usual window.
        scroll.SizeChanged += (_, e) =>
        {
            _columns = Math.Max(1, (int)((e.NewSize.Width - 24) / 330));
            foreach (var grid in _sections.Children.OfType<UniformGrid>()) grid.Columns = _columns;
        };
        center.Children.Add(scroll);
        center.Children.Add(_empty);
        var main = new DockPanel();
        DockPanel.SetDock(side, Dock.Left);
        main.Children.Add(side);
        main.Children.Add(center);
        var body = new DockPanel();
        DockPanel.SetDock(tools, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        body.Children.Add(tools);
        body.Children.Add(_status);
        body.Children.Add(main);
        StoreLook.Dress(this, caption, body, project is null ? null : projectName ?? Path.GetFileName(project), _refresh, _share);
        _share.Click += async (_, _) => await ShareAsync(null);
        _refresh.Click += async (_, _) => await RefreshAsync();
        _retry.Click += async (_, _) => await RefreshAsync();
        _search.TextChanged += (_, _) => Render();
        KeyDown += async (_, e) => { if (e.Key == Key.F5) await RefreshAsync(); };
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            _scope?.Dispose();
        };
    }

    internal string StatusText => _status.Text;
    internal bool ShowsEmpty => _empty.Visibility == Visibility.Visible;
    internal string EmptyText => _emptyText.Text;
    internal string SearchText { get => _search.Text; set => _search.Text = value; }
    internal IReadOnlyList<string> Categories => _categories.Children.OfType<RadioButton>().Select(tab => (string)tab.Tag).ToList();
    /// <summary>"" (전체), <see cref="AppliedCategory"/> or a category's name</summary>
    internal string Category
    {
        get => _category;
        set
        {
            _category = value == AllCategory ? "" : value;
            foreach (var tab in _categories.Children.OfType<RadioButton>())
                tab.IsChecked = string.Equals(FilterOf((string)tab.Tag), _category, StringComparison.OrdinalIgnoreCase);
            Render();
        }
    }

    private static string FilterOf(string shown) => shown == AllCategory ? "" : shown;

    internal async Task RefreshAsync()
    {
        if (_busy) return;
        SetBusy(true, "불러오는 중…");
        string? failure = null;
        try { _skills = _store is null ? [] : await _store.ListAsync(_lifetime.Token); }
        catch (SkillStoreException exception) { failure = exception.Message; }
        catch (OperationCanceledException) { return; }
        _failure = failure;
        SetBusy(false, "");
        if (failure is null)
        {
            await UpdateLinkedAsync();
            await TakeOutWithdrawnAsync();
        }
        RenderCategories();
        Render();
    }

    private async Task TakeOutWithdrawnAsync()
    {
        if (_store is null) return;
        try
        {
            var removed = await SkillWithdrawals.SweepAsync(_store, _project is null ? _tasks : _tasks.Append(_project), _lifetime.Token);
            if (removed.Count > 0) ShowStatus(SkillWithdrawals.Notice(removed));
        }
        catch (Exception exception) when (exception is SkillStoreException or OperationCanceledException or JsonException) { }
    }

    // Skills that follow a GitHub file stay current here: a newer store version of one applied as the store's is taken at once.
    private async Task UpdateLinkedAsync()
    {
        if (_store is null) return;
        var installed = TryList();
        int updated = 0;
        foreach (var skill in _skills.Where(skill => skill.Source is { Length: > 0 }))
        {
            if (installed.GetValueOrDefault(skill.Name) is not { FromStore: true, Applied: true, Mode: SkillMode.Store } local || skill.Version <= local.Version) continue;
            try
            {
                InstalledSkills.Apply(await _store.GetVersionAsync(skill.Name, skill.Version, apply: true, _lifetime.Token));
                updated++;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception exception) when (exception is SkillStoreException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { }
        }
        if (updated > 0) ShowStatus($"깃허브에서 바뀐 스킬 {updated}개를 업데이트했습니다. " + InstalledSkills.AppliedNow);
    }

    // The store's categories in 가나다 order with 기타 (shared without one) last.
    private List<string> CategoryNames() => _skills.Select(skill => skill.CategoryName).Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name == StoreSkill.OtherCategory).ThenBy(name => name, StringComparer.CurrentCulture).ToList();

    // On the left: 전체, 사용 중, a line, then each category; every row with its count (filled in by Render).
    private void RenderCategories()
    {
        var names = CategoryNames();
        if (_category.Length > 0 && _category != AppliedCategory && _category != MySkills.Category && !names.Contains(_category, StringComparer.OrdinalIgnoreCase)) _category = "";
        _categories.Children.Clear();
        _categoryCounts.Clear();
        void Row(string shown)
        {
            var name = new TextBlock { Text = shown, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var count = new TextBlock { FontSize = 12.5, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            count.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var content = new DockPanel();
            DockPanel.SetDock(count, Dock.Right);
            content.Children.Add(count);
            content.Children.Add(name);
            var tab = new RadioButton { Content = content, Tag = shown, GroupName = "SkillCategory",
                IsChecked = string.Equals(FilterOf(shown), _category, StringComparison.OrdinalIgnoreCase) };
            tab.SetResourceReference(StyleProperty, "StoreSideItem");
            AutomationProperties.SetName(tab, shown);
            tab.Checked += (_, _) => { _category = FilterOf(shown); Render(); };
            _categories.Children.Add(tab);
            _categoryCounts[shown] = count;
        }
        Row(AllCategory);
        Row(AppliedCategory);
        var line = new Border { Height = 1, Margin = new Thickness(10, 8, 10, 8) };
        line.SetResourceReference(Border.BackgroundProperty, "Line");
        _categories.Children.Add(line);
        foreach (string name in names) Row(name);
        // 나만의 스킬 (learned or written here) apart from the store's.
        var mine = new Border { Height = 1, Margin = new Thickness(10, 8, 10, 8) };
        mine.SetResourceReference(Border.BackgroundProperty, "Line");
        _categories.Children.Add(mine);
        Row(MySkills.Category);
    }

    private void Render()
    {
        _sections.Children.Clear();
        Dictionary<string, InstalledSkills.Entry> installed;
        HashSet<string> global = [], off = [];
        try
        {
            installed = InstalledSkills.List();
            if (_project is not null)
            {
                global = InstalledSkills.ListGlobal().Values.Where(entry => entry.Applied).Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
                off = Services.TaskSkillSwitches.Off(_project).ToHashSet(StringComparer.Ordinal);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            installed = [];
            ShowStatus("적용한 스킬 폴더를 읽지 못했습니다 · " + exception.Message);
        }
        // Every store skill with how it stands here.
        var rows = _skills.Select(skill =>
        {
            installed.TryGetValue(skill.Name, out var local);
            // A global skill (not this task's own copy) is on here unless this task turned it off.
            bool fromGlobal = global.Contains(skill.Name) && local is not { Applied: true };
            bool globalOff = fromGlobal && off.Contains(skill.Name);
            return (Skill: skill, Local: local, FromGlobal: fromGlobal, GlobalOff: globalOff, Applied: local is { Applied: true } || fromGlobal && !globalOff);
        }).ToList();
        // Applied (or kept) skills that are no longer in the store, or while it is unreachable, stay manageable.
        var missing = installed.Values.Where(entry => entry.FromStore && _skills.All(skill => skill.Name != entry.Name)).OrderBy(entry => entry.Name).ToList();
        foreach (var (shown, count) in _categoryCounts)
            count.Text = (shown == AllCategory ? _skills.Count
                : shown == MySkills.Category ? MySkills.List().Count
                : shown == AppliedCategory ? rows.Count(row => row.Applied) + missing.Count(entry => entry.Applied)
                : _skills.Count(skill => string.Equals(skill.CategoryName, shown, StringComparison.OrdinalIgnoreCase))).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string query = _search.Text.Trim();
        if (_category == MySkills.Category)
        {
            RenderMine(query);
            return;
        }
        bool Matches(StoreSkill skill) =>
            query.Length == 0 || new[] { skill.Title, skill.Description, skill.Summary, skill.Author, skill.CategoryName, skill.Name, skill.Origin }
                .Any(text => text?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true);
        var shownRows = rows.Where(row => Matches(row.Skill) && (_category.Length == 0 ||
            (_category == AppliedCategory ? row.Applied : string.Equals(row.Skill.CategoryName, _category, StringComparison.OrdinalIgnoreCase)))).ToList();
        // In a part: applied first, then starred, then the latest change.
        IEnumerable<Border> Cards(IEnumerable<(StoreSkill Skill, InstalledSkills.Entry? Local, bool FromGlobal, bool GlobalOff, bool Applied)> part) =>
            part.OrderByDescending(row => row.Applied).ThenByDescending(row => _favorites.Contains(row.Skill.Name)).ThenByDescending(row => row.Skill.ShownDate)
                .Select(row =>
                {
                    // Without a description of its own a skill's is its title, which the card already shows: then its first line of
                    // instructions (relay 2.3 and later).
                    string description = (row.Skill.Description ?? "").Trim();
                    if (string.Equals(description, (row.Skill.Title ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) description = (row.Skill.Summary ?? "").Trim();
                    return Card(row.Skill.Title ?? row.Skill.Name, description, row.Skill.Name, row.Local, row.Skill, row.FromGlobal, row.GlobalOff);
                });
        if (_category.Length == 0)
            foreach (string name in CategoryNames())
                AddPart(name, Cards(shownRows.Where(row => string.Equals(row.Skill.CategoryName, name, StringComparison.OrdinalIgnoreCase))).ToList());
        else AddPart(_category, Cards(shownRows).ToList());
        if (query.Length == 0 && (_category.Length == 0 || _category == AppliedCategory))
            AddPart("스토어 목록에 없음", missing.Where(entry => _category.Length == 0 || entry.Applied)
                .Select(local => Card(local.Title ?? local.Name, "스토어 목록에 없음", local.Name, local, null, false, false)).ToList());
        bool any = _sections.Children.OfType<UniformGrid>().Any(grid => grid.Children.Count > 0);
        _empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        string? problem = _store is null ? NoStore : _failure;
        if (!any)
        {
            _emptyText.Text = problem ?? (_skills.Count > 0 ? "찾는 스킬이 없습니다." : "아직 공유된 스킬이 없습니다.");
            _retry.Visibility = _store is not null && _failure is not null ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (problem is not null && _status.Text.Length == 0) ShowStatus(problem);
    }

    // A part: its heading (name · count) and its cards.
    private void AddPart(string name, IReadOnlyList<Border> cards)
    {
        if (cards.Count == 0) return;
        var heading = new TextBlock { Text = $"{name} · {cards.Count}", FontSize = 13.5, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(18, _sections.Children.Count == 0 ? 2 : 16, 18, 6) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        AutomationProperties.SetAutomationId(heading, "SkillPartHeading");
        var grid = new UniformGrid { Columns = _columns, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 4) };
        foreach (var card in cards) grid.Children.Add(card);
        _sections.Children.Add(heading);
        _sections.Children.Add(grid);
    }

    // A card: the title (a lock when only its author sees it, a link when it follows GitHub), the latest version's badge and the
    // star; a short description and the 출처 when it has one; at the bottom left the date (made, or last changed) and the author in
    // gold, at the right 업데이트 when the store has a newer version, ⋯ for everything else, and the switch. The whole card opens the
    // same menu on a right click.
    private Border Card(string title, string description, string name, InstalledSkills.Entry? local, StoreSkill? skill, bool global, bool globalOff)
    {
        bool applied = local is { Applied: true } || global && !globalOff, own = local is { FromStore: false };
        string? notice = local is { FromStore: true, Notice.Length: > 0 } ? local.Notice : null;
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock { Text = title, FontSize = 14.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 42, VerticalAlignment = VerticalAlignment.Center, ToolTip = name });
        var marks = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        if (skill is { Private: true }) marks.Children.Add(Mark(StoreLook.LockGlyph, "비공개 · 나만 보고 쓰는 스킬"));
        if (skill is { Source.Length: > 0 } linked)
            marks.Children.Add(Mark(StoreLook.LinkGlyph, "깃허브 파일을 따라 자동 업데이트\n" + linked.Source + (linked.SourceError is { Length: > 0 } error ? "\n" + error : "")));
        if (skill is not null) marks.Children.Add(VersionBadge(skill, local));
        Grid.SetColumn(marks, 1);
        head.Children.Add(marks);
        if (skill is not null)
        {
            var star = new ToggleButton { IsChecked = _favorites.Contains(name), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, -4, -6, 0) };
            star.SetResourceReference(StyleProperty, "StoreStar");
            AutomationProperties.SetName(star, "즐겨찾기 · " + title);
            star.Click += (_, _) =>
            {
                SkillFavorites.Set(_favorites, name, star.IsChecked == true);
                Render();
            };
            Grid.SetColumn(star, 2);
            head.Children.Add(star);
        }
        layout.Children.Add(head);
        if ((notice ?? description).Length > 0)
        {
            // The 출처 takes one of its three lines: cards in a row grow together, and all of them would for one 출처.
            var detail = new TextBlock { Text = notice ?? description, FontSize = 13, LineHeight = 19, TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = skill is { Origin.Length: > 0 } ? 38 : 57, Margin = new Thickness(0, 8, 0, 0) };
            detail.SetResourceReference(TextBlock.ForegroundProperty, notice is null ? "TextSecondary" : "Accent");
            Grid.SetRow(detail, 1);
            layout.Children.Add(detail);
        }
        if (skill is { Origin.Length: > 0 })
        {
            var origin = OriginLine(skill);
            Grid.SetRow(origin, 2);
            layout.Children.Add(origin);
        }
        var foot = new Grid { Margin = new Thickness(0, 10, -6, -4) };
        foot.ColumnDefinitions.Add(new ColumnDefinition());
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string byline = string.Join(" · ", new[] { skill?.ShownDate?.ToLocalTime().ToString("yyyy-MM-dd"), skill?.Author }.Where(part => !string.IsNullOrEmpty(part)));
        if (byline.Length > 0)
        {
            var meta = new TextBlock { Text = byline, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = skill!.Version > 1 ? "마지막 수정" : "처음 공유" };
            meta.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            foot.Children.Add(meta);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (!global && local is { FromStore: true, Applied: true, Mode: SkillMode.Store } && skill is not null && skill.Version > local.Version)
        {
            var update = new Button { Content = "업데이트", ToolTip = $"v{local.Version} → v{skill.Version}", VerticalAlignment = VerticalAlignment.Center };
            update.SetResourceReference(StyleProperty, "AccentButton");
            update.Click += async (_, _) => await ApplyAsync(skill, null);
            actions.Children.Add(update);
        }
        var menu = Menu(name, title, local, skill);
        var more = new Button { Content = StoreLook.Glyph(StoreLook.MoreGlyph, 16), Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        more.SetResourceReference(StyleProperty, "IconButton");
        AutomationProperties.SetName(more, "더 보기 · " + title);
        more.Click += (_, _) => StoreLook.Open(menu, more);
        actions.Children.Add(more);
        // A disabled switch says why; a global skill's says it is global (off turns it off for this task only).
        var toggle = new ToggleButton { IsChecked = applied, IsEnabled = !own && (global || applied || skill is not null), Margin = new Thickness(8, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = own ? "이 PC에서 직접 만든 같은 이름의 스킬이 있습니다." : global ? (globalOff ? "전역 스킬 · 이 태스크에서는 끔" : "전역 스킬 · 끄면 이 태스크에서만 빠집니다") : null };
        toggle.SetResourceReference(StyleProperty, "StoreSwitch");
        ToolTipService.SetShowOnDisabled(toggle, true);
        AutomationProperties.SetName(toggle, (_project is null ? "적용 · " : "이 태스크에 적용 · ") + title);
        // The switch moves at once; the list is drawn again from what this PC then has.
        toggle.Click += async (_, _) =>
        {
            if (global) SetGlobalOff(name, title, off: applied);
            else if (applied) await RemoveAsync(name, title);
            else if (skill is not null) await ApplyAsync(skill, null);
        };
        actions.Children.Add(toggle);
        Grid.SetColumn(actions, 1);
        foot.Children.Add(actions);
        Grid.SetRow(foot, 3);
        layout.Children.Add(foot);
        var card = new Border { Child = layout, Tag = name, CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14, 14, 10),
            Margin = new Thickness(6), MinHeight = 136, BorderThickness = new Thickness(1), ContextMenu = menu };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "ButtonLine");
        card.MouseEnter += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, "ButtonLineHover");
        card.MouseLeave += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, "ButtonLine");
        return card;
    }

    // 출처 and what it says: a link opens in the browser, a user id is only shown.
    private TextBlock OriginLine(StoreSkill skill)
    {
        var line = new TextBlock { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0),
            ToolTip = skill.OriginLink?.AbsoluteUri ?? skill.Origin };
        line.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        line.Inlines.Add("출처  ");
        var text = new Documents.Run(Regex.Replace(skill.Origin!, "^https?://", "", RegexOptions.IgnoreCase));
        if (skill.OriginLink is not { } link)
        {
            text.SetResourceReference(Documents.TextElement.ForegroundProperty, "TextSecondary");
            line.Inlines.Add(text);
            return line;
        }
        var open = new Documents.Hyperlink(text);
        open.SetResourceReference(Documents.TextElement.ForegroundProperty, "Accent");
        AutomationProperties.SetName(open, "출처 열기 · " + link.AbsoluteUri);
        open.Click += (_, _) =>
        {
            try { OpenLink(link); }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            { ShowStatus("링크를 열지 못했습니다 · " + exception.Message); }
        };
        line.Inlines.Add(open);
        return line;
    }

    // A global skill turned off (or on again) for this task only: OpenCode hides it from this task's next sessions.
    private void SetGlobalOff(string name, string title, bool off)
    {
        if (_busy || _project is null) return;
        try
        {
            Services.TaskSkillSwitches.Set(_project, name, off);
            InstalledSkills.NotifyChanged(InstalledSkills.ProjectDirectory(_project));
            ShowStatus($"‘{title}’ 전역 스킬을 이 태스크에서 {(off ? "껐습니다" : "다시 켰습니다")}. " + InstalledSkills.AppliedNow);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowStatus("이 태스크의 스킬 설정을 저장하지 못했습니다 · " + exception.Message);
        }
        Render();
    }

    // The latest version as a small pill left of the star; an applied other version says which one this PC has.
    private static Border VersionBadge(StoreSkill skill, InstalledSkills.Entry? local)
    {
        var text = new TextBlock { Text = $"v{skill.Version}", FontSize = 11, FontWeight = FontWeights.SemiBold };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var badge = new Border { Child = text, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Padding = new Thickness(7, 1, 7, 1),
            Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = local is { FromStore: true, Applied: true, Version: > 0 } && local.Version != skill.Version
                ? $"최신 v{skill.Version} · 이 PC는 v{local.Version} 적용 중" : $"최신 v{skill.Version}" };
        badge.SetResourceReference(Border.BackgroundProperty, "SurfaceElevated");
        badge.SetResourceReference(Border.BorderBrushProperty, "ButtonLine");
        AutomationProperties.SetName(badge, "버전 " + text.Text);
        return badge;
    }

    private static TextBlock Mark(string glyph, string tip)
    {
        var mark = StoreLook.Glyph(glyph, 12);
        mark.Margin = new Thickness(0, 0, 4, 0);
        mark.ToolTip = tip;
        mark.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return mark;
    }

    private ContextMenu Menu(string name, string title, InstalledSkills.Entry? local, StoreSkill? skill)
    {
        var menu = StoreLook.Menu();
        StoreLook.Item(menu, "내용 보기", async () => await ShowContentAsync(name, title, local is { Applied: true }));
        // Its author, or a store admin whoever wrote it, changes and deletes it. Who sees it is its author's alone.
        bool mine = skill is not null && _store is not null && string.Equals(skill.Author, Environment.UserName, StringComparison.OrdinalIgnoreCase);
        bool manage = mine || skill is not null && _store is not null && SkillStoreAdmins.Allowed;
        if (skill is not null && _store is not null)
        {
            if (skill.VersionCount > 1) StoreLook.Item(menu, "버전 기록", async () => await ShowVersionsAsync(skill, local));
            if (manage) StoreLook.Item(menu, "수정", async () => await ShareAsync(skill));
            if (mine) StoreLook.Item(menu, skill.Private ? "공개로 바꾸기" : "비공개로 바꾸기", async () => await SetPrivateAsync(skill, !skill.Private));
        }
        if (local is { FromStore: true }) StoreLook.Item(menu, "내 지침", async () => await CustomizeAsync(name, title));
        if (manage) StoreLook.Item(menu, "삭제", async () => await DeleteAsync(skill!));
        return menu;
    }

    /// <summary>Applies a version (the latest when null). My additions stay; my own full text is kept and backed up.</summary>
    internal async Task ApplyAsync(StoreSkill skill, int? version)
    {
        if (_busy || _store is null) return;
        var local = TryList().GetValueOrDefault(skill.Name);
        if (local is { FromStore: true, Applied: true, Mode: SkillMode.Local } &&
            !Confirm($"‘{skill.DisplayTitle}’은(는) 직접 관리 중입니다. 스토어 버전 + 내 지침으로 바꿀까요?\n직접 관리하던 내용은 보관됩니다."))
        {
            Render();
            return;
        }
        SetBusy(true, $"‘{skill.DisplayTitle}’ 적용 중…");
        string message;
        try
        {
            int number = version ?? skill.Version;
            var content = await _store.GetVersionAsync(skill.Name, number, apply: true, _lifetime.Token);
            InstalledSkills.Apply(content);
            message = $"‘{StoreSkill.Shown(content.Version.Title, content.Version.Author)}’ v{content.Version.Version}을(를) " +
                (_project is null ? "적용했습니다" : "이 태스크에 적용했습니다") + ". " + InstalledSkills.AppliedNow;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception exception) when (exception is SkillStoreException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { message = "적용하지 못했습니다 · " + exception.Message; }
        SetBusy(false, message);
        Render();
    }

    internal Task RemoveAsync(string name, string title)
    {
        if (_busy) return Task.CompletedTask;
        string message;
        try
        {
            InstalledSkills.Remove(name);
            message = $"‘{title}’을(를) 해제했습니다. " + InstalledSkills.AppliedNow;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { message = "해제하지 못했습니다 · " + exception.Message; }
        ShowStatus(message);
        Render();
        return Task.CompletedTask;
    }

    /// <summary>Its author shows a skill to everyone or keeps it private; making it private takes it off the PCs that applied it.</summary>
    internal async Task SetPrivateAsync(StoreSkill skill, bool isPrivate)
    {
        if (_busy || _store is null) return;
        if (!Confirm(isPrivate
            ? $"‘{skill.DisplayTitle}’을(를) 비공개로 바꿀까요?\n나에게만 보입니다. 이미 적용한 다른 사용자도 앱을 켜거나 스킬 창을 열면 해제되어 쓸 수 없게 됩니다."
            : $"‘{skill.DisplayTitle}’을(를) 공개할까요?\n스킬을 쓰는 모든 사용자에게 보이고 적용할 수 있습니다.")) return;
        SetBusy(true, $"‘{skill.DisplayTitle}’ 공개 범위를 바꾸는 중…");
        string message;
        try
        {
            await _store.SetPrivateAsync(skill.Name, isPrivate, _lifetime.Token);
            message = isPrivate ? $"‘{skill.DisplayTitle}’을(를) 비공개로 바꿨습니다." : $"‘{skill.DisplayTitle}’을(를) 공개했습니다.";
        }
        catch (OperationCanceledException) { return; }
        catch (SkillStoreException exception) { message = "공개 범위를 바꾸지 못했습니다 · " + exception.Message; }
        SetBusy(false, "");
        await RefreshAsync();
        ShowStatus(message);
    }

    /// <summary>Takes a skill out of the store (the store's keepers only); PCs that applied it keep their copy.</summary>
    internal async Task DeleteAsync(StoreSkill skill)
    {
        if (_busy || _store is null) return;
        if (!Confirm($"‘{skill.DisplayTitle}’을(를) 스토어에서 삭제할까요?\n이미 적용한 PC의 스킬은 그대로 남습니다.")) return;
        SetBusy(true, $"‘{skill.DisplayTitle}’ 삭제 중…");
        string message;
        try
        {
            await _store.DeleteAsync(skill.Name, _lifetime.Token);
            message = $"‘{skill.DisplayTitle}’을(를) 삭제했습니다.";
        }
        catch (OperationCanceledException) { return; }
        catch (SkillStoreException exception) { message = "삭제하지 못했습니다 · " + exception.Message; }
        SetBusy(false, "");
        await RefreshAsync();
        ShowStatus(message);
    }

    private Dictionary<string, InstalledSkills.Entry> TryList()
    {
        try { return InstalledSkills.List(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
    }

    private async Task ShowContentAsync(string name, string title, bool applied)
    {
        string? content;
        try { content = applied ? InstalledSkills.ReadContent(name) : null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowStatus("적용한 스킬을 읽지 못했습니다 · " + exception.Message);
            return;
        }
        if (content is null && _store is not null && !_busy)
        {
            SetBusy(true, "내용을 불러오는 중…");
            try { content = (await _store.GetVersionAsync(name, null, apply: false, _lifetime.Token)).Content; SetBusy(false, ""); }
            catch (OperationCanceledException) { return; }
            catch (Exception exception) when (exception is SkillStoreException or JsonException) { SetBusy(false, exception.Message); return; }
        }
        if (content is not null) SkillTextWindow.Show(this, "내용", title, content);
    }

    private async Task ShowVersionsAsync(StoreSkill skill, InstalledSkills.Entry? local)
    {
        if (_busy || _store is null) return;
        SetBusy(true, "버전 기록을 불러오는 중…");
        IReadOnlyList<StoreSkillVersion> versions;
        try { versions = await _store.VersionsAsync(skill.Name, _lifetime.Token); SetBusy(false, ""); }
        catch (OperationCanceledException) { return; }
        catch (Exception exception) when (exception is SkillStoreException or JsonException) { SetBusy(false, exception.Message); return; }
        int? current = local is { FromStore: true, Applied: true, Mode: SkillMode.Store } ? local.Version : null;
        var dialog = new SkillVersionsDialog(_store, skill, versions, current) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.ChosenVersion is { } chosen) await ApplyAsync(skill, chosen);
    }

    private Task CustomizeAsync(string name, string title)
    {
        if (_busy) return Task.CompletedTask;
        try
        {
            var dialog = new SkillCustomizeDialog(name, title) { Owner = this };
            if (dialog.ShowDialog() == true) ShowStatus(dialog.Result);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { ShowStatus("내 지침을 열지 못했습니다 · " + exception.Message); }
        Render();
        return Task.CompletedTask;
    }

    private async Task ShareAsync(StoreSkill? existing)
    {
        if (_busy || _store is null) return;
        var categories = _skills.Select(skill => skill.CategoryName).Append(StoreSkill.OtherCategory).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name == StoreSkill.OtherCategory).ThenBy(name => name, StringComparer.CurrentCulture).ToList();
        var dialog = new ShareSkillDialog(_store, existing, categories) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await RefreshAsync();
        ShowStatus(existing is null
            ? $"‘{dialog.SharedTitle}’을(를) 공유했습니다."
            : dialog.SharedVersion == existing.Version ? $"‘{dialog.SharedTitle}’의 설정을 저장했습니다."
            : $"‘{dialog.SharedTitle}’ v{dialog.SharedVersion}을(를) 올렸습니다.");
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        _share.IsEnabled = _retry.IsEnabled = _refresh.IsEnabled = !busy;
        foreach (var row in _sections.Children.OfType<UniformGrid>().SelectMany(grid => grid.Children.OfType<Border>())) row.IsEnabled = !busy;
        ShowStatus(status);
    }

    private void ShowStatus(string message) => _status.Text = message;
}

/// <summary>The store's starred skills, kept for this Windows user (%LOCALAPPDATA%\TerminalNotes\skill-favorites.json).</summary>
internal static class SkillFavorites
{
    private static string FilePath => AppContext.GetData("TerminalNotes.SkillFavoritesPath") is string test ? test
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerminalNotes", "skill-favorites.json");

    internal static HashSet<string> Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath))?.ToHashSet(StringComparer.Ordinal) ?? [] : []; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    internal static void Set(HashSet<string> favorites, string name, bool on)
    {
        if (on) favorites.Add(name);
        else favorites.Remove(name);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(favorites.Order(StringComparer.Ordinal).ToList()));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Who may change and delete any skill in the store (an author always may their own): the master accounts (MASTER_USERS in
/// .env, the same list as AnnouncementEditors'); matched against the Windows identity's account name.
/// </summary>
internal static class SkillStoreAdmins
{
    internal static readonly string[] AllowedUsers = Services.AnnouncementEditors.AllowedUsers;
    // Tests force an account.
    internal static Func<string>? AccountOverride = null;
    private static readonly Lazy<string> Current = new(() =>
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return identity.Name;
    });

    internal static bool Allowed => IsAllowedUser(AccountOverride?.Invoke() ?? Current.Value);

    internal static bool IsAllowedUser(string account)
    {
        string name = account[(account.LastIndexOf('\\') + 1)..];
        return AllowedUsers.Any(user => string.Equals(name, user, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Who sees the skill store (the 스킬 button and a task's 스킬…): the master accounts for now (MASTER_USERS in .env). Writing, changing
/// one's own skills and choosing who sees them are already everyone's: to open the store, add * to that line (MASTER_USERS=dm,*).
/// Only this class reads * as "everyone"; every other master right matches names, so those stay with the listed accounts.
/// Test hosts (another entry assembly) keep it unless a test forces an account.
/// </summary>
internal static class SkillStoreUsers
{
    internal static readonly string[]? AllowedUsers = Services.AnnouncementEditors.AllowedUsers;
    internal static Func<string>? AccountOverride = null;
    private static readonly Lazy<bool> CurrentAllowed = new(() =>
        !string.Equals(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name, "TerminalNotes", StringComparison.OrdinalIgnoreCase) ||
        IsAllowedUser(CurrentAccount()));

    internal static bool Allowed => AccountOverride is { } account ? IsAllowedUser(account()) : CurrentAllowed.Value;

    internal static bool IsAllowedUser(string account)
    {
        if (AllowedUsers is not { Length: > 0 } users || users.Contains("*")) return true;
        string name = account[(account.LastIndexOf('\\') + 1)..];
        return users.Any(user => string.Equals(name, user, StringComparison.OrdinalIgnoreCase));
    }

    private static string CurrentAccount()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return identity.Name;
    }
}

/// <summary>
/// A skill its author made private is no longer for anyone else, on the PCs that applied it too (사용자: 문제가 있어서 비공개하는 경우도
/// 있으니). Copies in OpenCode's global folder and in the tasks' own are taken out as 해제 does: additions stay for a later apply, which
/// needs the skill public again. Only the relay's plain answer does this: an unreachable relay or a deleted skill changes nothing.
/// It runs once when the app starts and when a skill window opens (the 스킬 button, a task's 스킬…), never on a timer: every PC polling
/// the relay is load it does not need (사용자: 스킬버튼을 클릭한 경우에 최신화).
/// </summary>
internal static class SkillWithdrawals
{
    private const int NamesPerRequest = 200;

    /// <summary>Takes out what was withdrawn; the titles of what left this PC.</summary>
    internal static async Task<IReadOnlyList<string>> SweepAsync(SkillStoreClient store, IEnumerable<string> projects, CancellationToken token)
    {
        var folders = new List<string> { InstalledSkills.GlobalDirectory };
        folders.AddRange(projects.Select(InstalledSkills.ProjectDirectory).Where(Directory.Exists));
        var applied = new Dictionary<string, List<(string Folder, string Title)>>(StringComparer.Ordinal);
        foreach (string folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var entry in ListIn(folder).Where(entry => entry is { FromStore: true, Applied: true }))
            {
                if (!applied.TryGetValue(entry.Name, out var places)) applied[entry.Name] = places = [];
                places.Add((folder, entry.Title ?? entry.Name));
            }
        }
        var removed = new List<string>();
        foreach (string[] names in applied.Keys.Chunk(NamesPerRequest))
            foreach (string name in await store.WithdrawnAsync(names, token))
            {
                if (!applied.TryGetValue(name, out var places)) continue;
                bool taken = false;
                foreach (var (folder, _) in places)
                {
                    // One locked folder leaves that copy for the next pass, not the others.
                    try
                    {
                        using (InstalledSkills.Scope(folder)) InstalledSkills.Remove(name);
                        taken = true;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
                if (taken) removed.Add(places[0].Title);
            }
        return removed;
    }

    internal static string Notice(IReadOnlyList<string> titles) =>
        $"작성자가 비공개로 바꾼 스킬을 이 PC에서 해제했습니다: {string.Join(", ", titles)} · 열린 OpenCode 터미널에서도 바로 빠집니다(작업 중인 터미널은 그 작업이 끝나는 대로).";

    private static IEnumerable<InstalledSkills.Entry> ListIn(string folder)
    {
        try
        {
            using (InstalledSkills.Scope(folder)) return InstalledSkills.List().Values.ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
    }
}

/// <summary>A read-only window for a SKILL.md.</summary>
internal static class SkillTextWindow
{
    internal static void Show(Window owner, string caption, string subtitle, string content)
    {
        var box = new TextBox { Text = content, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = StoreLook.Mono, FontSize = 13, BorderThickness = new Thickness(0),
            Padding = new Thickness(22, 16, 22, 16) };
        var viewer = new Window { Title = $"{caption} · {subtitle}", Owner = owner, Width = 720, Height = 600, MinWidth = 420, MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        StoreLook.Dress(viewer, caption, box, subtitle);
        viewer.ShowDialog();
    }
}

/// <summary>Line changes between two versions' SKILL.md: the common start and end set aside, the rest aligned by the longest
/// common subsequence (or removed and added as whole blocks when too large to align). <see cref="Align"/> lays them out side by
/// side as Visual Studio Code does: in each change the most similar lines face each other with their changed characters, the
/// other lines stay whole, and the shorter side is padded.</summary>
internal static class SkillDiff
{
    internal enum Kind { Same, Removed, Added }

    internal readonly record struct Line(Kind Kind, string Text);

    /// <summary>A line on one side: its number and text, and the characters that changed. A whole removed or added line lists
    /// none: all of it changed.</summary>
    internal sealed record Side(int Number, string Text, bool Whole, IReadOnlyList<(int Start, int Length)> Marks);

    /// <summary>One row: the older line on the left and the newer on the right; in a change either may be missing.</summary>
    internal sealed record Row(Side? Before, Side? After, bool Changed);

    private const long MaxAligned = 2_000_000; // lines × lines, about 8 MB
    private const long MaxPaired = 40_000; // lines × lines in one change
    private const long MaxCharAligned = 250_000; // characters × characters in one line, about 1 MB
    private const double Similar = 0.4; // two-letter pieces in common, from 0 to 1

    internal static List<Line> Compare(string older, string newer)
    {
        string[] a = older.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n'), b = newer.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        int start = 0, endA = a.Length, endB = b.Length;
        while (start < endA && start < endB && a[start] == b[start]) start++;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) { endA--; endB--; }
        var lines = new List<Line>(a.Length + b.Length);
        for (int i = 0; i < start; i++) lines.Add(new(Kind.Same, a[i]));
        int n = endA - start, m = endB - start;
        if ((long)n * m > MaxAligned)
        {
            for (int i = start; i < endA; i++) lines.Add(new(Kind.Removed, a[i]));
            for (int j = start; j < endB; j++) lines.Add(new(Kind.Added, b[j]));
        }
        else
        {
            // common[i, j]: the longest common subsequence of what follows line i of the older and line j of the newer middle.
            var common = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    common[i, j] = a[start + i] == b[start + j] ? common[i + 1, j + 1] + 1 : Math.Max(common[i + 1, j], common[i, j + 1]);
            for (int i = 0, j = 0; i < n || j < m;)
            {
                if (i < n && j < m && a[start + i] == b[start + j]) { lines.Add(new(Kind.Same, a[start + i])); i++; j++; }
                else if (i < n && (j == m || common[i + 1, j] >= common[i, j + 1])) lines.Add(new(Kind.Removed, a[start + i++]));
                else lines.Add(new(Kind.Added, b[start + j++]));
            }
        }
        for (int i = endA; i < a.Length; i++) lines.Add(new(Kind.Same, a[i]));
        return lines;
    }

    internal static List<Row> Align(string older, string newer) => Align(Compare(older, newer));

    internal static List<Row> Align(List<Line> lines)
    {
        var rows = new List<Row>(lines.Count);
        int before = 0, after = 0;
        for (int index = 0; index < lines.Count;)
        {
            if (lines[index].Kind == Kind.Same)
            {
                string text = lines[index++].Text;
                rows.Add(new(new Side(++before, text, false, []), new Side(++after, text, false, []), false));
                continue;
            }
            var removed = new List<string>();
            var added = new List<string>();
            for (; index < lines.Count && lines[index].Kind != Kind.Same; index++)
                (lines[index].Kind == Kind.Removed ? removed : added).Add(lines[index].Text);
            int i = 0, j = 0;
            foreach (var (pairedI, pairedJ) in Pairs(removed, added).Append((removed.Count, added.Count)))
            {
                // The lines with no partner before this pair, side by side from the top; the shorter side is padded.
                while (i < pairedI || j < pairedJ)
                {
                    rows.Add(new(i < pairedI ? new Side(++before, removed[i++], true, []) : null,
                        j < pairedJ ? new Side(++after, added[j++], true, []) : null, true));
                }
                if (pairedI == removed.Count) break;
                var (left, right) = Marks(removed[pairedI], added[pairedJ]);
                rows.Add(new(new Side(++before, removed[i++], false, left), new Side(++after, added[j++], false, right), true));
            }
        }
        return rows;
    }

    /// <summary>The characters that changed between two facing lines. As in Code, a match of one or two characters between two
    /// changes counts as changed too.</summary>
    internal static (List<(int Start, int Length)> Before, List<(int Start, int Length)> After) Marks(string older, string newer)
    {
        int start = 0, endA = older.Length, endB = newer.Length;
        while (start < endA && start < endB && older[start] == newer[start]) start++;
        while (endA > start && endB > start && older[endA - 1] == newer[endB - 1]) { endA--; endB--; }
        int n = endA - start, m = endB - start;
        var changes = new List<(int A, int AEnd, int B, int BEnd)>();
        if (n == 0 || m == 0 || (long)n * m > MaxCharAligned)
        {
            if (n > 0 || m > 0) changes.Add((start, endA, start, endB));
        }
        else
        {
            var common = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    common[i, j] = older[start + i] == newer[start + j] ? common[i + 1, j + 1] + 1 : Math.Max(common[i + 1, j], common[i, j + 1]);
            for (int i = 0, j = 0; i < n || j < m;)
            {
                if (i < n && j < m && older[start + i] == newer[start + j]) { i++; j++; continue; }
                int fromA = i, fromB = j;
                while ((i < n || j < m) && !(i < n && j < m && older[start + i] == newer[start + j]))
                {
                    if (i < n && (j == m || common[i + 1, j] >= common[i, j + 1])) i++;
                    else j++;
                }
                changes.Add((start + fromA, start + i, start + fromB, start + j));
            }
        }
        for (int k = changes.Count - 1; k > 0; k--)
            if (changes[k].A - changes[k - 1].AEnd <= 2)
            {
                changes[k - 1] = (changes[k - 1].A, changes[k].AEnd, changes[k - 1].B, changes[k].BEnd);
                changes.RemoveAt(k);
            }
        return (changes.Where(change => change.AEnd > change.A).Select(change => (change.A, change.AEnd - change.A)).ToList(),
            changes.Where(change => change.BEnd > change.B).Select(change => (change.B, change.BEnd - change.B)).ToList());
    }

    // The most similar lines of a change face each other, in order; a change too large to compare pairs none.
    private static List<(int, int)> Pairs(List<string> removed, List<string> added)
    {
        int n = removed.Count, m = added.Count;
        var pairs = new List<(int, int)>();
        if (n == 0 || m == 0 || (long)n * m > MaxPaired) return pairs;
        string[] left = removed.Select(line => line.Trim()).ToArray(), right = added.Select(line => line.Trim()).ToArray();
        int[][] leftPieces = left.Select(Pieces).ToArray(), rightPieces = right.Select(Pieces).ToArray();
        var score = new double[n, m];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < m; j++)
                score[i, j] = left[i] == right[j] ? 1 : Shared(leftPieces[i], rightPieces[j]);
        // best[i, j]: the most similarity the pairs after line i of the removed and line j of the added can add up to.
        var best = new double[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                best[i, j] = Math.Max(Math.Max(best[i + 1, j], best[i, j + 1]), score[i, j] >= Similar ? score[i, j] + best[i + 1, j + 1] : 0);
        for (int i = 0, j = 0; i < n && j < m;)
        {
            if (score[i, j] >= Similar && best[i, j] == score[i, j] + best[i + 1, j + 1]) pairs.Add((i++, j++));
            else if (best[i, j] == best[i + 1, j]) i++;
            else j++;
        }
        return pairs;
    }

    // A line's two-letter pieces, sorted.
    private static int[] Pieces(string line)
    {
        var pieces = new int[Math.Max(0, line.Length - 1)];
        for (int k = 0; k < pieces.Length; k++) pieces[k] = line[k] << 16 | line[k + 1];
        Array.Sort(pieces);
        return pieces;
    }

    private static double Shared(int[] a, int[] b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        int shared = 0;
        for (int i = 0, j = 0; i < a.Length && j < b.Length;)
        {
            if (a[i] == b[j]) { shared++; i++; j++; }
            else if (a[i] < b[j]) i++;
            else j++;
        }
        return 2.0 * shared / (a.Length + b.Length);
    }
}

/// <summary>Two versions compared as Visual Studio Code compares them: the older on the left and the newer on the right, each with
/// its line numbers and −/+; changed lines tinted with their changed characters stronger, a whole removed or added line strong,
/// and hatching where a side has no line; the changes on an overview ruler at the right edge (a click or drag goes there); ↑ ↓
/// (Shift+Alt+F5 · Alt+F5, F7) to the previous or next change, going round; it opens at the first change. Lines wrap, as Code
/// wraps Markdown. Narrower than 900 it shows each change inline (removed lines above added ones), as Code does. Its colors and
/// font are Code's default dark ones, removed red included: the user's one exception to the app's no-dark-red rule, for this
/// window only (AGENTS.md). Only the lines in view are made, so a long SKILL.md opens at once; the view scrolls a line at a time.</summary>
internal sealed class SkillDiffWindow : Window
{
    private const double InlineBelow = 900, LineHeight = 19;
    // Code's Dark Modern editor (#1F1F1F, text #CCCCCC, line numbers #6E7681) with its diff colors laid on it as Code lays them: a
    // changed line 20% of #9BB955 (added) or #FF0000 (removed), its changed characters another 20% of #9CCC2C or #FF0000, the
    // diagonal fill #CCCCCC at 20%, and the overview ruler in those 20% colors under the #797979 40% view.
    private static readonly Media.FontFamily Code = new("Consolas, Malgun Gothic");
    private static readonly Media.Brush Editor = Frozen(0x1F, 0x1F, 0x1F), Ink = Frozen(0xCC, 0xCC, 0xCC), NumberInk = Frozen(0x6E, 0x76, 0x81),
        Divider = Frozen(0x2B, 0x2B, 0x2B), AddedLine = Frozen(0x38, 0x3E, 0x2A), AddedText = Frozen(0x4C, 0x5A, 0x2A),
        RemovedLine = Frozen(0x4C, 0x19, 0x19), RemovedText = Frozen(0x70, 0x14, 0x14), AddedRuler = Frozen(0x9C, 0xCC, 0x2C, 0x33),
        RemovedRuler = Frozen(0xFF, 0x00, 0x00, 0x33), Slider = Frozen(0x79, 0x79, 0x79, 0x66), Hatch = Hatching();
    private readonly List<SkillDiff.Row> _rows;
    private readonly Lines _lines;
    private readonly ScrollViewer _scroll;
    private readonly Canvas _ruler = new() { ClipToBounds = true, Cursor = Cursors.Hand };
    private readonly Button _previous, _next;
    private readonly GridLength _numbers;
    // Each change as lines of the list, and whether it removes or adds any (the ruler's left and right halves).
    private readonly List<(int First, int Last, bool Removes, bool Adds)> _changes = [];
    private double _expected = -1;

    internal int AddedCount { get; }
    internal int RemovedCount { get; }
    internal IReadOnlyList<SkillDiff.Row> Rows => _rows;
    internal bool SideBySide { get; private set; }
    internal int ChangeCount => _changes.Count;
    internal int? CurrentChange { get; private set; }

    internal SkillDiffWindow(string title, string older, string newer, string? subtitle = null)
    {
        Title = subtitle is null ? title : $"{subtitle} · {title}";
        var screen = SystemParameters.WorkArea;
        Width = Math.Min(1100, screen.Width - 40); Height = Math.Min(720, screen.Height - 40); MinWidth = 480; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var lines = SkillDiff.Compare(older, newer);
        AddedCount = lines.Count(line => line.Kind == SkillDiff.Kind.Added);
        RemovedCount = lines.Count(line => line.Kind == SkillDiff.Kind.Removed);
        _rows = SkillDiff.Align(lines);
        int widest = _rows.Max(row => Math.Max(row.Before?.Number ?? 0, row.After?.Number ?? 0));
        _numbers = new GridLength(widest.ToString().Length * 8 + 26);
        _lines = new Lines(Line);
        _lines.ApplyTemplate();
        _scroll = (ScrollViewer)_lines.Template.FindName("Scroll", _lines);
        _previous = Navigation(0xE74A, "이전 변경 (Shift+Alt+F5)", -1);
        _next = Navigation(0xE74B, "다음 변경 (Alt+F5)", 1);
        var view = new Grid { Background = Editor };
        view.ColumnDefinitions.Add(new ColumnDefinition());
        view.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        view.Children.Add(_lines);
        var ruler = new Border { Child = _ruler, BorderBrush = Divider, BorderThickness = new Thickness(1, 0, 0, 0) };
        Grid.SetColumn(ruler, 1);
        view.Children.Add(ruler);
        StoreLook.Dress(this, title, view, subtitle, _previous, _next);
        Build(Width >= InlineBelow);
        _scroll.ScrollChanged += (_, _) =>
        {
            // Scrolled by hand: the next change is found from where the view is.
            if (Math.Abs(_scroll.VerticalOffset - _expected) > 1) CurrentChange = null;
            DrawRuler();
        };
        _ruler.SizeChanged += (_, _) => DrawRuler();
        _ruler.MouseLeftButtonDown += (_, e) => { _ruler.CaptureMouse(); ScrollToRuler(e.GetPosition(_ruler).Y); };
        _ruler.MouseMove += (_, e) => { if (_ruler.IsMouseCaptured) ScrollToRuler(e.GetPosition(_ruler).Y); };
        _ruler.MouseLeftButtonUp += (_, _) => _ruler.ReleaseMouseCapture();
        SizeChanged += (_, _) =>
        {
            if ((ActualWidth >= InlineBelow) != SideBySide) Build(ActualWidth >= InlineBelow);
        };
        PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.F7 || (key == Key.F5 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)))
            {
                GoToChange(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                e.Handled = true;
            }
        };
        Loaded += (_, _) =>
        {
            _scroll.Focus();
            UpdateLayout();
            if (_changes.Count > 0 && EndOf(_changes[0]) > _scroll.VerticalOffset + _scroll.ViewportHeight) Reveal(0);
        };
    }

    internal static void Show(Window owner, string title, string older, string newer, string? subtitle = null) =>
        new SkillDiffWindow(title, older, newer, subtitle) { Owner = owner }.ShowDialog();

    /// <summary>Goes to the next (1) or previous (−1) change: after the one gone to last, or from where the view is.</summary>
    internal void GoToChange(int direction)
    {
        if (_changes.Count == 0) return;
        int target;
        if (CurrentChange is { } current) target = (current + direction + _changes.Count) % _changes.Count;
        else
        {
            double top = _scroll.VerticalOffset;
            target = direction > 0 ? _changes.FindIndex(change => change.First > top + 0.5) : _changes.FindLastIndex(change => change.First < top - 0.5);
            if (target < 0) target = direction > 0 ? 0 : _changes.Count - 1;
        }
        Reveal(target);
    }

    // A change in the middle of the view, or its start at the top when it is longer than the view.
    private void Reveal(int index)
    {
        UpdateLayout();
        var change = _changes[index];
        double size = EndOf(change) - change.First, view = _scroll.ViewportHeight;
        double offset = Math.Clamp(Math.Floor(size < view ? change.First + size / 2 - view / 2 : change.First), 0, Math.Max(0, _scroll.ExtentHeight - view));
        CurrentChange = index;
        _expected = offset;
        _scroll.ScrollToVerticalOffset(offset);
    }

    private void Build(bool sideBySide)
    {
        SideBySide = sideBySide;
        CurrentChange = null;
        _changes.Clear();
        var items = new List<object>(_rows.Count);
        if (sideBySide)
        {
            foreach (var row in _rows)
            {
                if (row.Changed) Track(items.Count, row.Before is not null, row.After is not null);
                items.Add(row);
            }
        }
        else
        {
            // Inline: each change's removed lines, then its added ones.
            for (int index = 0; index < _rows.Count;)
            {
                if (!_rows[index].Changed)
                {
                    items.Add(new Inline(_rows[index++].After!, Removed: false, Changed: false));
                    continue;
                }
                int end = index;
                while (end < _rows.Count && _rows[end].Changed) end++;
                for (int k = index; k < end; k++)
                    if (_rows[k].Before is { } before)
                    {
                        Track(items.Count, removes: true, adds: false);
                        items.Add(new Inline(before, Removed: true, Changed: true));
                    }
                for (int k = index; k < end; k++)
                    if (_rows[k].After is { } after)
                    {
                        Track(items.Count, removes: false, adds: true);
                        items.Add(new Inline(after, Removed: false, Changed: true));
                    }
                index = end;
            }
        }
        _lines.ItemsSource = items;
        _previous.IsEnabled = _next.IsEnabled = _changes.Count > 0;
    }

    private void Track(int line, bool removes, bool adds)
    {
        if (_changes.Count > 0 && _changes[^1].Last == line - 1)
        {
            var last = _changes[^1];
            _changes[^1] = (last.First, line, last.Removes || removes, last.Adds || adds);
        }
        else _changes.Add((line, line, removes, adds));
    }

    private static int EndOf((int First, int Last, bool, bool) change) => change.Last + 1;

    // One line of the list: both sides with the divider between them, or one line inline.
    private UIElement Line(object item)
    {
        var grid = new Grid();
        if (item is SkillDiff.Row row)
        {
            foreach (var width in new[] { _numbers, new GridLength(18), new GridLength(1, GridUnitType.Star), new GridLength(1), _numbers, new GridLength(18),
                         new GridLength(1, GridUnitType.Star) })
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            Put(grid, 0, row.Before, removed: true, row.Changed);
            Place(grid, new Border { Background = Divider }, 3);
            Put(grid, 4, row.After, removed: false, row.Changed);
        }
        else if (item is Inline line)
        {
            foreach (var width in new[] { _numbers, new GridLength(18), new GridLength(1, GridUnitType.Star) })
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            Put(grid, 0, line.Side, line.Removed, line.Changed);
        }
        return grid;
    }

    // One side of a line: its tint, number, −/+ and text; or, in a change, hatching where it has no line.
    private static void Put(Grid grid, int column, SkillDiff.Side? side, bool removed, bool changed)
    {
        if (side is null)
        {
            Place(grid, new Border { Background = Hatch }, column, 3);
            return;
        }
        if (changed)
        {
            Place(grid, new Border { Background = removed ? RemovedLine : AddedLine }, column, 3);
            if (side.Whole) Place(grid, new Border { Background = removed ? RemovedText : AddedText }, column + 2);
            var sign = Mono(removed ? "−" : "+", Ink, TextAlignment.Center);
            sign.Opacity = 0.7;
            Place(grid, sign, column + 1);
        }
        var number = Mono(side.Number.ToString(), NumberInk, TextAlignment.Right);
        number.Padding = new Thickness(0, 0, 8, 0);
        Place(grid, number, column);
        var text = Mono("", Ink, TextAlignment.Left);
        text.TextWrapping = TextWrapping.Wrap;
        text.Padding = new Thickness(4, 0, 14, 0);
        int at = 0;
        foreach (var (start, length) in side.Marks)
        {
            if (start > at) text.Inlines.Add(new System.Windows.Documents.Run(side.Text[at..start]));
            text.Inlines.Add(new System.Windows.Documents.Run(side.Text.Substring(start, length)) { Background = removed ? RemovedText : AddedText });
            at = start + length;
        }
        if (at < side.Text.Length) text.Inlines.Add(new System.Windows.Documents.Run(side.Text[at..]));
        Place(grid, text, column + 2);
    }

    private static TextBlock Mono(string text, Media.Brush ink, TextAlignment alignment) => new()
    {
        Text = text, Foreground = ink, TextAlignment = alignment, FontFamily = Code, FontSize = 14,
        LineHeight = LineHeight, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };

    private static void Place(Grid grid, UIElement element, int column, int span = 1)
    {
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, span);
        grid.Children.Add(element);
    }

    // The overview ruler: removed (red) on the left half and added (green) on the right, where they are in the whole text (a text
    // shorter than the view as high as it is), and the part in view.
    private void DrawRuler()
    {
        _ruler.Children.Clear();
        double height = _ruler.ActualHeight, width = _ruler.ActualWidth, extent = Span();
        if (height <= 0 || extent <= 0) return;
        double scale = height / extent;
        foreach (var change in _changes)
        {
            double top = change.First * scale, size = Math.Max(2, (EndOf(change) - change.First) * scale);
            if (change.Removes) Mark(0, top, width / 2, size, RemovedRuler);
            if (change.Adds) Mark(width / 2, top, width / 2, size, AddedRuler);
        }
        if (_scroll.ExtentHeight > _scroll.ViewportHeight) Mark(0, _scroll.VerticalOffset * scale, width, _scroll.ViewportHeight * scale, Slider);
    }

    private void Mark(double left, double top, double width, double height, Media.Brush fill)
    {
        var mark = new System.Windows.Shapes.Rectangle { Width = width, Height = height, Fill = fill, IsHitTestVisible = false };
        Canvas.SetLeft(mark, left);
        Canvas.SetTop(mark, top);
        _ruler.Children.Add(mark);
    }

    private void ScrollToRuler(double y) =>
        _scroll.ScrollToVerticalOffset(y / Math.Max(1, _ruler.ActualHeight) * Span() - _scroll.ViewportHeight / 2);

    // How many lines the ruler stands for: the whole list, or as many as fit when it is shorter than the view.
    private double Span() => Math.Max(_scroll.ExtentHeight, _scroll.ActualHeight / LineHeight);

    private Button Navigation(int glyph, string name, int direction)
    {
        var button = new Button { Content = StoreLook.Glyph(((char)glyph).ToString(), 14), ToolTip = name };
        button.SetResourceReference(StyleProperty, "IconButton");
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => GoToChange(direction);
        return button;
    }

    private static Media.SolidColorBrush Frozen(byte red, byte green, byte blue, byte alpha = 0xFF)
    {
        var brush = new Media.SolidColorBrush(Media.Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    // Code's diagonal fill: thin light stripes rising to the right. Two stripes per line height, so the hatching runs on from
    // one line to the next.
    private static Media.Brush Hatching()
    {
        Media.Color stripe = Media.Color.FromArgb(0x33, 0xCC, 0xCC, 0xCC), clear = Media.Color.FromArgb(0, 0xCC, 0xCC, 0xCC);
        double half = LineHeight / 4; // the stripes repeat every LineHeight / 2 down
        var brush = new Media.LinearGradientBrush { MappingMode = Media.BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(half, half),
            SpreadMethod = Media.GradientSpreadMethod.Repeat };
        brush.GradientStops.Add(new Media.GradientStop(stripe, 0));
        brush.GradientStops.Add(new Media.GradientStop(stripe, 0.21));
        brush.GradientStops.Add(new Media.GradientStop(clear, 0.21));
        brush.GradientStops.Add(new Media.GradientStop(clear, 1));
        brush.Freeze();
        return brush;
    }

    private sealed record Inline(SkillDiff.Side Side, bool Removed, bool Changed);

    // A list that makes only the lines in view (Recycling), each by the window.
    private sealed class Lines : ItemsControl
    {
        private const string Frame = """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ItemsControl">
             <ScrollViewer x:Name="Scroll" CanContentScroll="True" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto" Focusable="True" FocusVisualStyle="{x:Null}">
              <ItemsPresenter/>
             </ScrollViewer>
            </ControlTemplate>
            """;
        private readonly Func<object, UIElement> _make;

        internal Lines(Func<object, UIElement> make)
        {
            _make = make;
            Focusable = false;
            ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
            VirtualizingPanel.SetIsVirtualizing(this, true);
            VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
            Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(Frame);
        }

        protected override bool IsItemItsOwnContainerOverride(object item) => false;

        protected override DependencyObject GetContainerForItemOverride() => new ContentPresenter();

        protected override void PrepareContainerForItemOverride(DependencyObject element, object item) => ((ContentPresenter)element).Content = _make(item);

        protected override void ClearContainerForItemOverride(DependencyObject element, object item) => ((ContentPresenter)element).Content = null;
    }
}

/// <summary>A skill's versions, newest first: when and by whom, what changed (the note, and 비교 with the version before it), its
/// content, and applying any of them.</summary>
internal sealed class SkillVersionsDialog : Window
{
    internal int? ChosenVersion { get; private set; }

    internal SkillVersionsDialog(SkillStoreClient store, StoreSkill skill, IReadOnlyList<StoreSkillVersion> versions, int? applied)
    {
        Title = "버전 기록 · " + skill.DisplayTitle;
        Width = 640; Height = 520; MinWidth = 480; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var list = new StackPanel { Margin = new Thickness(24, 6, 24, 12) };
        for (int index = 0; index < versions.Count; index++)
        {
            var version = versions[index];
            var older = index + 1 < versions.Count ? versions[index + 1] : null;
            var head = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
            head.Inlines.Add(new System.Windows.Documents.Run($"v{version.Version}") { FontSize = 15, FontWeight = FontWeights.SemiBold });
            string when = string.Join(" · ", new[] { version.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), version.Author }.Where(part => !string.IsNullOrEmpty(part)));
            if (when.Length > 0)
            {
                var meta = new System.Windows.Documents.Run("   " + when) { FontSize = 12.5 };
                meta.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Muted");
                head.Inlines.Add(meta);
            }
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(head);
            if (version.Note is { Length: > 0 } note)
            {
                var change = new TextBlock { Text = note, FontSize = 13, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
                change.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                text.Children.Add(change);
            }
            // 비교 (absent on the first version) comes first, so 내용 and 적용 line up on every row.
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            if (older is not null)
                buttons.Children.Add(Quiet("비교", async () =>
                {
                    var before = await store.GetVersionAsync(skill.Name, older.Version, apply: false, CancellationToken.None);
                    var after = await store.GetVersionAsync(skill.Name, version.Version, apply: false, CancellationToken.None);
                    SkillDiffWindow.Show(this, $"SKILL.md (v{older.Version}) ↔ SKILL.md (v{version.Version})", before.Content, after.Content,
                        skill.DisplayTitle);
                }));
            buttons.Children.Add(Quiet("내용", async () => SkillTextWindow.Show(this, "내용", $"{skill.DisplayTitle} v{version.Version}",
                (await store.GetVersionAsync(skill.Name, version.Version, apply: false, CancellationToken.None)).Content)));
            if (version.Version == applied)
            {
                var inUse = new TextBlock { Text = "적용 중", FontSize = 13, FontWeight = FontWeights.SemiBold, Width = 76, TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
                inUse.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
                buttons.Children.Add(inUse);
            }
            else
            {
                var apply = new Button { Content = "적용", MinWidth = 76, Margin = new Thickness(6, 0, 0, 0) };
                apply.Click += (_, _) => { ChosenVersion = version.Version; DialogResult = true; };
                buttons.Children.Add(apply);
            }
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(text);
            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);
            var item = new Border { Child = row, Tag = version.Version, Padding = new Thickness(0, 14, 0, 14), BorderThickness = new Thickness(0, index == 0 ? 0 : 1, 0, 0) };
            item.SetResourceReference(Border.BorderBrushProperty, "Line");
            list.Children.Add(item);
        }
        StoreLook.Dress(this, "버전 기록", new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, skill.DisplayTitle);
    }

    // A text button whose work asks the store; why it failed is shown.
    private Button Quiet(string label, Func<Task> click)
    {
        var button = new Button { Content = label };
        button.SetResourceReference(StyleProperty, "QuietButton");
        button.Click += async (_, _) =>
        {
            try { await click(); }
            catch (Exception exception) when (exception is SkillStoreException or JsonException) { AppMessageDialog.ShowError(this, "버전 기록", exception.Message); }
        };
        return button;
    }
}

/// <summary>My own instructions under the store version (they survive new versions): one box. Only a skill already on my whole
/// text (its SKILL.md edited by hand) also shows that text, with tabs to keep it or go back to the store version.</summary>
internal sealed class SkillCustomizeDialog : Window
{
    private readonly string _name;
    private readonly RadioButton _storeMode = new() { Content = "스토어 + 내 지침", GroupName = "SkillMode" };
    private readonly RadioButton _localMode = new() { Content = "전부 직접 관리", GroupName = "SkillMode" };
    private readonly TextBox _custom = new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Tag = "스토어 버전 아래에 덧붙일 지침" };
    private readonly TextBox _local = new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _error = new();

    internal string Result { get; private set; } = "";
    internal string CustomText { get => _custom.Text; set => _custom.Text = value; }
    internal bool LocalMode => _localMode.IsChecked == true;
    internal bool ShowsTabs { get; }

    internal SkillCustomizeDialog(string name, string title)
    {
        _name = name;
        Title = "내 지침 · " + title;
        Width = 640; Height = 480; MinWidth = 480; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        ShowsTabs = InstalledSkills.List().GetValueOrDefault(name)?.Mode == SkillMode.Local;
        _custom.Text = InstalledSkills.ReadCustom(name);
        foreach (var box in new[] { _custom, _local })
        {
            box.FontFamily = StoreLook.Mono;
            box.FontSize = 13;
            box.Padding = new Thickness(12, 10, 12, 10);
        }
        var cancel = new Button { Content = "취소", IsCancel = true, MinWidth = 88 };
        var save = new Button { Content = "저장", MinWidth = 88 };
        save.SetResourceReference(StyleProperty, "PrimaryButton");
        save.Click += (_, _) => { if (SaveChanges()) DialogResult = true; };
        var views = new Grid();
        views.Children.Add(_custom);
        var content = new DockPanel { Margin = new Thickness(24, 16, 24, 0) };
        if (ShowsTabs)
        {
            string local = InstalledSkills.ReadLocal(name);
            _local.Text = local.Length > 0 ? local : InstalledSkills.Compose(InstalledSkills.ReadBase(name), _custom.Text);
            views.Children.Add(_local);
            var tabs = StoreLook.Tabs(_storeMode, _localMode);
            tabs.Margin = new Thickness(0, 0, 0, 14);
            _storeMode.Checked += (_, _) => ShowMode();
            _localMode.Checked += (_, _) => ShowMode();
            DockPanel.SetDock(tabs, Dock.Top);
            content.Children.Add(tabs);
        }
        content.Children.Add(views);
        var footer = StoreLook.Footer(_error, cancel, save);
        var body = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);
        body.Children.Add(content);
        StoreLook.Dress(this, "내 지침", body, title);
        (ShowsTabs ? _localMode : _storeMode).IsChecked = true;
        ShowMode();
        Loaded += (_, _) => (LocalMode ? _local : _custom).Focus();
    }

    private void ShowMode()
    {
        bool local = _localMode.IsChecked == true;
        _custom.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        _local.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
    }

    internal bool SaveChanges()
    {
        try
        {
            if (LocalMode)
            {
                InstalledSkills.SaveLocal(_name, _local.Text);
                Result = "직접 관리하는 내용을 저장했습니다.";
            }
            else
            {
                InstalledSkills.SaveCustom(_name, _custom.Text);
                if (InstalledSkills.List().GetValueOrDefault(_name)?.Mode == SkillMode.Local) InstalledSkills.UseStoreVersion(_name);
                Result = _custom.Text.Trim().Length > 0 ? "내 지침을 저장했습니다." : "내 지침을 비웠습니다.";
            }
            Result += " " + InstalledSkills.AppliedNow;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _error.Text = exception.Message;
            return false;
        }
    }
}

/// <summary>A skill file dropped on or chosen in the share window: Markdown or text (UTF-8, UTF-16 or Korean ANSI), an OpenCode
/// SKILL.md whose frontmatter gives the name and description, or a folder holding one; or the text written in its 텍스트 tab.</summary>
internal sealed record SkillSourceFile(string Label, string Stem, string Body, string? Name, string? Description, string? Heading, long Bytes)
{
    internal const long MaxBytes = 1_000_000;
    internal const int MaxBodyLength = 100_000; // the relay's SKILL_BODY_MAX
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The title a new share starts with: the file's '# heading', else its name.</summary>
    internal string SuggestedTitle => Heading is { Length: > 0 } heading ? heading.Length > 80 ? heading[..80].TrimEnd() : heading : Stem;

    internal static SkillSourceFile Read(string path)
    {
        if (Directory.Exists(path))
            path = Directory.EnumerateFiles(path).FirstOrDefault(file => Path.GetFileName(file).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("폴더 안에 SKILL.md가 없습니다. 지침 파일(.md·.txt)을 놓아 주세요.");
        var info = new FileInfo(path);
        if (!info.Exists) throw new InvalidDataException("파일을 찾지 못했습니다.");
        if (info.Length > MaxBytes) throw new InvalidDataException("파일이 너무 큽니다(1 MB까지).");
        string text = (Decode(File.ReadAllBytes(path)) ?? throw new InvalidDataException("텍스트 파일이 아닙니다. 지침을 담은 .md나 .txt 파일을 놓아 주세요."))
            .TrimStart('﻿').ReplaceLineEndings("\n");
        bool skillFile = Path.GetFileName(path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase);
        string folder = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path))) ?? "";
        var file = Parse(text, skillFile ? Path.Combine(folder, Path.GetFileName(path)) : Path.GetFileName(path),
            skillFile ? folder : Path.GetFileNameWithoutExtension(path), info.Length);
        if (file.Body.Length == 0) throw new InvalidDataException("파일 내용이 비어 있습니다.");
        if (file.Body.Length > MaxBodyLength) throw new InvalidDataException("내용이 너무 깁니다(10만 자까지).");
        return file;
    }

    /// <summary>The 텍스트 tab's instructions, read like a file's: a pasted SKILL.md keeps its name and description, and a '# heading'
    /// gives the title. Not checked here; the share window says what is missing.</summary>
    /// <summary>A GitHub file's text as the relay read it: the file's name, or its folder's for a SKILL.md, is the stem.</summary>
    internal static SkillSourceFile FromLink(string text, string link)
    {
        string[] parts = (Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri) ? uri.AbsolutePath : link)
            .Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        string file = parts.LastOrDefault() ?? "SKILL.md";
        bool document = new[] { ".md", ".markdown", ".txt" }.Any(extension => file.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        string stem = !document ? file
            : file.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) && parts.Length > 1 ? parts[^2] : Path.GetFileNameWithoutExtension(file);
        string label = !document ? file + "/SKILL.md" : parts.Length > 1 ? parts[^2] + "/" + file : file;
        return Parse(text.TrimStart('\uFEFF').ReplaceLineEndings("\n"), label, stem, Encoding.UTF8.GetByteCount(text));
    }

    internal static SkillSourceFile FromText(string text) =>
        Parse(text.TrimStart('﻿').ReplaceLineEndings("\n"), "텍스트", "", Encoding.UTF8.GetByteCount(text));

    private static SkillSourceFile Parse(string text, string label, string stem, long bytes)
    {
        string? name = null, description = null;
        string[] lines = text.Split('\n');
        if (lines.Length > 2 && lines[0].Trim() == "---" && Array.FindIndex(lines, 1, line => line.Trim() == "---") is int close and > 0)
        {
            var fields = Fields(lines[1..close]);
            name = fields.GetValueOrDefault("name");
            description = fields.GetValueOrDefault("description");
            text = string.Join('\n', lines[(close + 1)..]);
        }
        string body = Regex.Replace(text, @"\A(?:[ \t]*\n)+", "").TrimEnd();
        string first = body[..(body.IndexOf('\n') is int end and >= 0 ? end : body.Length)].Trim();
        return new SkillSourceFile(label, stem, body, name, description, first.StartsWith("# ", StringComparison.Ordinal) ? first[2..].Trim() : null, bytes);
    }

    /// <summary>The instructions to upload: the relay writes the '# title' line itself, so the file's own copy of it goes.</summary>
    internal string BodyFor(string title)
    {
        string first = Body[..(Body.IndexOf('\n') is int end and >= 0 ? end : Body.Length)];
        return first.Trim() == "# " + title.Trim() ? Regex.Replace(Body[first.Length..], @"\A(?:[ \t]*\n)+", "") : Body;
    }

    private static string? Decode(byte[] bytes)
    {
        if (bytes is [0xFF, 0xFE, ..]) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes is [0xFE, 0xFF, ..]) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (Array.IndexOf(bytes, (byte)0) >= 0) return null;
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { }
        // Notepad's ANSI on Korean Windows.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try { return Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
    }

    // The simple YAML OpenCode's frontmatter uses: plain, quoted and folded (| >) values.
    private static Dictionary<string, string> Fields(string[] lines)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < lines.Length; i++)
        {
            var match = Regex.Match(lines[i], @"^([A-Za-z][\w-]*)\s*:\s*(.*)$");
            if (!match.Success) continue;
            string value = match.Groups[2].Value.Trim();
            if (value is "|" or ">" or "|-" or ">-" or "|+" or ">+")
            {
                var parts = new List<string>();
                while (i + 1 < lines.Length && (lines[i + 1].Length == 0 || char.IsWhiteSpace(lines[i + 1][0]))) parts.Add(lines[++i].Trim());
                value = string.Join(' ', parts.Where(part => part.Length > 0));
            }
            else if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                try { value = JsonSerializer.Deserialize<string>(value) ?? ""; }
                catch (JsonException) { value = value[1..^1]; }
            }
            else if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') value = value[1..^1].Replace("''", "'");
            fields[match.Groups[1].Value] = value;
        }
        return fields;
    }
}

/// <summary>Shares a skill with only its title typed, or — for the one who shared it — a new version. A category (one of the
/// store's or a new one), 나만 보기 (only its author sees it) and the 출처 go with it. Below them the 텍스트, 파일 and 깃허브 tabs take the
/// instructions as written text, a dropped or chosen file, or a GitHub (or GitHub Enterprise) link the relay reads and then
/// follows, so the skill updates itself when the file changes; the chosen tab is what is shared. The store name carries this
/// PC's Windows account; a new version takes the next number unless the author writes a larger one.</summary>
internal sealed class ShareSkillDialog : Window
{
    internal enum From { Text, File, GitHub }

    private readonly SkillStoreClient _store;
    private readonly StoreSkill? _existing;
    private readonly IReadOnlyList<string> _knownCategories;
    private readonly string _user = Environment.UserName;
    private readonly TextBox _title = new() { MaxLength = 80, Tag = "제목" };
    private readonly TextBox _category = new() { MaxLength = 30, Tag = "카테고리 (선택)", Width = 220 };
    private readonly Button _categoryPick = new() { Content = StoreLook.Glyph(StoreLook.ChevronGlyph, 11), ToolTip = "카테고리 고르기" };
    private readonly ToggleButton _private = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _origin = new() { MaxLength = 300, Tag = "출처 (선택) · 유저 ID나 링크", Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBox _version = new() { MaxLength = 6, Width = 72, TextAlignment = TextAlignment.Center };
    private readonly TextBox _note = new() { MaxLength = 200, Tag = "바뀐 점 (선택)" };
    private readonly RadioButton _textTab = new() { Content = "텍스트", IsChecked = true };
    private readonly RadioButton _fileTab = new() { Content = "파일" };
    private readonly RadioButton _gitHubTab = new() { Content = "깃허브" };
    private readonly Button _drop = new();
    private readonly TextBlock _dropText = new() { FontSize = 13.5, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly TextBox _text = new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = StoreLook.Mono, FontSize = 13, Padding = new Thickness(12, 10, 12, 10), Tag = "지침을 쓰거나 붙여 넣으세요" };
    private readonly TextBox _link = new() { MaxLength = 500, Tag = "깃허브 링크 (SKILL.md 파일이나 그 폴더)" };
    private readonly TextBlock _linkState = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 10, 2, 0) };
    private readonly StackPanel _gitHub = new();
    private readonly System.Windows.Threading.DispatcherTimer _linkPause = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly TextBlock _error = new();
    private readonly Button _submit = new() { Content = "공유", MinWidth = 88 };
    private SkillSourceFile? _file, _linked;
    private string _linkedFrom = "";
    private string _autoTitle = "";
    private bool _busy, _latestAsked;

    internal string SharedTitle { get; private set; } = "";
    internal int SharedVersion { get; private set; }

    internal ShareSkillDialog(SkillStoreClient store, StoreSkill? existing = null, IReadOnlyList<string>? categories = null)
    {
        _store = store;
        _existing = existing;
        _knownCategories = categories ?? [];
        Title = existing is null ? "스킬 공유하기" : "새 버전 올리기";
        Width = 600; MinWidth = 480; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var form = new StackPanel { Margin = new Thickness(24, 20, 24, 0) };
        if (existing is not null)
        {
            _title.Text = existing.Title;
            _version.Text = (existing.Version + 1).ToString();
            _category.Text = existing.Category ?? "";
            _private.IsChecked = existing.Private;
            _origin.Text = existing.Origin ?? "";
            if (existing.Source is { Length: > 0 } link) _link.Text = link;
        }
        form.Children.Add(_title);
        form.Children.Add(Options());
        AutomationProperties.SetName(_origin, "출처");
        form.Children.Add(_origin);
        var tabs = StoreLook.Tabs(_textTab, _fileTab, _gitHubTab);
        tabs.Margin = new Thickness(0, 14, 0, 12);
        foreach (var tab in new[] { _textTab, _fileTab, _gitHubTab }) tab.Checked += (_, _) => ShowSource();
        _textTab.Click += (_, _) => _text.Focus();
        _gitHubTab.Click += (_, _) => _link.Focus();
        _text.TextChanged += (_, _) =>
        {
            if (!_busy) _error.Text = "";
            SyncTitle();
        };
        // A link is read once typing pauses.
        _link.TextChanged += (_, _) =>
        {
            _linkPause.Stop();
            if (_link.Text.Trim() != _linkedFrom) { _linked = null; _linkState.Text = ""; }
            if (_link.Text.Trim().Length > 0) _linkPause.Start();
        };
        _linkPause.Tick += async (_, _) =>
        {
            _linkPause.Stop();
            await LoadLinkAsync();
        };
        form.Children.Add(tabs);
        // The drop zone is a button too: a click opens the file picker.
        _drop.Content = _dropText;
        _drop.SetResourceReference(StyleProperty, "DropButton");
        AutomationProperties.SetAutomationId(_drop, "SkillShareDropZone");
        AutomationProperties.SetName(_drop, "지침 파일을 끌어 놓거나 고르는 곳");
        AutomationProperties.SetAutomationId(_text, "SkillShareText");
        AutomationProperties.SetName(_text, "지침 텍스트");
        AutomationProperties.SetName(_link, "깃허브 링크");
        _linkState.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _gitHub.Children.Add(_link);
        _gitHub.Children.Add(_linkState);
        // The tabs not shown are only hidden, so switching keeps the window's size.
        var source = new Grid { Height = 200 };
        source.Children.Add(_drop);
        source.Children.Add(_text);
        source.Children.Add(_gitHub);
        form.Children.Add(source);
        if (existing is not null)
        {
            // The new number (the next one, or a larger one the author writes) and what changed, on one line.
            var details = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
            var prefix = new TextBlock { Text = "v", FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0) };
            prefix.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            _version.Margin = new Thickness(0, 0, 10, 0);
            AutomationProperties.SetName(_version, "새 버전");
            DockPanel.SetDock(prefix, Dock.Left);
            DockPanel.SetDock(_version, Dock.Left);
            details.Children.Add(prefix);
            details.Children.Add(_version);
            details.Children.Add(_note);
            form.Children.Add(details);
        }
        var cancel = new Button { Content = "취소", IsCancel = true, MinWidth = 88 };
        _submit.SetResourceReference(StyleProperty, "PrimaryButton");
        if (existing is not null) _submit.Content = "새 버전 올리기";
        var footer = StoreLook.Footer(_error, cancel, _submit);
        var body = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);
        body.Children.Add(form);
        StoreLook.Dress(this, Title, body, existing is null ? null : $"{existing.DisplayTitle} · 지금 v{existing.Version}");
        RenderFile();
        if (existing?.Source is { Length: > 0 }) _gitHubTab.IsChecked = true;
        ShowSource();
        // Files can be dropped anywhere on the window; text dragged into a box still goes to the box.
        AllowDrop = true;
        PreviewDragEnter += (_, e) => DragFiles(e);
        PreviewDragOver += (_, e) => DragFiles(e);
        PreviewDragLeave += (_, _) => Highlight(false);
        PreviewDrop += (_, e) =>
        {
            Highlight(false);
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Handled = true;
            if (!_busy && e.Data.GetData(DataFormats.FileDrop) is string[] paths) LoadDropped(paths);
        };
        _drop.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Title = "공유할 지침 파일", Filter = "지침 파일 (*.md, *.txt)|*.md;*.markdown;*.txt|모든 파일 (*.*)|*.*" };
            if (picker.ShowDialog(this) == true) LoadFile(picker.FileName);
        };
        _submit.Click += async (_, _) => { if (await SubmitAsync()) DialogResult = true; };
        Closed += (_, _) => _linkPause.Stop();
        Loaded += (_, _) => (existing is null ? _title : _note).Focus();
    }

    // Under the title: the category (typed, or picked from the store's with ⌄) and 나만 보기.
    private UIElement Options()
    {
        var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var category = new Grid { Width = 220 };
        _category.Padding = new Thickness(11, 8, 34, 8);
        AutomationProperties.SetName(_category, "카테고리");
        category.Children.Add(_category);
        _categoryPick.SetResourceReference(StyleProperty, "IconButton");
        _categoryPick.Width = 30;
        _categoryPick.Height = 30;
        _categoryPick.HorizontalAlignment = HorizontalAlignment.Right;
        _categoryPick.Margin = new Thickness(0, 0, 3, 0);
        _categoryPick.Visibility = _knownCategories.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_categoryPick, "카테고리 고르기");
        _categoryPick.Click += (_, _) =>
        {
            var menu = StoreLook.Menu();
            foreach (string name in _knownCategories) StoreLook.Item(menu, name, () => _category.Text = name);
            StoreLook.Open(menu, _categoryPick);
        };
        category.Children.Add(_categoryPick);
        DockPanel.SetDock(category, Dock.Left);
        row.Children.Add(category);
        var only = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _private.SetResourceReference(StyleProperty, "StoreSwitch");
        AutomationProperties.SetName(_private, "나만 보기");
        var label = new TextBlock { Text = "나만 보기", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
            ToolTip = "켜면 나에게만 보이고, 다른 사용자는 적용한 스킬도 쓸 수 없게 됩니다." };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        label.MouseLeftButtonUp += (_, _) => _private.IsChecked = _private.IsChecked != true;
        only.Children.Add(label);
        only.Children.Add(_private);
        // Who sees a skill is its author's: a store admin's new version of someone else's skill leaves it as it is.
        only.Visibility = Mine ? Visibility.Visible : Visibility.Collapsed;
        row.Children.Add(only);
        return row;
    }

    internal string TitleText { get => _title.Text; set => _title.Text = value; }
    internal string VersionText { get => _version.Text; set => _version.Text = value; }
    internal string NoteText { get => _note.Text; set => _note.Text = value; }
    internal string CategoryText { get => _category.Text; set => _category.Text = value; }
    internal string OriginText { get => _origin.Text; set => _origin.Text = value; }
    internal bool PrivateOnly { get => _private.IsChecked == true; set => _private.IsChecked = value; }
    private bool Mine => _existing is null || string.Equals(_existing.Author, _user, StringComparison.OrdinalIgnoreCase);
    internal string LinkText { get => _link.Text; set => _link.Text = value; }
    internal string LinkState => _linkState.Text;
    internal string ErrorText => _error.Text;
    internal string? FileLabel => _file?.Label;
    internal From SourceTab
    {
        get => _gitHubTab.IsChecked == true ? From.GitHub : _fileTab.IsChecked == true ? From.File : From.Text;
        set => (value switch { From.GitHub => _gitHubTab, From.File => _fileTab, _ => _textTab }).IsChecked = true;
    }
    internal bool TextMode { get => SourceTab == From.Text; set => SourceTab = value ? From.Text : From.File; }
    internal string SkillText { get => _text.Text; set => _text.Text = value; }
    internal bool NameLocked => _existing is not null;

    // English words in the title become the name ("Impala 조회" → "impala"); a Korean-only title gives none.
    internal static string SuggestName(string title) =>
        Regex.Replace(Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "-"), "-{2,}", "-").Trim('-') is { Length: > 0 } name
            ? name.Length > 64 ? name[..64].TrimEnd('-') : name : "";

    /// <summary>This PC's account as store names carry it ("test_07" → "test-07"); one without English letters or digits by a short
    /// hash of it.</summary>
    internal static string OwnerName(string user)
    {
        string owner = SuggestName(user);
        if (owner.Length == 0)
            owner = "u" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user.ToLowerInvariant())))[..6].ToLowerInvariant();
        return owner.Length > 20 ? owner[..20].TrimEnd('-') : owner;
    }

    /// <summary>A new share's store name: the SKILL.md's own name, else the file's or the title's English words ("skill" when there
    /// are none), then this PC's account; numbered when the store already has that name.</summary>
    internal static string NameFor(SkillSourceFile file, string title, string user, IEnumerable<string> taken)
    {
        string owner = OwnerName(user);
        string stem = new[] { file.Name is { } own && InstalledSkills.ValidName(own) ? own : "", SuggestName(file.Stem), SuggestName(title) }
            .FirstOrDefault(candidate => candidate.Length > 0) ?? "skill";
        if (stem.EndsWith("-" + owner, StringComparison.Ordinal)) stem = stem[..^(owner.Length + 1)];
        int room = 64 - owner.Length - 5; // "-" + the account, and "-NNN" for a repeat
        if (stem.Length > room) stem = stem[..room].TrimEnd('-');
        var used = new HashSet<string>(taken, StringComparer.Ordinal);
        string name = stem + "-" + owner;
        for (int repeat = 2; used.Contains(name); repeat++) name = $"{stem}-{owner}-{repeat}";
        return name;
    }

    /// <summary>Files dropped on the window, in any tab: exactly one file or skill folder, shown in the 파일 tab.</summary>
    internal void LoadDropped(IReadOnlyList<string> paths)
    {
        _fileTab.IsChecked = true;
        if (paths.Count != 1) { _error.Text = "파일은 하나만 끌어 놓으세요."; return; }
        LoadFile(paths[0]);
    }

    internal void LoadFile(string path)
    {
        try { _file = SkillSourceFile.Read(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            _error.Text = exception is InvalidDataException ? exception.Message : "파일을 읽지 못했습니다 · " + exception.Message;
            return;
        }
        _error.Text = "";
        _fileTab.IsChecked = true;
        SyncTitle();
        RenderFile();
    }

    /// <summary>Reads the GitHub link through the relay (which follows it afterwards): its title fills the title box.</summary>
    internal async Task<bool> LoadLinkAsync()
    {
        string link = _link.Text.Trim();
        if (link.Length == 0) return false;
        if (_linked is not null && link == _linkedFrom) return true;
        _linkState.Text = "읽는 중…";
        try
        {
            string content = await _store.FetchSourceAsync(link, CancellationToken.None);
            if (_link.Text.Trim() != link) return false;
            _linked = SkillSourceFile.FromLink(content, link);
            _linkedFrom = link;
            _linkState.Text = $"{_linked.Label} · {_linked.Body.Split('\n').Length}줄 · 깃허브에서 바뀌면 자동으로 새 버전이 됩니다";
            _linkState.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            SyncTitle();
            return true;
        }
        catch (Exception exception) when (exception is SkillStoreException or JsonException)
        {
            if (_link.Text.Trim() != link) return false;
            _linked = null;
            _linkState.Text = exception.Message;
            _linkState.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            return false;
        }
    }

    internal async Task<bool> SubmitAsync()
    {
        if (_busy) return false;
        var from = SourceTab;
        if (from == From.GitHub && !await LoadLinkAsync())
        {
            _error.Text = _link.Text.Trim().Length == 0 ? "깃허브 링크를 넣으세요." : "깃허브 링크를 읽지 못했습니다.";
            return false;
        }
        SyncTitle();
        string title = _title.Text.Trim(), note = _note.Text.Trim(), category = Regex.Replace(_category.Text, @"\s+", " ").Trim();
        string origin = Regex.Replace(_origin.Text, @"\s+", " ").Trim();
        var source = Source();
        int? version = null;
        string? problem = title.Length == 0 ? "제목을 쓰세요." :
            source is null ? (from == From.Text ? "지침을 쓰거나 붙여 넣으세요." : "파일을 끌어 놓거나 눌러서 고르세요.") :
            source.Body.Length > SkillSourceFile.MaxBodyLength ? "내용이 너무 깁니다(10만 자까지)." : null;
        if (problem is null && _existing is { } current)
        {
            if (!int.TryParse(_version.Text.Trim(), out int chosen) || chosen is < 1 or > 999_999) problem = "버전은 숫자로 씁니다.";
            else if (chosen <= current.Version) problem = $"버전은 지금 버전(v{current.Version})보다 큰 숫자로 씁니다.";
            else version = chosen;
        }
        string body = source?.BodyFor(title) ?? "";
        if (problem is null && body.Trim().Length == 0)
            problem = (from switch { From.Text => "텍스트", From.File => "파일", _ => "깃허브 파일" }) + "에 제목 말고 지침 내용이 없습니다.";
        if (problem is not null) { _error.Text = problem; return false; }
        // OpenCode picks a skill by its description: the SKILL.md's own, else the one shared before, else the title.
        string description = OneLine(source!.Description) ?? OneLine(_existing?.Description) ?? title;
        // Shared from a link, the skill follows it; from text or a file, it stops following one it had.
        string? link = from == From.GitHub ? _linkedFrom : _existing?.Source is { Length: > 0 } ? "" : null;
        SetBusy(true);
        _error.Text = _existing is null ? "공유하는 중…" : "새 버전을 올리는 중…";
        try
        {
            if (_existing is { } skill)
            {
                int published = await _store.PublishAsync(skill.Name, title, description, body, _user, CancellationToken.None, note, version,
                    Mine ? PrivateOnly : null, link, category, edit: true, origin: origin);
                // Only what goes with the text changed (나만 보기, the category, the 출처, the link): saved, and no new version.
                bool settingsChanged = PrivateOnly != skill.Private || category != (skill.Category ?? "") || origin != (skill.Origin ?? "") ||
                    link is not null && link != (skill.Source ?? "");
                if (published <= skill.Version && !settingsChanged)
                {
                    _error.Text = $"내용이 지금 버전(v{skill.Version})과 같아 새 버전을 만들지 않았습니다.";
                    return false;
                }
                SharedVersion = published;
            }
            else
            {
                var listed = await _store.ListAsync(CancellationToken.None);
                if (listed.FirstOrDefault(other => string.Equals(other.Author, _user, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(other.Title.Trim(), title, StringComparison.OrdinalIgnoreCase)) is { } mine)
                {
                    _error.Text = $"‘{mine.DisplayTitle}’은(는) 이미 공유했습니다. 그 스킬의 ‘수정’으로 올리세요.";
                    return false;
                }
                var taken = listed.Select(other => other.Name).ToList();
                for (int attempt = 1; ; attempt++)
                {
                    string name = NameFor(source, title, _user, taken);
                    try
                    {
                        SharedVersion = await _store.PublishAsync(name, title, description, body, _user, CancellationToken.None, "", null,
                            PrivateOnly, link, category, origin: origin);
                        break;
                    }
                    // Another PC took the name a moment ago: the next number.
                    catch (SkillStoreException exception) when (exception.Status == HttpStatusCode.Conflict && attempt < 3) { taken.Add(name); }
                }
            }
            // A store admin's new version of someone else's skill keeps it theirs.
            SharedTitle = StoreSkill.Shown(title, _existing?.Author ?? _user);
            _error.Text = "";
            return true;
        }
        catch (SkillStoreException exception)
        {
            _error.Text = exception.Message;
            return false;
        }
        finally { SetBusy(false); }
    }

    private static string? OneLine(string? text) =>
        Regex.Replace(text ?? "", @"\s+", " ").Trim() is { Length: > 0 } line ? line.Length > 500 ? line[..500] : line : null;

    // The chosen file's name in gold, or what to do.
    private void RenderFile()
    {
        _dropText.Text = _file?.Label ?? "파일을 끌어 놓거나 눌러서 고르세요";
        _dropText.FontWeight = _file is null ? FontWeights.Normal : FontWeights.SemiBold;
        _dropText.SetResourceReference(TextBlock.ForegroundProperty, _file is null ? "TextSecondary" : "Accent");
    }

    // The chosen tab is what is shared; the others keep what they hold.
    private void ShowSource()
    {
        var from = SourceTab;
        if (!_busy) _error.Text = "";
        _text.Visibility = from == From.Text ? Visibility.Visible : Visibility.Hidden;
        _drop.Visibility = from == From.File ? Visibility.Visible : Visibility.Hidden;
        _gitHub.Visibility = from == From.GitHub ? Visibility.Visible : Visibility.Hidden;
        SyncTitle();
        if (from == From.Text && _existing is { } skill && !_latestAsked) _ = FillLatestAsync(skill);
        if (from == From.GitHub && _link.Text.Trim().Length > 0 && _linked is null) _linkPause.Start();
    }

    /// <summary>What the chosen tab holds: the written text, the file, or the linked GitHub file; null when it is empty.</summary>
    private SkillSourceFile? Source() => SourceTab switch
    {
        From.Text => _text.Text.Trim().Length > 0 ? SkillSourceFile.FromText(_text.Text) : null,
        From.File => _file,
        _ => _linked,
    };

    // A new share takes the chosen tab's title until one is typed.
    private void SyncTitle()
    {
        if (_existing is not null || (_title.Text.Trim().Length > 0 && _title.Text != _autoTitle)) return;
        _title.Text = _autoTitle = Source()?.SuggestedTitle ?? "";
    }

    // A new version's text starts from the latest version's instructions, unless something was written meanwhile.
    private async Task FillLatestAsync(StoreSkill skill)
    {
        _latestAsked = true;
        try
        {
            string latest = (await _store.GetVersionAsync(skill.Name, null, apply: false, CancellationToken.None)).Body;
            if (_text.Text.Length == 0) _text.Text = latest;
        }
        catch (Exception exception) when (exception is SkillStoreException or JsonException)
        {
            _latestAsked = false;
            _error.Text = exception.Message;
        }
    }

    private void DragFiles(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = _busy ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
        Highlight(!_busy);
    }

    // A gold edge while a file is dragged over the window.
    private void Highlight(bool on)
    {
        if (on)
        {
            _drop.SetResourceReference(Control.BorderBrushProperty, "Accent");
            _drop.SetResourceReference(Control.BackgroundProperty, "SurfaceHover");
        }
        else
        {
            _drop.ClearValue(Control.BorderBrushProperty);
            _drop.ClearValue(Control.BackgroundProperty);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _submit.IsEnabled = _drop.IsEnabled = !busy;
    }
}
