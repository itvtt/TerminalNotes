using System.IO;
using TerminalNotes;

// Why the PC went down before this boot (2026-11-12 사용자: "다음날 와보면 PC가 꺼져있는 경우가 있어"): each cause from synthetic
// System log entries, the app's last resource record before the boot, and (printed only) this PC's real log.
internal static class Program
{
    private static int _failures;

    private static void Check(bool condition, string name, string? detail = null)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + name + (condition || detail is null ? "" : " · " + detail));
        if (!condition) _failures++;
    }

    private static ShutdownCause.Entry Entry(int id, DateTime at, string provider = "", Dictionary<string, string>? named = null, params string[] values) =>
        new(id, provider, at, named ?? new Dictionary<string, string>(), values);

    private static int Main()
    {
        var boot = new DateTime(2026, 11, 13, 8, 12, 0);
        var last = new ShutdownCause.Snapshot(boot.AddHours(-5), 97, 210L * 1048576, 12_000_000_000, 12_400_000_000);

        var abrupt = ShutdownCause.Describe([
            Entry(2004, boot.AddHours(-6)), Entry(2004, boot.AddHours(-5.5)), Entry(2004, boot.AddDays(-2)),
            Entry(6008, boot.AddSeconds(40)),
            Entry(41, boot.AddSeconds(30), "Microsoft-Windows-Kernel-Power", new() { ["BugcheckCode"] = "0", ["PowerButtonTimestamp"] = "0" }),
        ], boot, last);
        Check(abrupt is { Unexpected: true } a && a.Text.Contains("갑자기 꺼졌습니다", StringComparison.Ordinal) &&
            a.Text.Contains("메모리 97%", StringComparison.Ordinal) && a.Text.Contains("남은 메모리 210MB", StringComparison.Ordinal) &&
            a.Text.Contains("메모리 부족 경고 2회", StringComparison.Ordinal) && a.Text.Contains("다시 켜짐 11-13 08:12", StringComparison.Ordinal),
            "an abrupt stop says so, with memory just before and the low-memory warnings of the day before", abrupt?.Text);

        var button = ShutdownCause.Describe([
            Entry(41, boot.AddSeconds(30), "Microsoft-Windows-Kernel-Power", new() { ["BugcheckCode"] = "0", ["PowerButtonTimestamp"] = "133758912000000000" }),
        ], boot, null);
        Check(button is { Unexpected: true } b && b.Text.StartsWith("전원 버튼을 길게", StringComparison.Ordinal) && !b.Text.Contains("직전", StringComparison.Ordinal),
            "a long press of the power button is told apart", button?.Text);

        var blue = ShutdownCause.Describe([
            Entry(41, boot.AddSeconds(30), "Microsoft-Windows-Kernel-Power", new() { ["BugcheckCode"] = "159" }),
            Entry(1001, boot.AddSeconds(50), "Microsoft-Windows-WER-SystemErrorReporting", null, "0x0000009f (0x0000000000000003, 0xffff)", "C:\\WINDOWS\\MEMORY.DMP"),
        ], boot, last);
        Check(blue is { Unexpected: true } c && c.Text.Contains("블루스크린", StringComparison.Ordinal) && c.Text.Contains("0x0000009f", StringComparison.Ordinal),
            "a blue screen says its code", blue?.Text);

        var update = ShutdownCause.Describe([
            Entry(1074, boot.AddMinutes(-3), "User32", null, @"C:\WINDOWS\servicing\TrustedInstaller.exe (PC)", "PC", "운영 체제: 업그레이드(계획됨)", "0x80020003", "다시 시작", "", @"NT AUTHORITY\SYSTEM"),
            Entry(6006, boot.AddMinutes(-2)),
        ], boot, last);
        Check(update is { Unexpected: false } d && d.Text.StartsWith("Windows 업데이트(TrustedInstaller.exe)가 11-13 08:09에 PC를 다시 시작했습니다", StringComparison.Ordinal) &&
            d.Text.Contains("이유: 운영 체제: 업그레이드(계획됨)", StringComparison.Ordinal),
            "Windows Update restarting the PC is named", update?.Text);

        var program = ShutdownCause.Describe([
            Entry(1074, boot.AddHours(-10), "User32", null, @"C:\Program Files\Power\PowerAgent.exe (PC)", "PC", "기타(계획됨)", "0x80000000", "전원 끄기", "", @"NT AUTHORITY\SYSTEM"),
            Entry(6006, boot.AddHours(-10).AddMinutes(1)),
        ], boot, null);
        Check(program is { Unexpected: false } e && e.Text.StartsWith("PowerAgent.exe가", StringComparison.Ordinal) && e.Text.Contains("PC를 껐습니다", StringComparison.Ordinal),
            "another program turning the PC off is named, even long before the boot", program?.Text);

        var own = ShutdownCause.Describe([
            Entry(1074, boot.AddMinutes(-3), "User32", null, @"C:\WINDOWS\SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe (PC)", "PC", "기타(계획되지 않음)", "0x0", "전원 끄기", "", @"PC\user"),
            Entry(6006, boot.AddMinutes(-2)),
        ], boot, last);
        Check(own is null, "the user's own shutdown from the Start menu says nothing", own?.Text);

        var stale = ShutdownCause.Describe([
            Entry(1074, boot.AddDays(-2), "User32", null, @"C:\Program Files\Power\PowerAgent.exe (PC)", "PC", "기타", "0x0", "전원 끄기", "", @"NT AUTHORITY\SYSTEM"),
            Entry(6006, boot.AddDays(-2).AddMinutes(1)),
            Entry(6006, boot.AddMinutes(-2)),
        ], boot, null);
        Check(stale is null, "a program's shutdown of an earlier day is not taken for the last one", stale?.Text);

        // The last record before the boot, across the rotated log and the current one.
        string folder = Path.Combine(Path.GetTempPath(), "TerminalNotes shutdown " + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        string log = Path.Combine(folder, "settings.json.resource-diagnostics.jsonl");
        string Line(DateTime at, int load) =>
            $"{{\"timestampUtc\":\"{new DateTimeOffset(at).ToUniversalTime():o}\",\"event\":\"resource_snapshot\",\"systemMemoryLoadPercent\":{load},\"availablePhysicalBytes\":1048576000,\"commitTotalBytes\":5,\"commitLimitBytes\":10}}";
        File.WriteAllLines(log + ".1", [Line(boot.AddHours(-9), 40), Line(boot.AddHours(-8), 50)]);
        File.WriteAllLines(log, [Line(boot.AddHours(-7), 88), "{broken", Line(boot.AddMinutes(5), 20)]);
        var snapshot = ShutdownCause.LastSnapshotBefore(log, boot);
        Check(snapshot is { MemoryPercent: 88 } s && Math.Abs((s.At - boot.AddHours(-7)).TotalSeconds) < 1,
            "the last resource record before the boot is found across both logs, skipping broken and later lines", snapshot?.ToString());
        Directory.Delete(folder, true);

        // This PC's real log: read without errors; what it says is printed.
        DateTime realBoot = ShutdownCause.BootTime();
        var events = ShutdownCause.ReadEvents(realBoot.AddDays(-3));
        var here = ShutdownCause.Describe(events, realBoot, null);
        Console.WriteLine($"THIS PC · boot {realBoot:MM-dd HH:mm} · {events.Count} events · " + (here is { } h ? h.Text : "(the user's own shutdown: nothing to tell)"));
        Console.WriteLine(_failures == 0 ? "SHUTDOWN CAUSE PASSED" : $"SHUTDOWN CAUSE FAILED ({_failures})");
        return _failures == 0 ? 0 : 1;
    }
}
