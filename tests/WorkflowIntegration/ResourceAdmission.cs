using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TerminalNotes;
using TerminalNotes.Services;

internal static partial class Program
{
    private sealed class ResourceTestLease : IDisposable { public void Dispose() { } }

    private static async Task VerifyResourceAdmissionUiAsync(string fixture)
    {
        string workspace = Path.Combine(fixture, "resource admission workspace");
        string project = Path.Combine(workspace, "queued input project");
        string otherProject = Path.Combine(workspace, "other project");
        string executionProject = Path.Combine(workspace, "queued execution project");
        string changedPlanProject = Path.Combine(workspace, "queued changed Plan project");
        foreach (string directory in new[] { project, otherProject, executionProject, changedPlanProject })
        {
            Directory.CreateDirectory(Path.Combine(directory, "terminal-notes"));
            await File.WriteAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(directory), "# Preserve existing Plan\nCheck the project.");
        }
        VerifyResourceAdmissionRunOnceButton(project);
        string settings = Path.Combine(fixture, "resource admission settings.json");
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new
        { Workspace = workspace, CodexWorkspace = workspace, SetupCompleted = true, WorkspaceDefaultsVersion = 1 }));
        Type admissionType = typeof(TerminalPane).Assembly.GetType("TerminalNotes.Services.TerminalStartupAdmission")!;
        FieldInfo acquireField = admissionType.GetField("AcquireOverride", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? previousAcquire = acquireField.GetValue(null);
        var tickets = new ConcurrentQueue<TaskCompletionSource<IDisposable>>();
        int cancelledTickets = 0;
        Func<CancellationToken, Task<IDisposable>> acquire = async token =>
        {
            var ticket = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
            tickets.Enqueue(ticket);
            try { return await ticket.Task.WaitAsync(token); }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelledTickets); throw; }
        };
        acquireField.SetValue(null, acquire);
        object? previousData = AppContext.GetData("TerminalNotes.DataDirectory");
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(fixture, "resource admission app data"));
        var window = new MainWindow(settings, autoStartCodex: true, startupHooks: FakeStartupHooks);
        try
        {
            window.Show();
            await UntilAsync(() => (bool)FieldValue(window, "_openCodeReady")!, "isolated fake OpenCode readiness");
            Call(window, "EnsureProjectTaskRow", project);
            Call(window, "SelectTaskProject", project, true);
            await UntilAsync(() => Field<TextBox>(window, "_tabDocumentEditor").IsEnabled, "queued project's Plan load");
            var input = Field<TextBox>(window, "_projectInputEditor");
            const string request = "Preserve this queued request exactly once.";
            input.Text = request;
            Task queuedSubmission = (Task)Call(window, "SaveTabDocumentAsync", true)!;
            object runtime = Runtime(window, project);
            await UntilAsync(() => tickets.Count == 1 && RuntimeField(runtime, "SingleStartPhase")?.ToString()?.Contains("기다리는", StringComparison.Ordinal) == true,
                "the queued request shows a resource wait phase");
            string planPath = TerminalTabDocumentStore.GetProjectPlanPath(project);
            string savedRequest = await File.ReadAllTextAsync(planPath);
            Require(!queuedSubmission.IsCompleted && FieldValue(window, "_tabDocumentSaving") is false &&
                Field<TextBox>(window, "_tabDocumentEditor").IsEnabled && input.Text == request,
                "Resource waiting must keep the saved request pending and release the document UI lock.");
            await (Task)Call(window, "SaveTabDocumentAsync", true)!;
            Require(tickets.Count == 1 && await File.ReadAllTextAsync(planPath) == savedRequest,
                "A duplicate Execute during resource waiting created another ticket or appended the request twice.");

            Call(window, "EnsureProjectTaskRow", otherProject);
            Call(window, "SelectTaskProject", otherProject, true);
            await UntilAsync(() => Field<TextBox>(window, "_tabDocumentEditor").IsEnabled &&
                FieldValue(window, "_projectInputProject")?.ToString() == otherProject, "other project's editable Plan");
            input.Text = "Save another project's request while the first waits.";
            await (Task)Call(window, "SaveTabDocumentAsync", false)!;
            Require(input.Text.Length == 0 && (await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(otherProject))).Contains("Save another project's request", StringComparison.Ordinal) &&
                !queuedSubmission.IsCompleted && FieldValue(window, "_tabDocumentSaving") is false,
                "Another project could not save independently while the first project's native launch was queued.");

            await (Task)Call(window, "StopProjectTaskAsync", project)!;
            await queuedSubmission.WaitAsync(TimeSpan.FromSeconds(10));
            await UntilAsync(() => Volatile.Read(ref cancelledTickets) == 1, "Stop cancels the actual admission ticket");
            Require(tickets.TryDequeue(out var stoppedTicket), "The stopped admission fixture lost its ticket.");
            stoppedTicket!.TrySetResult(new ResourceTestLease());
            Call(window, "SelectTaskProject", project, true);
            Require(input.Text == request && await File.ReadAllTextAsync(planPath) == savedRequest &&
                RuntimeField(runtime, "SingleSubmitted") is false && RuntimeField(runtime, "SingleStopped") is true,
                "Stopping a queued launch discarded its saved request or incorrectly marked it submitted.");
            Task retriedSubmission = (Task)Call(window, "SaveTabDocumentAsync", true)!;
            await UntilAsync(() => tickets.Count == 1 && RuntimeField(runtime, "SingleStartPhase")?.ToString()?.Contains("기다리는", StringComparison.Ordinal) == true,
                "the stopped project can retry with a fresh admission ticket");
            Require(await File.ReadAllTextAsync(planPath) == savedRequest,
                "Retrying a stopped queued request appended it to the Plan twice.");
            Require(tickets.TryDequeue(out var retryTicket), "The retried request lost its admission ticket.");
            retryTicket!.TrySetResult(new ResourceTestLease());
            await retriedSubmission.WaitAsync(TimeSpan.FromSeconds(30));
            Require(RuntimeField(runtime, "SingleSubmitted") is true && RuntimeField(runtime, "SingleError") is null && input.Text.Length == 0 &&
                Field<TextBlock>(window, "_projectInputHint").Text.Contains("전송 완료", StringComparison.Ordinal),
                "The stopped project could not retry, or accepted request dispatch did not clear its input and show completion.");

            Call(window, "EnsureProjectTaskRow", executionProject);
            Call(window, "SelectTaskProject", executionProject, true);
            await UntilAsync(() => Field<TextBox>(window, "_tabDocumentEditor").IsEnabled, "execution project's Plan load");
            Task queuedExecution = (Task)Call(window, "SaveTabDocumentAsync", true)!;
            object executionRuntime = Runtime(window, executionProject);
            await UntilAsync(() => tickets.Count == 1 && RuntimeField(executionRuntime, "SingleStartPhase")?.ToString()?.Contains("기다리는", StringComparison.Ordinal) == true,
                "ordinary Plan execution enters the resource queue");
            Require(FieldValue(window, "_tabDocumentSaving") is false && !queuedExecution.IsCompleted,
                "Ordinary Plan Execute retained the global save lock during resource waiting.");
            Call(window, "SelectTaskProject", otherProject, true);
            Require(tickets.TryDequeue(out var admittedTicket), "The execution admission fixture lost its ticket.");
            admittedTicket!.TrySetResult(new ResourceTestLease());
            await queuedExecution.WaitAsync(TimeSpan.FromSeconds(30));
            Require(RuntimeField(executionRuntime, "SingleSubmitted") is true && RuntimeField(executionRuntime, "SingleError") is null,
                "A captured project did not automatically execute after admission when the user selected another project.");

            Call(window, "EnsureProjectTaskRow", changedPlanProject);
            Call(window, "SelectTaskProject", changedPlanProject, true);
            await UntilAsync(() => Field<TextBox>(window, "_tabDocumentEditor").IsEnabled, "changed project's Plan load");
            input.Text = "Keep this request if the queued Plan is edited.";
            Task changedSubmission = (Task)Call(window, "SaveTabDocumentAsync", true)!;
            object changedRuntime = Runtime(window, changedPlanProject);
            await UntilAsync(() => tickets.Count == 1 && RuntimeField(changedRuntime, "SingleStartPhase")?.ToString()?.Contains("기다리는", StringComparison.Ordinal) == true,
                "the editable queued request waits for admission");
            var editor = Field<TextBox>(window, "_tabDocumentEditor");
            string newerDraft = editor.Text + "\nA newer unsaved Plan edit.";
            editor.Text = newerDraft;
            Require(tickets.TryDequeue(out var changedTicket), "The changed Plan admission fixture lost its ticket.");
            changedTicket!.TrySetResult(new ResourceTestLease());
            await changedSubmission.WaitAsync(TimeSpan.FromSeconds(30));
            Require(RuntimeField(changedRuntime, "SingleSubmitted") is false && RuntimeField(changedRuntime, "SingleError") is string &&
                input.Text == "Keep this request if the queued Plan is edited." && editor.Text == newerDraft,
                "A changed queued Plan was submitted, or the retained request/newer unsaved edit was discarded.");
            Console.WriteLine("PASS resource admission UI: visible pending state, independent saves, duplicate guard, actual queue cancellation, retained/newer drafts, and automatic background execution using fake OpenCode only.");
        }
        finally
        {
            Call(window, "DisposeResources");
            window.Close();
            acquireField.SetValue(null, previousAcquire);
            AppContext.SetData("TerminalNotes.DataDirectory", previousData);
        }
    }

    private static void VerifyResourceAdmissionRunOnceButton(string project)
    {
        // Unloaded panes never create a process. Exercise the real WPF click binding with a ticket callback.
        using var ordinary = new TerminalPane(project, startupCommand: "opencode", useProjectContext: false);
        using var planner = new TerminalPane(project, startupCommand: "opencode", useProjectContext: false);
        Call(planner, "EnableResourceAdmissionRunOnce");
        Type waitType = typeof(TerminalPane).Assembly.GetType("TerminalNotes.Services.StartupResourceWait")!;
        object wait = Activator.CreateInstance(waitType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object[] { "memory", "메모리 여유를 기다리는 중 · UI 테스트" }, null)!;
        int accepted = 0;
        Func<bool> authorize = () => { accepted++; return true; };
        waitType.GetProperty("TryRunOnce", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(wait, authorize);
        Call(ordinary, "UpdateResourceAdmissionWait", wait);
        Call(planner, "UpdateResourceAdmissionWait", wait);
        var hidden = Field<Button>(ordinary, "_resourceAdmissionRunOnce");
        var button = Field<Button>(planner, "_resourceAdmissionRunOnce");
        Require(hidden.Visibility == Visibility.Collapsed && button.Visibility == Visibility.Visible && button.IsEnabled &&
            System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "TerminalResourceAdmissionRunOnce",
            "The run-once action must be discoverable only on an opted-in planning pane.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(accepted == 1 && !button.IsEnabled,
            "The run-once button did not forward exactly one authorization to the pending ticket.");
        Call(planner, "UpdateResourceAdmissionWait", new object?[] { null });
        Require(Field<Border>(planner, "_resourceAdmissionOverlay").Visibility == Visibility.Collapsed,
            "Admission completion left the planning pane's run-once overlay visible.");
        Console.WriteLine("PASS run-once WPF binding: PlanMaker opt-in, discoverable button, exact callback once, disabled after click, hidden after admission; no process/model started.");
    }
}
