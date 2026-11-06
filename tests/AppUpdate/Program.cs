using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using TerminalNotes.Services;

// Auto-update against a local fake GitHub API: release lookup, version comparison, token header,
// package download and validation, and the updater's file swap (without restarting anything).
string root = Path.Combine(Path.GetTempPath(), "TerminalNotes update " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int port = FreePort();
string api = $"http://127.0.0.1:{port}";
string? lastAuthorization = null, lastAccept = null;
string latestTag = "v9.9.9";
bool noRelease = false, repositoryVisible = true;
byte[] package = Zip(("TerminalNotes.exe", "new exe"), ("TerminalNotes.dll", "new dll"), ("공지.md", "packaged notice"));
byte[] badPackage = Zip(("readme.txt", "no exe"));
bool serveBad = false;

using var listener = new HttpListener();
listener.Prefixes.Add(api + "/");
listener.Start();
_ = Task.Run(async () =>
{
    while (listener.IsListening)
    {
        HttpListenerContext context;
        try { context = await listener.GetContextAsync(); } catch { return; }
        lastAuthorization = context.Request.Headers["Authorization"];
        lastAccept = context.Request.Headers["Accept"];
        string path = context.Request.Url!.AbsolutePath;
        byte[] body;
        if (path == "/repos/team/terminal-notes" && repositoryVisible)
        {
            body = Encoding.UTF8.GetBytes("""{"full_name":"team/terminal-notes"}""");
            context.Response.ContentType = "application/json";
        }
        else if (path == "/repos/team/terminal-notes/releases/latest" && !noRelease)
        {
            body = Encoding.UTF8.GetBytes($$"""{"tag_name":"{{latestTag}}","body":"notes","assets":[{"name":"other.zip","url":"{{api}}/assets/0","size":1},{"name":"TerminalNotes-win-x64.zip","url":"{{api}}/assets/7","size":{{package.Length}}}]}""");
            context.Response.ContentType = "application/json";
        }
        else if (path == "/assets/7") body = serveBad ? badPackage : package;
        else { context.Response.StatusCode = 404; body = []; }
        await context.Response.OutputStream.WriteAsync(body);
        context.Response.Close();
    }
});

var settings = new UpdateSettings(api, "team/terminal-notes", "read-token", UpdateSettings.DefaultAsset);
var service = new UpdateService(settings);

var update = await service.CheckAsync(new Version(1, 0, 110), CancellationToken.None);
Check(update is { } found && found.Version == new Version(9, 9, 9) && found.AssetUrl == api + "/assets/7", "newer release with the configured asset is offered");
Check(lastAuthorization == "Bearer read-token" && lastAccept!.Contains("vnd.github"), "the API call carries the read token and GitHub media type");
Check(await service.CheckAsync(new Version(9, 9, 9, 0), CancellationToken.None) is null, "the same version is not offered again");
latestTag = "v1.0.5";
Check(await service.CheckAsync(new Version(1, 0, 110), CancellationToken.None) is null, "an older release is never offered");
latestTag = "v9.9.9";
noRelease = true;
Check(await service.CheckAsync(new Version(1, 0, 110), CancellationToken.None) is null, "a readable repository without releases is not an error");
repositoryVisible = false;
try
{
    await service.CheckAsync(new Version(1, 0, 110), CancellationToken.None);
    Check(false, "a repository the token cannot read must be reported, not treated as 'no release'");
}
catch (UpdateAccessException error) { Check(error.Message.Contains("UPDATE_TOKEN"), "the access error names the token setting"); }
noRelease = false;
repositoryVisible = true;

string zip = await service.DownloadAsync(update!, Path.Combine(root, "updates"), CancellationToken.None);
Check(File.Exists(zip) && lastAccept!.Contains("octet-stream") && File.ReadAllBytes(zip).SequenceEqual(package), "the package downloads through the asset API");
serveBad = true;
try
{
    await service.DownloadAsync(update!, Path.Combine(root, "bad"), CancellationToken.None);
    Check(false, "a package without TerminalNotes.exe must be rejected");
}
catch (InvalidDataException) { }
Check(!Directory.GetFiles(Path.Combine(root, "bad"), "*.zip").Any(), "a rejected package leaves no installable zip");

// Run the real updater script against a temporary install folder (restart disabled).
string install = Path.Combine(root, "install");
Directory.CreateDirectory(install);
File.WriteAllText(Path.Combine(install, "TerminalNotes.exe"), "old exe");
File.WriteAllText(Path.Combine(install, "공지.md"), "user notice");
using var waited = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
waited.WaitForExit();
string log = Path.Combine(root, "update.log");
string script = UpdateService.BuildUpdaterScript(zip, install, waited.Id, log, restart: false);
var run = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe",
    "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
    { CreateNoWindow = true, UseShellExecute = false })!;
Check(run.WaitForExit(120_000) && run.ExitCode == 0, "the updater script finishes");
Check(File.ReadAllText(Path.Combine(install, "TerminalNotes.exe")) == "new exe" && File.ReadAllText(Path.Combine(install, "TerminalNotes.dll")) == "new dll",
    "the updater replaces the app files");
Check(File.ReadAllText(Path.Combine(install, "공지.md")) == "user notice",
    "the updater keeps the notice");
Check(File.ReadAllText(log).Contains("updated"), "the updater logs success");


listener.Stop();
try { Directory.Delete(root, true); } catch (IOException) { }
Console.WriteLine("PASS app update: release check, token, version guard, download validation, file swap keeping user files");
return 0;

static void Check(bool condition, string message)
{
    if (!condition) { Console.WriteLine("FAIL " + message); Environment.Exit(1); }
}

static byte[] Zip(params (string Name, string Text)[] files)
{
    using var memory = new MemoryStream();
    using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        foreach (var (name, text) in files)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
    return memory.ToArray();
}

static int FreePort()
{
    var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    int value = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    return value;
}
