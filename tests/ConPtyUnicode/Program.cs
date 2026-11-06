using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using TerminalNotes.Services;

// Korean typed into a terminal reaches the program in it through the app's bundled ConPTY host
// (conpty.dll + x64\OpenConsole.exe; Windows 10's inbox host dropped it), and the inbox host stays as the fallback.
internal static class Program
{
    private static int Main()
    {
        try
        {
            string folder = AppContext.BaseDirectory;
            Require(File.Exists(Path.Combine(folder, "conpty.dll")) && File.Exists(Path.Combine(folder, "x64", "OpenConsole.exe")),
                "The bundled ConPTY files were not copied next to the app.");
            string bundled = RoundTrip(expectBundledHost: true);
            Environment.SetEnvironmentVariable("TERMINALNOTES_INBOX_CONPTY", "1");
            string inbox = RoundTrip(expectBundledHost: false);
            Environment.SetEnvironmentVariable("TERMINALNOTES_INBOX_CONPTY", null);
            Console.WriteLine($"PASS ConPTY Korean input: bundled OpenConsole host ({bundled}) and the inbox fallback ({inbox}) both echo typed Hangul exactly.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
    }

    // Types a Korean line into a PowerShell that reads one line and echoes it between markers.
    private static string RoundTrip(bool expectBundledHost)
    {
        string work = Path.Combine(Path.GetTempPath(), "TerminalNotes conpty " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var output = new ConcurrentQueue<string>();
        using var session = new ConPtySession();
        session.OutputReceived += output.Enqueue;
        try
        {
            session.Start(work, 100, 30, startupCommand: "$line = [Console]::In.ReadLine(); Write-Output ('<<' + $line + '>>')",
                exitOnStartupCommandCompletion: true);
            string Text() => string.Concat(output);
            Wait(() => session.ProcessId is not null, "the shell to start");
            bool hostSeen = false;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && !(hostSeen = OpenConsoleChildOfThisProcess())) Thread.Sleep(200);
            Require(hostSeen == expectBundledHost, expectBundledHost
                ? "The session did not run in the bundled OpenConsole host."
                : "The inbox fallback still started the bundled OpenConsole host.");
            Thread.Sleep(1500);
            session.Write("한글 입력 테스트 텍스트\r");
            Wait(() => Text().Contains("<<한글 입력 테스트 텍스트>>", StringComparison.Ordinal), "the Korean line to come back");
            return expectBundledHost ? "OpenConsole" : "conhost";
        }
        finally
        {
            session.CloseAsync().Wait(TimeSpan.FromSeconds(10));
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    // An OpenConsole.exe running from this test's own folder is the bundled host.
    private static bool OpenConsoleChildOfThisProcess()
    {
        string own = Path.Combine(AppContext.BaseDirectory, "x64", "OpenConsole.exe");
        foreach (var process in Process.GetProcessesByName("OpenConsole"))
            using (process)
                try { if (string.Equals(process.MainModule?.FileName, own, StringComparison.OrdinalIgnoreCase)) return true; }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return false;
    }

    private static void Wait(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Timed out waiting for " + what + ".");
            Thread.Sleep(100);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
