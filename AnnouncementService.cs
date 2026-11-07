using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace TerminalNotes.Services;

internal sealed record AnnouncementSnapshot(string Text, string Revision, bool Exists)
{
    internal bool HasContent => !string.IsNullOrWhiteSpace(Text);
    /// <summary>The relay's shared board, which every user sees.</summary>
    internal bool Shared { get; init; }
    /// <summary>The relay could not be reached: the last shared board this PC saw, or the local file.</summary>
    internal bool Offline { get; init; }
    /// <summary>Why the shared board could not be read (server unreachable, an earlier relay, a wrong key).</summary>
    internal string? OfflineReason { get; init; }
    /// <summary>How many PCs the relay says have this board (relay.py 2.1 and later).</summary>
    internal int? SeenBy { get; init; }
    /// <summary>The relay's board when this was read; a save must still match it.</summary>
    internal string? RelayRevision { get; init; }
    internal bool? RelayExists { get; init; }
}

/// <summary>
/// Who may add and edit announcements: only the Windows accounts listed here, matched against the Windows
/// identity's account name (DOMAIN\name → name). Separate from <c>LoopAutoRecommandUsers</c>.
/// </summary>
internal static class AnnouncementEditors
{
    /// <summary>
    /// Windows account names allowed to edit announcements: the master accounts, MASTER_USERS in .env (comma-separated),
    /// compiled into the assembly at build time (TerminalNotes.csproj). Without that line the default below applies (the same as
    /// LoopAutoRecommandUsers').
    /// </summary>
    internal static readonly string[] AllowedUsers = BuildAccountList("TerminalNotes.MasterUsers") ?? ["test_01", "test_07", "test_08"];

    internal static string[]? BuildAccountList(string key) =>
        typeof(AnnouncementEditors).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == key)?.Value is { } value
            ? value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(user => user[(user.LastIndexOf('\\') + 1)..]).Where(user => user.Length > 0).ToArray()
            : null;
}

/// <summary>
/// The announcement board. With a relay (relay.py /api/announcements) it is shared: what an editor saves, every user
/// sees (사용자 지시: 공지를 추가하면 다른 유저들도 추가된 공지를 봐야 함). Until the relay has a board, the 공지.md shipped
/// with the app is shown and an editor's first save goes to the relay; while the relay is unreachable, the last shared
/// board this PC saw is shown. Without a relay it is the local Markdown file, whose Windows ACL app authorization does
/// not replace.
/// </summary>
internal sealed class AnnouncementService
{
    internal const int MaximumBytes = 262_144;
    internal const int MaximumLines = 2048;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Func<string> _accountName;
    private readonly AnnouncementRelay? _relay;
    private readonly string _sharedCachePath;
    private readonly string _readStatePath;
    private readonly SemaphoreSlim _readStateGate = new(1, 1);
    private long _displaySequence;
    internal string SourcePath { get; }
    internal bool CanEdit
    {
        get
        {
            try { return IsEditorAccount(_accountName()); }
            catch { return false; }
        }
    }

    internal AnnouncementService() : this((AnnouncementRelay?)null) { }

    internal AnnouncementService(AnnouncementRelay? relay) : this(ResolveSourcePath(AppContext.BaseDirectory),
        AppContext.GetData("TerminalNotes.DataDirectory") is string directory ? directory :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerminalNotes"),
        CurrentWindowsAccount, relay) { }

    // These overloads are internal so isolated tests never impersonate an account or touch real AppData.
    internal AnnouncementService(string sourcePath, string userDataDirectory, Func<string> accountName)
        : this(sourcePath, userDataDirectory, accountName, null) { }

    internal AnnouncementService(string sourcePath, string userDataDirectory, Func<string> accountName, AnnouncementRelay? relay)
    {
        SourcePath = Path.GetFullPath(sourcePath);
        _accountName = accountName;
        _relay = relay;
        string sourceKey = Hash(Utf8.GetBytes(SourcePath.ToUpperInvariant()));
        _readStatePath = Path.Combine(userDataDirectory, "announcements", sourceKey + ".read");
        _sharedCachePath = Path.Combine(userDataDirectory, "announcements", "shared-board.json");
    }

    internal bool UsesRelay => _relay is not null;

    /// <summary>
    /// One line on where this board is and whether other PCs see it, for the announcement window; null when nothing needs
    /// saying (a reader looking at the shared board).
    /// </summary>
    internal string? ShareStatus(AnnouncementSnapshot snapshot)
    {
        if (snapshot.Offline) return "⚠ " + (snapshot.OfflineReason ?? "공지 서버에 연결하지 못했습니다.") + " 이 PC에 남은 공지를 보여 줍니다.";
        if (!CanEdit) return null;
        if (_relay is null) return "⚠ 중계 서버가 없는 빌드라 이 PC에만 저장됩니다(다른 PC에는 보이지 않음).";
        if (!snapshot.Shared) return "아직 공유 공지가 없습니다. 저장하면 모든 PC에 보입니다.";
        return "모든 PC에 보이는 공유 공지" + (snapshot.SeenBy is { } seen ? $" · 지금 이 공지를 받은 PC {seen}대" : "");
    }

    /// <summary>Where the board lives, for editors' tooltips.</summary>
    internal string SourceLabel => _relay is not null ? "공유 공지 · 중계 서버(모든 사용자에게 보임)" : SourcePath;

    private static string CurrentWindowsAccount()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.Name;
    }

    internal static bool IsEditorAccount(string account)
    {
        string leaf = account[(account.LastIndexOf('\\') + 1)..];
        return AnnouncementEditors.AllowedUsers.Any(user => string.Equals(leaf, user, StringComparison.OrdinalIgnoreCase));
    }

    internal static string ResolveSourcePath(string applicationDirectory)
    {
        string fullPath = Path.GetFullPath(applicationDirectory);
        for (DirectoryInfo? directory = new(fullPath); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TerminalNotes.csproj")) &&
                File.Exists(Path.Combine(directory.FullName, "launcher.app.json")))
                return Path.Combine(directory.FullName, "공지.md");
        return Path.Combine(fullPath, "공지.md");
    }

    internal Task<AnnouncementSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            using var timeout = OperationTimeout(cancellationToken);
            return await ReadCoreAsync(timeout.Token).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<AnnouncementSnapshot> ReadCoreAsync(CancellationToken cancellationToken)
    {
        if (_relay is not { } relay) return await ReadLocalAsync(cancellationToken).ConfigureAwait(false);
        using var relayTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        relayTimeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            AnnouncementRelay.Board board = await relay.ReadAsync(relayTimeout.Token).ConfigureAwait(false);
            ValidateLineCount(board.Text);
            if (board.Exists)
            {
                WriteSharedCache(board);
                return new(board.Text, board.Revision, true) { Shared = true, RelayRevision = board.Revision, RelayExists = true, SeenBy = board.SeenBy };
            }
            // No shared board yet: show the file shipped with the app; an editor's first save creates the shared board.
            return (await ReadLocalAsync(cancellationToken).ConfigureAwait(false)) with { RelayRevision = board.Revision, RelayExists = false };
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
            (error is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException ||
             error is AnnouncementRelayException))
        {
            string reason = error is AnnouncementRelayException ? error.Message : relay.UnreachableMessage;
            if (ReadSharedCache() is { } cached) return cached with { Offline = true, OfflineReason = reason };
            return (await ReadLocalAsync(cancellationToken).ConfigureAwait(false)) with { Offline = true, OfflineReason = reason };
        }
    }

    private async Task<AnnouncementSnapshot> ReadLocalAsync(CancellationToken cancellationToken)
    {
        byte[] bytes;
        try { bytes = await ReadBoundedAsync(SourcePath, MaximumBytes, cancellationToken).ConfigureAwait(false); }
        catch (FileNotFoundException) { return new("", "missing", false); }
        catch (DirectoryNotFoundException) { return new("", "missing", false); }
        string text = Utf8.GetString(bytes).TrimStart('\uFEFF');
        ValidateLineCount(text);
        return new(text, Hash(bytes), true);
    }

    internal Task<bool> HasUnreadAsync(AnnouncementSnapshot snapshot, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            if (!snapshot.HasContent) return false;
            using var timeout = OperationTimeout(cancellationToken);
            try
            {
                byte[] state = await ReadBoundedAsync(_readStatePath, 128, timeout.Token).ConfigureAwait(false);
                return !string.Equals(Utf8.GetString(state), snapshot.Revision, StringComparison.Ordinal);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException)
            { return true; }
        }, cancellationToken);

    internal Task MarkReadAsync(AnnouncementSnapshot displayed, CancellationToken cancellationToken = default)
    {
        long sequence = Interlocked.Increment(ref _displaySequence);
        return Task.Run(async () =>
        {
            if (!displayed.Exists || !displayed.HasContent) return;
            using var timeout = OperationTimeout(cancellationToken);
            await _readStateGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                if (sequence != Volatile.Read(ref _displaySequence)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(_readStatePath)!);
                await WriteAtomicAsync(_readStatePath, Utf8.GetBytes(displayed.Revision), timeout.Token).ConfigureAwait(false);
            }
            finally { _readStateGate.Release(); }
        }, cancellationToken);
    }

    internal Task<AnnouncementSnapshot> SaveAsync(AnnouncementSnapshot expected, string text,
        CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        // Check actual Windows identity again for every mutation, independently of UI visibility.
        if (!CanEdit) throw new UnauthorizedAccessException("이 Windows 계정은 공지를 작성할 수 없습니다.");
        if (Utf8.GetByteCount(text) > MaximumBytes) throw new InvalidDataException("공지는 UTF-8 기준 256KB까지 저장할 수 있습니다.");
        ValidateLineCount(text);
        byte[] bytes = Utf8.GetBytes(text);
        if (_relay is { } relay)
        {
            // The shared board: the relay checks the editor and the base revision again and refuses a stale save.
            using var relayTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            relayTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            AnnouncementRelay.Board saved;
            try
            {
                saved = await relay.SaveAsync(text, expected.RelayRevision ?? expected.Revision, expected.RelayExists ?? expected.Exists,
                    _accountName(), relayTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
                error is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
            { throw new IOException(relay.UnreachableMessage + " 잠시 후 다시 저장하세요. 쓴 내용은 창에 그대로 있습니다.", error); }
            WriteSharedCache(saved);
            return new AnnouncementSnapshot(saved.Text, saved.Revision, true) { Shared = true, RelayRevision = saved.Revision, RelayExists = true, SeenBy = saved.SeenBy };
        }
        using var timeout = OperationTimeout(cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(SourcePath)!);
        // Serializes cooperating app instances; the final hash comparison also detects external editors.
        using var writeLock = new FileStream(SourcePath + ".write.lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
        AnnouncementSnapshot current = await ReadCoreAsync(timeout.Token).ConfigureAwait(false);
        EnsureUnchanged(expected, current);
        string temporary = SourcePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await WriteNewAsync(temporary, bytes, timeout.Token).ConfigureAwait(false);
            current = await ReadCoreAsync(timeout.Token).ConfigureAwait(false);
            EnsureUnchanged(expected, current);
            timeout.Token.ThrowIfCancellationRequested();
            if (!CanEdit) throw new UnauthorizedAccessException("이 Windows 계정은 공지를 작성할 수 없습니다.");
            if (current.Exists) File.Replace(temporary, SourcePath, null);
            else File.Move(temporary, SourcePath); // Never overwrites a concurrently created board.
            return new AnnouncementSnapshot(text, Hash(bytes), true);
        }
        finally { TryDeleteTemporary(temporary); }
    }, cancellationToken);

    private AnnouncementSnapshot? ReadSharedCache()
    {
        try
        {
            if (!File.Exists(_sharedCachePath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(_sharedCachePath, Utf8));
            string text = document.RootElement.GetProperty("text").GetString() ?? "";
            string revision = document.RootElement.GetProperty("revision").GetString() ?? "";
            return revision.Length == 0 ? null : new(text, revision, true) { Shared = true, RelayRevision = revision, RelayExists = true };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or
            InvalidOperationException or DecoderFallbackException)
        { return null; }
    }

    private void WriteSharedCache(AnnouncementRelay.Board board)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_sharedCachePath)!);
            string temporary = _sharedCachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { text = board.Text, revision = board.Revision }), Utf8);
            File.Move(temporary, _sharedCachePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void EnsureUnchanged(AnnouncementSnapshot expected, AnnouncementSnapshot current)
    {
        if (expected.Exists != current.Exists || expected.Revision != current.Revision)
            throw new AnnouncementConflictException();
    }

    private static void ValidateLineCount(string text)
    {
        int lines = 1;
        foreach (char character in text)
            if (character == '\n' && ++lines > MaximumLines)
                throw new InvalidDataException("공지는 2,048줄까지 표시하고 저장할 수 있습니다.");
    }

    private static CancellationTokenSource OperationTimeout(CancellationToken token)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        return timeout;
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int limit, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > limit) throw new InvalidDataException("공지 파일 크기가 허용 범위를 초과했습니다.");
        using var content = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            if (content.Length + count > limit) throw new InvalidDataException("공지 파일 크기가 허용 범위를 초과했습니다.");
            content.Write(buffer, 0, count);
        }
        return content.ToArray();
    }

    private static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            8192, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken token)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await WriteNewAsync(temporary, bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { TryDeleteTemporary(temporary); }
    }

    private static void TryDeleteTemporary(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

internal sealed class AnnouncementConflictException() : IOException("공지가 다른 곳에서 변경되었습니다. 내용을 복사한 뒤 창을 다시 열어 주세요.") { }

internal sealed class AnnouncementRelayException(string message) : IOException(message) { }

/// <summary>The relay's shared board: GET with ETag (an unchanged board costs one short reply), PUT with the base revision.</summary>
internal sealed class AnnouncementRelay
{
    // The relay is a PC on the local network: reached directly, never through the system proxy (which may not reach it and
    // answers 502 instead).
    private static readonly HttpClient SharedHttp = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
    /// <summary>Sent with every relay request, so the relay's status page shows each PC's app version.</summary>
    internal static readonly string UserAgent = "TerminalNotes/" + (typeof(AnnouncementRelay).Assembly.GetName().Version is { } version
        ? $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}" : "0");
    private readonly Uri _endpoint;
    private readonly string _key;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private string? _etag;
    private Board? _last;

    internal sealed record Board(string Text, string Revision, bool Exists)
    {
        internal int? SeenBy { get; init; }
    }

    internal AnnouncementRelay(Uri address, string key, HttpClient? http = null)
    {
        _endpoint = new Uri(new Uri(address.AbsoluteUri.TrimEnd('/') + "/"), "api/announcements");
        _key = key;
        _http = http ?? SharedHttp;
        UnreachableMessage = $"공지 서버({address.Authority})에 연결하지 못했습니다.";
    }

    /// <summary>Names the server, so a user can tell which address their PC cannot reach.</summary>
    internal string UnreachableMessage { get; }

    internal static AnnouncementRelay? From(string? url, string? key) =>
        Uri.TryCreate(url, UriKind.Absolute, out var address) && address.Scheme is "http" or "https" ? new AnnouncementRelay(address, key ?? "") : null;

    internal async Task<Board> ReadAsync(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
        request.Headers.Add("X-TerminalNotes-Key", _key);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        Board? last;
        lock (_gate)
        {
            last = _last;
            if (_etag is not null && last is not null) request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
        }
        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified && last is not null) return last with { SeenBy = SeenBy(response) ?? last.SeenBy };
        await ThrowIfFailedAsync(response, token).ConfigureAwait(false);
        Board board = Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false)) with { SeenBy = SeenBy(response) };
        lock (_gate)
        {
            _etag = response.Headers.ETag?.ToString();
            _last = board;
        }
        return board;
    }

    internal async Task<Board> SaveAsync(string text, string expectedRevision, bool expectedExists, string author, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, _endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { text, expectedRevision, expectedExists, author }), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-TerminalNotes-Key", _key);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, token).ConfigureAwait(false);
        Board board = Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false)) with { SeenBy = SeenBy(response) };
        lock (_gate)
        {
            _etag = null;
            _last = board;
        }
        return board;
    }

    private static int? SeenBy(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-TerminalNotes-Seen-By", out var values) && int.TryParse(values.FirstOrDefault(), out int count) ? count : null;

    private static Board Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        return new(root.GetProperty("text").GetString() ?? "", root.GetProperty("revision").GetString() ?? "missing", root.GetProperty("exists").GetBoolean());
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        string detail = "";
        try
        {
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (body.StartsWith('"')) detail = JsonSerializer.Deserialize<string>(body) ?? "";
        }
        catch (Exception error) when (error is JsonException or HttpRequestException) { }
        throw response.StatusCode switch
        {
            HttpStatusCode.Conflict => new AnnouncementConflictException(),
            HttpStatusCode.Forbidden => new UnauthorizedAccessException(detail.Length > 0 ? detail : "이 Windows 계정은 공지를 작성할 수 없습니다."),
            HttpStatusCode.BadRequest when detail.Length is > 0 and < 300 => new InvalidDataException(detail),
            HttpStatusCode.Unauthorized => new AnnouncementRelayException("공지 서버 키가 맞지 않습니다(앱과 서버의 RELAY_INGEST_KEY가 다름)."),
            // relay.py always answers /api/announcements; the earlier .NET relay has no such address.
            HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed =>
                new AnnouncementRelayException("중계 서버가 이전 버전이라 공지를 공유하지 못합니다(서버를 relay.py로 바꿔야 함)."),
            _ => new AnnouncementRelayException($"공지 서버 응답 오류({(int)response.StatusCode}).")
        };
    }
}
