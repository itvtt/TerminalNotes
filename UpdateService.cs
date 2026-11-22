using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TerminalNotes.Services;

/// <summary>Where to look for new builds: a GitHub (or GitHub Enterprise) repository's releases.</summary>
public sealed record UpdateSettings(string Api, string Repository, string? Token, string Asset)
{
    public const string DefaultAsset = "TerminalNotes-win-x64.zip";
}

public sealed record AvailableUpdate(Version Version, string Tag, string AssetUrl, long Size, string? Notes);

/// <summary>The release repository is not readable with the built-in token (missing, wrong, or without read access).</summary>
public sealed class UpdateAccessException(string repository, int status) : InvalidOperationException(
    $"업데이트 저장소({repository})에 접근할 수 없습니다 ({status}). .env의 UPDATE_TOKEN(읽기 권한)을 확인하세요.");

/// <summary>
/// Checks the latest GitHub release built by the self-hosted Actions runner, downloads its package,
/// and hands the file swap to a small updater that runs after this process exits.
/// </summary>
public sealed class UpdateService(UpdateSettings settings, HttpMessageHandler? handler = null)
{
    private const string ExecutableName = "TerminalNotes.exe";

    // update.json is written next to the app at build time from .env (UPDATE_*); tests point elsewhere.
    public static UpdateSettings? LoadSettings()
    {
        string path = AppContext.GetData("TerminalNotes.UpdateConfigPath") as string
            ?? Path.Combine(AppContext.BaseDirectory, "update.json");
        try
        {
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            string Read(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()!.Trim() : "";
            string repository = Read("Repository");
            if (repository.Length == 0 || !repository.Contains('/')) return null;
            string api = Read("Api");
            string asset = Read("Asset");
            string token = Read("Token");
            api = api.Length == 0 ? "https://api.github.com" : api.TrimEnd('/');
            return new UpdateSettings(api, repository,
                token.Length == 0 ? LauncherToken(api, repository) : token, asset.Length == 0 ? UpdateSettings.DefaultAsset : asset);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // update.json 에 토큰이 없으면: 이 PC 사용자가 i-luncher 에서 이 프로젝트에 넣은 토큰(앱 우클릭 → 토큰…, RepositoryTokens "호스트/소유자/이름"),
    // 없으면 그 호스트의 토큰(Tokens, 마스터 PC 의 git 로그인). i-luncher AppKit 의 LauncherToken 과 같음.
    // 비공개 저장소의 릴리스를 GitHub 에 바로 묻는 데 씀. 런처와 같은 사용자 · PC 에서만 풀림. 없으면 null (공개 저장소는 토큰 없이)
    internal static string? LauncherToken(string api, string? repository = null)
    {
        if (!Uri.TryCreate(api, UriKind.Absolute, out var uri)) return null;
        string host = (uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}").ToLowerInvariant();
        if (host is "api.github.com" or "www.github.com") host = "github.com";
        try
        {
            // 런처 설정 폴더: C:\i-luncher, 그 폴더를 쓸 수 없는 PC 와 예전 런처는 %LOCALAPPDATA%\i-luncher
            string? path = (Environment.GetEnvironmentVariable("ILUNCHER_DATA_DIR") is { Length: > 0 } folder
                    ? new[] { folder }
                    : new[] { @"C:\i-luncher", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "i-luncher") })
                .Select(data => Path.Combine(data, "settings.json")).FirstOrDefault(File.Exists);
            if (path is null) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            string project = repository is { Length: > 0 } ? (host + "/" + repository.Trim('/')).ToLowerInvariant() : "";
            foreach (var (property, key) in new[] { ("RepositoryTokens", project), ("Tokens", host) })
            {
                if (key.Length == 0 || !document.RootElement.TryGetProperty(property, out var tokens) || tokens.ValueKind != JsonValueKind.Object) continue;
                foreach (var item in tokens.EnumerateObject())
                    if (string.Equals(item.Name, key, StringComparison.OrdinalIgnoreCase) && item.Value.GetString() is { Length: > 0 } stored)
                    {
                        byte[] plain = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(stored),
                            Encoding.UTF8.GetBytes("i-luncher/github-token/v1"), System.Security.Cryptography.DataProtectionScope.CurrentUser);
                        if (Encoding.UTF8.GetString(plain).Trim() is { Length: > 0 } token) return token;
                    }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or FormatException
            or InvalidOperationException or System.Security.Cryptography.CryptographicException) { }
        return null;
    }

    /// <summary>
    /// Installed with TerminalNotes-Setup.exe (HKCU "Apps" entry pointing at this folder): keep the version shown in
    /// Windows "Apps" equal to the version that actually runs, since the app replaces its own files when it updates.
    /// </summary>
    public static void SyncInstalledVersion()
    {
        const string uninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TerminalNotes";
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(uninstallKey, writable: true);
            if (key?.GetValue("InstallLocation") is not string location ||
                !string.Equals(Path.GetFullPath(location).TrimEnd('\\'), AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
            string version = CurrentVersion.ToString(3);
            if (!string.Equals(key.GetValue("DisplayVersion") as string, version, StringComparison.Ordinal)) key.SetValue("DisplayVersion", version);
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException) { }
    }

    public static Version CurrentVersion => Normalize(Version.TryParse(ApplicationDiagnostics.ApplicationVersion, out var version) ? version : new Version(0, 0, 0));

    private HttpClient CreateClient(string accept)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TerminalNotes", CurrentVersion.ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        if (settings.Token is { } token) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Returns the latest published release when it is newer than <paramref name="current"/>.</summary>
    public async Task<AvailableUpdate?> CheckAsync(Version current, CancellationToken token)
    {
        using var client = CreateClient("application/vnd.github+json");
        using var response = await client.GetAsync($"{settings.Api}/repos/{settings.Repository}/releases/latest", token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // GitHub answers 404 both for "no release yet" and for a private repository the token cannot read.
            using var repository = await client.GetAsync($"{settings.Api}/repos/{settings.Repository}", token);
            if (repository.IsSuccessStatusCode) return null;
            throw new UpdateAccessException(settings.Repository, (int)repository.StatusCode);
        }
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new UpdateAccessException(settings.Repository, (int)response.StatusCode);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = document.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsed)) return null;
        var latest = Normalize(parsed);
        if (latest <= Normalize(current)) return null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), settings.Asset, StringComparison.OrdinalIgnoreCase)) continue;
            // The API asset URL (with Accept: octet-stream) works for private and Enterprise repositories.
            string url = asset.GetProperty("url").GetString() ?? "";
            long size = asset.TryGetProperty("size", out var length) ? length.GetInt64() : 0;
            string? notes = root.TryGetProperty("body", out var body) ? body.GetString() : null;
            return url.Length == 0 ? null : new AvailableUpdate(latest, tag, url, size, notes);
        }
        return null;
    }

    /// <summary>Downloads and validates the package; returns the local zip path.</summary>
    public async Task<string> DownloadAsync(AvailableUpdate update, string directory, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"TerminalNotes-{update.Version}.zip");
        string partial = path + ".part";
        using (var client = CreateClient("application/octet-stream"))
        using (var response = await client.GetAsync(update.AssetUrl, HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var target = File.Create(partial);
            await source.CopyToAsync(target, token);
        }
        using (var archive = ZipFile.OpenRead(partial))
        {
            if (!archive.Entries.Any(entry => string.Equals(Path.GetFileName(entry.FullName), ExecutableName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("업데이트 패키지에 TerminalNotes.exe가 없습니다.");
        }
        File.Move(partial, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// Builds the updater: waits for this process to exit, unpacks the package over the install folder
    /// (keeping 공지.md), then starts the new version.
    /// </summary>
    public static string BuildUpdaterScript(string zipPath, string installDirectory, int processId, string logPath, bool restart = true)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        return string.Join("\n",
            "$ErrorActionPreference = 'Stop'",
            $"$zip = {Quote(zipPath)}; $target = {Quote(installDirectory.TrimEnd('\\'))}; $log = {Quote(logPath)}",
            "function Log($m) { Add-Content -LiteralPath $log -Value ((Get-Date -Format s) + ' ' + $m) -Encoding UTF8 }",
            "try {",
            $"  Log 'waiting for process {processId}'",
            $"  Wait-Process -Id {processId} -ErrorAction SilentlyContinue",
            "  $staging = Join-Path $env:TEMP ('TerminalNotesUpdate_' + [guid]::NewGuid().ToString('N'))",
            "  Expand-Archive -LiteralPath $zip -DestinationPath $staging -Force",
            "  $root = $staging",
            "  if (-not (Test-Path -LiteralPath (Join-Path $root 'TerminalNotes.exe'))) {",
            "    $sub = Get-ChildItem -LiteralPath $staging -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'TerminalNotes.exe') } | Select-Object -First 1",
            "    if ($sub) { $root = $sub.FullName } else { throw 'package has no TerminalNotes.exe' }",
            "  }",
            "  $keep = @('공지.md')",
            "  for ($i = 0; $i -lt 20; $i++) {",
            "    try {",
            "      Get-ChildItem -LiteralPath $root | Where-Object { -not ($keep -contains $_.Name -and (Test-Path -LiteralPath (Join-Path $target $_.Name))) } | Copy-Item -Destination $target -Recurse -Force",
            "      break",
            "    } catch { if ($i -eq 19) { throw }; Start-Sleep -Seconds 1 }",
            "  }",
            "  Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue",
            "  Log 'updated'",
            restart ? "  Start-Process -FilePath (Join-Path $target 'TerminalNotes.exe')" : "",
            "} catch { Log ('failed: ' + $_.Exception.Message)" + (restart ? "; Start-Process -FilePath (Join-Path $target 'TerminalNotes.exe')" : "") + " }");
    }

    public static void LaunchUpdater(string zipPath, string installDirectory, string logPath)
    {
        string script = BuildUpdaterScript(zipPath, installDirectory, Environment.ProcessId, logPath);
        // An inline encoded command is not a script file, so a restrictive execution policy cannot block it.
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}")
        { UseShellExecute = false, CreateNoWindow = true };
        System.Diagnostics.Process.Start(start)?.Dispose();
    }

    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));
}
