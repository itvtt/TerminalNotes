using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace TerminalNotes.Setup;

/// <summary>
/// Per-user install of Terminal Notes: %LOCALAPPDATA%\Programs\TerminalNotes, Start menu and desktop shortcuts,
/// and an entry in Windows "Apps" (HKCU). The folder stays user-writable, so the app's own GitHub update
/// (UpdateService) keeps replacing files there. Settings and logs in %LOCALAPPDATA%\TerminalNotes are never touched.
/// A PC without the .NET 10 Desktop Runtime gets the bundled runtime in &lt;install folder&gt;\dotnet: the app's launcher
/// looks there first (AppHostDotNetSearch in TerminalNotes.csproj), so no administrator rights or download are needed.
/// </summary>
internal static class Installer
{
    internal const string AppName = "Terminal Notes";
    internal const string ExecutableName = "TerminalNotes.exe";
    internal const string UninstallerName = "uninstall.exe";
    internal const string RuntimeFolder = "dotnet";
    private const int RuntimeMajor = 10;
    // Test overrides (never set for users): TN_SETUP_UNINSTALL_KEY and TN_SETUP_SHORTCUT_DIR keep a test run
    // out of the real Windows "Apps" list, Start menu and desktop; TN_SETUP_IGNORE_INSTALLED_DOTNET acts as a PC without .NET.
    private static readonly string UninstallKey = Environment.GetEnvironmentVariable("TN_SETUP_UNINSTALL_KEY") is { Length: > 0 } testKey
        ? @"Software\" + testKey : @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TerminalNotes";
    private static readonly string? ShortcutDirectory =
        Environment.GetEnvironmentVariable("TN_SETUP_SHORTCUT_DIR") is { Length: > 0 } testDirectory ? testDirectory : null;
    private static readonly bool IgnoreInstalledDotNet = Environment.GetEnvironmentVariable("TN_SETUP_IGNORE_INSTALLED_DOTNET") == "1";
    // Files the user may have edited; an install over an existing copy keeps them (same rule as the updater).
    private static readonly string[] KeepFiles = ["공지.md"];

    internal static string DefaultInstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TerminalNotes");

    internal static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    internal static string SelfPath => Process.GetCurrentProcess().MainModule?.FileName ?? Assembly.GetExecutingAssembly().Location;

    internal static string StartMenuShortcut => Path.Combine(ShortcutDirectory is null
        ? Environment.GetFolderPath(Environment.SpecialFolder.Programs) : Path.Combine(ShortcutDirectory, "Programs"), AppName + ".lnk");

    internal static string DesktopShortcut => Path.Combine(ShortcutDirectory is null
        ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) : Path.Combine(ShortcutDirectory, "Desktop"), AppName + ".lnk");

    internal sealed class Options(string directory, bool shortcuts, bool desktop, bool register, bool launch)
    {
        public string Directory { get; } = directory;
        public bool Shortcuts { get; } = shortcuts;
        public bool Desktop { get; } = desktop;
        public bool Register { get; } = register;
        public bool Launch { get; } = launch;
    }

    /// <summary>Copies the embedded release into the install folder. Throws with a message for the user on failure.</summary>
    internal static void Install(Options options, Action<string> progress)
    {
        string target = Path.GetFullPath(options.Directory);
        EnsureNotRunning(target);
        progress("파일 준비 중…");
        string staging = Path.Combine(Path.GetTempPath(), "TerminalNotes-Setup-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
                ?? throw new InvalidOperationException("설치 파일에 앱 패키지가 없습니다. 설치 파일을 다시 받으세요."))
            using (var archive = new ZipArchive(payload, ZipArchiveMode.Read))
                archive.ExtractToDirectory(staging);
            if (!File.Exists(Path.Combine(staging, ExecutableName)))
                throw new InvalidOperationException("앱 패키지에 " + ExecutableName + "가 없습니다.");

            progress("파일 복사 중…");
            Directory.CreateDirectory(target);
            foreach (string source in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                string relative = source.Substring(staging.Length).TrimStart('\\', '/');
                string destination = Path.Combine(target, relative);
                if (KeepFiles.Contains(relative, StringComparer.OrdinalIgnoreCase) && File.Exists(destination)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }
            WriteUninstaller(target);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }

        if (!HasDesktopRuntime(target))
        {
            progress(".NET 실행 환경 설치 중…");
            InstallBundledRuntime(target);
        }

        string exe = Path.Combine(target, ExecutableName);
        if (options.Shortcuts)
        {
            progress("바로 가기 만드는 중…");
            CreateShortcut(StartMenuShortcut, exe, target);
            if (options.Desktop) CreateShortcut(DesktopShortcut, exe, target);
        }
        if (options.Register)
        {
            progress("Windows 앱 목록에 등록 중…");
            Register(target);
        }
        progress("설치 완료");
        if (options.Launch) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = target });
    }

    /// <summary>
    /// The .NET 10 Desktop Runtime the app needs: next to the app (an earlier Setup put it there), in DOTNET_ROOT,
    /// or installed for the machine. Both shared frameworks and a host resolver must be present.
    /// </summary>
    internal static bool HasDesktopRuntime(string target)
    {
        if (IsRuntimeRoot(Path.Combine(target, RuntimeFolder))) return true;
        if (IgnoreInstalledDotNet) return false;
        foreach (string? root in InstalledDotNetRoots())
            if (!string.IsNullOrWhiteSpace(root) && IsRuntimeRoot(root!)) return true;
        return false;
    }

    private static string?[] InstalledDotNetRoots()
    {
        string? registered = null;
        try
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? setup = machine.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64");
            registered = setup?.GetValue("InstallLocation") as string;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return
        [
            Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            registered,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet")
        ];
    }

    private static bool IsRuntimeRoot(string root)
    {
        try
        {
            bool HasMajor(string path) => Directory.Exists(path) &&
                Directory.EnumerateDirectories(path).Any(folder => System.Version.TryParse(Path.GetFileName(folder).Split('-')[0], out var version) && version.Major == RuntimeMajor);
            return HasMajor(Path.Combine(root, "shared", "Microsoft.NETCore.App")) &&
                HasMajor(Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App")) &&
                Directory.Exists(Path.Combine(root, "host", "fxr")) && Directory.EnumerateDirectories(Path.Combine(root, "host", "fxr")).Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Unpacks the bundled .NET 10 runtime into &lt;target&gt;\dotnet (replacing an incomplete copy).</summary>
    private static void InstallBundledRuntime(string target)
    {
        using Stream runtime = Assembly.GetExecutingAssembly().GetManifestResourceStream("runtime.zip")
            ?? throw new InvalidOperationException(
                "이 PC에 .NET 10 Desktop Runtime이 없고, 이 설치 파일에는 실행 환경이 들어 있지 않습니다. 릴리스의 TerminalNotes-Setup.exe로 다시 설치하세요.");
        string destination = Path.Combine(target, RuntimeFolder);
        string staging = destination + ".setup-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var archive = new ZipArchive(runtime, ZipArchiveMode.Read))
                archive.ExtractToDirectory(staging);
            if (!IsRuntimeRoot(staging))
                throw new InvalidDataException("설치 파일의 .NET 실행 환경이 손상되었습니다. 설치 파일을 다시 받으세요.");
            TryDeleteDirectory(destination);
            if (Directory.Exists(destination))
                throw new IOException("이전 .NET 실행 환경 폴더를 지우지 못했습니다: " + destination);
            Directory.Move(staging, destination);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    // The small uninstaller built alongside the Setup (no payloads); a Setup without one copies itself.
    private static void WriteUninstaller(string target)
    {
        string destination = Path.Combine(target, UninstallerName);
        using Stream? uninstaller = Assembly.GetExecutingAssembly().GetManifestResourceStream(UninstallerName);
        if (uninstaller is not null)
        {
            using var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            uninstaller.CopyTo(file);
        }
        else if (!string.Equals(Path.GetFullPath(SelfPath), destination, StringComparison.OrdinalIgnoreCase))
            File.Copy(SelfPath, destination, overwrite: true);
    }

    /// <summary>Removes shortcuts, the Apps entry and the install folder. User settings and logs stay.</summary>
    internal static void Uninstall(string directory, Action<string> progress)
    {
        string target = Path.GetFullPath(directory);
        if (!File.Exists(Path.Combine(target, ExecutableName)))
            throw new InvalidOperationException("Terminal Notes 설치 폴더가 아닙니다: " + target);
        EnsureNotRunning(target);
        progress("바로 가기 삭제 중…");
        foreach (string link in new[] { StartMenuShortcut, DesktopShortcut })
            if (File.Exists(link) && ShortcutTarget(link) is { } linked &&
                string.Equals(Path.GetDirectoryName(Path.GetFullPath(linked)), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                File.Delete(link);
        using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
            if (key?.GetValue("InstallLocation") is string location &&
                string.Equals(Path.GetFullPath(location).TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        progress("파일 삭제 중…");
        string self = Path.GetFullPath(SelfPath);
        foreach (string file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
            if (!string.Equals(Path.GetFullPath(file), self, StringComparison.OrdinalIgnoreCase)) File.Delete(file);
        foreach (string folder in Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        // The running uninstaller cannot delete itself: a detached cmd removes it and the folder once it exits.
        if (self.StartsWith(target.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & del /f /q \"{self}\" & rmdir \"{target}\"")
                { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        else if (!Directory.EnumerateFileSystemEntries(target).Any()) Directory.Delete(target);
        progress("제거 완료");
    }

    /// <summary>Install location recorded in Windows "Apps", if Terminal Notes was installed for this user.</summary>
    internal static string? RegisteredInstallDirectory()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key?.GetValue("InstallLocation") as string;
    }

    internal static bool HasWebView2Runtime()
    {
        const string client = @"Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        foreach (var (hive, path) in new[] { (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\" + client), (Registry.LocalMachine, @"SOFTWARE\" + client), (Registry.CurrentUser, @"Software\" + client) })
        {
            using var key = hive.OpenSubKey(path);
            if (key?.GetValue("pv") is string version && version.Length > 0 && version != "0.0.0.0") return true;
        }
        return false;
    }

    private static void EnsureNotRunning(string target)
    {
        string exe = Path.Combine(target, ExecutableName);
        foreach (var process in Process.GetProcessesByName("TerminalNotes"))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Terminal Notes가 실행 중입니다. 앱을 종료한 뒤 다시 실행하세요.");
            }
            catch (System.ComponentModel.Win32Exception) { }
            finally { process.Dispose(); }
        }
    }

    private static void Register(string target)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        string uninstaller = Path.Combine(target, UninstallerName);
        long sizeKb = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length) / 1024;
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", AppName);
        key.SetValue("InstallLocation", target);
        key.SetValue("DisplayIcon", Path.Combine(target, ExecutableName));
        key.SetValue("UninstallString", $"\"{uninstaller}\" /uninstall");
        key.SetValue("QuietUninstallString", $"\"{uninstaller}\" /uninstall /S");
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeKb), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void CreateShortcut(string path, string exe, string workingDirectory)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("바로 가기를 만들 수 없습니다.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = exe;
            link.WorkingDirectory = workingDirectory;
            link.IconLocation = exe + ",0";
            link.Description = AppName;
            link.Save();
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }

    private static string? ShortcutTarget(string path)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return null;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try { return (string)shell.CreateShortcut(path).TargetPath; }
        catch (Exception) { return null; }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
