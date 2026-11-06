using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TerminalNotes;
using TerminalNotes.Services;

internal static partial class Program
{
    private static async Task VerifyTerminalCloseAsync(string fixture)
    {
        string workspace = Path.Combine(fixture, "terminal close workspace");
        string first = Path.Combine(workspace, "close this project");
        string other = Path.Combine(workspace, "keep this project");
        foreach (string directory in new[] { first, other })
        {
            Directory.CreateDirectory(Path.Combine(directory, "terminal-notes"));
            await File.WriteAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(directory), "# Existing plan\nPreserve project records.");
            await File.WriteAllTextAsync(Path.Combine(directory, ".fixture-busy-composer"), "fake API only");
        }
        string settings = Path.Combine(fixture, "terminal close settings.json");
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new
        { Workspace = workspace, CodexWorkspace = workspace, SetupCompleted = true, WorkspaceDefaultsVersion = 1, AutoResumeEnabled = false }));
        Type admission = typeof(TerminalPane).Assembly.GetType("TerminalNotes.Services.TerminalStartupAdmission")!;
        FieldInfo acquire = admission.GetField("AcquireOverride", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? previousAcquire = acquire.GetValue(null);
        acquire.SetValue(null, (Func<CancellationToken, Task<IDisposable>>)(_ => Task.FromResult<IDisposable>(new ResourceTestLease())));
        object? previousData = AppContext.GetData("TerminalNotes.DataDirectory");
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(fixture, "terminal close data"));
        var window = new MainWindow(settings, autoStartCodex: true, startupHooks: FakeStartupHooks);
        var startedPanes = new List<TerminalPane>();
        try
        {
            window.Show();
            await UntilAsync(() => FieldValue(window, "_openCodeReady") is true, "fake OpenCode readiness");
            async Task<(object Runtime, TabItem Tab, TerminalPane Pane, int Pid)> Start(string directory)
            {
                Call(window, "EnsureProjectTaskRow", directory);
                Call(window, "SelectTaskProject", directory, true);
                await UntilAsync(() => FieldValue(window, "_projectInputProject")?.ToString() == directory &&
                    Field<TextBox>(window, "_tabDocumentEditor").IsEnabled, "close fixture project selection");
                Field<TextBox>(window, "_projectInputEditor").Text = "Keep this fake request running.";
                await ((Task)Call(window, "SaveTabDocumentAsync", true)!).WaitAsync(TimeSpan.FromSeconds(30));
                object runtime = Runtime(window, directory);
                var tab = (TabItem)RuntimeField(runtime, "SingleTerminal")!;
                var pane = (TerminalPane)GetProperty(tab.Tag, "Pane")!;
                startedPanes.Add(pane);
                await UntilAsync(() => GetProperty(pane, "NativeOpenCodeStatus") is OpenCodeStatusKind.Busy, "fake pane Busy");
                using var client = new OpenCodeConversationClient((Uri)GetProperty(pane, "NativeOpenCodeEndpoint")!, directory);
                int pid = (await client.GetSessionAsync((string)FieldValue(pane, "_headlessSessionId")!)).GetProperty("testProcessId").GetInt32();
                return (runtime, tab, pane, pid);
            }
            var target = await Start(first);
            var retained = await Start(other);
            var input = Field<TextBox>(window, "_projectInputEditor");
            input.Text = "Retain the other project's unsent draft.";
            var tabs = (TabControl)window.FindName("TerminalTabs");
            object? selectedTab = tabs.SelectedItem;
            string firstPlan = await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(first));
            string otherPlan = await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(other));
            Call(window, "ShowProjectTask", first);
            var view = Field<Dictionary<string, Grid>>(window, "_projectTaskViews")[first];
            var frame = view.Children.OfType<Border>().Single(item => ReferenceEquals(item.Tag, target.Tab));
            var header = (Grid)((Grid)frame.Child).Children[0];
            var actions = header.Children.OfType<WrapPanel>().Single();
            var close = actions.Children.OfType<Button>().Single(button => button.Content as string == "×");
            Require(actions.Children.OfType<Button>().All(button => button.Content as string != "닫기"),
                "The visible terminal X is duplicated by a close button.");
            Require(ReferenceEquals(close.Tag, target.Tab) &&
                System.Windows.Automation.AutomationProperties.GetName(close) == "이 터미널 종료" &&
                actions.Children.OfType<Button>().All(button => !(button.ToolTip as string ?? "").Contains("칸 비우기", StringComparison.Ordinal)),
                "The visible X still clears a slot or does not identify its terminal.");
            // Restore selection after preparing the other project's frame.
            Call(window, "SelectTaskProject", other, true);
            await UntilAsync(() => FieldValue(window, "_projectInputProject")?.ToString() == other, "retained project selection");
            selectedTab = tabs.SelectedItem;
            SetField(window, "_closePending", true);
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SetField(window, "_closePending", false);
            Require(tabs.Items.Contains(target.Tab) && target.Pane.IsRunning, "X bypassed the existing close guard.");
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => !tabs.Items.Contains(target.Tab), "one-click terminal X cleanup", 20);
            bool Exists(int pid) { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
            await UntilAsync(() => !Exists(target.Pid), "closed fake session process exit", 10);
            Require(FieldValue(target.Pane, "_disposed") is true && !target.Pane.IsRunning &&
                RuntimeField(target.Runtime, "SingleTerminal") is null && RuntimeField(target.Runtime, "SingleStopped") is true,
                "X did not dispose its exact session and persist the task stop.");
            Require(tabs.Items.Contains(retained.Tab) && retained.Pane.IsRunning && Exists(retained.Pid) &&
                ReferenceEquals(RuntimeField(retained.Runtime, "SingleTerminal"), retained.Tab) && ReferenceEquals(tabs.SelectedItem, selectedTab) &&
                FieldValue(window, "_selectedProjectPath")?.ToString() == other && input.Text == "Retain the other project's unsent draft.",
                "Closing another terminal changed the retained pane, process, selection or composer draft.");
            Require(await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(first)) == firstPlan &&
                await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(other)) == otherPlan,
                "Closing a terminal changed saved project plans.");
            // A legacy role and a current single session can share one project.
            // Closing the role must not abort the single session or its stage/checkpoint.
            var role = await Start(first);
            SetField(role.Runtime, "SingleTerminal", null);
            SetField(role.Runtime, "SingleSubmitted", false);
            var sibling = await Start(first);
            var team = (System.Collections.IDictionary)GetProperty(role.Runtime, "Team")!;
            team[AutomationRole.Implementer] = role.Tab;
            team[AutomationRole.Reviewer] = sibling.Tab;
            var microClient = new OpenCodeAutomationClient((Uri)GetProperty(sibling.Pane, "NativeOpenCodeEndpoint")!, first);
            var microType = typeof(MainWindow).GetNestedType("MicroManagementStage", BindingFlags.NonPublic)!;
            var microStages = (System.Collections.IDictionary)GetProperty(role.Runtime, "MicroStages")!;
            microStages[AutomationRole.Reviewer] = Activator.CreateInstance(microType, AutomationRole.Reviewer, Guid.NewGuid(), sibling.Pane, microClient)!;
            var store = new AutomationStore(first);
            var run = await store.StartApprovedPlanAsync("# Fake legacy workflow");
            var claim = new AutomationStepClaim(run.Id, Guid.NewGuid(), run.Revision, AutomationRole.Reviewer, 1,
                first, run.ApprovedPlanSnapshot, run.Options, 0, null, "Keep the sibling request running", DateTimeOffset.UtcNow);
            var stageClient = new OpenCodeAutomationClient((Uri)GetProperty(sibling.Pane, "NativeOpenCodeEndpoint")!, first);
            var stageType = typeof(MainWindow).GetNestedType("ActiveAutomationStage", BindingFlags.NonPublic)!;
            object observedStage = Activator.CreateInstance(stageType, store, claim,
                new AutomationCommand(first, "", "", "", "", AutomationRole.Reviewer), sibling.Pane, null, stageClient,
                new OpenCodeAutomationDispatch((string)FieldValue(sibling.Pane, "_headlessSessionId")!, "fake-observer"))!;
            SetField(role.Runtime, "Stage", observedStage);
            long siblingIntent = (long)RuntimeField(role.Runtime, "IntentVersion")!;
            bool siblingBusy = (bool)FieldValue(sibling.Pane, "_automationBusy")!;
            using var pendingSiblingStartup = new CancellationTokenSource();
            SetField(role.Runtime, "StartingSingle", true);
            SetField(role.Runtime, "SingleStartupCancellation", pendingSiblingStartup);
            Field<HashSet<string>>(window, "_startingProjectTasks").Add(first);
            var roleX = new Button { Content = "×", Tag = role.Tab };
            Call(window, "TerminalClose_Click", roleX, new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => !tabs.Items.Contains(role.Tab), "same-project role X cleanup", 20);
            await UntilAsync(() => !Exists(role.Pid), "closed role fake process exit", 10);
            await (Task)Call(window, "RetireStoppedStageAsync", observedStage)!;
            Require(!pendingSiblingStartup.IsCancellationRequested && RuntimeField(role.Runtime, "StartingSingle") is true &&
                ReferenceEquals(RuntimeField(role.Runtime, "SingleStartupCancellation"), pendingSiblingStartup),
                "Role X waited for or canceled an unrelated sibling startup.");
            SetField(role.Runtime, "StartingSingle", false);
            SetField(role.Runtime, "SingleStartupCancellation", null);
            Field<HashSet<string>>(window, "_startingProjectTasks").Remove(first);
            Require(sibling.Pane.IsRunning && Exists(sibling.Pid) && GetProperty(sibling.Pane, "NativeOpenCodeStatus") is OpenCodeStatusKind.Busy &&
                RuntimeField(role.Runtime, "SingleStopped") is false && (long)RuntimeField(role.Runtime, "IntentVersion")! == siblingIntent &&
                (bool)FieldValue(sibling.Pane, "_automationBusy")! == siblingBusy &&
                ReferenceEquals(RuntimeField(role.Runtime, "SingleTerminal"), sibling.Tab) && team.Count == 1 &&
                ReferenceEquals(team[AutomationRole.Reviewer], sibling.Tab) && (await store.GetAsync(run.Id))!.State == AutomationState.Stopped &&
                RuntimeField(role.Runtime, "Stage") is null && microStages.Count == 0 &&
                !Field<HashSet<TerminalPane>>(window, "_automationPanesToReplace").Contains(sibling.Pane),
                "Role X aborted or changed the same-project single/stage/checkpoint session, falsely stopped it, or retained a respawning role.");
            Console.WriteLine("PASS same-project role X stops only its process; sibling Busy/input ownership/intent survive, legacy workflow stops without sibling abort, closed role binding is removed.");
            string continuationPlan = await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(first));
            string continuedSession = await (Task<string>)Call(sibling.Pane, "CaptureContinuationSessionAsync")!;
            await (Task)Call(window, "StopProjectTaskCoreAsync", first)!;
            await (Task)Call(sibling.Pane, "StopForExternalContinuationAsync")!;
            Call(sibling.Pane, "CompleteExternalContinuation", continuedSession);
            var external = ExternalTerminalLauncher.Prepare(Environment.ProcessPath!, first, continuedSession,
                TerminalTabDocumentStore.GetProjectPlanPath(first), "owned external close fixture", false);
            using var launchJson = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(external.Directory, "launch.json")));
            string leasePath = launchJson.RootElement.GetProperty("Lease").GetString()!;
            string readyPath = Path.Combine(external.Directory, "fixture-ready");
            string script = "$lease=[IO.File]::Open('" + (@"\\?\" + leasePath).Replace("'", "''") +
                "',[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);" +
                "[IO.File]::WriteAllText('" + readyPath.Replace("'", "''") + "','ready');while($true){Start-Sleep -Milliseconds 100}";
            // Closing a console can terminate its shell without finally; use only this fixture's owned hidden tree.
            using var externalOwner = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                Arguments = "/d /c powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand " +
                    Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)),
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = first
            })!;
            try
            {
                await UntilAsync(() => File.Exists(readyPath), "owned external lease readiness");
                Require(ExternalTerminalLauncher.IsSessionActive(first, continuedSession) &&
                    (bool)Call(window, "CanControlTerminalProcess", first, sibling.Pane)! == false,
                    "A live external session allowed an embedded terminal restart.");
                object taskRow = ((System.Collections.IDictionary)FieldValue(window, "_projectTaskRows")!)[first]!;
                var taskStart = (Button)GetProperty(taskRow, "Start")!;
                await UntilAsync(() =>
                {
                    Call(window, "UpdateProjectTaskActivity");
                    return ((TextBlock)GetProperty(taskRow, "State")!).Text.Contains("CMD에서 이어가기", StringComparison.Ordinal);
                }, "task row observes live external lease", 10);
                Require(!taskStart.IsEnabled, "Task row Start stayed enabled for a live external CMD.");
                await (Task)Call(window, "ToggleProjectTaskAsync", first)!;
                await (Task)Call(window, "RunTerminalLifecycleAsync", sibling.Tab, true)!;
                Require(ReferenceEquals(RuntimeField(sibling.Runtime, "SingleTerminal"), sibling.Tab),
                    "Restart replaced a pane while its external session was alive.");
                externalOwner.Kill(entireProcessTree: true);
                await externalOwner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                await UntilAsync(() => !ExternalTerminalLauncher.IsProjectActive(first), "abrupt external tree exit releases lease");
                Require(File.Exists(leasePath) && GetProperty(sibling.Pane, "WasContinuedExternally") is true && !sibling.Pane.IsRunning,
                    "Fixture did not preserve the stale lease file and historical handoff state.");
                Require((bool)Call(window, "CanControlTerminalProcess", first, sibling.Pane)!,
                    "Exited external CMD permanently blocks the stopped terminal's Start action.");
                await UntilAsync(() =>
                {
                    Call(window, "UpdateProjectTaskActivity");
                    return taskStart.IsEnabled;
                }, "task row Start reenabled after external lease exit", 10);
                // The same exact session can be prepared again without erasing its lease/history.
                ExternalTerminalLauncher.Prepare(Environment.ProcessPath!, first, continuedSession,
                    TerminalTabDocumentStore.GetProjectPlanPath(first), "same session after close", false);
                await (Task)Call(window, "ToggleProjectTaskAsync", first)!;
                var restartedTab = (TabItem)RuntimeField(sibling.Runtime, "SingleTerminal")!;
                var restartedPane = (TerminalPane)GetProperty(restartedTab.Tag, "Pane")!;
                startedPanes.Add(restartedPane);
                await UntilAsync(() => restartedPane.IsRunning, "restart after external CMD exit", 20);
                Require(!ReferenceEquals(restartedTab, sibling.Tab) && FieldValue(sibling.Pane, "_disposed") is true &&
                    GetProperty(sibling.Pane, "WasContinuedExternally") is true && retained.Pane.IsRunning && Exists(retained.Pid) &&
                    await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(first)) == continuationPlan,
                    "Restart lost the handoff history, changed another process or changed the plan.");
                // Exercise the terminal toolbar restart as well as the task row action.
                string restartedSession = await (Task<string>)Call(restartedPane, "CaptureContinuationSessionAsync")!;
                await (Task)Call(window, "StopProjectTaskCoreAsync", first)!;
                await (Task)Call(restartedPane, "StopForExternalContinuationAsync")!;
                Call(restartedPane, "CompleteExternalContinuation", restartedSession);
                await (Task)Call(window, "RunTerminalLifecycleAsync", restartedTab, true)!;
                var toolbarTab = (TabItem)RuntimeField(sibling.Runtime, "SingleTerminal")!;
                var toolbarPane = (TerminalPane)GetProperty(toolbarTab.Tag, "Pane")!;
                startedPanes.Add(toolbarPane);
                await UntilAsync(() => toolbarPane.IsRunning, "terminal toolbar restart after handoff exit", 20);
                Require(!ReferenceEquals(toolbarTab, restartedTab) && FieldValue(restartedPane, "_disposed") is true &&
                    GetProperty(restartedPane, "WasContinuedExternally") is true,
                    "Terminal toolbar did not replace the historical handoff pane safely.");
                Console.WriteLine("PASS live external lease blocks both restart paths; abrupt owned CMD tree exit releases its unchanged lease file, refreshes task row Start, permits same-session preparation and both task/toolbar restart.");
            }
            finally
            {
                if (!externalOwner.HasExited) externalOwner.Kill(entireProcessTree: true);
                await externalOwner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            Console.WriteLine("PASS one-click terminal X awaits exact fake session/process disposal, persists Stop, honors guards, retains other live pane/project/selection/draft and both plans; no real models/settings.");
        }
        finally
        {
            Call(window, "DisposeResources");
            foreach (var pane in startedPanes) await (Task)Call(pane, "StopOwnedOpenCodeProcessAsync")!;
            window.Close();
            acquire.SetValue(null, previousAcquire);
            AppContext.SetData("TerminalNotes.DataDirectory", previousData);
        }
    }
}
