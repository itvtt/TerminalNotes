using System.Text;
using TerminalNotes.Services;

// The editor is the first master account the build allows (.env MASTER_USERS, default test_01, test_07, test_08).
string editorName = ((string[])typeof(AnnouncementService).Assembly.GetType("TerminalNotes.Services.AnnouncementEditors")!
    .GetField("AllowedUsers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!)[0];
string editorAccount = @"DOMAIN\" + editorName;
string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes-announcements-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
try
{
    string source = Path.Combine(fixture, "project", "공지.md");
    string userOne = Path.Combine(fixture, "user-one");
    string account = editorAccount;
    var editor = new AnnouncementService(source, userOne, () => account);
    var reader = new AnnouncementService(source, Path.Combine(fixture, "user-two"), () => @"DOMAIN\reader");
    Require(AnnouncementService.IsEditorAccount(editorAccount.ToUpperInvariant()), "account leaf comparison is case insensitive");
    Require(!AnnouncementService.IsEditorAccount(editorName + "@example.com"), "similar account does not gain write permission");
    Require(!AnnouncementService.IsEditorAccount(editorName + @"\reader"), "only the actual account leaf is matched");
    Require(!new AnnouncementService(source, userOne, () => throw new InvalidOperationException()).CanEdit,
        "identity failure denies authorization");
    AnnouncementSnapshot empty = await editor.ReadAsync();
    Require(!empty.Exists && !await editor.HasUnreadAsync(empty), "missing board has no fabricated unread notice");
    await FailsAsync<UnauthorizedAccessException>(() => reader.SaveAsync(empty, "unauthorized"), "read-only user cannot mutate through service");
    Require(!File.Exists(source), "denied save creates no board");
    account = @"DOMAIN\reader";
    await FailsAsync<UnauthorizedAccessException>(() => editor.SaveAsync(empty, "changed identity"), "identity is rechecked at mutation");
    account = editorAccount;
    AnnouncementSnapshot first = await editor.SaveAsync(empty, "## 첫 공지\n\n본문 <script>literal only</script>");
    Require((await reader.ReadAsync()).Text == first.Text, "all accounts can read the saved board");
    Require(await editor.HasUnreadAsync(first), "newly saved content stays unread until displayed");
    await editor.MarkReadAsync(first);
    Require(!await editor.HasUnreadAsync(first), "displayed snapshot is persisted read");
    var restarted = new AnnouncementService(source, userOne, () => @"DOMAIN\reader");
    Require(!await restarted.HasUnreadAsync(await restarted.ReadAsync()), "read state survives service restart");
    Require(await reader.HasUnreadAsync(first), "another user's read state is independent");
    AnnouncementSnapshot second = await editor.SaveAsync(first, first.Text + "\n\n## 두 번째 공지\n변경");
    await editor.MarkReadAsync(first);
    Require(await editor.HasUnreadAsync(second), "update while old content is displayed stays unread");
    await editor.MarkReadAsync(second);
    Require(!await editor.HasUnreadAsync(second), "newly displayed update becomes read");
    await Task.WhenAll(editor.MarkReadAsync(first), editor.MarkReadAsync(second));
    Require(!await editor.HasUnreadAsync(second), "delayed old read-state writes cannot supersede the latest displayed snapshot");
    string statePath = Directory.GetFiles(Path.Combine(userOne, "announcements"), "*.read").Single();
    File.WriteAllBytes(statePath, [0xC0, 0xAF]);
    Require(await editor.HasUnreadAsync(second), "damaged read marker safely leaves content unread");
    await editor.MarkReadAsync(second);
    await FailsAsync<AnnouncementConflictException>(() => editor.SaveAsync(first, "stale draft"), "stale edit is rejected");
    Require((await editor.ReadAsync()).Text == second.Text, "stale edit preserves current source");
    File.WriteAllText(source, "## External\nchanged", new UTF8Encoding(false));
    await FailsAsync<AnnouncementConflictException>(() => editor.SaveAsync(second, "overwrite external"), "external edits are detected");
    Require(File.ReadAllText(source) == "## External\nchanged", "external edit remains intact");
    AnnouncementSnapshot external = await editor.ReadAsync();
    await FailsAsync<InvalidDataException>(() => editor.SaveAsync(external, new string('한', AnnouncementService.MaximumBytes)),
        "save limit counts UTF-8 bytes");
    Require((await editor.ReadAsync()).Revision == external.Revision, "oversize save preserves source");
    string tooManyLines = new('\n', AnnouncementService.MaximumLines);
    await FailsAsync<InvalidDataException>(() => editor.SaveAsync(external, tooManyLines), "newline-heavy save cannot create an oversized UI block tree");
    Require((await editor.ReadAsync()).Revision == external.Revision, "line-limit rejection preserves source");
    File.WriteAllText(source, tooManyLines);
    await FailsAsync<InvalidDataException>(() => editor.ReadAsync(), "newline-heavy source is rejected before UI rendering");
    File.WriteAllBytes(source, new byte[AnnouncementService.MaximumBytes + 1]);
    await FailsAsync<InvalidDataException>(() => editor.ReadAsync(), "oversize source is rejected before loading");
    File.WriteAllBytes(source, [0xC0, 0xAF]);
    await FailsAsync<DecoderFallbackException>(() => editor.ReadAsync(), "invalid UTF-8 is rejected without replacement");
    File.WriteAllText(source, "");
    AnnouncementSnapshot blank = await editor.ReadAsync();
    Require(blank.Exists && !await editor.HasUnreadAsync(blank), "blank source has no unread marker");
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await FailsAsync<OperationCanceledException>(() => editor.SaveAsync(blank, "cancelled", cancelled.Token), "cancelled save is rejected");
    Require(File.ReadAllText(source) == "", "cancelled save preserves source");
    Task<AnnouncementSnapshot>[] racing = [editor.SaveAsync(blank, "one"), editor.SaveAsync(blank, "two")];
    try { await Task.WhenAll(racing); } catch (IOException) { }
    Require(racing.Count(task => task.IsCompletedSuccessfully) == 1, "concurrent writers commit exactly one version");
    Require(new[] { "one", "two" }.Contains(File.ReadAllText(source)), "racing write leaves an intact winner");
    File.WriteAllText(source, "winner");
    await FailsAsync<AnnouncementConflictException>(() => editor.SaveAsync(empty, "initial creation"), "missing-file draft cannot overwrite a concurrent creation");
    string portable = Path.Combine(fixture, "portable");
    Require(AnnouncementService.ResolveSourcePath(portable) == Path.Combine(portable, "공지.md"), "standalone source is application adjacent");
    string project = Path.Combine(fixture, "locator-project");
    Directory.CreateDirectory(project);
    File.WriteAllText(Path.Combine(project, "TerminalNotes.csproj"), "");
    File.WriteAllText(Path.Combine(project, "launcher.app.json"), "{}");
    string nested = Path.Combine(project, "dist", "win-x64");
    Directory.CreateDirectory(nested);
    Require(AnnouncementService.ResolveSourcePath(nested) == Path.Combine(project, "공지.md"), "published project build uses mutable project source");
    Require(!Directory.EnumerateFiles(fixture, "*.tmp", SearchOption.AllDirectories).Any(), "atomic writes clean temporary files");
    Console.WriteLine("PASS: announcements authorization, persistence, unread versions, conflict protection, limits and source resolution.");

    // The shared board on the relay: what an editor saves on one PC, every other PC sees (사용자 지시).
    var relay = new FakeRelay();
    var http = new HttpClient(relay);
    string shipped = Path.Combine(fixture, "shipped", "공지.md");
    Directory.CreateDirectory(Path.GetDirectoryName(shipped)!);
    File.WriteAllText(shipped, "## 앱과 함께 온 공지", new UTF8Encoding(false));
    AnnouncementService Pc(string user, string pcAccount) =>
        new(shipped, Path.Combine(fixture, "shared-" + user), () => pcAccount, new AnnouncementRelay(new Uri("http://relay.test:5080"), "key", http));
    var editorPc = Pc("editor", editorAccount);
    var readerPc = Pc("reader", @"DOMAIN\reader");
    AnnouncementSnapshot before = await readerPc.ReadAsync();
    Require(before.Text == "## 앱과 함께 온 공지" && !before.Shared && before.RelayExists == false,
        "without a shared board the shipped file is shown");
    AnnouncementSnapshot shared = await editorPc.SaveAsync(await editorPc.ReadAsync(), "## 2026-10-22\n모두에게 보이는 공지");
    Require(shared.Shared && relay.Text == "## 2026-10-22\n모두에게 보이는 공지" && relay.Author == editorAccount,
        "an editor's save goes to the relay with the editor's account");
    AnnouncementSnapshot seen = await readerPc.ReadAsync();
    Require(seen.Shared && seen.Text == shared.Text && seen.Revision == shared.Revision && await readerPc.HasUnreadAsync(seen),
        "another PC sees the shared announcement as unread");
    await readerPc.MarkReadAsync(seen);
    int fullReads = relay.FullReads;
    Require(!await readerPc.HasUnreadAsync(await readerPc.ReadAsync()) && relay.FullReads == fullReads,
        "an unchanged board is answered by ETag (304) and stays read");
    await FailsAsync<UnauthorizedAccessException>(() => readerPc.SaveAsync(seen, "reader"), "a reader cannot save the shared board");
    relay.Text = "## 다른 PC에서 고침";
    relay.Revision = "OTHER";
    await FailsAsync<AnnouncementConflictException>(() => editorPc.SaveAsync(shared, "stale"), "a stale shared save is refused");
    relay.Offline = true;
    AnnouncementSnapshot offline = await Pc("reader", @"DOMAIN\reader").ReadAsync();
    Require(offline.Offline && offline.Text == shared.Text, "while the relay is unreachable the last shared board is shown");
    await FailsAsync<IOException>(() => editorPc.SaveAsync(offline, "offline"), "saving without the relay fails with a message");
    Require(offline.OfflineReason == "공지 서버(relay.test:5080)에 연결하지 못했습니다." &&
        Pc("reader", @"DOMAIN\reader").ShareStatus(offline)!.StartsWith('⚠'), "the window names the server this PC cannot reach");
    Require(relay.UserAgents.Count > 0 && relay.UserAgents.All(agent => agent == AnnouncementRelay.UserAgent && agent.StartsWith("TerminalNotes/")),
        "every relay request names the app version for the relay's PC list");
    Console.WriteLine("PASS: shared announcements via the relay: shipped file until the first save, editor-only saves, other PCs see it unread, ETag reads, conflicts and offline fallback.");

    // The earlier .NET relay has no announcement address: say so, for readers and on save, instead of a bare error.
    var legacy = new FakeRelay { Legacy = true };
    var legacyHttp = new HttpClient(legacy);
    AnnouncementService LegacyPc(string user, string pcAccount) =>
        new(shipped, Path.Combine(fixture, "legacy-" + user), () => pcAccount, new AnnouncementRelay(new Uri("http://relay.test:5080"), "key", legacyHttp));
    var legacyReader = LegacyPc("reader", @"DOMAIN\reader");
    AnnouncementSnapshot old = await legacyReader.ReadAsync();
    Require(old.Offline && old.Text == "## 앱과 함께 온 공지" && old.OfflineReason!.Contains("이전 버전") && legacyReader.ShareStatus(old)!.Contains("relay.py"),
        "an earlier relay is named as the reason the board is not shared");
    var legacyEditor = LegacyPc("editor", editorAccount);
    string legacyMessage = "";
    try { await legacyEditor.SaveAsync(await legacyEditor.ReadAsync(), "## 공유되지 않을 공지"); }
    catch (AnnouncementRelayException error) { legacyMessage = error.Message; }
    Require(legacyMessage.Contains("relay.py"), "saving to an earlier relay fails with the reason");
    var noRelay = new AnnouncementService(shipped, Path.Combine(fixture, "no-relay"), () => editorAccount);
    Require(noRelay.ShareStatus(await noRelay.ReadAsync())!.Contains("이 PC에만"), "an editor on a build without a relay is told saves stay on this PC");
    Console.WriteLine("PASS: an earlier relay and a build without a relay are named; the announcement window says why a board is not shared.");

    // The real relay.py: an editor saves on one PC, another PC (another loopback address, so the relay sees two PCs)
    // receives it, and both learn how many PCs have it; with the relay gone the window names the unreachable server.
    int port = FreePort();
    string relayRoot = Path.Combine(fixture, "relay");
    Directory.CreateDirectory(relayRoot);
    // The relay reads no .env: this run's settings go in as --set.
    string[] settings = [$"RELAY_PORT={port}", "RELAY_INGEST_KEY=announce-key", "RELAY_ADMIN_PASSWORD=announce-admin",
        $"RELAY_DATA_DIR={Path.Combine(relayRoot, "reports")}", $"RELAY_SKILLS_DIR={Path.Combine(relayRoot, "skills")}", $"RELAY_PLUGINS_DIR={Path.Combine(relayRoot, "plugins")}",
        $"RELAY_ANNOUNCEMENTS_DIR={Path.Combine(relayRoot, "announcements")}", $"RELAY_LOG_FILE={Path.Combine(relayRoot, "relay.log")}",
        "MASTER_USERS=" + editorName];
    var start = new System.Diagnostics.ProcessStartInfo("python") { UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = relayRoot };
    foreach (string argument in new[] { "-X", "utf8", FindRelay() }.Concat(settings.SelectMany(setting => new[] { "--set", setting }))
        .Concat(["--bind", "127.0.0.1", "--quiet"])) start.ArgumentList.Add(argument);
    using (var server = System.Diagnostics.Process.Start(start)!)
    {
        try
        {
            string address = $"http://127.0.0.1:{port}";
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            for (int attempt = 0; ; attempt++)
            {
                if (server.HasExited) throw new Exception("relay.py exited: " + server.StandardError.ReadToEnd());
                try { if (await probe.GetStringAsync(address + "/health") == "ok") break; } catch (HttpRequestException) { }
                if (attempt > 150) throw new Exception("relay.py did not start");
                await Task.Delay(100);
            }
            using var editorHttp = FromPc("127.0.0.1");
            using var readerHttp = FromPc("127.0.0.2");
            AnnouncementService RealPc(string user, string pcAccount, HttpClient pcHttp) =>
                new(shipped, Path.Combine(fixture, "real-" + user), () => pcAccount, new AnnouncementRelay(new Uri(address), "announce-key", pcHttp));
            var realEditor = RealPc("editor", editorAccount, editorHttp);
            var realReader = RealPc("reader", @"DOMAIN\reader", readerHttp);
            AnnouncementSnapshot fresh = await realEditor.ReadAsync();
            Require(!fresh.Shared && fresh.RelayExists == false && realEditor.ShareStatus(fresh)!.Contains("아직 공유 공지가 없습니다") &&
                realReader.ShareStatus(await realReader.ReadAsync()) is null, "a new relay: the shipped file, and the editor is told the first save shares it");
            AnnouncementSnapshot posted = await realEditor.SaveAsync(fresh, "## 2026-10-22\n실제 서버로 공유한 공지");
            Require(posted.Shared && posted.SeenBy == 1 && realEditor.ShareStatus(posted)!.EndsWith("받은 PC 1대"), "after saving, the editor's PC has it");
            AnnouncementSnapshot received = await realReader.ReadAsync();
            Require(received.Shared && received.Text == "## 2026-10-22\n실제 서버로 공유한 공지" && received.Revision == posted.Revision &&
                received.SeenBy == 2 && await realReader.HasUnreadAsync(received), "the other PC receives the announcement unread and is counted");
            await realReader.MarkReadAsync(received);
            Require(!await realReader.HasUnreadAsync(await realReader.ReadAsync()), "read on the other PC stays read");
            Require((await realEditor.ReadAsync()).SeenBy == 2 && (await realEditor.ReadAsync()) is { SeenBy: 2, Shared: true },
                "the editor sees that two PCs have it, also from an unchanged (304) reply");
            await FailsAsync<UnauthorizedAccessException>(() => realReader.SaveAsync(received, "reader"), "the real relay refuses a reader's save");
            using var admin = new HttpRequestMessage(HttpMethod.Get, address + "/status");
            admin.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:announce-admin")));
            string status = await (await probe.SendAsync(admin)).Content.ReadAsStringAsync();
            Require(status.Contains("127.0.0.2") && status.Contains(AnnouncementRelay.UserAgent["TerminalNotes/".Length..]) && status.Contains("최신"),
                "the relay's status page lists the other PC, its app version and that it has the latest announcement");
            server.Kill(entireProcessTree: true);
            server.WaitForExit(5000);
            AnnouncementSnapshot gone = await realReader.ReadAsync();
            Require(gone.Offline && gone.Text == received.Text && gone.OfflineReason == $"공지 서버(127.0.0.1:{port})에 연결하지 못했습니다." &&
                realReader.ShareStatus(gone)!.StartsWith('⚠'), "with the relay gone the other PC shows the last board and names the server");
        }
        finally
        {
            if (!server.HasExited) { server.Kill(entireProcessTree: true); server.WaitForExit(5000); }
        }
    }
    Console.WriteLine("PASS: real relay.py: one PC saves, another PC receives it unread, both see how many PCs have it, the status page lists them, and an unreachable server is named.");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }
finally { Directory.Delete(fixture, recursive: true); }

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task FailsAsync<T>(Func<Task> action, string message) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception(message);
}

static int FreePort()
{
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static string FindRelay()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        // The relay lives in the ApiServer project next to this one (Projects\ApiServer\server); an older checkout kept it in relay\.
            string candidate = Path.Combine(directory.FullName, "ApiServer", "server", "relay.py");
            if (File.Exists(candidate)) return candidate;
            candidate = Path.Combine(directory.FullName, "relay", "relay.py");
        if (File.Exists(candidate)) return candidate;
    }
    throw new FileNotFoundException("ApiServer\\server\\relay.py (the API server project next to TerminalNotes) was not found above the test folder.");
}

// A client that connects from the given loopback address, so the relay sees requests from another PC.
static HttpClient FromPc(string localAddress) => new(new SocketsHttpHandler
{
    ConnectCallback = async (context, token) =>
    {
        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
        try
        {
            socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Parse(localAddress), 0));
            await socket.ConnectAsync(context.DnsEndPoint, token);
            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }
}) { Timeout = TimeSpan.FromSeconds(10) };

// relay.py's /api/announcements in memory: ETag reads, editor-only PUT with the base revision, and an offline switch.
sealed class FakeRelay : HttpMessageHandler
{
    internal string Text = "";
    internal string Revision = "missing";
    internal string? Author;
    internal bool Offline;
    internal bool Legacy;
    internal int FullReads;
    internal readonly List<string> UserAgents = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Offline) throw new HttpRequestException("relay offline");
        UserAgents.Add(request.Headers.TryGetValues("User-Agent", out var agents) ? string.Join(" ", agents) : "");
        if (Legacy) return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        if (!request.Headers.TryGetValues("X-TerminalNotes-Key", out var keys) || keys.Single() != "key")
            return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
        bool exists = Revision != "missing";
        if (request.Method == HttpMethod.Get)
        {
            string etag = "\"" + Revision + "\"";
            if (request.Headers.TryGetValues("If-None-Match", out var tags) && tags.Single() == etag)
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotModified);
            FullReads++;
            var response = Json(new { text = Text, revision = Revision, exists });
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
            return response;
        }
        using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var root = body.RootElement;
        string author = root.GetProperty("author").GetString()!;
        string editor = ((string[])typeof(AnnouncementService).Assembly.GetType("TerminalNotes.Services.AnnouncementEditors")!
            .GetField("AllowedUsers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!)[0];
        if (!author.EndsWith(editor, StringComparison.OrdinalIgnoreCase))
            return Json("이 Windows 계정은 공지를 작성할 수 없습니다.", System.Net.HttpStatusCode.Forbidden);
        if (root.GetProperty("expectedExists").GetBoolean() != exists || exists && root.GetProperty("expectedRevision").GetString() != Revision)
            return Json("공지가 다른 곳에서 변경되었습니다.", System.Net.HttpStatusCode.Conflict);
        Text = root.GetProperty("text").GetString()!;
        Revision = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Text)));
        Author = author;
        return Json(new { text = Text, revision = Revision, exists = true });
    }

    private static HttpResponseMessage Json(object value, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
}
