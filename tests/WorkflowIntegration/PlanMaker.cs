using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TerminalNotes;
using TerminalNotes.Services;

internal static partial class Program
{
    private static async Task VerifyPlanMakerAsync(string fixture)
    {
        string workspace = Path.Combine(fixture, "modal planner workspace");
        string[] projects = Enumerable.Range(0, 10).Select(index => Path.Combine(workspace, "modal planner " + index)).ToArray();
        const string original = "# Existing Plan\nPreserve the user's requirements.";
        const string existingReportDetail = "Purpose: Keep CSV columns `Order ID` and `Total (KRW)`; maximum 5000 rows; exclude canceled orders; write `C:\\Reports\\daily.csv`; retain `amount >= 0`.";
        const string existingReportChecks = "## Existing report checks\n- Verify existing filter combinations.";
        foreach (string project in projects)
        {
            Directory.CreateDirectory(project);
            if (project != projects[7])
            {
                Directory.CreateDirectory(Path.Combine(project, "terminal-notes"));
                string initialPlan = project == projects[0]
                    ? original + "\n\n# Confirmed Plan\n\n" + existingReportDetail + "\n\n" + existingReportChecks
                    : original;
                await File.WriteAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(project), initialPlan);
            }
            await File.WriteAllTextAsync(Path.Combine(project, ".fixture-plan-maker"), "fake API only");
        }
        await File.WriteAllTextAsync(Path.Combine(projects[0], ".fixture-busy-composer"), "retain main coding task");
        await File.WriteAllTextAsync(Path.Combine(projects[0], ".fixture-plan-maker-cumulative"), "replay cumulative requirements");
        await File.WriteAllTextAsync(Path.Combine(projects[4], ".fixture-plan-maker-no-tools"), "unsupported API");
        await File.WriteAllTextAsync(Path.Combine(projects[5], ".fixture-plan-maker-no-permission"), "ignored permission");
        await File.WriteAllTextAsync(Path.Combine(projects[8], ".fixture-plan-maker-hold-answer"), "wait for explicit finish during busy answer");
        await File.WriteAllTextAsync(Path.Combine(projects[8], ".fixture-plan-maker-native-model"), "native default initially, exact chosen alias on continuation");
        string settings = Path.Combine(fixture, "modal planner settings.json");
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new
        { Workspace = workspace, CodexWorkspace = workspace, SetupCompleted = true, WorkspaceDefaultsVersion = 1 }));
        Type admission = typeof(TerminalPane).Assembly.GetType("TerminalNotes.Services.TerminalStartupAdmission")!;
        FieldInfo acquire = admission.GetField("AcquireOverride", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? previousAcquire = acquire.GetValue(null);
        acquire.SetValue(null, (Func<CancellationToken, Task<IDisposable>>)(_ => Task.FromResult<IDisposable>(new ResourceTestLease())));
        object? previousData = AppContext.GetData("TerminalNotes.DataDirectory");
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(fixture, "modal planner app data"));
        var window = new MainWindow(settings, autoStartCodex: true, startupHooks: FakeStartupHooks);
        try
        {
            Type instructions = typeof(TerminalPane).Assembly.GetType("TerminalNotes.Services.PlanMakerInstructions")!;
            MethodInfo readInstructions = instructions.GetMethod("ReadCurrent", BindingFlags.Static | BindingFlags.NonPublic)!;
            MethodInfo saveInstructions = instructions.GetMethod("TrySave", BindingFlags.Static | BindingFlags.NonPublic)!;
            string instructionsPath = (string)instructions.GetProperty("FilePath", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Require(instructionsPath.StartsWith(Path.GetFullPath(fixture) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Planner instruction fixture escaped the isolated data directory.");
            void SaveGuidance(string expected, string replacement) => Require(
                saveInstructions.Invoke(null, new object[] { expected, replacement })?.ToString() == "Saved",
                "Isolated planner guidance did not save.");
            const string firstGuidance = "통합 테스트 최초 지침: 선택지를 두 가지로 설명하고 답변을 확인하세요.";
            const string nextGuidance = "통합 테스트 갱신 지침: 쉬운 용어로 예외 상황을 확인하세요.";
            SaveGuidance((string)readInstructions.Invoke(null, null)!, firstGuidance);
            window.Show();
            await UntilAsync(() => FieldValue(window, "_openCodeReady") is true, "fake installation readiness");
            // This scenario drives the planner's own window (the hidden-main-window path); the docked view has its own tests.
            SetField(window, "_planMakerDeck", null);
            var input = Field<TextBox>(window, "_projectInputEditor");
            var editor = Field<TextBox>(window, "_tabDocumentEditor");
            var planMaker = Field<Button>(window, "_planMakerButton");
            Require(FieldValue(window, "_projectInputProject") is null && !planMaker.IsEnabled &&
                planMaker.ToolTip?.ToString()?.Contains("프로젝트", StringComparison.Ordinal) is true &&
                ToolTipService.GetShowOnDisabled(planMaker), "The unselected planner did not explain that a project is required.");
            async Task Select(string project)
            {
                Call(window, "EnsureProjectTaskRow", project);
                Call(window, "SelectTaskProject", project, true);
                await UntilAsync(() => editor.IsEnabled && FieldValue(window, "_projectInputProject")?.ToString() == project,
                    "selected project document");
            }
            object? Dialog(string project) => ((System.Collections.IDictionary)FieldValue(window, "_planMakerDialogs")!)[project];
            async Task<(string Project, Window Dialog, TerminalPane Pane, OpenCodeConversationClient Client)> Start(string project, string wish, string? initialDraft = null)
            {
                await Select(project);
                if (initialDraft is not null) editor.Text = initialDraft;
                input.Text = wish;
                Require(planMaker.IsEnabled, "The selected project's planner button was disabled for request: " + JsonSerializer.Serialize(wish));
                planMaker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await UntilAsync(() => Dialog(project) is Window dialog && dialog.IsLoaded, "dedicated modal loads");
                var dialog = (Window)Dialog(project)!;
                var pane = (TerminalPane)GetProperty(dialog, "Pane")!;
                await UntilAsync(() => GetProperty(pane, "IsPlanMakerActive") is true || GetProperty(dialog, "Error") is string,
                    "native modal planner starts or reports unsupported API");
                var client = new OpenCodeConversationClient((Uri)GetProperty(pane, "NativeOpenCodeEndpoint")!, project);
                return (project, dialog, pane, client);
            }
            async Task<JsonElement> Question(OpenCodeConversationClient client, string? expected = null)
            {
                JsonElement result = default;
                await UntilAsync(async () =>
                {
                    JsonElement questions = await client.ReadQuestionsAsync();
                    if (questions.GetArrayLength() != 1) return false;
                    result = questions[0];
                    return expected is null || result.GetProperty("questions")[0].GetProperty("question").GetString()!.Contains(expected, StringComparison.Ordinal);
                }, "one native question");
                Require(result.GetProperty("questions").GetArrayLength() == 1, "Planner requested more than one question.");
                return result;
            }
            async Task Answer(TerminalPane pane, JsonElement question, string answer) =>
                await (Task)Call(pane, "ReplyNativeQuestionAsync", JsonSerializer.SerializeToElement(new
                { requestID = question.GetProperty("id").GetString(), answers = new[] { new[] { answer } } }))!;
            async Task FinishInterview(Window dialog)
            {
                Button finish = Field<Button>(dialog, "_finish");
                await UntilAsync(() => finish.IsEnabled, "confirmed draft enables explicit interview completion");
                Require(finish.Content?.ToString() == "플랜으로 돌아가기" && GetProperty(dialog, "PlanningCompleted") is false,
                    "The interview completed before its explicit finish action.");
                finish.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await UntilAsync(() => GetProperty(dialog, "PlanningCompleted") is true, "explicit interview completion");
                await UntilAsync(() => !dialog.IsVisible && GetProperty(GetProperty(dialog, "Pane")!, "IsRunning") is false,
                    "one completion click returns and stops its owned planner");
            }
            async Task Finish(Window dialog, TerminalPane pane, OpenCodeConversationClient client)
            {
                await Answer(pane, await Question(client), "A useful reports page");
                await Answer(pane, await Question(client, "A useful reports page"), "Exported report matches filters");
                await Question(client, "Exported report matches filters");
                await FinishInterview(dialog);
            }
            async Task Close(string project, Window dialog)
            {
                string promptsPath = Path.Combine(project, ".fixture-plan-maker-prompts");
                object pane = GetProperty(dialog, "Pane")!;
                object? host = FieldValue(pane, "_headlessOpenCodeHost");
                object? ownedPid = host is null ? null : GetProperty(host, "ProcessId");
                string CloseState()
                {
                    bool processAlive = false;
                    if (ownedPid is not null)
                    {
                        try { using var process = System.Diagnostics.Process.GetProcessById(Convert.ToInt32(ownedPid)); processAlive = !process.HasExited; }
                        catch (ArgumentException) { }
                    }
                    return "visible=" + dialog.IsVisible + "; running=" + GetProperty(pane, "IsRunning") +
                        "; disposed=" + FieldValue(pane, "_disposed") + "; cleaned=" + FieldValue(dialog, "_cleaned") +
                        "; cleanupStarted=" + FieldValue(dialog, "_cleanupStarted") + "; ownedPid=" + ownedPid +
                        "; processAlive=" + processAlive + "; lifecycle=" + FieldValue(pane, "_lastLifecycleStage");
                }
                var closeEvents = new List<string>();
                dialog.Closing += (_, args) => closeEvents.Add("Closing(cancel=" + args.Cancel + "): " + CloseState());
                dialog.Closed += (_, _) => closeEvents.Add("Closed: " + CloseState());
                bool alreadyReturned = !dialog.IsVisible;
                if (!alreadyReturned) dialog.Close();
                closeEvents.Add("Close returned: " + CloseState());
                await UntilAsync(() => !((System.Collections.IDictionary)FieldValue(window, "_planMakers")!).Contains(project),
                    "clicked planner operation completes", seconds: 20);
                Require(!dialog.IsVisible && GetProperty(GetProperty(dialog, "Pane")!, "IsRunning") is false &&
                    (alreadyReturned || closeEvents.Any(item => item.StartsWith("Closed:", StringComparison.Ordinal))),
                    "Modal operation completed before the dialog closed and its owned process stopped: project=" + project +
                    "; " + CloseState() + "; events=" + string.Join(" | ", closeEvents) +
                    "; error=" + GetProperty(dialog, "Error") + "; diagnostic=" + GetProperty(pane, "NativeAutomationDiagnostic"));
                await File.WriteAllTextAsync(Path.Combine(project, ".fixture-plan-maker-close"),
                    string.Join("\n", closeEvents) + "\nOperation completed: " + CloseState() + "; error=" + GetProperty(dialog, "Error"));
                string? promptsAfterClose = File.Exists(promptsPath) ? await File.ReadAllTextAsync(promptsPath) : null;
                await Task.Delay(1200);
                Require(!File.Exists(promptsPath) ? promptsAfterClose is null :
                    await File.ReadAllTextAsync(promptsPath) == promptsAfterClose,
                    "A closed planner sent another continuation request.");
            }

            await Select(projects[0]);
            input.Text = "Main coding request stays busy.";
            Task firstDispatch = (Task)Call(window, "SaveTabDocumentAsync", true)!;
            try { await firstDispatch.WaitAsync(TimeSpan.FromSeconds(45)); }
            catch (TimeoutException)
            {
                object waiting = Runtime(window, projects[0]);
                var waitingPane = (TerminalPane)GetProperty(((TabItem)RuntimeField(waiting, "SingleTerminal")!).Tag, "Pane")!;
                throw new InvalidOperationException("Fake main startup stalled: " + RuntimeField(waiting, "SingleStartPhase") +
                    "; ready=" + waitingPane.IsReady + "; running=" + waitingPane.IsRunning +
                    "; block=" + waitingPane.PlanExecutionBlockReason + "; diagnostic=" + GetProperty(waitingPane, "NativeAutomationDiagnostic"));
            }
            object runtime = Runtime(window, projects[0]);
            var mainTab = (TabItem)RuntimeField(runtime, "SingleTerminal")!;
            var mainPane = (TerminalPane)GetProperty(mainTab.Tag, "Pane")!;
            await UntilAsync(() => GetProperty(mainPane, "NativeOpenCodeStatus") is OpenCodeStatusKind.Busy, "existing coding task busy");
            string mainSession = FieldValue(mainPane, "_headlessSessionId")!.ToString()!;
            if (_loopFullPlanMakerOnly)
            {
                await VerifyLoopFullPlanMakerAsync(mainPane, projects[0]);
                return;
            }
            using var mainClient = new OpenCodeConversationClient((Uri)GetProperty(mainPane, "NativeOpenCodeEndpoint")!, projects[0]);
            int mainPid = (await mainClient.GetSessionAsync(mainSession)).GetProperty("testProcessId").GetInt32();
            string savedBefore = await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(projects[0]));
            bool KeepsExistingMainPlan() => savedBefore.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
                .Where(line => !string.IsNullOrWhiteSpace(line)).All(line => editor.Text.Contains(line, StringComparison.Ordinal));
            int slotsBefore = Field<TabControl>(window, "TerminalTabs").Items.Count;
            var accepted = await Start(projects[0], "");
            using (accepted.Client)
            {
                JsonElement first = await Question(accepted.Client);
                string plannerSession = first.GetProperty("sessionID").GetString()!;
                string initialPrompt = (await accepted.Client.ReadMessagesAsync(plannerSession))[0]
                    .GetProperty("parts")[0].GetProperty("text").GetString()!;
                Require(initialPrompt.Contains(firstGuidance, StringComparison.Ordinal) && !initialPrompt.Contains(nextGuidance, StringComparison.Ordinal),
                    "A new planner interview did not receive its saved Korean guidance.");
                SaveGuidance(firstGuidance, nextGuidance);
                int plannerPid = (await accepted.Client.GetSessionAsync(plannerSession)).GetProperty("testProcessId").GetInt32();
                Require(!Field<Button>(accepted.Dialog, "_finish").IsEnabled,
                    "Finish was enabled before the interview produced a confirmed draft.");
                Require(plannerPid != mainPid && !ReferenceEquals(mainPane, accepted.Pane) &&
                    Field<TabControl>(window, "TerminalTabs").Items.Count == slotsBefore &&
                    ReferenceEquals(RuntimeField(runtime, "SingleTerminal"), mainTab), "PlanMaker consumed or reused the coding terminal.");
                Require(!planMaker.IsEnabled && planMaker.ToolTip?.ToString()?.Contains("이미 진행", StringComparison.Ordinal) is true,
                    "An active planner did not explain its duplicate-start guard.");
                await (Task)Call(window, "MakeProjectPlanAsync")!;
                await (Task)Call(window, "SaveTabDocumentAsync", true)!;
                Require(await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(projects[0])) == savedBefore &&
                    GetProperty(accepted.Pane, "HasUnfinishedAutoResumeWork") is false &&
                    GetProperty(accepted.Pane, "IsContinuousImprovementEnabled") is false,
                    "Planning allowed duplicate dispatch, saved early, or enabled Loop.");
                await Task.Delay(700);
                Require(GetProperty(accepted.Dialog, "PlanningCompleted") is false, "Unanswered native tool question became a final plan.");
                await Answer(accepted.Pane, first, "A useful reports page");
                await Question(accepted.Client, "A useful reports page");
                await UntilAsync(() => editor.Text.Contains("A useful reports page", StringComparison.Ordinal), "live accumulated draft");
                Require(KeepsExistingMainPlan() &&
                    editor.Text.Split("# Confirmed Plan", StringSplitOptions.None).Length == 2 &&
                    editor.Text.IndexOf("Purpose: A useful reports page", StringComparison.Ordinal) <
                        editor.Text.IndexOf(existingReportChecks, StringComparison.Ordinal) &&
                    await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(projects[0])) == savedBefore,
                    "Intermediate answers did not join the existing category, replaced original requirements, or saved the plan.");
                await Answer(accepted.Pane, await Question(accepted.Client), "Exported report matches filters");
                JsonElement third = await Question(accepted.Client, "Exported report matches filters");
                string thirdText = third.GetProperty("questions")[0].GetProperty("question").GetString()!;
                Require(third.GetProperty("sessionID").GetString() == plannerSession &&
                    thirdText.Contains("A useful reports page", StringComparison.Ordinal) &&
                    GetProperty(accepted.Dialog, "PlanningCompleted") is false,
                    "An early model completion ended the interview or lost the earlier answers in its next question.");
                JsonElement interviewMessages = await accepted.Client.ReadMessagesAsync(plannerSession);
                JsonElement[] requests = interviewMessages.EnumerateArray().Where(message =>
                    message.GetProperty("info").GetProperty("role").GetString() == "user").ToArray();
                Require(requests.Length == 2 && requests.Select(message => message.GetProperty("info").GetProperty("id").GetString()).Distinct().Count() == 2 &&
                    interviewMessages.EnumerateArray().Any(message => message.GetProperty("info").TryGetProperty("parentID", out var parent) &&
                        parent.GetString() == requests[1].GetProperty("info").GetProperty("id").GetString()),
                    "Continuation did not use a fresh native request and response parent in the same session.");
                string[] plannerRequestTraces = Directory.GetFiles(Environment.GetEnvironmentVariable("TN_WORKFLOW_TRACE")!, "plan-maker-*.json");
                Require(plannerRequestTraces.Length >= 2, "Initial and continued planner native request payloads were not recorded.");
                foreach (string requestTrace in plannerRequestTraces)
                {
                    using JsonDocument request = JsonDocument.Parse(await File.ReadAllTextAsync(requestTrace));
                    Require(request.RootElement.GetProperty("model").GetProperty("providerID").GetString() == "company" &&
                        request.RootElement.GetProperty("model").GetProperty("modelID").GetString() == "planning-alias",
                        "A planner initial or continued native request replaced the configured company model alias.");
                }
                string retainedConversation = interviewMessages.GetRawText();
                Require(!requests[1].GetProperty("parts")[0].GetProperty("text").GetString()!.Contains(nextGuidance, StringComparison.Ordinal),
                    "Saving new guidance injected it into an already-running interview.");
                Require(retainedConversation.Contains("A useful reports page", StringComparison.Ordinal) &&
                    retainedConversation.Contains("Exported report matches filters", StringComparison.Ordinal),
                    "The continued interview lost confirmed requirements from its conversation history.");
                const string addedConstraint = "Only administrators may export reports";
                await Task.Delay(650);
                await Answer(accepted.Pane, third, addedConstraint);
                await UntilAsync(() => editor.Text.Contains(addedConstraint, StringComparison.Ordinal), "third answer enriches draft");
                Require(KeepsExistingMainPlan() &&
                    editor.Text.Contains("A useful reports page", StringComparison.Ordinal) &&
                    editor.Text.Contains("Exported report matches filters", StringComparison.Ordinal) &&
                    await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(projects[0])) == savedBefore,
                    "A later partial provider draft discarded confirmed requirements or saved automatically.");
                Button continueQuestions = Field<Button>(accepted.Dialog, "_continue");
                await UntilAsync(() => continueQuestions.IsEnabled &&
                    GetProperty(FieldValue(accepted.Dialog, "_interview")!, "IsAwaitingContinuation") is true,
                    "questionless completed turns wait for user continuation");
                int pausedRequests = (await accepted.Client.ReadMessagesAsync(plannerSession)).EnumerateArray().Count(message =>
                    message.GetProperty("info").GetProperty("role").GetString() == "user");
                Require(pausedRequests == 4, "The interview did not pause after exactly two questionless completed turns.");
                await Task.Delay(650);
                Require(GetProperty(accepted.Dialog, "PlanningCompleted") is false &&
                    (await accepted.Client.ReadQuestionsAsync()).GetArrayLength() == 0 &&
                    (await accepted.Client.ReadMessagesAsync(plannerSession)).EnumerateArray().Count(message =>
                        message.GetProperty("info").GetProperty("role").GetString() == "user") == pausedRequests,
                    "Questionless completion ended the interview or caused unbounded automatic prompting.");
                Require(continueQuestions.Content?.ToString() == "질문 계속", "The pause did not offer explicit question continuation.");
                continueQuestions.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                JsonElement resumed = await Question(accepted.Client, "더 추가할 요구사항");
                Require(resumed.GetProperty("sessionID").GetString() == plannerSession &&
                    (await accepted.Client.GetSessionAsync(plannerSession)).GetProperty("testProcessId").GetInt32() == plannerPid,
                    "Continuing questions recreated the owned process or session.");
                await FinishInterview(accepted.Dialog);
                await UntilAsync(() => GetProperty(accepted.Dialog, "PlanningCompleted") is true, "final unsaved planning draft");
                Require(!accepted.Dialog.IsVisible && !accepted.Pane.IsRunning &&
                    File.ReadAllLines(Path.Combine(projects[0], ".fixture-plan-maker-prompts")).Length == 5 &&
                    editor.Text.Contains("Exported report matches filters", StringComparison.Ordinal) &&
                    GetProperty(mainPane, "NativeOpenCodeStatus") is OpenCodeStatusKind.Busy &&
                    FieldValue(mainPane, "_headlessSessionId")?.ToString() == mainSession &&
                    await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(projects[0])) == savedBefore,
                    "Final planning saved/coded or changed the existing coding session.");
                Require(editor.Text.Split("Purpose: A useful reports page", StringSplitOptions.None).Length == 2 &&
                    editor.Text.Split("Acceptance: Exported report matches filters", StringSplitOptions.None).Length == 2 &&
                    editor.Text.Split("Constraint: " + addedConstraint, StringSplitOptions.None).Length == 2 &&
                    editor.Text.Split("# Confirmed Plan", StringSplitOptions.None).Length == 2 &&
                    editor.Text.Split(existingReportDetail, StringSplitOptions.None).Length == 2 &&
                    editor.Text.Contains(existingReportChecks, StringComparison.Ordinal) &&
                    editor.Text.IndexOf("Constraint: " + addedConstraint, StringComparison.Ordinal) <
                        editor.Text.IndexOf(existingReportChecks, StringComparison.Ordinal),
                    "Cumulative provider drafts duplicated a category or confirmed requirement, lost exact original details, or misplaced new conditions.");
                await Close(accepted.Project, accepted.Dialog);
                Require(input.Text.Length == 0 && (await mainClient.GetSessionAsync(mainSession)).GetProperty("testProcessId").GetInt32() == mainPid,
                    "Planner close erased main process or failed to clear the captured initial wish.");
                string finalDraft = editor.Text;
                await (Task)Call(window, "SaveTabDocumentAsync", false)!;
                Require(await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(projects[0])) == finalDraft,
                    "Explicit Save did not persist the accumulated draft.");
                Require((await mainClient.ReadMessagesAsync(mainSession)).GetArrayLength() == 1,
                    "Explicit Save started coding.");
                await (Task)Call(window, "SaveTabDocumentAsync", true)!;
                await Task.Delay(600);
                Require((await mainClient.ReadMessagesAsync(mainSession)).GetArrayLength() == 1,
                    "A busy terminal's ordinary saved-plan idle guard was bypassed.");
            }

            var newer = await Start(projects[1], "Original wish kept during planning.");
            using (newer.Client)
            {
                JsonElement first = await Question(newer.Client);
                string nextPrompt = (await newer.Client.ReadMessagesAsync(first.GetProperty("sessionID").GetString()!))[0]
                    .GetProperty("parts")[0].GetProperty("text").GetString()!;
                Require(nextPrompt.Contains(nextGuidance, StringComparison.Ordinal) && !nextPrompt.Contains(firstGuidance, StringComparison.Ordinal),
                    "A later planner interview did not pick up the newly saved guidance.");
                input.Text = "A newer request typed while planning.";
                await Finish(newer.Dialog, newer.Pane, newer.Client);
                await UntilAsync(() => GetProperty(newer.Dialog, "PlanningCompleted") is true, "newer request planning");
                await Close(newer.Project, newer.Dialog);
                Require(input.Text == "A newer request typed while planning.", "Planner erased a newer input revision.");
                Require(RuntimeField(Runtime(window, projects[1]), "SingleTerminal") is null &&
                    await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(projects[1])) == original,
                    "Completed planning created a main coding terminal or persisted the final plan.");
                input.Text = "";
                string executionDraft = editor.Text;
                await ((Task)Call(window, "SaveTabDocumentAsync", true)!).WaitAsync(TimeSpan.FromSeconds(30));
                object executedRuntime = Runtime(window, projects[1]);
                Require(RuntimeField(executedRuntime, "SingleTerminal") is TabItem &&
                    await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(projects[1])) == executionDraft,
                    "Explicit Execute did not save and begin the independent coding task.");
            }
            var busyFinish = await Start(projects[8], "Keep the latest answer when finishing during busy work.");
            using (busyFinish.Client)
            {
                await Answer(busyFinish.Pane, await Question(busyFinish.Client), "A useful reports page");
                await Answer(busyFinish.Pane, await Question(busyFinish.Client, "A useful reports page"), "Exported report matches filters");
                JsonElement third = await Question(busyFinish.Client, "Exported report matches filters");
                await Task.Delay(650);
                const string finalAnswer = "Audit every administrator export";
                Task pendingAnswer = Answer(busyFinish.Pane, third, finalAnswer);
                await UntilAsync(() => File.Exists(Path.Combine(projects[8], ".fixture-plan-maker-answer-busy")) &&
                    GetProperty(busyFinish.Pane, "NativeOpenCodeStatus") is OpenCodeStatusKind.Busy,
                    "third answer remains busy before explicit finish");
                Button finish = Field<Button>(busyFinish.Dialog, "_finish");
                Require(!finish.IsEnabled && !editor.Text.Contains(finalAnswer, StringComparison.Ordinal),
                    "Finish remained enabled while the manual answer POST was still unconfirmed.");
                finish.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(650);
                Require(GetProperty(busyFinish.Dialog, "PlanningCompleted") is false && !pendingAnswer.IsCompleted,
                    "Finish returned a stale draft while the current answer was still busy.");
                File.Delete(Path.Combine(projects[8], ".fixture-plan-maker-hold-answer"));
                await pendingAnswer;
                await UntilAsync(() => finish.IsEnabled, "confirmed manual answer enables one completion/return action");
                finish.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await UntilAsync(() => GetProperty(busyFinish.Dialog, "PlanningCompleted") is true,
                    "busy-answer finish drains the latest response");
                Require(editor.Text.Contains(finalAnswer, StringComparison.Ordinal) &&
                    editor.Text.Contains("A useful reports page", StringComparison.Ordinal) &&
                    editor.Text.Contains("Exported report matches filters", StringComparison.Ordinal) &&
                    await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(projects[8])) == original &&
                    File.ReadAllLines(Path.Combine(projects[8], ".fixture-plan-maker-prompts")).Length == 2,
                    "Busy finish lost the latest confirmed answer, saved automatically, or sent another question request.");
                await Close(busyFinish.Project, busyFinish.Dialog);
            }
            var conflict = await Start(projects[2], "Keep conflicting document edits.");
            using (conflict.Client)
            {
                await Question(conflict.Client);
                input.Text = "";
                await (Task)Call(window, "SaveTabDocumentAsync", false)!;
                await (Task)Call(window, "SaveTabDocumentAsync", true)!;
                input.Text = "Keep conflicting document edits.";
                editor.Text += "\nUser's newer unsaved edit.";
                string edited = editor.Text;
                await Answer(conflict.Pane, await Question(conflict.Client), "Conflicting purpose");
                await UntilAsync(() => GetProperty(conflict.Dialog, "Error") is string, "concurrent draft conflict");
                await Close(conflict.Project, conflict.Dialog);
                Require(editor.Text == edited && input.Text == "Keep conflicting document edits." &&
                    await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(projects[2])) == original,
                    "Planner overwrote a newer draft or discarded the retained request.");
            }
            var stopped = await Start(projects[3], "Keep this cancelled wish.");
            using (stopped.Client)
            {
                await Answer(stopped.Pane, await Question(stopped.Client), "Retain partial confirmation");
                await UntilAsync(() => editor.Text.Contains("Retain partial confirmation", StringComparison.Ordinal), "partial draft before cancellation");
                await Select(projects[1]);
                await Close(stopped.Project, stopped.Dialog);
                await Select(projects[3]);
                Require(input.Text == "Keep this cancelled wish." && editor.Text.Contains("Retain partial confirmation", StringComparison.Ordinal) &&
                    await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(projects[3])) == original,
                    "Cancel/project switch lost the accumulated draft or persisted it.");
            }
            foreach (int index in new[] { 4, 5 })
            {
                var rejected = await Start(projects[index], "Retain request on unsupported planner API.");
                using (rejected.Client)
                {
                    await UntilAsync(() => GetProperty(rejected.Dialog, "Error") is string, "unsupported planner API rejected");
                    await Close(rejected.Project, rejected.Dialog);
                    Require(input.Text == "Retain request on unsupported planner API." &&
                        await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(projects[index])) == original,
                        "Unsupported tools/permissions failed open or saved a plan.");
                }
            }
            const string unsavedBefore = original + "\r\nUser's unsaved requirements before planning.\r\n  \r\n";
            await File.WriteAllTextAsync(Path.Combine(projects[6], ".fixture-plan-maker-echo"), unsavedBefore);
            int existingSlots = Field<TabControl>(window, "TerminalTabs").Items.Count;
            var blank = await Start(projects[6], " \t\r\n", unsavedBefore);
            using (blank.Client)
            {
                JsonElement first = await Question(blank.Client);
                JsonElement messages = await blank.Client.ReadMessagesAsync(first.GetProperty("sessionID").GetString()!);
                string prompt = messages[0].GetProperty("parts")[0].GetProperty("text").GetString()!;
                Require(prompt.Contains("what they want to add, change, or clarify", StringComparison.Ordinal) &&
                    prompt.EndsWith("\n\nExisting Plan context:\n" + unsavedBefore, StringComparison.Ordinal) &&
                    input.Text == " \t\r\n" && RuntimeField(Runtime(window, projects[6]), "SingleTerminal") is null &&
                    Field<TabControl>(window, "TerminalTabs").Items.Count == existingSlots,
                    "Blank-request planning invented an editor request, lost the unsaved plan context, or allocated a coding terminal.");
                await Finish(blank.Dialog, blank.Pane, blank.Client);
                await UntilAsync(() => GetProperty(blank.Dialog, "PlanningCompleted") is true, "blank-request planning completes");
                Require(editor.Text.StartsWith(unsavedBefore, StringComparison.Ordinal) &&
                    editor.Text.Contains("Exported report matches filters", StringComparison.Ordinal) &&
                    editor.Text.Split("# Existing Plan", StringSplitOptions.None).Length == 2 &&
                    editor.Text.Split("User's unsaved requirements before planning.", StringSplitOptions.None).Length == 2 &&
                    editor.Text.Split("Purpose: A useful reports page", StringSplitOptions.None).Length == 2 &&
                    editor.Text.Split("Acceptance: Exported report matches filters", StringSplitOptions.None).Length == 2 &&
                    await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(projects[6])) == original,
                    "Blank-request provider echoes duplicated requirements, changed original whitespace, or saved automatically.");
                await Close(blank.Project, blank.Dialog);
                Require(input.Text.Length == 0 && RuntimeField(Runtime(window, projects[6]), "SingleTerminal") is null,
                    "Blank-request completion created a coding terminal or retained its captured whitespace.");
                string accumulated = editor.Text;
                await (Task)Call(window, "SaveTabDocumentAsync", false)!;
                Require(await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(projects[6])) == accumulated &&
                    RuntimeField(Runtime(window, projects[6]), "SingleTerminal") is null,
                    "Explicit Save failed to persist blank-request additions independently of coding.");
            }
            Require(!File.Exists(TerminalTabDocumentStore.GetProjectPlanPath(projects[7])), "The new-folder fixture unexpectedly had a plan.");
            var initial = await Start(projects[7], "");
            using (initial.Client)
            {
                JsonElement first = await Question(initial.Client);
                JsonElement messages = await initial.Client.ReadMessagesAsync(first.GetProperty("sessionID").GetString()!);
                string prompt = messages[0].GetProperty("parts")[0].GetProperty("text").GetString()!;
                Require(prompt.Contains("what goal they want this project to achieve", StringComparison.Ordinal) &&
                    prompt.EndsWith("\n\nExisting Plan context:\n", StringComparison.Ordinal) && input.Text == "",
                    "An empty plan did not begin with the user's goal or injected a synthetic editor request.");
                await Finish(initial.Dialog, initial.Pane, initial.Client);
                await UntilAsync(() => GetProperty(initial.Dialog, "PlanningCompleted") is true, "initial goal planning completes");
                Require(editor.Text.Contains("Exported report matches filters", StringComparison.Ordinal) &&
                    await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(projects[7])) == "" &&
                    RuntimeField(Runtime(window, projects[7]), "SingleTerminal") is null,
                    "Initial goal planning saved or executed its new draft.");
                await Close(initial.Project, initial.Dialog);
            }
            var cleanupFailure = await Start(projects[9], "Retain the request when attach cleanup fails.");
            using (cleanupFailure.Client)
            {
                JsonElement first = await Question(cleanupFailure.Client);
                int ownedServerPid = (await cleanupFailure.Client.GetSessionAsync(first.GetProperty("sessionID").GetString()!))
                    .GetProperty("testProcessId").GetInt32();
                object attach = FieldValue(cleanupFailure.Pane, "_session")!;
                await (Task)attach.GetType().GetMethod("CloseAsync", BindingFlags.Instance | BindingFlags.Public)!.Invoke(attach, null)!;
                Require(GetProperty(attach, "IsRunning") is false && cleanupFailure.Pane.IsRunning &&
                    FieldValue(cleanupFailure.Pane, "_disposed") is false,
                    "Cleanup-failure fixture did not stop its real attach while retaining its owned server.");
                const string injectedFailure = "injected attach cleanup failure";
                SetField(attach, "_closeTask", Task.FromException(new IOException(injectedFailure)));
                await Close(cleanupFailure.Project, cleanupFailure.Dialog);
                bool serverAlive = false;
                try { using var server = System.Diagnostics.Process.GetProcessById(ownedServerPid); serverAlive = !server.HasExited; }
                catch (ArgumentException) { }
                Require(!serverAlive && GetProperty(cleanupFailure.Dialog, "Error")?.ToString()?.Contains(injectedFailure, StringComparison.Ordinal) is true &&
                    input.Text == "Retain the request when attach cleanup fails." &&
                    await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(projects[9])) == original &&
                    mainPane.IsRunning && FieldValue(mainPane, "_headlessSessionId")?.ToString() == mainSession &&
                    ReferenceEquals(RuntimeField(runtime, "SingleTerminal"), mainTab),
                    "Attach cleanup failure was hidden, discarded user data, or stopped the unrelated coding pane.");
                using var unaffected = new OpenCodeConversationClient((Uri)GetProperty(mainPane, "NativeOpenCodeEndpoint")!, projects[0]);
                Require((await unaffected.GetSessionAsync(mainSession)).GetProperty("testProcessId").GetInt32() == mainPid,
                    "A failed planner cleanup replaced or stopped the unrelated coding server.");
            }
            await VerifyLoopFullPlanMakerAsync(mainPane, projects[0]);
            Console.WriteLine("PASS PlanMaker modal: actual enabled-button clicks, selection/duplicate tooltips, empty/whitespace requests with existing unsaved or new plans, independent process/no coding slots, saved Korean guidance applied only to new interviews, same-session adaptive questions after early completion, fresh native request IDs/parents, third-answer enrichment without losing earlier requirements, bounded questionless pause/user continuation, explicit interview finish including busy-answer drain and Save, busy main process preserved, duplicate/Loop guards, newer input/draft conflict, cancellation/project switch/no follow-up after close, unsupported permissions, injected attach cleanup failure retains error and stops only the owned server; fake API only.");
        }
        finally
        {
            Call(window, "CancelAllPlanMakers");
            await UntilAsync(() => ((System.Collections.IDictionary)FieldValue(window, "_planMakers")!).Count == 0, "planner modal cleanup");
            Call(window, "DisposeResources"); window.Close();
            acquire.SetValue(null, previousAcquire);
            AppContext.SetData("TerminalNotes.DataDirectory", previousData);
        }
    }

    private static async Task VerifyLoopFullPlanMakerAsync(TerminalPane codingPane, string project)
    {
        // Never load this view: all requests use the in-process transport. Borrow
        // only the already running fake backend identity to satisfy pane readiness,
        // then detach it before disposing this clean planning fixture.
        var pane = new TerminalPane(project, startupCommand: "opencode", protectTuiViewport: true,
            paneKey: "loop-full-fixture-" + Guid.NewGuid().ToString("N"), useProjectContext: false);
        object? ownedHost = FieldValue(pane, "_headlessOpenCodeHost");
        object? previousClient = FieldValue(pane, "_headlessOpenCodeClient");
        object? previousStatus = FieldValue(pane, "_openCodeStatus");
        string previousSession = (string)FieldValue(codingPane, "_headlessSessionId")!;
        string previousDraft = (string)FieldValue(pane, "_nativeComposerDraft")!;
        string savedPlan = await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(project));
        object? previousHost = FieldValue(codingPane, "_headlessOpenCodeHost");
        Require(FieldValue(pane, "_usesNativeOpenCodeTui") is false,
            "Full planning tests require the existing fake headless backend, with no PTY or real model.");
        Type workflow = typeof(TerminalPane).Assembly.GetType("TerminalNotes.Services.PlanMakerWorkflow")!;
        string prompt = (string)workflow.GetMethod("BuildLoopFullPlanPrompt", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, ["Recommend a remaining verification for the confirmed project.", savedPlan, null, false])!;
        Require(prompt.Contains("(Recommended)", StringComparison.Ordinal) && prompt.Contains("reason", StringComparison.OrdinalIgnoreCase),
            "The very first Full request omitted the recommendation and its reason.");
        SetField(pane, "_headlessOpenCodeHost", previousHost);
        SetField(pane, "_headlessSessionId", previousSession);
        SetField(pane, "_ready", true);
        try
        {
            foreach (string mode in new[] { "direct", "completion", "no-change", "invalid-final", "missing-recommendation", "missing-reason", "ambiguous-recommendation",
                "ranking-changed", "ranking-permission", "ranking-error", "ranking-input", "ranking-cancel", "ranking-uncertain", "manual-repair", "manual-off",
                "second-question", "permission", "cancel", "input", "new-user", "uncertain-answer" })
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                using var transport = new LoopFullPlannerTransport(project, previousSession, mode);
                using var http = new HttpClient(transport);
                using var client = new OpenCodeConversationClient(new Uri("http://127.0.0.1:43131/"), project, http);
                SetField(pane, "_headlessOpenCodeClient", client);
                SetField(pane, "_nativeComposerDraft", "");
                if (mode == "cancel") transport.OnQuestionRead = cancellation.Cancel;
                if (mode == "input") transport.OnQuestionRead = () =>
                    SetField(pane, "_planMakerInputRevision", (long)FieldValue(pane, "_planMakerInputRevision")! + 1);
                if (mode == "ranking-cancel") transport.OnRanking = cancellation.Cancel;
                if (mode == "ranking-input") transport.OnRanking = () =>
                    SetField(pane, "_planMakerInputRevision", (long)FieldValue(pane, "_planMakerInputRevision")! + 1);
                bool manual = mode.StartsWith("manual-", StringComparison.Ordinal);
                object? interview = manual ? Activator.CreateInstance(typeof(TerminalPane).Assembly.GetType("TerminalNotes.Services.PlanMakerInterviewControl")!, true) : null;
                if (mode == "manual-repair") Call(interview!, "SetRecommendedAnswersEnabled", true);
                if (mode == "manual-off") transport.OnQuestionRead = () => cancellation.CancelAfter(TimeSpan.FromMilliseconds(650));
                Require(previousStatus is OpenCodeStatusMonitor monitor && monitor.Snapshot.Kind is OpenCodeStatusKind.Unknown &&
                    Call(pane, "HasLocalPendingInput") is false && FieldValue(pane, "_nativeAutomationActive") is false &&
                    FieldValue(pane, "_startupApiActive") is false && GetProperty(pane, "HasPendingStartupSubmission") is false,
                    "The isolated Full planner must retain its fresh Unknown status monitor and have no pending input or dispatch: " + mode);
                Task<string> planning = (Task<string>)Call(pane, "CreatePlanWithQuestionsAsync", prompt,
                    (Func<bool>)(() =>
                    {
                        Require(pane.Dispatcher.CheckAccess(), "Planning ownership escaped the pane dispatcher.");
                        return true;
                    }), (Action<string>)(_ =>
                    {
                        Require(pane.Dispatcher.CheckAccess(), "Planning ownership/progress escaped the pane dispatcher.");
                        if (mode == "manual-repair" && transport.Replies == 1 && GetProperty(interview!, "CanRequestFinish") is true)
                            Call(interview!, "RequestFinish");
                    }), cancellation.Token, null, interview, !manual)!;
                Exception? failure = null;
                string? delta = null;
                try { delta = await planning.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (Exception exception) when (exception is InvalidDataException or OperationCanceledException or IOException)
                { failure = exception; }
                bool success = mode is "direct" or "completion" or "no-change" or "missing-recommendation" or "missing-reason" or "ambiguous-recommendation" or "manual-repair";
                Require(success ? failure is null : failure is not null, "Full planning result violated the " + mode + " guard.");
                if (success)
                {
                    Require(delta == (mode == "no-change" ? "" : LoopFullPlannerTransport.Delta) && transport.Replies == 1 &&
                        transport.ExactAnswer == transport.ExpectedAnswer && !transport.HasQuestion,
                        "Full planning did not use one exact initial recommended answer and return only its final delta: " + mode);
                    Require(transport.Prompts == (mode == "completion" ? 2 : 1),
                        "Full planning sent an unbounded interview continuation: " + mode);
                }
                else
                {
                    Require(delta is null && transport.Replies == (mode is "invalid-final" or "second-question" or "uncertain-answer" or "ranking-uncertain" ? 1 : 0) &&
                        transport.Prompts == (mode == "invalid-final" ? 2 : 1),
                        "Full planning guessed, resent an answer, or continued after its " + mode + " guard.");
                }
                // A marked option without a written reason is followed directly; only unmarked or doubly marked questions are ranked.
                bool repaired = mode is "missing-recommendation" or "ambiguous-recommendation" or "manual-repair" || mode.StartsWith("ranking-", StringComparison.Ordinal);
                Require(transport.HelperPrompts == (repaired ? 1 : 0) && transport.HelperAborts == (repaired ? 1 : 0),
                    "Recommendation recovery retried, ran while manual/off, or leaked its isolated helper: " + mode);
                Require(transport.Aborts == 1 && FieldValue(pane, "_planMakerSessionId") is null &&
                    FieldValue(pane, "_headlessSessionId")?.ToString() == previousSession && pane.IsRunning &&
                    ReferenceEquals(previousHost, FieldValue(pane, "_headlessOpenCodeHost")) &&
                    ReferenceEquals(previousHost, FieldValue(codingPane, "_headlessOpenCodeHost")) && codingPane.IsRunning &&
                    FieldValue(codingPane, "_headlessSessionId")?.ToString() == previousSession &&
                    await File.ReadAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(project)) == savedPlan,
                    "Full planning did not release its exact request/session or changed the existing backend or saved plan: " + mode);
                SetField(pane, "_headlessOpenCodeClient", previousClient);
            }
            Console.WriteLine("PASS Loop Full planner: initial recommended answer/reason, exact model and question-only tools, final marked delta/no-change, one completion request, missing recommendation/reason, second question, permission, cancellation/input/new user, uncertain answer no resend, exact abort/session cleanup; in-process fake transport only.");
        }
        finally
        {
            SetField(pane, "_headlessOpenCodeClient", previousClient);
            SetField(pane, "_openCodeStatus", previousStatus);
            SetField(pane, "_headlessOpenCodeHost", ownedHost);
            SetField(pane, "_headlessSessionId", null);
            SetField(pane, "_nativeComposerDraft", previousDraft);
            SetField(pane, "_ready", false);
            pane.Dispose();
        }
    }

    private sealed class LoopFullPlannerTransport(string project, string baseSession, string mode) : HttpMessageHandler
    {
        internal const string Delta = "# Existing Plan\n\n- Verify the confirmed export preserves its existing filters.";
        internal const string Recommended = "Verify existing export filters (Recommended)";
        private const string SessionId = "ses_loop_full_fixture";
        private const string HelperId = "ses_loop_recommendation_fixture";
        private const string AliasProvider = "fixture-enterprise";
        private const string AliasModel = "deployment/planning-alias";
        private readonly List<object> _history = new();
        private string? _requestId;
        private string? _prompt;
        private string? _helperRequest;
        private bool _changedQuestion;
        internal int Prompts, Replies, Aborts;
        internal int HelperPrompts, HelperAborts;
        internal string? ExactAnswer;
        internal Action? OnQuestionRead;
        internal Action? OnRanking;
        private bool NeedsRepair => mode is "missing-recommendation" or "ambiguous-recommendation" or "manual-repair" or "manual-off" ||
            mode.StartsWith("ranking-", StringComparison.Ordinal);
        internal string ExpectedAnswer => NeedsRepair ? "Review existing documentation" + (mode == "ambiguous-recommendation" ? " (Recommended)" : "") : Recommended;
        internal bool HasQuestion => Prompts > 0 && (Replies == 0 || mode == "second-question") && Aborts == 0;
        private object Session(string id) => new
        {
            id, directory = project, title = "Fixture planning session",
            permission = new[] { new { permission = "*", pattern = "*", action = "deny" },
                new { permission = "question", pattern = "*", action = "allow" } }
        };
        private object HelperSession() => new { id = HelperId, directory = project,
            permission = new[] { new { permission = "*", pattern = "*", action = "deny" } } };
        private object Assistant() => new
        {
            info = new { id = "msg_full_assistant_" + Prompts + "_" + Replies, sessionID = SessionId, parentID = _requestId,
                role = "assistant", finish = HasQuestion ? "tool-calls" : "stop", time = new { created = 1, completed = 2 } },
            parts = new[] { new { type = "text", text = HasQuestion ? "A recommended planning decision is ready."
                : mode == "invalid-final" || mode == "completion" && Prompts == 1 ? "The recommended decision was accepted."
                : "<<<TERMINALNOTES_PLAN>>>\n" + (mode == "no-change" ? "" : Delta) + "\n<<<END_TERMINALNOTES_PLAN>>>" } }
        };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string path = request.RequestUri!.AbsolutePath;
            object payload;
            HttpStatusCode status = HttpStatusCode.OK;
            if (request.Method == HttpMethod.Post && path == "/session")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                JsonElement permissions = body.RootElement.GetProperty("permission");
                if (permissions.GetArrayLength() == 1)
                {
                    Require(permissions[0].GetProperty("permission").GetString() == "*" && permissions[0].GetProperty("action").GetString() == "deny",
                        "Recommendation helper granted tools.");
                    payload = HelperSession();
                }
                else
                {
                Require(permissions.GetArrayLength() == 2 && permissions[0].GetProperty("permission").GetString() == "*" &&
                    permissions[0].GetProperty("action").GetString() == "deny" && permissions[1].GetProperty("permission").GetString() == "question" &&
                    permissions[1].GetProperty("action").GetString() == "allow", "Full planning did not establish question-only durable permissions.");
                payload = Session(SessionId);
                }
            }
            else if (request.Method == HttpMethod.Post && path == "/session/" + HelperId + "/prompt_async")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                JsonElement native = body.RootElement;
                Require(native.GetProperty("model").GetProperty("providerID").GetString() == AliasProvider &&
                    native.GetProperty("model").GetProperty("modelID").GetString() == AliasModel && !native.TryGetProperty("tools", out _) &&
                    native.GetProperty("parts")[0].GetProperty("text").GetString()!.Contains("exact pending PlanMaker question", StringComparison.Ordinal),
                    "Recovery changed the configured model snapshot or omitted the exact ranking protocol.");
                _helperRequest = native.GetProperty("messageID").GetString();
                HelperPrompts++;
                if (mode == "ranking-changed") _changedQuestion = true;
                OnRanking?.Invoke();
                payload = true;
            }
            else if (request.Method == HttpMethod.Post && path == "/session/" + SessionId + "/prompt_async")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                JsonElement native = body.RootElement;
                Require(native.GetProperty("agent").GetString() == "plan" && !native.TryGetProperty("tools", out _) &&
                    native.GetProperty("model").GetProperty("providerID").GetString() == AliasProvider &&
                    native.GetProperty("model").GetProperty("modelID").GetString() == AliasModel,
                    "Full initial/final request lost the exact model snapshot or question-only planning restriction.");
                if (_requestId is not null) _history.Add(Assistant());
                _requestId = native.GetProperty("messageID").GetString();
                _prompt = native.GetProperty("parts")[0].GetProperty("text").GetString();
                Prompts++;
                Require(Prompts <= 2 && (Prompts == 1 || (mode is "completion" or "invalid-final") && Replies == 1),
                    "Full planning repeated its initial request or entered a follow-up interview loop.");
                payload = true; status = HttpStatusCode.NoContent;
            }
            else if (request.Method == HttpMethod.Post && path.StartsWith("/question/", StringComparison.Ordinal) && path.EndsWith("/reply", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Require(path == "/question/que_full_0/reply" && body.RootElement.GetProperty("answers").GetArrayLength() == 1 &&
                    body.RootElement.GetProperty("answers")[0].GetArrayLength() == 1, "Full answered an unowned or multiple-choice question.");
                ExactAnswer = body.RootElement.GetProperty("answers")[0][0].GetString();
                Replies++;
                if (mode is "uncertain-answer" or "ranking-uncertain") throw new HttpRequestException("Fixture lost the recommendation acknowledgment.");
                payload = true;
            }
            else if (request.Method == HttpMethod.Post && path == "/session/" + SessionId + "/abort")
            { Aborts++; payload = true; }
            else if (request.Method == HttpMethod.Post && path == "/session/" + HelperId + "/abort")
            { HelperAborts++; payload = true; }
            else if (request.Method != HttpMethod.Get) throw new InvalidOperationException("Unexpected Full mutation: " + path);
            else if (path == "/experimental/tool/ids") payload = new[] { "question", "bash", "write", "task" };
            else if (path == "/agent") payload = new[] { new { name = "plan" } };
            else if (path == "/config") payload = new { model = AliasProvider + "/" + (Prompts == 0 ? AliasModel : "changed-after-start") };
            else if (path == "/provider") payload = new { all = Array.Empty<object>(), connected = Array.Empty<string>() };
            else if (path == "/session/status") payload = new Dictionary<string, object>();
            else if (path == "/permission") payload = (mode == "permission" && Prompts > 0 || mode == "ranking-permission" && HelperPrompts > 0) && Aborts == 0
                ? new object[] { new { id = "per_full", sessionID = SessionId } } : Array.Empty<object>();
            else if (path.EndsWith("/children", StringComparison.Ordinal)) payload = Array.Empty<object>();
            else if (path == "/question")
            {
                if (Prompts > 0 && OnQuestionRead is { } onQuestionRead) { OnQuestionRead = null; onQuestionRead(); }
                payload = HasQuestion ? new object[] { new { id = "que_full_" + Replies, sessionID = SessionId,
                    questions = new[] { new { question = _changedQuestion ? "A newer exact question now owns this request." : "Which verification should follow the completed work?", multiple = false,
                        options = new[] { new { label = NeedsRepair && mode != "ambiguous-recommendation" ? "Verify filters" : Recommended,
                            description = mode == "missing-reason" ? "" : "The confirmed export is already implemented; verifying existing filters gives concrete evidence." },
                            new { label = mode == "ambiguous-recommendation" ? "Review existing documentation (Recommended)" : "Review existing documentation", description = "Review the confirmed behavior." } } } } } } : Array.Empty<object>();
            }
            else if (path == "/session/" + HelperId) payload = HelperSession();
            else if (path == "/session/" + HelperId + "/message") payload = new[] { new
            {
                info = new { id = "msg_helper_assistant", sessionID = HelperId, parentID = _helperRequest, role = "assistant", finish = "stop",
                    time = new { completed = 2 }, error = mode == "ranking-error" ? new { name = "APIError" } : null },
                parts = new[] { new { type = "text", text = JsonSerializer.Serialize(new { label = ExpectedAnswer, reason = "Reviewing confirmed behavior preserves scope without guessing unknown user facts." }) } }
            } };
            else if (path == "/session") payload = new[] { Session(baseSession), Session(SessionId) };
            else if (path == "/session/" + SessionId + "/message")
            {
                var messages = new List<object>(_history);
                if (_requestId is not null)
                {
                    messages.Add(new { info = new { id = _requestId, sessionID = SessionId, role = "user", time = new { created = 1 } },
                        parts = new[] { new { type = "text", text = _prompt } } });
                    messages.Add(Assistant());
                    if (mode == "new-user") messages.Add(new { info = new { id = "msg_full_new_user", sessionID = SessionId, role = "user" },
                        parts = new[] { new { type = "text", text = "A newer user request owns this session." } } });
                }
                payload = messages;
            }
            else if (path.EndsWith("/message", StringComparison.Ordinal)) payload = Array.Empty<object>();
            else if (path.StartsWith("/session/", StringComparison.Ordinal)) payload = Session(path.Split('/')[2]);
            else throw new InvalidOperationException("Unexpected Full read: " + path);
            return new HttpResponseMessage(status) { RequestMessage = request,
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
        }
    }
}
