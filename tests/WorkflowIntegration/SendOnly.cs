using System.Collections;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TerminalNotes;
using TerminalNotes.Services;

internal static partial class Program
{
    // 전송 with no running task terminal starts it in a visible cell first (like ▷), then sends the raw text once;
    // the next 전송 reuses that terminal. The plan is never touched.
    private static async Task VerifySendOnlyStartsTerminalAsync(string fixture)
    {
        string workspace = Path.Combine(fixture, "send only workspace");
        string project = Path.Combine(workspace, "send only project");
        string settings = Path.Combine(fixture, "send only settings.json");
        string trace = Environment.GetEnvironmentVariable("TN_WORKFLOW_TRACE")
            ?? throw new InvalidOperationException("The workflow fixture must provide its fake API trace directory.");
        MainWindow? window = null;
        try
        {
            Directory.CreateDirectory(project);
            string planPath = TerminalTabDocumentStore.GetProjectPlanPath(project);
            Directory.CreateDirectory(Path.GetDirectoryName(planPath)!);
            const string plan = "# Send only plan\n\n- Keep this plan unchanged.";
            await File.WriteAllTextAsync(planPath, plan, new UTF8Encoding(false));
            // The fake accepts every raw prompt, not only a session's first.
            await File.WriteAllTextAsync(Path.Combine(project, ".fixture-busy-composer"), "fake API only");
            await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new
            { Workspace = workspace, CodexWorkspace = workspace, SetupCompleted = true, WorkspaceDefaultsVersion = 1 }));
            window = new MainWindow(settings, autoStartCodex: true, startupHooks: FakeStartupHooks);
            window.Show();
            await UntilAsync(() => (bool)FieldValue(window, "_openCodeReady")!, "send-only fake OpenCode readiness");
            Call(window, "EnsureProjectTaskRow", project);
            Call(window, "SelectTaskProject", project, true);
            Call(window, "UpdateProjectInputComposer");
            object runtime = Runtime(window, project);
            Require(RuntimeField(runtime, "SingleTerminal") is null, "The send-only fixture already had a task terminal.");
            var editor = Field<TextBox>(window, "_projectInputEditor");

            const string first = "터미널이 없을 때 전송: 이 문장을 그대로 보낸다.";
            editor.Text = first;
            await (Task)Call(window, "SendProjectInputOnlyAsync")!;
            var tab = RuntimeField(runtime, "SingleTerminal") as TabItem;
            var views = (IDictionary)FieldValue(window, "_projectTaskViews")!;
            Require(tab is not null && ((TabControl)window.FindName("TerminalTabs")).Items.Contains(tab) &&
                views[project] is Grid { Visibility: Visibility.Visible },
                "전송 did not start the task terminal in a visible cell.");
            await UntilAsync(() => FindStartupPromptReceipts(trace, project).Any(receipt => receipt.Prompt == first),
                "the raw text to reach the newly started terminal");
            Require(FindStartupPromptReceipts(trace, project).Length == 1 && editor.Text.Length == 0 &&
                await File.ReadAllTextAsync(planPath) == plan,
                "The first 전송 sent more than the raw text, kept its draft, or changed the plan.");

            const string second = "실행 중인 터미널로 한 번 더 전송한다.";
            editor.Text = second;
            await (Task)Call(window, "SendProjectInputOnlyAsync")!;
            await UntilAsync(() => FindStartupPromptReceipts(trace, project).Any(receipt => receipt.Prompt == second),
                "the second raw text to reach the running terminal");
            Require(ReferenceEquals(RuntimeField(runtime, "SingleTerminal"), tab) && FindStartupPromptReceipts(trace, project).Length == 2 &&
                await File.ReadAllTextAsync(planPath) == plan,
                "The second 전송 started another terminal, repeated a request, or changed the plan.");
            Console.WriteLine("PASS send only: 전송 starts a missing task terminal in a visible cell, sends the raw text once, then reuses the running terminal; the plan is unchanged.");
        }
        finally
        {
            if (window is not null) { Call(window, "DisposeResources"); window.Close(); }
        }
    }
}
