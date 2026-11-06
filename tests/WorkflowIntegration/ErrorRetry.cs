using System.Collections;
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
    private static async Task VerifyErrorRetryAsync(string fixture)
    {
        string resultPath = Path.Combine(AppContext.BaseDirectory, "error-retry-result.txt");
        File.WriteAllText(resultPath, "RUNNING " + DateTimeOffset.UtcNow.ToString("O"));
        string workspace = Path.Combine(fixture, "error retry workspace");
        string project = Path.Combine(workspace, "crashing fake project");
        string other = Path.Combine(workspace, "other selected project");
        foreach (string directory in new[] { project, other })
        {
            Directory.CreateDirectory(Path.Combine(directory, "terminal-notes"));
            await File.WriteAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(directory), "# Isolated error recovery\nPreserve existing work.");
            await File.WriteAllTextAsync(Path.Combine(directory, ".fixture-busy-composer"), "isolated fake only");
        }
        string settings = Path.Combine(fixture, "error retry settings.json");
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new
        { Workspace = workspace, CodexWorkspace = workspace, SetupCompleted = true, WorkspaceDefaultsVersion = 1, AutoResumeEnabled = false }));
        Type admissionType = typeof(TerminalPane).Assembly.GetType("TerminalNotes.Services.TerminalStartupAdmission")!;
        FieldInfo acquire = admissionType.GetField("AcquireOverride", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? previousAcquire = acquire.GetValue(null);
        acquire.SetValue(null, (Func<CancellationToken, Task<IDisposable>>)(_ => Task.FromResult<IDisposable>(new ResourceTestLease())));
        FieldInfo delayField = typeof(MainWindow).GetField("ProcessErrorRetryDelayOverride", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? previousDelay = delayField.GetValue(null);
        var retryAttempts = new List<int>();
        delayField.SetValue(null, (Func<int, TimeSpan>)(attempt => { retryAttempts.Add(attempt); return TimeSpan.FromSeconds(30); }));
        object? previousData = AppContext.GetData("TerminalNotes.DataDirectory");
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(fixture, "error retry app data"));
        string trace = Environment.GetEnvironmentVariable("TN_WORKFLOW_TRACE")!;
        List<JsonElement> Requests() => Directory.GetFiles(trace, "startup-prompt-*.json")
            .OrderBy(File.GetCreationTimeUtc)
            .Select(path => JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path)))
            .Where(item => item.GetProperty("Directory").GetString() == project).ToList();
        var window = new MainWindow(settings, autoStartCodex: true, startupHooks: FakeStartupHooks);
        try
        {
            window.Show();
            await UntilAsync(() => FieldValue(window, "_openCodeReady") is true, "fake-only error retry readiness");
            async Task Select(string directory)
            {
                Call(window, "EnsureProjectTaskRow", directory);
                Call(window, "SelectTaskProject", directory, true);
                await UntilAsync(() => FieldValue(window, "_projectInputProject")?.ToString() == directory &&
                    Field<TextBox>(window, "_tabDocumentEditor").IsEnabled, "retry project selection");
            }
            await Select(project);
            var input = Field<TextBox>(window, "_projectInputEditor");
            const string instruction = "Preserve original first line.\nSecond line has a quoted \"request\".\nThird line stays intact.";
            input.Text = instruction;
            await ((Task)Call(window, "SaveTabDocumentAsync", true)!).WaitAsync(TimeSpan.FromSeconds(30));
            object runtime = Runtime(window, project);
            TerminalPane Pane() => (TerminalPane)GetProperty(((TabItem)RuntimeField(runtime, "SingleTerminal")!).Tag, "Pane")!;
            async Task CrashOwnedFakeAsync(bool awaitEvidence = true)
            {
                var pane = Pane();
                if (awaitEvidence)
                {
                    var recovery = FieldValue(pane, "_autoResume")!;
                    try { await UntilAsync(() => ((OpenCodeAutoResumeController)recovery).GetConfirmedRecoverySnapshot() is not null, "exact confirmed fake recovery snapshot", 10); }
                    catch
                    {
                        Console.Error.WriteLine($"Retry snapshot debug: native={GetProperty(pane, "NativeOpenCodeStatus")}, running={pane.IsRunning}, busy={FieldValue(pane, "_automationBusy")}, status={GetProperty(recovery, "Status")}, session={FieldValue(recovery, "_sessionId")}, candidate={FieldValue(recovery, "_candidate")}, receipt={FieldValue(recovery, "_confirmedApiReceipt")}, instruction={FieldValue(recovery, "_lastUserInstruction")}, evidence={FieldValue(recovery, "_lastVerifiedEvidence")}");
                        throw;
                    }
                }
                string session = (string)FieldValue(pane, "_headlessSessionId")!;
                using var client = new OpenCodeConversationClient((Uri)GetProperty(pane, "NativeOpenCodeEndpoint")!, project);
                int pid = (await client.GetSessionAsync(session)).GetProperty("testProcessId").GetInt32();
                using var fake = Process.GetProcessById(pid);
                // The owned host is opencode.cmd; its fake HTTP child reports its own PID.
                Require(GetProperty(pane, "SessionProcessId") is uint &&
                    string.Equals(fake.MainModule!.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase),
                    "Only this isolated fixture executable's verified fake HTTP child may be killed.");
                fake.Kill(entireProcessTree: true);
                await fake.WaitForExitAsync();
                await UntilAsync(() => !pane.IsRunning && ((IDictionary)FieldValue(window, "_processErrorRecoveries")!).Contains(project), "owned fake exit retains retry snapshot");
            }
            // Pre-action contention must defer without stopping the exact owned host.
            var liveTab = (TabItem)RuntimeField(runtime, "SingleTerminal")!;
            string stalledPlanPath = TerminalTabDocumentStore.GetProjectPlanPath(project);
            string stalledPlanHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(await File.ReadAllTextAsync(stalledPlanPath))));
            var deferred = new OpenCodeStalledRestart("fixture-session", "fixture-message",
                stalledPlanPath, stalledPlanHash, instruction,
                () => true, () => Task.FromResult(true));
            FieldInfo saving = typeof(MainWindow).GetField("_tabDocumentSaving", BindingFlags.Instance | BindingFlags.NonPublic)!;
            saving.SetValue(window, true);
            try
            {
                Require(await (Task<OpenCodeRestartOutcome>)Call(window, "RestartStalledSingleProjectAsync", liveTab, deferred)! ==
                    OpenCodeRestartOutcome.RetryLater && Requests().Count == 1 && Pane().IsRunning,
                    "A transient plan save paused or stopped the live owned task.");
            }
            finally { saving.SetValue(window, false); }
            using (var lockedStalledPlan = new FileStream(TerminalTabDocumentStore.GetProjectPlanPath(project),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                // The real guard returns false when its synchronous plan read meets
                // a sharing violation. The async pre-action read must classify that
                // temporary failure before consulting this otherwise opaque guard.
                var lockedRequest = deferred with { CanRestart = () => false };
                Require(await (Task<OpenCodeRestartOutcome>)Call(window, "RestartStalledSingleProjectAsync", liveTab, lockedRequest)! ==
                    OpenCodeRestartOutcome.RetryLater && Requests().Count == 1 && Pane().IsRunning &&
                    !((HashSet<string>)FieldValue(window, "_projectTaskControlWrites")!).Contains(project),
                    "A locked saved plan paused the request or leaked the control gate before any recovery action.");
            }
            Require(await (Task<OpenCodeRestartOutcome>)Call(window, "RestartStalledSingleProjectAsync", liveTab,
                deferred with { CanRestart = () => false })! == OpenCodeRestartOutcome.Paused && Pane().IsRunning,
                "An invalid post-read ownership guard was bypassed after plan contention cleared.");
            Require(Field<bool>(window, "_autoResumeEnabled"), "The legacy disabled setting must migrate to built-in automatic progress.");
            await CrashOwnedFakeAsync(awaitEvidence: false);
            Call(window, "UpdateProjectTaskActivity");
            var buttons = (IDictionary)FieldValue(window, "_projectRetryButtons")!;
            var retryButtons = ((IEnumerable)buttons[project]!).Cast<WeakReference<Button>>()
                .Select(reference => reference.TryGetTarget(out var button) ? button : null).OfType<Button>().ToArray();
            Require(retryButtons.Length >= 2 && retryButtons.All(button => button.Visibility == Visibility.Visible && button.IsEnabled),
                "Manual retry must be visible on both card and pane header during the automatic countdown with Loop off.");
            Require(GetProperty(FieldValue(Pane(), "_errorRetryOverlay")!, "Visibility") is Visibility.Visible && Requests().Count == 1,
                "Error explanation must fill the waiting pane before the automatic countdown expires.");
            Call(window, "ShowProjectTask", project);
            window.UpdateLayout();
            await UntilAsync(() => ((FrameworkElement)FieldValue(Pane(), "_errorRetryOverlay")!).IsVisible,
                "error wait explanation visible in placed task pane");
            CaptureVisual(window, Path.Combine(AppContext.BaseDirectory, "error-retry-wait.png"));
            using (var lockedPlan = new FileStream(TerminalTabDocumentStore.GetProjectPlanPath(project), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await ((Task)Call(window, "RetryProjectErrorAsync", project)!).WaitAsync(TimeSpan.FromSeconds(30));
                Require(((IDictionary)FieldValue(window, "_processErrorRecoveries")!).Contains(project) && Requests().Count == 1 &&
                    retryButtons.All(button => button.Visibility == Visibility.Visible && button.IsEnabled),
                    "A transient plan read sharing violation discarded the safe original request/manual retry.");
            }
            await Select(other); input.Text = "Keep another project's unsent draft.";
            await ((Task)Call(window, "RetryProjectErrorAsync", project)!).WaitAsync(TimeSpan.FromSeconds(30));
            Require(Requests().Count == 2 && FieldValue(window, "_selectedProjectPath")?.ToString() == other &&
                input.Text == "Keep another project's unsent draft.", "Background retry stole selection/draft or did not replay once.");
            Require(FieldValue(Pane(), "_errorRetryOverlay") is null ||
                GetProperty(FieldValue(Pane(), "_errorRetryOverlay")!, "Visibility") is Visibility.Collapsed,
                "A successful recovery kept the full-pane error countdown over running work.");
            Require(Requests().All(request => request.GetProperty("Prompt").GetString()!.Contains(instruction, StringComparison.Ordinal)),
                "Recovery lost the original multiline request.");
            retryAttempts.Clear();
            delayField.SetValue(null, (Func<int, TimeSpan>)(attempt => { retryAttempts.Add(attempt); return TimeSpan.FromSeconds(2); }));
            Call(window, "ApplyAutoResumeSetting", true);
            await CrashOwnedFakeAsync();
            await UntilAsync(() => Requests().Count == 3 && RuntimeField(runtime, "SingleStartupCancellation") is null, "first automatic owned-process retry");
            await CrashOwnedFakeAsync();
            await UntilAsync(() => Requests().Count == 4 && RuntimeField(runtime, "SingleStartupCancellation") is null, "repeated automatic owned-process retry");
            var prompts = Requests().Select(request => request.GetProperty("Prompt").GetString()!).ToArray();
            Require(prompts.Skip(1).Distinct(StringComparer.Ordinal).Count() == 1 && retryAttempts.Count == 2 && retryAttempts[0] >= 1 && retryAttempts[1] == retryAttempts[0] + 1,
                $"Repeated recovery accumulated instructions or reset its backoff attempt (distinct={prompts.Skip(1).Distinct(StringComparer.Ordinal).Count()}, attempts={string.Join(',', retryAttempts)}).");

            // A known provider deadline survives host death and cannot leak the control gate on manual retry.
            delayField.SetValue(null, (Func<int, TimeSpan>)(_ => TimeSpan.FromSeconds(30)));
            await CrashOwnedFakeAsync();
            var pendingMap = (IDictionary)FieldValue(window, "_processErrorRecoveries")!;
            object pendingDeadline = pendingMap[project]!;
            var requestField = pendingDeadline.GetType().GetField("Request", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var deadlineRequest = (OpenCodeErrorRestart)requestField.GetValue(pendingDeadline)!;
            // Completed work requires a currently enabled, unexpired Loop even when
            // global automatic progress remains enabled.
            typeof(OpenCodeErrorRestart).GetProperty("RequiresContinuousImprovement")!.SetValue(deadlineRequest, true);
            var loopModes = (Dictionary<string, bool>)FieldValue(window, "_projectTaskContinuousModes")!;
            var loopDeadlines = (Dictionary<string, DateTimeOffset?>)FieldValue(window, "_projectTaskImprovementUntil")!;
            Require(Call(window, "AutomaticProcessErrorRetryEnabled", project, pendingDeadline) is false,
                "A completed-work snapshot bypassed disabled Loop through global automatic progress.");
            loopModes[project] = true;
            Require(Call(window, "AutomaticProcessErrorRetryEnabled", project, pendingDeadline) is true,
                "An enabled Loop could not recover its completed-work review.");
            loopDeadlines[project] = DateTimeOffset.Now.AddSeconds(-1);
            Require(Call(window, "AutomaticProcessErrorRetryEnabled", project, pendingDeadline) is false,
                "A completed-work snapshot bypassed an expired Loop deadline.");
            loopModes.Remove(project); loopDeadlines.Remove(project);
            typeof(OpenCodeErrorRestart).GetProperty("RequiresContinuousImprovement")!.SetValue(deadlineRequest, false);
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(2);
            typeof(OpenCodeErrorRestart).GetProperty("NotBeforeUtc")!.SetValue(deadlineRequest, deadline);
            await (Task)Call(window, "RetryProjectErrorAsync", project)!;
            Require(!((HashSet<string>)FieldValue(window, "_projectTaskControlWrites")!).Contains(project) && Requests().Count == 4 &&
                (DateTimeOffset)pendingDeadline.GetType().GetField("RetryAt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pendingDeadline)! >= deadline,
                "Manual retry bypassed the provider deadline or leaked the project control reservation.");
            await UntilAsync(() => Requests().Count == 5 && RuntimeField(runtime, "SingleStartupCancellation") is null,
                "provider deadline releases owned process recovery");

            // Fail replacement admission repeatedly before dispatch, then restore resources.
            int admissionAttempts = 0;
            acquire.SetValue(null, (Func<CancellationToken, Task<IDisposable>>)(_ =>
                ++admissionAttempts <= 2 ? Task.FromException<IDisposable>(new IOException("isolated temporary admission failure")) :
                Task.FromResult<IDisposable>(new ResourceTestLease())));
            delayField.SetValue(null, (Func<int, TimeSpan>)(_ => TimeSpan.FromMilliseconds(250)));
            await CrashOwnedFakeAsync();
            await UntilAsync(() => Requests().Count == 6 && RuntimeField(runtime, "SingleStartupCancellation") is null,
                "repeated proven-unsent replacement failures recover once", 30);
            Require(admissionAttempts >= 3 && Requests().Skip(1).Select(request => request.GetProperty("Prompt").GetString()).Distinct().Count() == 1,
                "Repeated startup failure stopped recovery, duplicated delivery, or changed the original request.");

            // Hold only the replacement's admission. The failed host is cleaned up, but no new request may bypass resources.
            var ticket = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
            acquire.SetValue(null, (Func<CancellationToken, Task<IDisposable>>)(token => ticket.Task.WaitAsync(token)));
            await CrashOwnedFakeAsync();
            Call(window, "ApplyAutoResumeSetting", false);
            Require(Field<bool>(window, "_autoResumeEnabled"), "A legacy opt-out must not disable automatic recovery.");
            await UntilAsync(() => GetProperty(Pane(), "IsWaitingForResources") is true, "automatic error replacement uses shared resource admission");
            var manualResourceRetry = (Task)Call(window, "RetryProjectErrorAsync", project)!;
            Require(Requests().Count == 6, "Error recovery posted before resource admission.");
            await (Task)Call(window, "StopProjectTaskAsync", project)!;
            ticket.TrySetResult(new ResourceTestLease());
            await manualResourceRetry.WaitAsync(TimeSpan.FromSeconds(10));
            await UntilAsync(() => RuntimeField(runtime, "SingleStartupCancellation") is null, "Stop cancels queued replacement startup");
            await Task.Delay(1500);
            Require(Requests().Count == 6 && RuntimeField(runtime, "SingleStopped") is true,
                "Stopped resource-wait recovery relaunched or resent the old request.");
            // Explicit Stop during the countdown invalidates the retained snapshot before any new launch.
            acquire.SetValue(null, (Func<CancellationToken, Task<IDisposable>>)(_ => Task.FromResult<IDisposable>(new ResourceTestLease())));
            await (Task)Call(window, "StartProjectTaskAsync", project)!;
            await UntilAsync(() => Requests().Count == 7 && RuntimeField(runtime, "SingleStartupCancellation") is null, "restart for countdown stop guard");
            delayField.SetValue(null, (Func<int, TimeSpan>)(_ => TimeSpan.FromSeconds(2)));
            await CrashOwnedFakeAsync();
            await (Task)Call(window, "StopProjectTaskAsync", project)!;
            await Task.Delay(2500);
            Require(Requests().Count == 7 && !((IDictionary)FieldValue(window, "_processErrorRecoveries")!).Contains(project),
                "Stop during a retry countdown resurrected the old request.");
            // This fake deliberately does not persist sessions between processes.
            // A retained compaction transaction must therefore fail closed on the
            // replacement, without creating a fallback session or replaying a prompt.
            delayField.SetValue(null, (Func<int, TimeSpan>)(_ => TimeSpan.FromHours(1)));
            await (Task)Call(window, "StartProjectTaskAsync", project)!;
            await UntilAsync(() => Requests().Count == 8 && RuntimeField(runtime, "SingleStartupCancellation") is null,
                "start isolated retained-context failure case");
            await CrashOwnedFakeAsync();
            object retainedContext = ((IDictionary)FieldValue(window, "_processErrorRecoveries")!)[project]!;
            var retainedRequest = (OpenCodeErrorRestart)retainedContext.GetType()
                .GetField("Request", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(retainedContext)!;
            typeof(OpenCodeErrorRestart).GetProperty("ReconcileOnly")!.SetValue(retainedRequest, true);
            int sessionAttempts = Directory.GetFiles(trace, "startup-session-attempt-*.json").Length;
            await ((Task)Call(window, "RetryProjectErrorAsync", project)!).WaitAsync(TimeSpan.FromSeconds(30));
            Require(Requests().Count == 8 && Directory.GetFiles(trace, "startup-session-attempt-*.json").Length == sessionAttempts &&
                ((IDictionary)FieldValue(window, "_processErrorRecoveries")!).Contains(project) &&
                RuntimeField(runtime, "SingleError") is string,
                "Missing retained compaction session created a new session, replayed the original prompt, or lost its safe retry snapshot.");
            await (Task)Call(window, "StopProjectTaskAsync", project)!;
            // The stall watchdog must preserve the same retained-context path after
            // awaiting its live owned host's death, without ordinary prompt replay.
            await (Task)Call(window, "StartProjectTaskAsync", project)!;
            await UntilAsync(() => Requests().Count == 9 && RuntimeField(runtime, "SingleStartupCancellation") is null,
                "start isolated stalled retained-context failure case");
            var stalledPane = Pane();
            var stalledController = (OpenCodeAutoResumeController)FieldValue(stalledPane, "_autoResume")!;
            await UntilAsync(() => stalledController.GetConfirmedRecoverySnapshot() is not null,
                "exact confirmed stalled-context snapshot");
            var stalledSnapshot = stalledController.GetConfirmedRecoverySnapshot()! with { ReconcileOnly = true };
            var stalledRecovery = new OpenCodeStalledRestart(stalledSnapshot.SessionId, stalledSnapshot.UserMessageId,
                stalledSnapshot.PlanPath, stalledSnapshot.PlanHash, stalledSnapshot.Prompt,
                () => true, () => Task.FromResult(true))
            { OriginalInstruction = stalledSnapshot.OriginalInstruction, ReconcileRecovery = stalledSnapshot };
            sessionAttempts = Directory.GetFiles(trace, "startup-session-attempt-*.json").Length;
            Require(await ((Task<OpenCodeRestartOutcome>)Call(window, "RestartStalledSingleProjectAsync",
                    (TabItem)RuntimeField(runtime, "SingleTerminal")!, stalledRecovery)!).WaitAsync(TimeSpan.FromSeconds(30)) ==
                    OpenCodeRestartOutcome.Paused && !stalledPane.IsRunning && Requests().Count == 9 &&
                Directory.GetFiles(trace, "startup-session-attempt-*.json").Length == sessionAttempts &&
                RuntimeField(runtime, "SingleError") is string,
                "Stalled retained compaction created a fallback session, replayed the original prompt, or skipped owned-host death.");
            object stalledPending = ((IDictionary)FieldValue(window, "_processErrorRecoveries")!)[project]!;
            Require(stalledPending is not null && ReferenceEquals(stalledSnapshot, stalledPending.GetType()
                    .GetField("Request", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(stalledPending)),
                "Stalled context recovery replaced the retained transaction with an ordinary restart snapshot.");
            await (Task)Call(window, "StopProjectTaskAsync", project)!;
            File.WriteAllText(resultPath, "PASS error retry including retained-context and stalled-context fail-closed: prompts=9; owned host stopped; no fallback session or replay; " + DateTimeOffset.UtcNow.ToString("O"));
            Console.WriteLine("PASS error retry: visible card/header manual actions during automatic countdown, full-pane reason, confirmed owned fake crashes, unchanged multiline request, repeated backoff, background selection/draft preservation, resource admission and Stop cancellation; fake API only.");
        }
        catch (Exception exception)
        {
            _failure = 1;
            // Closing the last application window can end WPF before the outer
            // harness prints its failure. Retain the actual assertion first.
            File.WriteAllText(Path.Combine(fixture, "error-retry-failure.txt"), exception.ToString());
            File.WriteAllText(resultPath, "FAIL " + exception);
            throw;
        }
        finally
        {
            Call(window, "DisposeResources"); window.Close();
            acquire.SetValue(null, previousAcquire); delayField.SetValue(null, previousDelay);
            AppContext.SetData("TerminalNotes.DataDirectory", previousData);
        }
    }
}
