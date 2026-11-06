using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TerminalNotes.Controls;
using TerminalNotes.TerminalEngine;

internal static partial class Program
{
    private static void VerifySnapshotPerformance()
    {
        var engines = Enumerable.Range(0, 8).Select(_ => new TerminalEmulator(80, 24)).ToArray();
        string update = "\x1b[H\x1b[32m한🙂 mixed ANSI text\x1b[0m";
        foreach (var engine in engines) { engine.Feed(update); engine.GetSnapshot(); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        long checksum = 0;
        for (int iteration = 0; iteration < 64; iteration++)
            foreach (var engine in engines)
            {
                engine.Feed(update);
                for (int consumer = 0; consumer < 3; consumer++) checksum += engine.GetSnapshot().Version;
            }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"MEASURED: eight engines / 64 changes / three snapshot consumers: {allocated / (1024d * 1024):N2} MiB, {timer.Elapsed.TotalMilliseconds:N1} ms, checksum={checksum}.");
        var surface = new NativeTerminalSurface(new TerminalEmulator());
        try
        {
            var measure = typeof(NativeTerminalSurface).GetMethod("MeasureCell", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Action>(surface);
            measure();
            double expectedWidth = surface.MinimumViewportWidth, expectedHeight = surface.MinimumViewportHeight;
            before = GC.GetAllocatedBytesForCurrentThread();
            timer.Restart();
            for (int iteration = 0; iteration < 1000; iteration++) measure();
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(surface.MinimumViewportWidth == expectedWidth && surface.MinimumViewportHeight == expectedHeight,
                "Cell measurement reuse changed the terminal grid dimensions.");
            Console.WriteLine($"MEASURED: 1,000 unchanged cell measurements: {allocated:N0} bytes, {timer.Elapsed.TotalMilliseconds:N1} ms.");
            Require(allocated < 1024, "Unchanged cell metrics still allocate text formatting objects.");
            var originalCulture = CultureInfo.CurrentCulture;
            try
            {
                foreach (var culture in new[] { CultureInfo.GetCultureInfo("ko-KR"), CultureInfo.GetCultureInfo("ar-SA") })
                    foreach (double fontSize in new[] { 12d, 21d })
                    {
                        CultureInfo.CurrentCulture = culture;
                        typeof(NativeTerminalSurface).GetField("_fontSize", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(surface, fontSize);
                        var typeface = (Typeface)typeof(NativeTerminalSurface).GetField("NormalTypeface", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                        var formatted = new FormattedText("M", culture, FlowDirection.LeftToRight, typeface, fontSize,
                            Brushes.White, VisualTreeHelper.GetDpi(surface).PixelsPerDip);
                        Require(surface.MinimumViewportWidth == NativeTerminalSurface.MinimumColumns * Math.Max(1, formatted.WidthIncludingTrailingWhitespace) + 20 &&
                            surface.MinimumViewportHeight == NativeTerminalSurface.MinimumRows * Math.Max(1, formatted.Height * 1.15) + 4,
                            "Cell measurement cache did not follow a changed font size or culture.");
                    }
            }
            finally { CultureInfo.CurrentCulture = originalCulture; }
        }
        finally { surface.DisposeSurface(); }

        var cached = new TerminalEmulator(12, 3, 10);
        var reference = new TerminalEmulator(12, 3, 10);
        var cacheField = typeof(TerminalEmulator).GetField("_cachedSnapshot", BindingFlags.Instance | BindingFlags.NonPublic);
        TerminalSnapshot FreshReference(int offset = 0)
        {
            // Bypass reuse on the independent engine so equal stale caches cannot
            // conceal an invalidation error in the engine under test.
            cacheField?.SetValue(reference, null);
            return reference.GetSnapshot(offset);
        }
        string[] changes = ["A한🙂", "\u0301", "\r\n1\r\n2\r\n3\r\n4", "\x1b[?1049hALT", "\x1b[?25l\x1b[?1h\x1b[?2004h\x1b[?1003h\x1b[?1006h", "\x1b]2;title\a", "\x1b[?1049l", "\x1b[3J", "\x1b[2J"];
        foreach (string change in changes)
        {
            cached.GetSnapshot();
            cached.Feed(change);
            reference.Feed(change);
            foreach (int offset in new[] { 0, 1, 1000, -1, 0 })
                Require(SnapshotsEqual(cached.GetSnapshot(offset), FreshReference(offset)),
                    "Snapshot reuse changed ANSI, Unicode, modes, title or scrollback cells.");
        }
        cached.Resize(18, 5); reference.Resize(18, 5);
        Require(SnapshotsEqual(cached.GetSnapshot(), FreshReference()), "Snapshot reused dimensions after resize.");
        cached.Clear(); reference.Clear();
        Require(SnapshotsEqual(cached.GetSnapshot(), FreshReference()), "Snapshot reused cells after clear.");
        cached.Response += _ =>
        {
            Require(string.Concat(cached.GetSnapshot().Lines[0].Cells.Select(cell => cell.Text)).StartsWith("X"),
                "An in-feed device reply observed stale snapshot cells.");
        };
        cached.GetSnapshot();
        cached.Feed("X\x1b[6nY");
        Require(string.Concat(cached.GetSnapshot().Lines[0].Cells.Select(cell => cell.Text)).StartsWith("XY"),
            "A device reply snapshot was reused after Feed continued.");
        TerminalSnapshot stable = cached.GetSnapshot();
        Require(ReferenceEquals(stable, cached.GetSnapshot(-1)), "Equivalent clamped offsets did not share the current immutable snapshot.");
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1000; iteration++) cached.GetSnapshot();
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(allocated < 1024, "Unchanged snapshots still allocate immutable cell copies.");
        cached.Feed("\x1b[2J");
        Require(!ReferenceEquals(stable, cached.GetSnapshot()) &&
            string.Concat(stable.Lines[0].Cells.Select(cell => cell.Text)).StartsWith("XY"),
            "New output reused a stale snapshot or mutated a previously published view.");
        var bytewise = new TerminalEmulator(12, 3);
        bytewise.Feed(Encoding.UTF8.GetBytes("한"));
        TerminalSnapshot beforeCombining = bytewise.GetSnapshot();
        bytewise.Feed(Encoding.UTF8.GetBytes("\u0301"));
        Require(bytewise.GetSnapshot().Lines[0].Cells[0].Text == "한\u0301" && beforeCombining.Lines[0].Cells[0].Text == "한",
            "A combining-only UTF-8 Feed reused stale cells or mutated a published snapshot.");
        Console.WriteLine("PASS: snapshot cells/styles/metadata match across combining-only changes, scrollback, alternate screen, modes, title, resize, clear and in-feed device replies.");
    }

    private static bool SnapshotsEqual(TerminalSnapshot left, TerminalSnapshot right) =>
        left.Columns == right.Columns && left.Rows == right.Rows && left.CursorColumn == right.CursorColumn &&
        left.CursorRow == right.CursorRow && left.CursorVisible == right.CursorVisible &&
        left.AlternateScreen == right.AlternateScreen && left.ApplicationCursorKeys == right.ApplicationCursorKeys &&
        left.BracketedPaste == right.BracketedPaste && left.MouseMode == right.MouseMode && left.SgrMouse == right.SgrMouse &&
        left.ScrollbackCount == right.ScrollbackCount && left.Title == right.Title && left.Version == right.Version &&
        left.Lines.Count == right.Lines.Count && left.Lines.Zip(right.Lines).All(pair => pair.First.Wrapped == pair.Second.Wrapped &&
            pair.First.Cells.SequenceEqual(pair.Second.Cells));
}
