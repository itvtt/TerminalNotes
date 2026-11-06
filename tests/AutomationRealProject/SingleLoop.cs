using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TerminalNotes;
using TerminalNotes.Services;

internal static partial class Program
{
    private static async Task RunSingleLoopAsync(bool crashAfterCompletion = false)
    {
        string runRoot = Path.Combine(FindRepositoryRoot(), "artifacts", "loop-verification-20261015",
            "single-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        string project = Path.Combine(runRoot, "project");
        Directory.CreateDirectory(Path.Combine(project, "terminal-notes"));
        const string plan = "Work only in this isolated verification project. Create loop-evidence.txt with a numbered UTC journal entry and a tiny useful local improvement. Verify the changed files by reading them back. On each later Loop review, make and verify one further small improvement and append a new journal entry; preserve all previous entries. Keep each turn short, use no network, install nothing, start no server, and ask for approval when required. Report the concrete change and verification before finishing each turn.";
        await File.WriteAllTextAsync(TerminalTabDocumentStore.GetProjectPlanPath(project), plan);
        string settings = Path.Combine(runRoot, "settings.json");
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new
        {
            Workspace = project, CodexWorkspace = project, SetupCompleted = true, WorkspaceDefaultsVersion = 1,
            AutoResumeEnabled = true, UseAutoMode = ReadGlobalUseAutoMode(), OpenCodeOnly = "Y"
        }));
        object? oldData = AppContext.GetData("TerminalNotes.DataDirectory");
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(runRoot, "app-data"));
        MainWindow? window = null;
        var completed = new Dictionary<string, object>(StringComparer.Ordinal);
        DateTime began = DateTime.UtcNow;
        string result = "failed";
        string? failure = null;
        string failureReasonCode = "startup";
        string? session = null;
        uint? ownedProcess = null;
        bool stopVerified = false;
        string? cleanupFailure = null;
        string priorJournal = "";
        int priorJournalEntries = 0;
        object? crashEvidence = null;
        string? recoveryPrompt = null;
        string? originalInstruction = null;
        bool crashInjected = false, replacementVerified = false, replayVerified = false;
        int recoverySubmissionCount = 0;
        int completedReviewsAfterRecovery = 0;
        uint? replacementProcess = null;
        string? crashedSession = null;
        try
        {
            Console.WriteLine("REAL_SINGLE_LOOP_READY project=" + project);
            Console.WriteLine("REAL_SINGLE_LOOP_POLICY inherited default provider/model; no fixture, model override, credential or config changes");
            window = new MainWindow(settings, autoStartCodex: true, initialWorkspace: project);
            window.ShowActivated = false;
            window.Show();
            Call(window, "RememberProjectName", project, "Single Loop Verification");
            Call(window, "EnsureProjectTaskRow", project);
            var rows = Field<IDictionary>(window, "_projectTaskRows");
            ((ToggleButton)GetProperty(rows[project]!, "Loop")!).IsChecked = true;
            Task startup = (Task)Call(window, "StartProjectTaskAsync", project)!;
            while (!startup.IsCompleted)
            {
                if (File.Exists(Path.Combine(runRoot, "STOP"))) throw new OperationCanceledException("Isolated verification Stop requested.");
                if (FieldValue(window, "_openCodeReady") is false && FieldValue(window, "_openCodeInstallationFailure") is string)
                    throw new InvalidOperationException("Installed OpenCode startup failed before native submission.");
                if (DateTime.UtcNow >= began.AddMinutes(5)) throw new TimeoutException("Isolated native startup exceeded five minutes.");
                await Task.Delay(250);
            }
            await startup;
            object runtime = Call(window, "RuntimeFor", project)!;
            object? RuntimeValue(string name) => runtime.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(runtime);
            var tab = RuntimeValue("SingleTerminal") as TabItem ?? throw new InvalidOperationException("No owned single-task terminal was created.");
            var pane = (TerminalPane)GetProperty(tab.Tag, "Pane")!;
            Uri endpoint = (Uri)GetProperty(pane, "NativeOpenCodeEndpoint")!;
            session = (string?)FieldValue(pane, "_headlessSessionId");
            if (session is null || endpoint is null) throw new InvalidOperationException("No native session was accepted.");
            ownedProcess = (uint?)GetProperty(pane, "SessionProcessId");
            using var verifier = new OpenCodeConversationClient(endpoint, project);
            await verifier.GetSessionAsync(session); // Exact root-session directory ownership.
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
            http.Timeout = TimeSpan.FromSeconds(15);
            DateTime firstCompletionDeadline = began.AddMinutes(5);
            DateTime overallDeadline = began.AddMinutes(15);
            int loopCompletions = 0;
            while (DateTime.UtcNow < overallDeadline)
            {
                if (File.Exists(Path.Combine(runRoot, "STOP"))) throw new OperationCanceledException("Isolated verification Stop requested.");
                if (completed.Count == 0 && DateTime.UtcNow >= firstCompletionDeadline)
                    throw new TimeoutException("No successful native model turn within the bounded five-minute attempt.");
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                if (crashInjected && !replacementVerified)
                {
                    failureReasonCode = "await_distinct_owned_replacement";
                    var nextTab = RuntimeValue("SingleTerminal") as TabItem;
                    var nextPane = nextTab?.Tag is { } nextTag ? GetProperty(nextTag, "Pane") as TerminalPane : null;
                    if (nextPane is null || ReferenceEquals(nextPane, pane) || !nextPane.IsRunning ||
                        RuntimeValue("SingleSubmitted") is not true || RuntimeValue("SingleStartupCancellation") is not null)
                    { await Task.Delay(1000); continue; }
                    Uri nextEndpoint = (Uri)GetProperty(nextPane, "NativeOpenCodeEndpoint")!;
                    string nextSession = (string)FieldValue(nextPane, "_headlessSessionId")!;
                    replacementProcess = (uint?)GetProperty(nextPane, "SessionProcessId");
                    if (replacementProcess is null || replacementProcess == ownedProcess || nextSession == crashedSession)
                        throw new InvalidDataException("Recovery did not create a distinct owned native host and session.");
                    using var nextVerifier = new OpenCodeConversationClient(nextEndpoint, project);
                    await nextVerifier.GetSessionAsync(nextSession, cancellation.Token);
                    var nextController = (OpenCodeAutoResumeController)FieldValue(nextPane, "_autoResume")!;
                    if ((string?)FieldValue(nextController, "_lastUserInstruction") != originalInstruction)
                        throw new InvalidDataException("Recovery changed the exact original instruction.");
                    pane = nextPane; endpoint = nextEndpoint; session = nextSession;
                    replacementVerified = true;
                    Console.WriteLine($"REAL_SINGLE_LOOP_REPLACEMENT oldHost={ownedProcess} newHost={replacementProcess} oldSession={crashedSession} newSession={session}");
                }
                using var recovery = new OpenCodeRecoveryClient(endpoint, project);
                failureReasonCode = "native_evidence_read";
                string? authorization = (string?)FieldValue(recovery, "_authorization");
                var read = await recovery.ReadAsync(session, cancellation.Token);
                if (read.Evidence is { HasSuccessfulFinalResponse: true, LatestUserMessageId: { } userId } evidence &&
                    !completed.ContainsKey(userId))
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint,
                        "session/" + Uri.EscapeDataString(session) + "/message?directory=" + Uri.EscapeDataString(project)));
                    if (!string.IsNullOrWhiteSpace(authorization)) request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);
                    using var response = await http.SendAsync(request, cancellation.Token);
                    response.EnsureSuccessStatusCode();
                    using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation.Token));
                    JsonElement[] messages = payload.RootElement.EnumerateArray().ToArray();
                    JsonElement user = messages.Single(message => message.GetProperty("info").GetProperty("id").GetString() == userId);
                    bool isLoop = user.GetProperty("parts").EnumerateArray().Any(part =>
                        part.TryGetProperty("text", out var text) && text.GetString()?.StartsWith("Loop 지속 개선:", StringComparison.Ordinal) == true);
                    bool isRecoveryReplay = false;
                    if (replacementVerified)
                    {
                        failureReasonCode = "exactly_one_preserved_recovery_submission";
                        bool IsReplay(JsonElement message) => message.GetProperty("info").GetProperty("role").GetString() == "user" &&
                            message.GetProperty("parts").EnumerateArray().Any(part =>
                                part.TryGetProperty("text", out var text) && text.GetString() == recoveryPrompt);
                        recoverySubmissionCount = messages.Count(IsReplay);
                        if (recoverySubmissionCount != 1)
                            throw new InvalidDataException("Replacement session did not contain exactly one preserved recovery submission.");
                        isRecoveryReplay = IsReplay(user);
                    }
                    JsonElement assistant = messages.Last(message =>
                        message.GetProperty("info").TryGetProperty("id", out var id) && id.GetString() == evidence.LatestAssistantMessageId).GetProperty("info");
                    failureReasonCode = "exact_successful_native_final_response";
                    if (assistant.GetProperty("role").GetString() != "assistant" ||
                        !assistant.TryGetProperty("parentID", out var parent) || parent.GetString() != userId ||
                        !assistant.TryGetProperty("finish", out var finish) || finish.GetString() != "stop" ||
                        !assistant.GetProperty("time").TryGetProperty("completed", out var finishedAt) || finishedAt.ValueKind != JsonValueKind.Number ||
                        assistant.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                        throw new InvalidDataException("Native completion does not have an exact successful final assistant response to the current user request.");
                    string? ModelField(string name) => assistant.TryGetProperty(name, out var value) ? value.GetString() : null;
                    string? model = ModelField("modelID"), provider = ModelField("providerID");
                    failureReasonCode = "actual_native_provider_model_required";
                    if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(provider))
                        throw new InvalidDataException("Successful native response did not identify its actual provider/model.");
                    string journalPath = Path.Combine(project, "loop-evidence.txt");
                    string journal = File.Exists(journalPath) ? await File.ReadAllTextAsync(journalPath) : "";
                    int journalEntries = journal.Split('\n').Count(line => !string.IsNullOrWhiteSpace(line));
                    failureReasonCode = "journal_append_and_preservation_required";
                    if (journalEntries <= priorJournalEntries || !journal.StartsWith(priorJournal, StringComparison.Ordinal))
                        throw new InvalidDataException("A completed native turn did not append persistent work evidence while preserving its previous journal.");
                    string journalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(journal)));
                    completed.Add(userId, new { UserMessageId = userId, AssistantMessageId = evidence.LatestAssistantMessageId,
                        Provider = provider, Model = model, IsLoopReview = isLoop, CompletedAtUtc = DateTime.UtcNow,
                        JournalEntries = journalEntries, JournalSha256 = journalHash });
                    priorJournal = journal;
                    priorJournalEntries = journalEntries;
                    if (isRecoveryReplay) replayVerified = true;
                    if (replacementVerified && isLoop && replayVerified) completedReviewsAfterRecovery++;
                    if (isLoop) loopCompletions++;
                    Console.WriteLine($"REAL_SINGLE_LOOP_COMPLETED turn={completed.Count} automaticReviews={loopCompletions} provider={provider} model={model} user={userId} assistant={evidence.LatestAssistantMessageId}");
                    if ((!crashAfterCompletion && loopCompletions >= 2) ||
                        (crashAfterCompletion && replacementVerified && replayVerified && completedReviewsAfterRecovery >= 1))
                    {
                        if (journalEntries < 3)
                            throw new InvalidDataException("Required completed native turns lack the requested persistent three-entry work journal.");
                        result = "passed";
                        break;
                    }
                }
                if (crashAfterCompletion && !crashInjected && completed.Count > 0)
                {
                    failureReasonCode = "genuine_completed_review_snapshot_required";
                    var controller = (OpenCodeAutoResumeController)FieldValue(pane, "_autoResume")!;
                    OpenCodeErrorRestart? snapshot;
                    Process? host = null;
                    object? verifiedOwner = null;
                    lock (FieldValue(controller, "_gate")!)
                    {
                        snapshot = controller.GetConfirmedRecoverySnapshot();
                        if (snapshot is { RequiresContinuousImprovement: true, AutomaticRetryEnabled: true } &&
                            snapshot.SessionId == session && snapshot.CanRestart() && completed.ContainsKey(snapshot.UserMessageId) &&
                            read.Evidence is { Status: OpenCodeRecoveryStatus.Idle, HasRunningTools: false, HasBusyDescendants: false,
                                HasPendingPermission: false, HasPendingQuestion: false, HasPendingPermissionInDescendants: false,
                                HasPendingQuestionInDescendants: false, HasSuccessfulFinalResponse: true } latest && latest.LatestUserMessageId == snapshot.UserMessageId &&
                            FieldValue(controller, "_nextCompletedReviewAt") is DateTimeOffset reviewAt && reviewAt > DateTimeOffset.UtcNow.AddSeconds(2) &&
                            FieldValue(controller, "_pendingMessageId") is null &&
                            FieldValue(controller, "_abortActionInFlight") is false &&
                            !((ISet<string>)FieldValue(window, "_pendingProjectInputs")!).Contains(project) &&
                            FieldValue(pane, "_manualInputOverride") is false && Call(pane, "HasLocalPendingInput") is false &&
                            GetProperty(pane, "IsUserInputBlocked") is false)
                        {
                            host = VerifyOwnedSingleLoopHost(pane, project, began, out verifiedOwner);
                            recoveryPrompt = snapshot.Prompt;
                            originalInstruction = snapshot.OriginalInstruction;
                            crashedSession = session;
                            failureReasonCode = "verified_owned_host_unexpected_exit";
                            host.Kill(entireProcessTree: true); // Unexpected death: do not invoke production Stop/invalidation APIs.
                            crashInjected = true;
                        }
                    }
                    if (host is not null)
                    {
                        using (host)
                        {
                            await host.WaitForExitAsync(cancellation.Token);
                            crashEvidence = new { Host = verifiedOwner, SessionId = crashedSession,
                                CompletedUserMessageId = snapshot!.UserMessageId, RequiresContinuousImprovement = snapshot.RequiresContinuousImprovement,
                                ExitConfirmed = host.HasExited, ExitCode = host.ExitCode, ExitedAtUtc = DateTime.UtcNow,
                                OriginalInstructionSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(originalInstruction!))) };
                        }
                        Console.WriteLine($"REAL_SINGLE_LOOP_CRASH host={ownedProcess} session={crashedSession} completedUser={snapshot!.UserMessageId} exitConfirmed=true");
                    }
                }
                await Task.Delay(2000);
            }
            if (result != "passed")
            {
                failureReasonCode = crashAfterCompletion && !crashInjected ? "completed_review_snapshot_timeout" : "completed_review_turns_timeout";
                throw new TimeoutException("Required completed automatic Loop review turns were not verified within fifteen minutes.");
            }
        }
        catch (Exception exception)
        {
            failure = exception.GetType().Name;
            Console.WriteLine("REAL_SINGLE_LOOP_UNVERIFIED reason=" + failure + " reasonCode=" + failureReasonCode + " completedTurns=" + completed.Count);
            throw new InvalidOperationException("Real single Loop verification did not complete; inspect the isolated sanitized result and application diagnostics.");
        }
        finally
        {
            try
            {
                if (window is not null)
                {
                    try
                    {
                        await ((Task)Call(window, "StopProjectTaskAsync", project)!).WaitAsync(TimeSpan.FromSeconds(30));
                        var runtime = Call(window, "RuntimeFor", project)!;
                        stopVerified = runtime.GetType().GetField("SingleStopped")!.GetValue(runtime) is true;
                    }
                    finally
                    {
                        Call(window, "CloseAllTerminals", false);
                        await ((Task)Call(window, "RequestExitAsync")!).WaitAsync(TimeSpan.FromSeconds(30));
                    }
                }
            }
            catch (Exception exception)
            {
                cleanupFailure = exception.GetType().Name;
            }
            finally
            {
                AppContext.SetData("TerminalNotes.DataDirectory", oldData);
                if (!stopVerified || cleanupFailure is not null) result = "failed";
                await File.WriteAllTextAsync(Path.Combine(runRoot, "result.json"), JsonSerializer.Serialize(new
                {
                    Result = result, FailureType = failure, FailureReasonCode = failure is null ? null : failureReasonCode, CleanupFailureType = cleanupFailure,
                    StartedAtUtc = began, FinishedAtUtc = DateTime.UtcNow,
                    Project = project, NativeSessionId = session, OwnedHostProcessId = ownedProcess, StopVerified = stopVerified,
                    CrashMode = crashAfterCompletion, Crash = crashEvidence, ReplacementHostProcessId = replacementProcess,
                    ReplacementVerified = replacementVerified, RecoverySubmissionCount = recoverySubmissionCount,
                    OriginalInstructionPreserved = replacementVerified, ReplayCompleted = replayVerified,
                    CompletedAutomaticReviewsAfterRecovery = completedReviewsAfterRecovery,
                    CompletedTurns = completed.Values, ProviderModelPolicy = "Inherited installed default; no overrides",
                    Limit = "Bounded interactive verification, not a real overnight endurance test"
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("REAL_SINGLE_LOOP_RESULT " + Path.Combine(runRoot, "result.json"));
            }
        }
        if (result != "passed") throw new InvalidOperationException("Real single Loop cleanup did not verify Stop.");
    }

    private static Process VerifyOwnedSingleLoopHost(TerminalPane pane, string project, DateTime began, out object evidence)
    {
        uint hostId = (uint)(GetProperty(pane, "OpenCodeServerProcessId") ?? throw new InvalidDataException("No owned native server host PID."));
        if (!pane.IsRunning || (uint?)GetProperty(pane, "SessionProcessId") != hostId ||
            !string.Equals(Path.GetFullPath(pane.WorkingDirectory), Path.GetFullPath(project), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Native pane no longer owns the isolated project host.");
        object transport = FieldValue(pane, "_headlessOpenCodeHost")!;
        if ((uint?)GetProperty(transport, "ProcessId") != hostId || GetProperty(transport, "IsRunning") is not true)
            throw new InvalidDataException("Native host transport does not confirm the selected PID.");
        Type sourceType = typeof(MainWindow).Assembly.GetType("TerminalNotes.Services.WindowsResourceDiagnosticsSource")!;
        object source = Activator.CreateInstance(sourceType, BindingFlags.Instance | BindingFlags.NonPublic, null, [null], null)!;
        object graph = sourceType.GetMethod("ReadProcessGraph")!.Invoke(source, [CancellationToken.None])!;
        if (GetProperty(graph, "IsPartial") is true) throw new InvalidDataException("Cannot prove native host ancestry from an incomplete process snapshot.");
        var parents = ((IEnumerable)GetProperty(graph, "Edges")!).Cast<object>().ToDictionary(
            edge => (int)GetProperty(edge, "ProcessId")!, edge => (int)GetProperty(edge, "ParentProcessId")!);
        int ancestor = checked((int)hostId);
        var ancestry = new List<int> { ancestor };
        while (ancestor != Environment.ProcessId && ancestry.Count <= 32 && parents.TryGetValue(ancestor, out int parent))
        { ancestor = parent; ancestry.Add(ancestor); }
        if (ancestor != Environment.ProcessId) throw new InvalidDataException("Native host is not a descendant of this isolated test process.");
        Process process = Process.GetProcessById(checked((int)hostId));
        if (process.HasExited || process.StartTime.ToUniversalTime() < began.AddSeconds(-1))
        { process.Dispose(); throw new InvalidDataException("Owned native host identity is stale."); }
        evidence = new { ProcessId = hostId, TestProcessId = Environment.ProcessId, Ancestry = ancestry,
            Executable = process.MainModule!.FileName, StartedAtUtc = process.StartTime.ToUniversalTime(), Project = project };
        return process;
    }
}
