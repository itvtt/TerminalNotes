using System.Collections;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using TerminalNotes;
using TerminalNotes.Services;

internal static partial class Program
{
    // Loop keeps going: a started Loop task whose terminal went away without the user stopping it is rerun from the
    // saved plan once its grace passes, the next rerun waits longer, and a task the user stopped is left alone.
    private static async Task VerifyLoopWatchdogAsync(string fixture)
    {
        string workspace = Path.Combine(fixture, "loop watchdog workspace");
        string project = Path.Combine(workspace, "loop watchdog project");
        string settings = Path.Combine(fixture, "loop watchdog settings.json");
        string trace = Environment.GetEnvironmentVariable("TN_WORKFLOW_TRACE")
            ?? throw new InvalidOperationException("The workflow fixture must provide its fake API trace directory.");
        MainWindow? window = null;
        try
        {
            Directory.CreateDirectory(project);
            string planPath = TerminalTabDocumentStore.GetProjectPlanPath(project);
            Directory.CreateDirectory(Path.GetDirectoryName(planPath)!);
            await File.WriteAllTextAsync(planPath, "# Loop watchdog plan\n\n- Keep improving.", new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(project, ".fixture-busy-composer"), "fake API only");
            await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new
            { Workspace = workspace, CodexWorkspace = workspace, SetupCompleted = true, WorkspaceDefaultsVersion = 1 }));
            window = new MainWindow(settings, autoStartCodex: true, startupHooks: FakeStartupHooks);
            window.Show();
            await UntilAsync(() => (bool)FieldValue(window, "_openCodeReady")!, "loop watchdog fake OpenCode readiness");
            Call(window, "EnsureProjectTaskRow", project);
            Call(window, "SelectTaskProject", project, true);
            ((IDictionary)FieldValue(window, "_projectTaskContinuousModes")!)[project] = true;
            string prompt = ExpectedSavedPlanPrompt(planPath);
            int Runs() => FindStartupPromptReceipts(trace, project).Count(receipt => receipt.Prompt == prompt);
            // OpenCode's terminal goes away on its own (no user stop, so no close confirmation either).
            void LoseTerminal(TabItem tab)
            {
                var pane = (TerminalPane)GetProperty(tab.Tag!, "Pane")!;
                Call(window!, "DetachTaskTerminal", tab);
                pane.Dispose();
                ((TabControl)window!.FindName("TerminalTabs")).Items.Remove(tab);
            }
            bool Rerunning() => ((HashSet<string>)FieldValue(window!, "_rerunningProjectTasks")!).Contains(project);

            await (Task)Call(window, "StartProjectTaskAsync", project)!;
            await UntilAsync(() => Runs() == 1, "the Loop task's first run");
            object runtime = Runtime(window, project);
            var first = (TabItem)RuntimeField(runtime, "SingleTerminal")!;
            LoseTerminal(first);
            Require(RuntimeField(runtime, "SingleTerminal") is null && RuntimeField(runtime, "SingleStopped") is false,
                "The fixture did not leave the Loop task without a terminal and without a user stop.");
            var start = DateTimeOffset.Now;
            Call(window, "CheckLoopWatchdog", start);
            Call(window, "CheckLoopWatchdog", start.AddSeconds(20));
            await Task.Delay(500);
            Require(Runs() == 1, "The watchdog reran the task before its grace passed.");
            Call(window, "CheckLoopWatchdog", start.AddSeconds(35));
            await UntilAsync(() => Runs() == 2 && RuntimeField(runtime, "SingleTerminal") is TabItem rerun && !ReferenceEquals(rerun, first),
                "the watchdog to rerun the stopped Loop task in a fresh terminal", seconds: 60);
            await UntilAsync(() => !Rerunning(), "the rerun to finish starting");

            // Gone again right away: the next rerun waits for the backoff, not just the grace.
            LoseTerminal((TabItem)RuntimeField(runtime, "SingleTerminal")!);
            Call(window, "CheckLoopWatchdog", start.AddSeconds(40));
            Call(window, "CheckLoopWatchdog", start.AddSeconds(75));
            await Task.Delay(500);
            Require(Runs() == 2, "A second rerun ignored the backoff after a rerun without progress.");
            Call(window, "CheckLoopWatchdog", start.AddSeconds(100));
            await UntilAsync(() => Runs() == 3 && RuntimeField(runtime, "SingleTerminal") is not null,
                "the second rerun after its backoff", seconds: 60);
            await UntilAsync(() => !Rerunning(), "the second rerun to finish starting");

            // The user stops the task: Loop stays on, but nothing is rerun.
            await (Task)Call(window, "StopProjectTaskAsync", project)!;
            Call(window, "CheckLoopWatchdog", start.AddMinutes(30));
            Call(window, "CheckLoopWatchdog", start.AddMinutes(60));
            await Task.Delay(1000);
            Require(Runs() == 3 && RuntimeField(runtime, "SingleStopped") is true,
                "The watchdog reran a task the user stopped.");
            Console.WriteLine("PASS Loop watchdog: a Loop task left without a terminal is rerun from the saved plan after its grace, the next rerun waits for its backoff, and a user-stopped task is left alone.");
        }
        finally
        {
            if (window is not null) { Call(window, "DisposeResources"); window.Close(); }
        }
    }
}
