using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using TerminalNotes;
using TerminalNotes.Services;
using Application = System.Windows.Application;
using State = TerminalNotes.MainWindow.AgentState;
using Notice = TerminalNotes.MainWindow.AgentNotice;

// The agent board (herdr-like): every terminal's agent state, blocked first, and a notification when one gets blocked or finishes.
internal static class Program
{
    private static int _failures;

    [STAThread]
    private static int Main()
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(); }
            catch (Exception ex) { _failures++; Console.WriteLine("FAIL " + ex); }
            finally { app.Shutdown(); }
        };
        app.Run();
        Console.WriteLine(_failures == 0 ? "PASS agent board: states, notices once after a few seconds, board rows and summary" : $"FAIL agent board ({_failures})");
        return _failures;
    }

    private static void Assert(bool condition, string name)
    {
        if (condition) return;
        _failures++;
        Console.WriteLine("FAIL " + name);
    }

    private static async Task RunAsync()
    {
        // The states the board shows.
        Assert(MainWindow.AgentStateOf(TerminalActivity.NeedsInput, false) == State.Blocked, "an answer or approval is blocked");
        Assert(MainWindow.AgentStateOf(TerminalActivity.RateLimited, false) == State.Blocked, "a rate limit is blocked");
        Assert(MainWindow.AgentStateOf(TerminalActivity.Error, false) == State.Blocked, "an error is blocked");
        Assert(MainWindow.AgentStateOf(TerminalActivity.Active, false) == State.Working, "active is working");
        Assert(MainWindow.AgentStateOf(TerminalActivity.Idle, true) == State.Done, "idle after work is done");
        Assert(MainWindow.AgentStateOf(TerminalActivity.Idle, false) == State.Waiting, "idle before work is waiting");
        Assert(MainWindow.AgentStateOf(TerminalActivity.Exited, true) == State.Ended, "exited is ended");

        // When to notify.
        var t = DateTimeOffset.UtcNow;
        var watch = new MainWindow.AgentWatch();
        Assert(MainWindow.Observe(watch, State.Blocked, t, false) == Notice.None, "what a terminal already is when first seen is not news");
        Assert(MainWindow.Observe(watch, State.Blocked, t.AddSeconds(10), false) == Notice.None, "...nor later");
        Assert(MainWindow.Observe(watch, State.Working, t.AddSeconds(11), false) == Notice.None, "working is not told");
        Assert(MainWindow.Observe(watch, State.Done, t.AddSeconds(20), false) == Notice.None, "a finish is not told at once (it may be a blink)");
        Assert(MainWindow.Observe(watch, State.Working, t.AddSeconds(21), false) == Notice.None, "a blink of a finish");
        Assert(MainWindow.Observe(watch, State.Done, t.AddSeconds(30), false) == Notice.None, "finished again");
        Assert(MainWindow.Observe(watch, State.Done, t.AddSeconds(34), false) == Notice.Done, "a finish after work that held is told");
        Assert(MainWindow.Observe(watch, State.Done, t.AddSeconds(60), false) == Notice.None, "...once");
        Assert(MainWindow.Observe(watch, State.Waiting, t.AddSeconds(61), false) == Notice.None, "waiting is not told");
        Assert(MainWindow.Observe(watch, State.Done, t.AddSeconds(62), false) == Notice.None, "done without work in between");
        Assert(MainWindow.Observe(watch, State.Done, t.AddSeconds(70), false) == Notice.None, "...is not told again");
        Assert(MainWindow.Observe(watch, State.Blocked, t.AddSeconds(71), false) == Notice.None, "blocked is not told at once");
        Assert(MainWindow.Observe(watch, State.Blocked, t.AddSeconds(75), false) == Notice.Blocked, "blocked that held is told");
        Assert(MainWindow.Observe(watch, State.Blocked, t.AddSeconds(90), false) == Notice.None, "...once");
        Assert(MainWindow.Observe(watch, State.Working, t.AddSeconds(91), true) == Notice.None, "a task terminal works");
        Assert(MainWindow.Observe(watch, State.Done, t.AddSeconds(92), true) == Notice.None, "and finishes");
        Assert(MainWindow.Observe(watch, State.Done, t.AddSeconds(99), true) == Notice.FinishedQuietly, "a task's finish is told by the task notifications (but still learned from)");

        // On the window.
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes agent board " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        await File.WriteAllTextAsync(Path.Combine(fixture, "settings.json"), System.Text.Json.JsonSerializer.Serialize(new
            { Workspace = fixture, CodexWorkspace = fixture, SetupCompleted = true, WorkspaceDefaultsVersion = 1 }));
        var window = new MainWindow(Path.Combine(fixture, "settings.json"), autoStartCodex: false);
        try
        {
            window.Show();
            await Idle(window);
            var button = (Button)typeof(MainWindow).GetField("_agentBoardButton", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Assert(button.Visibility == Visibility.Collapsed, "no terminals, no board button");
            Assert(AutomationProperties.GetAutomationId(button) == "AgentBoardButton", "the board button can be found");
            var tabs = (TabControl)window.FindName("TerminalTabs");
            typeof(MainWindow).GetMethod("CreateProfileTerminal", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [null]);
            for (int i = 0; i < 40 && tabs.Items.Count == 0; i++) await Task.Delay(100);
            await Task.Delay(1300);
            await Idle(window);
            Assert(tabs.Items.Count == 1, "a terminal opened");
            Assert(button.Visibility == Visibility.Visible, "with a terminal the board button shows");
            Assert(window.AgentBoardSummary.Length > 0, "the button sums the agents up: " + window.AgentBoardSummary);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Idle(window);
            var rows = (StackPanel)typeof(MainWindow).GetField("_agentBoardRows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Assert(rows.Children.OfType<Button>().Count() == 1, "the board lists the terminal");
            string? row = rows.Children.OfType<Button>().Select(AutomationProperties.GetName).FirstOrDefault();
            Assert(row is not null && row.StartsWith("01 ", StringComparison.Ordinal) && row.Contains(" · ", StringComparison.Ordinal), "a row names the terminal and its state: " + row);
            Console.WriteLine("summary: " + window.AgentBoardSummary + " | row: " + row);
            string shots = Path.Combine(Path.GetTempPath(), "TerminalNotes.AgentBoard");
            Directory.CreateDirectory(shots);
            Save((FrameworkElement)window.FindName("TerminalAreaHeader"), Path.Combine(shots, "header.png"));
            Save((FrameworkElement)((FrameworkElement)rows.Parent).Parent, Path.Combine(shots, "board.png"));
        }
        finally
        {
            window.Close();
            await Task.Delay(300);
            try { Directory.Delete(fixture, true); } catch { }
        }
    }

    private static void Save(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        int width = (int)Math.Ceiling(element.ActualWidth), height = (int)Math.Ceiling(element.ActualHeight);
        if (width == 0 || height == 0) { Console.WriteLine("not rendered " + path); return; }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x12)), null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, width, height));
        }
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
        Console.WriteLine("PREVIEW " + path);
    }

    private static async Task Idle(Window window)
    {
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await Task.Delay(40);
    }
}
