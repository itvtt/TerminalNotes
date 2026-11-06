using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using TerminalNotes.Controls;
using TerminalNotes.TerminalEngine;

internal static partial class Program
{
    // OpenCode's screen mostly stays put while a spinner, a timer or the typed line changes. Every such frame is drawn on the UI
    // thread that all terminals and the window share, so its cost is what several terminals add up to.
    private static void VerifyTuiRedrawCost()
    {
        const int columns = 160, rows = 45, frames = 120;
        var engine = new TerminalEmulator(columns, rows, 10000);
        var surface = new NativeTerminalSurface(engine);
        var window = new Window { Width = 1400, Height = 900, Content = surface, ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000 };
        MethodInfo render = typeof(NativeTerminalSurface).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            window.Show();
            window.UpdateLayout();
            // A full-screen, many-coloured screen like OpenCode's: alternate buffer, eight styled runs per row.
            var screen = new System.Text.StringBuilder("\x1b[?1049h\x1b[H");
            string[] colours = ["31", "32", "33", "34", "35", "36", "37", "90"];
            for (int row = 0; row < rows; row++)
            {
                screen.Append($"\x1b[{row + 1};1H");
                for (int run = 0; run < 8; run++) screen.Append($"\x1b[{colours[(row + run) % 8]}m").Append(new string((char)('a' + run), columns / 8 - 1)).Append(' ');
            }
            screen.Append("\x1b[0m");
            engine.Feed(screen.ToString());
            Render(surface, render);
            string[] spinner = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            for (int frame = 0; frame < frames; frame++)
            {
                engine.Feed($"\x1b[{rows};2H\x1b[33m{spinner[frame % spinner.Length]}\x1b[0m {frame,4}");
                Render(surface, render);
            }
            timer.Stop();
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            double perFrame = timer.Elapsed.TotalMilliseconds / frames;
            Console.WriteLine($"MEASURED: {columns}x{rows} TUI screen, one row changing: {perFrame:N2} ms and {allocated / (double)frames / 1024:N0} KiB per frame on the UI thread.");
            // Unchanged rows are reused: a frame that changes one row costs a small part of a full redraw.
            // TERMINALNOTES_NATIVE_REDRAW_REPORT_ONLY=1 only reports it (to compare against an earlier build).
            if (Environment.GetEnvironmentVariable("TERMINALNOTES_NATIVE_REDRAW_REPORT_ONLY") == "1") return;
            Require(perFrame < 4 && allocated / frames < 400 * 1024, $"A one-row change still redraws the whole screen: {perFrame:N2} ms, {allocated / frames / 1024} KiB per frame.");
        }
        finally { window.Close(); }
    }

    private static void Render(NativeTerminalSurface surface, MethodInfo render)
    {
        var visual = new DrawingVisual();
        using var context = visual.RenderOpen();
        render.Invoke(surface, [context]);
    }
}
