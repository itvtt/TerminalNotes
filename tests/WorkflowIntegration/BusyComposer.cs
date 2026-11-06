using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using TerminalNotes;
using TerminalNotes.Services;

internal static partial class Program
{
    private static async Task VerifyBusyComposerAsync(string fixture)
    {
        string workspace = Path.Combine(fixture, "busy composer workspace");
        string project = Path.Combine(workspace, "busy project");
        string other = Path.Combine(workspace, "other project");
        foreach (string directory in new[] { project, other })
        {
            Directory.CreateDirectory(Path.Combine(directory, "terminal-notes"));
            await File.WriteAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(directory), "# Existing Plan\nPreserve existing work.");
            await File.WriteAllTextAsync(Path.Combine(directory, ".fixture-busy-composer"), "fake API only");
        }
        string settings = Path.Combine(fixture, "busy composer settings.json");
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new
        { Workspace = workspace, CodexWorkspace = workspace, SetupCompleted = true, WorkspaceDefaultsVersion = 1 }));
        Type admission = typeof(TerminalPane).Assembly.GetType("TerminalNotes.Services.TerminalStartupAdmission")!;
        FieldInfo acquire = admission.GetField("AcquireOverride", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? previousAcquire = acquire.GetValue(null);
        acquire.SetValue(null, (Func<CancellationToken, Task<IDisposable>>)(_ => Task.FromResult<IDisposable>(new ResourceTestLease())));
        object? previousData = AppContext.GetData("TerminalNotes.DataDirectory");
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(fixture, "busy composer app data"));
        var window = new MainWindow(settings, autoStartCodex: true, startupHooks: FakeStartupHooks);
        string hold = Path.Combine(project, ".fixture-hold-composer-receipt");
        string signal = Path.Combine(project, ".fixture-composer-receipt-waiting");
        string trace = Environment.GetEnvironmentVariable("TN_WORKFLOW_TRACE")!;
        List<JsonElement> Requests() => Directory.GetFiles(trace, "startup-prompt-*.json")
            .Select(path => JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path)))
            .Where(item => item.GetProperty("Directory").GetString() == project).ToList();
        try
        {
            window.Show();
            await UntilAsync(() => FieldValue(window, "_openCodeReady") is true, "fake OpenCode readiness");
            async Task Select(string directory)
            {
                Call(window, "EnsureProjectTaskRow", directory);
                Call(window, "SelectTaskProject", directory, true);
                await UntilAsync(() => Field<TextBox>(window, "_tabDocumentEditor").IsEnabled &&
                    FieldValue(window, "_projectInputProject")?.ToString() == directory, "composer selection");
            }
            await Select(project);
            var input = Field<TextBox>(window, "_projectInputEditor");
            input.Text = "First task remains running.";
            await ((Task)Call(window, "SaveTabDocumentAsync", true)!).WaitAsync(TimeSpan.FromSeconds(30));
            object runtime = Runtime(window, project);
            var tab = (TabItem)RuntimeField(runtime, "SingleTerminal")!;
            var pane = (TerminalPane)GetProperty(tab.Tag, "Pane")!;
            bool IsBusy() => GetProperty(pane, "NativeOpenCodeStatus") is OpenCodeStatusKind.Busy;
            await UntilAsync(IsBusy, "first native task is busy");
            string session = (string)FieldValue(pane, "_headlessSessionId")!;
            using var client = new OpenCodeConversationClient((Uri)GetProperty(pane, "NativeOpenCodeEndpoint")!, project);
            int pid = (await client.GetSessionAsync(session)).GetProperty("testProcessId").GetInt32();
            Require(!pane.CanExecuteSavedPlan && Requests().Count == 1, "Ordinary Plan execution must retain its idle gate.");
            const string request = "Add export validation while the first task is running.";
            input.Text = request;
            await File.WriteAllTextAsync(hold, "hold accepted response");
            await File.WriteAllTextAsync(Path.Combine(project, ".fixture-drop-startup-prompt-response-once"), "lose response after acceptance");
            Task dispatch = (Task)Call(window, "SaveTabDocumentAsync", true)!;
            await UntilAsync(() => File.Exists(signal), "busy request immediately reaches native prompt_async");
            Require(!dispatch.IsCompleted && IsBusy() && Requests().Count == 2 &&
                FieldValue(window, "_tabDocumentSaving") is false, "Busy request waited for idle or held the global save lock.");
            await (Task)Call(window, "SaveTabDocumentAsync", true)!;
            Require(Requests().Count == 2, "A duplicate click posted the busy request again.");
            string saved = await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(project));
            Require(saved.Split(request, StringSplitOptions.None).Length == 2, "The busy request was appended more than once.");
            input.Text = "Keep my newer draft.";
            await Select(other);
            input.Text = "Another project may save independently.";
            await (Task)Call(window, "SaveTabDocumentAsync", false)!;
            File.Delete(hold);
            await dispatch.WaitAsync(TimeSpan.FromSeconds(15));
            await Select(project);
            Require(ReferenceEquals(RuntimeField(runtime, "SingleTerminal"), tab) && FieldValue(pane, "_headlessSessionId")?.ToString() == session &&
                (await client.GetSessionAsync(session)).GetProperty("testProcessId").GetInt32() == pid && input.Text == "Keep my newer draft." &&
                Requests().Count == 2 && Requests().All(item => item.GetProperty("SessionId").GetString() == session),
                "Busy dispatch replaced the pane/session, repeated an uncertain POST, or erased newer draft text.");
            // A further request clears only after exact native receipt while old work stays busy.
            await ((Task)Call(window, "SaveTabDocumentAsync", true)!).WaitAsync(TimeSpan.FromSeconds(15));
            Require(input.Text.Length == 0 && Field<TextBlock>(window, "_projectInputHint").Text.Contains("전송 완료", StringComparison.Ordinal) &&
                IsBusy() && Requests().Count == 3 && RuntimeField(runtime, "SingleError") is null,
                "Accepted busy input did not clear/show transmission completion, or was confused with task completion.");
            // Server-side approval remains a guard even if the local busy status has not caught up.
            await File.WriteAllTextAsync(Path.Combine(project, ".fixture-composer-permission"), "owned approval");
            input.Text = "Retain this request until approval is handled.";
            Task blocked = (Task)Call(window, "SaveTabDocumentAsync", true)!;
            await Task.Delay(1200);
            Require(Requests().Count == 3 && input.Text.Length > 0, "Busy allowance bypassed the exact session permission guard.");
            await (Task)Call(window, "StopProjectTaskAsync", project)!;
            await blocked.WaitAsync(TimeSpan.FromSeconds(10));
            Require(input.Text == "Retain this request until approval is handled." && Requests().Count == 3,
                "A failed/stopped busy dispatch cleared the request or posted it despite pending approval.");
            Console.WriteLine("PASS busy composer: same running pane/session, immediate prompt receipt, lost-response deduplication, duplicate click guard, cross-project save, newer draft preservation, acceptance-only completion and permission guard; fake API only.");
        }
        finally
        {
            if (File.Exists(hold)) File.Delete(hold);
            Call(window, "DisposeResources"); window.Close();
            acquire.SetValue(null, previousAcquire);
            AppContext.SetData("TerminalNotes.DataDirectory", previousData);
        }
    }
}
