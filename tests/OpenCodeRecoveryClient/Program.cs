using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TerminalNotes.Services;

internal static class Program
{
    private static readonly Uri ServerUri = new("http://127.0.0.1:43811/");
    private static readonly string ProjectDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "TerminalNotes recovery project"));
    private const string RootSessionId = "ses_root_123";

    private static async Task<int> Main()
    {
        string? oldPassword = Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD");
        string? oldUsername = Environment.GetEnvironmentVariable("OPENCODE_SERVER_USERNAME");
        try
        {
            Environment.SetEnvironmentVariable("OPENCODE_SERVER_PASSWORD", "test-only-password");
            Environment.SetEnvironmentVariable("OPENCODE_SERVER_USERNAME", "recovery-test");
            VerifyMessageIdsMatchOpenCodeAscendingShape();
            VerifyServerNamedWaits();
            await VerifyTaskCompletionEvidenceAsync();
            await VerifyIntermediateToolErrorsDoNotPoisonTerminalEvidenceAsync();
            await VerifyActivityFingerprintCurrentTurnAsync();
            await VerifyGeneratedAppLaunchEvidenceAndAbortGuardAsync();
            await VerifyTruncatedAssistantWindowResolvesExactUserParentAsync();
            await VerifyUntrackedWorkGuardsAsync();
            await VerifyActivityFingerprintPreflightRacesAsync();
            await VerifyExactSessionEvidenceAndRequestScopingAsync();
            await VerifyPendingInputAndBusyDescendantVetoAsync();
            await VerifyHistoricalErrorsAndUserMessagesDoNotResetProgressAsync();
            await VerifyMissingAndAmbiguousSessionFailClosedAsync();
            await VerifyDisconnectedStatusFailsClosedAsync();
            await VerifyAbsentStatusEntryMeansIdleOnlyAfterIdentityVerificationAsync();
            await VerifyContinuationUsesSameSessionAndMessageIdAsync();
            await VerifyLostResponseIsReconciledWithoutRetryAsync();
            await VerifyUnconfirmedContinuationRemainsAmbiguousAsync();
            await VerifyContinuationTimeoutIsBoundedAndNeverRetriedAsync();
            await VerifyExpectedTurnAndLastMomentGuardAsync();
            await VerifyOnlyExpectedAbortMarkerCanBeClearedAsync();
            await VerifyIncompleteTodoAndAbortGuardsAsync();
            await VerifyProgressFingerprintRaceGatesAsync();
            await VerifyAbortResponseBodyTimeoutIsBoundedAsync();
            await VerifyAbortUsesNativeSameSessionEndpointAsync();
            Console.WriteLine("PASS: OpenCode recovery client preserves exact session identity and does not duplicate ambiguous continuations.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCODE_SERVER_PASSWORD", oldPassword);
            Environment.SetEnvironmentVariable("OPENCODE_SERVER_USERNAME", oldUsername);
        }
    }

    private static async Task VerifyTaskCompletionEvidenceAsync()
    {
        string todoStatus = "completed", finish = "stop", parent = "msg_user_2";
        string Messages() => JsonSerializer.Serialize(new object[]
        {
            new { info = new { id = "msg_user_2", sessionID = RootSessionId, role = "user", time = new { created = 1000 } }, parts = Array.Empty<object>() },
            new { info = new { id = "msg_assistant_2", sessionID = RootSessionId, parentID = parent, role = "assistant", finish,
                time = new { created = 2000, completed = 3000 } }, parts = new[] { new { type = "text", text = "Implemented, built and verified." } } }
        });
        var handler = new FakeHandler();
        handler.Respond = (request, _) => Task.FromResult(JsonResponse(RouteJson(request, status: "absent", messages: Messages(),
            todos: JsonSerializer.Serialize(new[] { new { content = "Implement and verify", status = todoStatus, priority = "high" } }))));
        using var http = new HttpClient(handler);
        using var client = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, http);
        var result = await client.ReadAsync(RootSessionId);
        Require(result.Evidence is { AllTodosCompleted: true, HasSuccessfulFinalResponse: true }, "Exact successful response and completed todos were not recognized.");
        todoStatus = "cancelled";
        Require((await client.ReadAsync(RootSessionId)).Evidence?.AllTodosCompleted == false, "Cancelled work was shown as completed.");
        todoStatus = "completed"; finish = "tool-calls";
        Require((await client.ReadAsync(RootSessionId)).Evidence?.HasSuccessfulFinalResponse == false, "An intermediate tool turn was shown as completed.");
        finish = "stop"; parent = "msg_old";
        Require((await client.ReadAsync(RootSessionId)).Evidence?.HasSuccessfulFinalResponse == false, "A historical response completed the current task.");
    }

    private static async Task VerifyIntermediateToolErrorsDoNotPoisonTerminalEvidenceAsync()
    {
        string toolStatus = "error", toolOutput = "File not found";
        string? failureType = null;
        int? statusCode = null;
        bool? retryable = true;
        string? errorMessage = null;
        bool successful = true;
        string Messages() => JsonSerializer.Serialize(new object[]
        {
            new { info = new { id = "msg_user_2", sessionID = RootSessionId, role = "user", time = new { created = 1000 } }, parts = Array.Empty<object>() },
            new { info = new { id = "msg_tool_attempt", sessionID = RootSessionId, parentID = "msg_user_2", role = "assistant", finish = "tool-calls", time = new { created = 2000 } },
                parts = new[] { new { type = "tool", tool = "read", state = new { status = toolStatus, input = new { filePath = "missing.txt" }, error = toolOutput } } } },
            new { info = new { id = "msg_final", sessionID = RootSessionId, parentID = "msg_user_2", role = "assistant", finish = successful ? "stop" : null,
                time = new { created = 3000, completed = successful ? (int?)4000 : null },
                error = failureType is null ? null : new { name = failureType, data = new { statusCode, isRetryable = retryable, message = errorMessage } } },
                parts = new[] { new { type = "text", text = "Verified requested work." } } }
        });
        var handler = new FakeHandler();
        handler.Respond = (request, _) => Task.FromResult(JsonResponse(RouteJson(request, status: "absent", messages: Messages())));
        using var http = new HttpClient(handler);
        using var client = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, http);
        RecoverySessionEvidence evidence = (await client.ReadAsync(RootSessionId)).Evidence!;
        Require(evidence.HasSuccessfulFinalResponse && !evidence.HasErrorMarker && evidence.CurrentErrors.Count == 0,
            "A failed intermediate tool poisoned a later successful final for the same request.");
        Require(evidence.NativeMessages.Single(message => message.Id == "msg_tool_attempt").Errors.Single().Type == "tool_error",
            "An intermediate tool failure was lost from native diagnostic evidence.");
        string activity = evidence.ActivityFingerprint;
        toolOutput = "Access denied";
        Require((await client.ReadAsync(RootSessionId)).Evidence!.ActivityFingerprint != activity,
            "Changing tool failure output was lost from current-request activity evidence.");
        toolStatus = "running";
        evidence = (await client.ReadAsync(RootSessionId)).Evidence!;
        Require(evidence.HasRunningTools && !evidence.HasSuccessfulFinalResponse, "A running tool authorized completion.");
        toolStatus = "error"; toolOutput = "Tool execution aborted";
        evidence = (await client.ReadAsync(RootSessionId)).Evidence!;
        Require(evidence.HasErrorMarker && evidence.CurrentErrors.Single().Type == "ToolExecutionAborted",
            "An explicit tool cancellation lost its recovery veto.");
        toolOutput = "File not found"; successful = false; failureType = "APIError"; statusCode = 503;
        evidence = (await client.ReadAsync(RootSessionId)).Evidence!;
        Require(evidence.HasConfirmedErrorResponse && evidence.CurrentErrors.Count == 1 && OpenCodeFailureClassifier.CanRetryAutomatically(evidence),
            "An intermediate tool failure blocked a separately verified transient terminal API error.");
        statusCode = 401;
        Require(!OpenCodeFailureClassifier.CanRetryAutomatically((await client.ReadAsync(RootSessionId)).Evidence!),
            "A tool failure caused terminal authentication errors to retry automatically.");
        failureType = "ProviderConfigError"; statusCode = null;
        Require(!OpenCodeFailureClassifier.CanRetryAutomatically((await client.ReadAsync(RootSessionId)).Evidence!),
            "A tool failure caused terminal configuration errors to retry automatically.");
        failureType = "tool_error";
        evidence = (await client.ReadAsync(RootSessionId)).Evidence!;
        Require(evidence.HasErrorMarker && evidence.CurrentErrors.Single().Type == "tool_error" && !evidence.CurrentErrors.Single().IsToolExecutionError,
            "A native message error sharing the diagnostic tool error name was suppressed.");

        // A shared self-hosted model behind a gateway: overload and dropped connections only in the message text.
        async Task<bool> Retries(string type, int? status, bool? isRetryable, string? message)
        {
            failureType = type; statusCode = status; retryable = isRetryable; errorMessage = message;
            return OpenCodeFailureClassifier.CanRetryAutomatically((await client.ReadAsync(RootSessionId)).Evidence!);
        }
        Require(await Retries("UnknownError", null, null, "fetch failed: socket hang up"), "A dropped gateway connection was not retried.");
        Require(await Retries("UnknownError", null, null, "현재 요청이 많아 처리할 수 없습니다. 잠시 후 다시 시도해 주세요."), "A Korean busy-model message was not retried.");
        Require(await Retries("APIError", 400, false, "Too many requests, please slow down"), "A rate limit reported as 400/non-retryable was not retried.");
        Require(await Retries("ProviderError", null, null, "upstream model overloaded"), "An overloaded upstream model was not retried.");
        Require(!await Retries("UnknownError", null, null, "Invalid API key"), "An API key error was retried automatically.");
        Require(!await Retries("UnknownError", null, null, "maximum context length exceeded, please try again"), "A context-length error was retried automatically.");
        Require(OpenCodeFailureClassifier.CanCompact((await client.ReadAsync(RootSessionId)).Evidence!),
            "A context-length error reported only in words was not routed to compaction.");
        Require(!await Retries("UnknownError", null, null, null), "An unexplained error was retried automatically.");
        Require(!await Retries("ProviderConfigError", null, null, "timeout"), "A configuration error was retried because of its message.");
        Require(!await Retries("UnknownError", 401, null, "server busy"), "A 401 was retried because of its message.");
        // A failed request may be started over by Loop; a person's stop or refusal never is.
        Require(!OpenCodeFailureClassifier.WasStoppedByPerson((await client.ReadAsync(RootSessionId)).Evidence!), "A 401 was taken for a person's stop.");
        foreach (string stop in new[] { "MessageAbortedError", "PermissionDeniedError", "QuestionRejectedError" })
        {
            failureType = stop; statusCode = null; errorMessage = null;
            Require(OpenCodeFailureClassifier.WasStoppedByPerson((await client.ReadAsync(RootSessionId)).Evidence!), stop + " was not taken for a person's stop.");
        }
        failureType = "tool_error"; statusCode = null; retryable = true; errorMessage = null;
    }

    private static void VerifyMessageIdsMatchOpenCodeAscendingShape()
    {
        string first = OpenCodeRecoveryClient.CreateMessageId();
        string second = OpenCodeRecoveryClient.CreateMessageId();
        Require(System.Text.RegularExpressions.Regex.IsMatch(first, @"\Amsg_[0-9a-f]{12}[0-9A-Za-z]{14}\z"), "The generated ID does not match OpenCode's ascending message ID shape.");
        Require(string.CompareOrdinal(first, second) < 0, "Generated OpenCode message IDs are not lexically ascending.");
    }

    private static async Task VerifyExactSessionEvidenceAndRequestScopingAsync()
    {
        FakeHandler handler = CreateHandler();
        handler.Respond = (request, _) => Task.FromResult(JsonResponse(RouteJson(request, status: "retry")));
        using var http = new HttpClient(handler);
        using var client = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, http);

        OpenCodeRecoveryReadResult result = await client.ReadAsync(RootSessionId);
        Require(result.IsAvailable && result.Evidence is not null, "A well-formed exact session read failed.");
        RecoverySessionEvidence evidence = result.Evidence!;
        Require(evidence.SessionId == RootSessionId && evidence.ParentSessionId is null, "The client did not retain the exact root session identity.");
        Require(evidence.Status == OpenCodeRecoveryStatus.Retry && evidence.RetryAttempt == 3, "The retry status details were not parsed.");
        Require(evidence.RetryMessage == "Provider temporarily unavailable" && evidence.RetryNext == 123456789, "The retry schedule was not retained.");
        Require(evidence.LatestUserMessageId == "msg_user_2" && evidence.LatestAssistantMessageId == "msg_assistant_2", "The latest native message IDs were not parsed.");
        Require(evidence.LatestMessageId == "msg_assistant_2" && evidence.LatestUserMessageCreated is not null, "Latest message identity or creation time is missing.");
        Require(evidence.IncompleteTodoCount == 1 && evidence.TotalTodoCount == 2, "Todo completion counts were parsed incorrectly.");
        Require(evidence.TodoFingerprint.Length == 64 && evidence.ProgressFingerprint.Length == 64, "Todo or progress fingerprints are not stable SHA-256 values.");
        Require(evidence.HasRunningTools && evidence.HasErrorMarker && evidence.LatestErrorType == "ProviderError", "Tool activity or native error evidence was not captured.");
        Require(handler.Requests.All(request => request.Uri.Query.Contains("directory=" + Uri.EscapeDataString(ProjectDirectory), StringComparison.Ordinal)), "A recovery request omitted its directory scope.");
        Require(handler.Requests.All(request => request.Authorization == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("recovery-test:test-only-password"))), "Basic auth did not use OpenCode environment credentials.");
        Require(handler.Requests.All(request => !request.Uri.AbsolutePath.Contains("/tui/", StringComparison.Ordinal)), "The client used a TUI-control endpoint.");
        Require(handler.Requests.Any(request => request.Uri.AbsolutePath == "/session"), "The client did not cross-check the session list.");
        Require(handler.Requests.Any(request => request.Uri.AbsolutePath == "/session/" + RootSessionId), "The client did not verify exact session metadata.");
    }

    private static async Task VerifyPendingInputAndBusyDescendantVetoAsync()
    {
        FakeHandler handler = CreateHandler();
        handler.Respond = (request, _) => Task.FromResult(JsonResponse(request.Uri.AbsolutePath == "/session/status"
            ? "{\"" + RootSessionId + "\":{\"type\":\"busy\"},\"ses_child_456\":{\"type\":\"retry\",\"attempt\":1,\"message\":\"wait\",\"next\":123456790}}"
            : RouteJson(request,
            children: request.Uri.AbsolutePath == $"/session/{RootSessionId}/children"
                ? "[{\"id\":\"ses_child_456\",\"directory\":\"" + EscapeJson(ProjectDirectory) + "\",\"parentID\":\"" + RootSessionId + "\"}]"
                : "[]",
            permissions: "[{\"id\":\"perm-child\",\"sessionID\":\"ses_child_456\"},{\"id\":\"perm-other\",\"sessionID\":\"ses_unrelated\"}]",
            questions: "[{\"id\":\"question-root\",\"sessionID\":\"" + RootSessionId + "\"},{\"id\":\"question-other\",\"sessionID\":\"ses_unrelated\"}]")));
        using var http = new HttpClient(handler);
        using var client = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, http);

        OpenCodeRecoveryReadResult result = await client.ReadAsync(RootSessionId);
        Require(result.IsAvailable && result.Evidence is not null, "A root with one verified child could not be read.");
        RecoverySessionEvidence evidence = result.Evidence!;
        Require(evidence.Status == OpenCodeRecoveryStatus.Busy, "A busy root status was lost.");
        Require(evidence.HasPendingQuestion && !evidence.HasPendingPermission, "Pending requests from an unrelated session leaked into the exact target evidence.");
        Require(evidence.HasPendingPermissionInDescendants && !evidence.HasPendingQuestionInDescendants, "Pending child input was not exact-filtered.");
        Require(evidence.HasBusyDescendants, "An active descendant did not veto recovery.");
        Require(handler.Requests.Any(request => request.Uri.AbsolutePath == "/session/ses_child_456/children"), "The client did not walk the descendant tree.");
    }

    private static async Task VerifyMissingAndAmbiguousSessionFailClosedAsync()
    {
        FakeHandler missing = CreateHandler();
        missing.Respond = (request, _) => Task.FromResult(JsonResponse(RouteJson(request, includeRootInList: false)));
        using (var http = new HttpClient(missing))
        using (var client = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, http))
        {
            OpenCodeRecoveryReadResult result = await client.ReadAsync(RootSessionId);
            Require(!result.IsAvailable && result.Evidence is null && result.ErrorCode == "session_missing", "A missing exact ID did not fail closed.");
            Require(!missing.Requests.Any(request => request.Uri.AbsolutePath == "/session/status"), "The client read status after the exact target was missing.");
        }

        FakeHandler duplicate = CreateHandler();
        duplicate.Respond = (request, _) => Task.FromResult(JsonResponse(RouteJson(request, duplicateRootInList: true)));
        using (var http = new HttpClient(duplicate))
        using (var client = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, http))
        {
            OpenCodeRecoveryReadResult result = await client.ReadAsync(RootSessionId);
            Require(!result.IsAvailable && result.Evidence is null && result.ErrorCode == "session_ambiguous", "Duplicate exact IDs did not fail closed.");
        }

        FakeHandler wrongDirectory = CreateHandler();
        wrongDirectory.Respond = (request, _) => Task.FromResult(JsonResponse(RouteJson(request, detailDirectory: Path.Combine(ProjectDirectory, "other"))));
        using (var http = new HttpClient(wrongDirectory))
        using (var client = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, http))
        {
            OpenCodeRecoveryReadResult result = await client.ReadAsync(RootSessionId);
            Require(!result.IsAvailable && result.ErrorCode == "identity_mismatch", "A session from a different directory did not fail closed.");
        }
    }

    private static async Task VerifyHistoricalErrorsAndUserMessagesDoNotResetProgressAsync()
    {
        string firstMessages = "[{\"info\":{\"id\":\"msg_old_user\",\"sessionID\":\"" + RootSessionId + "\",\"role\":\"user\",\"time\":{\"created\":1000}},\"parts\":[{\"type\":\"text\",\"text\":\"old question\"}]}," +
            "{\"info\":{\"id\":\"msg_old_assistant\",\"sessionID\":\"" + RootSessionId + "\",\"role\":\"assistant\",\"time\":{\"created\":2000},\"error\":{\"name\":\"ProviderError\"}},\"parts\":[{\"type\":\"tool\",\"tool\":\"bash\",\"state\":{\"status\":\"error\",\"input\":{\"command\":\"old\"}}}]}," +
            "{\"info\":{\"id\":\"msg_latest_user_a\",\"sessionID\":\"" + RootSessionId + "\",\"role\":\"user\",\"time\":{\"created\":3000}},\"parts\":[{\"type\":\"text\",\"text\":\"first unrelated prompt\"}]}," +
            "{\"info\":{\"id\":\"msg_current_assistant\",\"sessionID\":\"" + RootSessionId + "\",\"role\":\"assistant\",\"time\":{\"created\":4000}},\"parts\":[{\"type\":\"text\",\"text\":\"same current progress\"}]}]";
        string secondMessages = firstMessages
            .Replace("msg_latest_user_a", "msg_latest_user_b", StringComparison.Ordinal)
            .Replace("first unrelated prompt", "different unrelated prompt", StringComparison.Ordinal)
            .Replace("same current progress", "different current output", StringComparison.Ordinal);

        async Task<RecoverySessionEvidence> ReadMessagesAsync(string messages, string? todos = null)
        {
            FakeHandler handler = CreateHandler();
            handler.Respond = (request, _) => Task.FromResult(JsonResponse(RouteJson(request, status: "absent", messages: messages, todos: todos)));
            using var http = new HttpClient(handler);
            using var client = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, http);
            OpenCodeRecoveryReadResult result = await client.ReadAsync(RootSessionId);
            Require(result.IsAvailable && result.Evidence is not null, "A later completed turn did not produce recovery evidence.");
            return result.Evidence!;
        }

        RecoverySessionEvidence first = await ReadMessagesAsync(firstMessages);
        RecoverySessionEvidence second = await ReadMessagesAsync(secondMessages);
        Require(!first.HasErrorMarker && !first.HasRunningTools, "A historical failed tool kept blocking the latest completed turn.");
        Require(first.ProgressFingerprint == second.ProgressFingerprint, "Changing user text or its message ID reset the progress fingerprint.");

        string completedTool = ReadyMessagesWithToolProgressJson();
        string duplicateTool = completedTool.Replace(
            "{\"type\":\"text\",\"text\":\"last assistant response\"}",
            "{\"type\":\"text\",\"text\":\"last assistant response\"},{\"type\":\"tool\",\"tool\":\"bash\",\"state\":{\"status\":\"completed\",\"title\":\"repeat title\",\"input\":{\"command\":\"dotnet test\"}}}",
            StringComparison.Ordinal);
        RecoverySessionEvidence oneTool = await ReadMessagesAsync(completedTool);
        RecoverySessionEvidence repeatedTool = await ReadMessagesAsync(duplicateTool);
        Require(oneTool.ProgressFingerprint == repeatedTool.ProgressFingerprint,
            "Repeated completed-tool evidence did not inflate measured progress.");

        string pendingTodo = "[{\"content\":\"inspect existing behavior\",\"status\":\"pending\",\"priority\":\"normal\"}]";
        string renamedTodo = "[{\"content\":\"review current behavior\",\"status\":\"pending\",\"priority\":\"low\"}]";
        RecoverySessionEvidence pending = await ReadMessagesAsync(ReadyMessagesJson(), pendingTodo);
        RecoverySessionEvidence renamed = await ReadMessagesAsync(ReadyMessagesJson(), renamedTodo);
        Require(pending.TodoFingerprint != renamed.TodoFingerprint && pending.ProgressFingerprint == renamed.ProgressFingerprint,
            "Renaming or reprioritizing an incomplete todo counted as completed work progress.");
    }

    private static async Task VerifyDisconnectedStatusFailsClosedAsync()
    {
        FakeHandler handler = CreateHandler();
        handler.Respond = (request, _) => request.Uri.AbsolutePath == "/session/status"
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException("fake disconnect"))
            : Task.FromResult(JsonResponse(RouteJson(request)));
        using var http = new HttpClient(handler);
        using var client = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, http);

        OpenCodeRecoveryReadResult result = await client.ReadAsync(RootSessionId);
        Require(!result.IsAvailable && result.Evidence is null && result.ErrorCode == "disconnected", "A disconnected status endpoint was treated as safe evidence.");
    }

    private static async Task VerifyAbsentStatusEntryMeansIdleOnlyAfterIdentityVerificationAsync()
    {
        FakeHandler handler = CreateHandler();
        handler.Respond = (request, _) => Task.FromResult(JsonResponse(RouteJson(request, status: "absent")));
        using var http = new HttpClient(handler);
        using var client = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, http);

        OpenCodeRecoveryReadResult result = await client.ReadAsync(RootSessionId);
        Require(result.IsAvailable && result.Evidence?.Status == OpenCodeRecoveryStatus.Idle, "An explicit healthy OpenCode status map omission was not interpreted as idle.");
        Require(handler.Requests.Any(request => request.Uri.AbsolutePath == "/session"), "Idle was inferred without a successful exact session list.");
    }

    private static async Task VerifyContinuationUsesSameSessionAndMessageIdAsync()
    {
        string messageId = OpenCodeRecoveryClient.CreateMessageId();
        FakeHandler handler = CreateHandler();
        handler.Respond = async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Post && request.Uri.AbsolutePath == $"/session/{RootSessionId}/prompt_async")
            {
                string body = request.Body!;
                Require(body.Contains(messageId, StringComparison.Ordinal) && body.Contains("Continue the existing task", StringComparison.Ordinal), "The continuation payload lost its message ID or prompt.");
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.Uri.AbsolutePath.StartsWith($"/session/{RootSessionId}/message/", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            return JsonResponse(RouteJson(request, status: "absent", messages: ReadyMessagesJson()));
        };
        using var http = new HttpClient(handler);
        using var client = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, http);

        OpenCodeRecoveryContinueResult result = await client.ContinueAsync(
            RootSessionId,
            "Continue the existing task",
            messageId,
            expectedUserMessageId: "msg_user_2");
        Require(result.IsAccepted && result.MessageId == messageId, "A 204 prompt_async response was not accepted.");
        Require(handler.Requests.Count(request => request.Method == HttpMethod.Post) == 1, "The client did not make exactly one recovery POST.");
        Require(handler.Requests.Any(request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath == $"/session/{RootSessionId}/prompt_async"), "The continuation did not target prompt_async on the exact session.");
        Require(handler.Requests.All(request => request.Uri.AbsolutePath != "/session" + "/new"), "The recovery client created a new session.");
    }

    private static async Task VerifyLostResponseIsReconciledWithoutRetryAsync()
    {
        string messageId = OpenCodeRecoveryClient.CreateMessageId();
        bool postDispatched = false;
        FakeHandler handler = CreateHandler();
        handler.Respond = async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                postDispatched = true;
                throw new HttpRequestException("fake response loss after dispatch");
            }
            if (request.Uri.AbsolutePath == $"/session/{RootSessionId}/message/{messageId}" && postDispatched)
                return JsonResponse("{\"info\":{\"id\":\"" + messageId + "\",\"sessionID\":\"" + RootSessionId + "\",\"role\":\"user\"},\"parts\":[]}");
            if (request.Uri.AbsolutePath.StartsWith($"/session/{RootSessionId}/message/", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            return JsonResponse(RouteJson(request, status: "absent", messages: ReadyMessagesJson()));
        };
        using var http = new HttpClient(handler);
        using var client = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, http);

        OpenCodeRecoveryContinueResult result = await client.ContinueAsync(RootSessionId, "Resume safely", messageId, expectedUserMessageId: "msg_user_2");
        Require(result.IsAccepted && result.ErrorCode == "message_confirmed", "The exact message was not used to reconcile a lost response.");
        Require(handler.Requests.Count(request => request.Method == HttpMethod.Post) == 1, "A lost prompt_async response caused a blind retry.");
        Require(handler.Requests.Any(request => request.Uri.AbsolutePath == $"/session/{RootSessionId}/message/{messageId}"), "The client did not check exact message presence after the response loss.");
    }

    private static async Task VerifyUnconfirmedContinuationRemainsAmbiguousAsync()
    {
        string messageId = OpenCodeRecoveryClient.CreateMessageId();
        FakeHandler handler = CreateHandler();
        handler.Respond = (request, _) =>
        {
            if (request.Method == HttpMethod.Post)
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("fake response loss"));
            if (request.Uri.AbsolutePath == $"/session/{RootSessionId}/message/{messageId}")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(JsonResponse(RouteJson(request, status: "absent", messages: ReadyMessagesJson())));
        };
        using var http = new HttpClient(handler);
        using var client = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, http);

        OpenCodeRecoveryContinueResult result = await client.ContinueAsync(RootSessionId, "Resume once", messageId, expectedUserMessageId: "msg_user_2");
        Require(result.Outcome == OpenCodeRecoveryOutcome.Ambiguous && result.IsAmbiguous, "An unconfirmed lost response was incorrectly treated as rejected or accepted.");
        Require(handler.Requests.Count(request => request.Method == HttpMethod.Post) == 1, "An ambiguous continuation was retried.");
    }

    private static async Task VerifyContinuationTimeoutIsBoundedAndNeverRetriedAsync()
    {
        string messageId = OpenCodeRecoveryClient.CreateMessageId();
        FakeHandler handler = CreateHandler();
        handler.Respond = async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Post)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (request.Uri.AbsolutePath == $"/session/{RootSessionId}/message/{messageId}")
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            return JsonResponse(RouteJson(request, status: "absent", messages: ReadyMessagesJson()));
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) };
        using var client = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, http);

        OpenCodeRecoveryContinueResult result = await client.ContinueAsync(
            RootSessionId,
            "Resume after a timeout",
            messageId,
            expectedUserMessageId: "msg_user_2");
        Require(result.IsAmbiguous && result.ErrorCode == "response_ambiguous", "An unconfirmed timeout was not reported as ambiguous.");
        Require(handler.Requests.Count(request => request.Method == HttpMethod.Post) == 1, "A timeout caused the continuation POST to be retried.");
    }

    private static async Task VerifyTruncatedAssistantWindowResolvesExactUserParentAsync()
    {
        const string parentId = "msg_older_current_user";
        string tail = TruncatedAssistantWindow(parentId);
        string exactParent = JsonSerializer.Serialize(new
        {
            info = new { id = parentId, sessionID = RootSessionId, role = "user", time = new { created = 1000 } },
            parts = new[] { new { type = "text", text = "the exact older current request" } }
        });
        FakeHandler valid = CreateHandler();
        valid.Respond = (request, _) => request.Uri.AbsolutePath == $"/session/{RootSessionId}/message/{parentId}"
            ? Task.FromResult(JsonResponse(exactParent))
            : Task.FromResult(JsonResponse(RouteJson(request, status: "busy", messages: tail)));
        using var validHttp = new HttpClient(valid);
        using var validClient = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, validHttp);
        OpenCodeRecoveryReadResult resolved = await validClient.ReadAsync(RootSessionId);
        Require(resolved.IsAvailable && resolved.Evidence?.LatestUserMessageId == parentId &&
            resolved.Evidence.LatestAssistantMessageId == "msg_tail_49" && !string.IsNullOrEmpty(resolved.Evidence.ActivityFingerprint),
            "A 50-assistant window did not recover its exact verified user parent and preserve the assistant tail.");
        Require(valid.Requests.Any(request => request.Uri.AbsolutePath == $"/session/{RootSessionId}/message/{parentId}"),
            "The capped tail's explicit parent ID was not read through its exact message endpoint.");

        string conflictingParents = TruncatedAssistantWindow(parentId);
        int parentOffset = conflictingParents.IndexOf(parentId, StringComparison.Ordinal);
        conflictingParents = conflictingParents.Remove(parentOffset, parentId.Length).Insert(parentOffset, "msg_different_parent");
        FakeHandler conflict = CreateHandler();
        conflict.Respond = (request, _) => Task.FromResult(JsonResponse(RouteJson(request, status: "busy", messages: conflictingParents)));
        using var conflictHttp = new HttpClient(conflict);
        using var conflictClient = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, conflictHttp);
        OpenCodeRecoveryReadResult rejectedConflict = await conflictClient.ReadAsync(RootSessionId);
        Require(!rejectedConflict.IsAvailable && rejectedConflict.ErrorCode == "current_user_unavailable" &&
            conflict.Requests.All(request => !request.Uri.AbsolutePath.Contains("/message/msg_older_current_user", StringComparison.Ordinal)),
            "Different assistant parents were guessed or treated as a single current user request.");

        string wrongSessionParent = JsonSerializer.Serialize(new
        {
            info = new { id = parentId, sessionID = "ses_another_session", role = "user", time = new { created = 1000 } },
            parts = new[] { new { type = "text", text = "must not cross sessions" } }
        });
        FakeHandler wrongSession = CreateHandler();
        wrongSession.Respond = (request, _) => request.Uri.AbsolutePath == $"/session/{RootSessionId}/message/{parentId}"
            ? Task.FromResult(JsonResponse(wrongSessionParent))
            : Task.FromResult(JsonResponse(RouteJson(request, status: "busy", messages: tail)));
        using var wrongSessionHttp = new HttpClient(wrongSession);
        using var wrongSessionClient = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, wrongSessionHttp);
        OpenCodeRecoveryReadResult rejectedSession = await wrongSessionClient.ReadAsync(RootSessionId);
        Require(!rejectedSession.IsAvailable && rejectedSession.ErrorCode == "invalid_current_user_parent",
            "A parent user message from another session was accepted.");

        string wrongIdParent = JsonSerializer.Serialize(new
        {
            info = new { id = "msg_not_the_parent", sessionID = RootSessionId, role = "user", time = new { created = 1000 } },
            parts = new[] { new { type = "text", text = "not the referenced id" } }
        });
        FakeHandler wrongId = CreateHandler();
        wrongId.Respond = (request, _) => request.Uri.AbsolutePath == $"/session/{RootSessionId}/message/{parentId}"
            ? Task.FromResult(JsonResponse(wrongIdParent))
            : Task.FromResult(JsonResponse(RouteJson(request, status: "busy", messages: tail)));
        using var wrongIdHttp = new HttpClient(wrongId);
        using var wrongIdClient = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, wrongIdHttp);
        OpenCodeRecoveryReadResult rejectedId = await wrongIdClient.ReadAsync(RootSessionId);
        Require(!rejectedId.IsAvailable && rejectedId.ErrorCode == "invalid_current_user_parent",
            "A user message with an ID other than the assistant's exact parent was accepted.");
        string wrongRoleParent = JsonSerializer.Serialize(new
        {
            info = new { id = parentId, sessionID = RootSessionId, role = "assistant", time = new { created = 1000 } },
            parts = new[] { new { type = "text", text = "not a user" } }
        });
        FakeHandler wrongRole = CreateHandler();
        wrongRole.Respond = (request, _) => request.Uri.AbsolutePath == $"/session/{RootSessionId}/message/{parentId}"
            ? Task.FromResult(JsonResponse(wrongRoleParent))
            : Task.FromResult(JsonResponse(RouteJson(request, status: "busy", messages: tail)));
        using var wrongRoleHttp = new HttpClient(wrongRole);
        using var wrongRoleClient = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, wrongRoleHttp);
        OpenCodeRecoveryReadResult rejectedRole = await wrongRoleClient.ReadAsync(RootSessionId);
        Require(!rejectedRole.IsAvailable && rejectedRole.ErrorCode == "invalid_current_user_parent",
            "An assistant parent message was accepted as the exact current user request.");
    }

    private static string TruncatedAssistantWindow(string parentId) => JsonSerializer.Serialize(
        Enumerable.Range(0, 50).Select(index => (object)new
        {
            info = new
            {
                id = $"msg_tail_{index:00}", sessionID = RootSessionId, parentID = parentId,
                role = "assistant", time = new { created = 2000L + index }
            },
            parts = new[] { new { type = "text", text = $"assistant continuation {index}" } }
        }).ToArray());
    private static async Task VerifyActivityFingerprintCurrentTurnAsync()
    {
        FakeHandler handler = CreateHandler();
        string messages = MessagesJson();
        handler.Respond = (request, _) => Task.FromResult(JsonResponse(RouteJson(request, status: "busy", messages: messages)));
        using var http = new HttpClient(handler);
        using var client = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, http);

        RecoverySessionEvidence initial = (await client.ReadAsync(RootSessionId)).Evidence!;
        messages = messages.Replace("historic user prompt", "different old request", StringComparison.Ordinal)
            .Replace("intermediate response", "different historical assistant response", StringComparison.Ordinal)
            .Replace("\"created\":1000", "\"created\":1100", StringComparison.Ordinal)
            .Replace("\"created\":2000", "\"created\":2200", StringComparison.Ordinal)
            .Replace("\"created\":3000", "\"created\":3300", StringComparison.Ordinal)
            .Replace("\"created\":4000", "\"created\":4400", StringComparison.Ordinal);
        RecoverySessionEvidence heartbeat = (await client.ReadAsync(RootSessionId)).Evidence!;
        Require(initial.ActivityFingerprint == heartbeat.ActivityFingerprint,
            "Historical text or message timestamps changed the current-turn activity fingerprint.");

        messages = messages.Replace("current assistant progress", "new current assistant output", StringComparison.Ordinal);
        RecoverySessionEvidence progressed = (await client.ReadAsync(RootSessionId)).Evidence!;
        Require(initial.ActivityFingerprint != progressed.ActivityFingerprint,
            "New assistant text in the current user turn did not change the activity fingerprint.");

        messages = ActivityTurnMessages("assistant text", "reason one", "running", "{\"cmd\":\"a\"}", "")
            .Replace("historic response", "rewritten historic response", StringComparison.Ordinal);
        RecoverySessionEvidence toolInitial = (await client.ReadAsync(RootSessionId)).Evidence!;
        messages = ActivityTurnMessages("assistant text", "reason one", "running", "{\"cmd\":\"a\"}", "")
            .Replace("\"created\":1000", "\"created\":9000", StringComparison.Ordinal)
            .Replace("\"created\":2000", "\"created\":8000", StringComparison.Ordinal);
        RecoverySessionEvidence toolHeartbeat = (await client.ReadAsync(RootSessionId)).Evidence!;
        Require(toolInitial.ActivityFingerprint == toolHeartbeat.ActivityFingerprint,
            "A message heartbeat changed the activity fingerprint when current-turn content was unchanged.");
        messages = ActivityTurnMessages("assistant text", "reason two", "running", "{\"cmd\":\"a\"}", "");
        RecoverySessionEvidence reasoning = (await client.ReadAsync(RootSessionId)).Evidence!;
        Require(toolInitial.ActivityFingerprint != reasoning.ActivityFingerprint,
            "A current-turn reasoning change did not change the activity fingerprint.");
        messages = ActivityTurnMessages("assistant text", "reason one", "completed", "{\"cmd\":\"a\"}", "done");
        RecoverySessionEvidence toolOutput = (await client.ReadAsync(RootSessionId)).Evidence!;
        Require(toolInitial.ActivityFingerprint != toolOutput.ActivityFingerprint,
            "Tool status/output changes did not change the activity fingerprint.");
    }

    private static async Task VerifyGeneratedAppLaunchEvidenceAndAbortGuardAsync()
    {
        string appDirectory = Path.Combine(ProjectDirectory, "app-fixture");
        Directory.CreateDirectory(appDirectory);
        string projectFile = Path.Combine(appDirectory, "App.csproj");
        await File.WriteAllTextAsync(projectFile, "<Project />");
        string outputDirectory = Path.Combine(appDirectory, "bin", "Release", "net10.0-windows");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "App.exe"), []);
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "testhost.exe"), []);
        try
        {
            async Task<RecoverySessionEvidence> ReadToolAsync(string command, string tool = "shell", bool secondTool = false, bool pendingTool = false)
            {
                string parts = "{\"type\":\"tool\",\"tool\":" + JsonSerializer.Serialize(tool) +
                    ",\"state\":{\"status\":\"running\",\"input\":{\"command\":" + JsonSerializer.Serialize(command) + "}}}";
                if (secondTool) parts += ",{\"type\":\"tool\",\"tool\":\"shell\",\"state\":{\"status\":\"running\",\"input\":{\"command\":\"dotnet run\"}}}";
                if (pendingTool) parts += ",{\"type\":\"tool\",\"tool\":\"shell\",\"state\":{\"status\":\"pending\",\"input\":{\"command\":\"dotnet run\"}}}";
                string messages = "[{\"info\":{\"id\":\"msg_current_user\",\"sessionID\":\"" + RootSessionId + "\",\"role\":\"user\",\"time\":{\"created\":1000}},\"parts\":[{\"type\":\"text\",\"text\":\"run app\"}]}," +
                    "{\"info\":{\"id\":\"msg_current_assistant\",\"sessionID\":\"" + RootSessionId + "\",\"parentID\":\"msg_current_user\",\"role\":\"assistant\",\"time\":{\"created\":2000}},\"parts\":[" + parts + "]}]";
                FakeHandler handler = CreateHandler();
                handler.Respond = (request, _) => Task.FromResult(JsonResponse(RouteJson(request, status: "busy", messages: messages)));
                using var http = new HttpClient(handler);
                using var client = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, http);
                return (await client.ReadAsync(RootSessionId)).Evidence!;
            }

            RecoverySessionEvidence app = await ReadToolAsync("dotnet run --project app-fixture/App.csproj");
            Require(!string.IsNullOrEmpty(app.StalledLaunchToolFingerprint), "A single project-local dotnet app launch was not identified.");
            Require(!string.IsNullOrEmpty((await ReadToolAsync("dotnet run --project app-fixture/App.csproj -c Release")).StalledLaunchToolFingerprint) &&
                !string.IsNullOrEmpty((await ReadToolAsync("dotnet run -c Debug --project=app-fixture/App.csproj")).StalledLaunchToolFingerprint),
                "Common dotnet run project/configuration options were not recognized.");
            Require(!string.IsNullOrEmpty((await ReadToolAsync("app-fixture/bin/Release/net10.0-windows/App.exe")).StalledLaunchToolFingerprint),
                "A direct project-local app executable in a build output directory was not recognized.");
            Require((await ReadToolAsync("app-fixture/bin/Release/net10.0-windows/testhost.exe")).StalledLaunchToolFingerprint is null &&
                (await ReadToolAsync("app-fixture/App.exe")).StalledLaunchToolFingerprint is null &&
                (await ReadToolAsync("app-fixture/bin/Release/net10.0-windows/App.exe --help")).StalledLaunchToolFingerprint is null,
                "A test runner, executable outside approved output, or executable with arguments was classified as an app launch.");
            Require((await ReadToolAsync("C:\\other\\dotnet.exe run --project app-fixture/App.csproj")).StalledLaunchToolFingerprint is null &&
                (await ReadToolAsync("dotnet run --project app-fixture/App.csproj -c Test")).StalledLaunchToolFingerprint is null,
                "An unrelated runtime path or unsupported configuration was classified as an app launch.");
            Require((await ReadToolAsync("dotnet test app-fixture/App.csproj")).StalledLaunchToolFingerprint is null,
                "A test command was incorrectly classified as an app launch.");
            Require((await ReadToolAsync("dotnet run --project ..\\outside\\App.csproj")).StalledLaunchToolFingerprint is null,
                "A command escaping the configured project was classified as an app launch.");
            Require((await ReadToolAsync("dotnet run --project app-fixture/App.csproj; taskkill /f /im app.exe")).StalledLaunchToolFingerprint is null,
                "A chained shell command was classified as a single owned app launch.");
            Require((await ReadToolAsync("dotnet run --project app-fixture/App.csproj", secondTool: true)).StalledLaunchToolFingerprint is null,
                "Multiple running tools were classified as one app launch.");
            Require((await ReadToolAsync("dotnet run --project app-fixture/App.csproj", pendingTool: true)).StalledLaunchToolFingerprint is null,
                "A running app tool plus a separately pending tool was classified as one app launch.");

            FakeHandler abortHandler = CreateHandler();
            string liveMessage = "[{\"info\":{\"id\":\"msg_current_user\",\"sessionID\":\"" + RootSessionId + "\",\"role\":\"user\",\"time\":{\"created\":1000}},\"parts\":[]},{\"info\":{\"id\":\"msg_current_assistant\",\"sessionID\":\"" + RootSessionId + "\",\"parentID\":\"msg_current_user\",\"role\":\"assistant\",\"time\":{\"created\":2000}},\"parts\":[{\"type\":\"tool\",\"tool\":\"shell\",\"state\":{\"status\":\"running\",\"input\":{\"command\":\"dotnet run --project app-fixture/App.csproj\"}}}]}]";
            abortHandler.Respond = (request, _) => request.Method == HttpMethod.Post && request.Uri.AbsolutePath.EndsWith("/abort", StringComparison.Ordinal)
                ? Task.FromResult(JsonResponse("true"))
                : Task.FromResult(JsonResponse(RouteJson(request, status: "busy", messages: liveMessage)));
            using var abortHttp = new HttpClient(abortHandler);
            using var abortClient = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, abortHttp);
            RecoverySessionEvidence launch = (await abortClient.ReadAsync(RootSessionId)).Evidence!;
            OpenCodeRecoveryActionResult accepted = await abortClient.AbortAsync(RootSessionId,
                expectedUserMessageId: launch.LatestUserMessageId, allowUntrackedWork: true,
                expectedProgressFingerprint: launch.ProgressFingerprint, expectedTodoFingerprint: launch.TodoFingerprint,
                expectedActivityFingerprint: launch.ActivityFingerprint,
                expectedStalledLaunchToolFingerprint: launch.StalledLaunchToolFingerprint);
            Require(accepted.IsAccepted, "The exact unchanged app-launch tool was not eligible for native abort.");
            OpenCodeRecoveryActionResult mismatch = await abortClient.AbortAsync(RootSessionId,
                expectedUserMessageId: launch.LatestUserMessageId, allowUntrackedWork: true,
                expectedStalledLaunchToolFingerprint: "different-tool-fingerprint");
            Require(!mismatch.IsAccepted, "A changed or mismatched app-launch identity passed the abort guard.");

            string abortedToolMessages = "[{\"info\":{\"id\":\"msg_current_user\",\"sessionID\":\"" + RootSessionId + "\",\"role\":\"user\",\"time\":{\"created\":1000}},\"parts\":[]},{\"info\":{\"id\":\"msg_current_assistant\",\"sessionID\":\"" + RootSessionId + "\",\"parentID\":\"msg_current_user\",\"role\":\"assistant\",\"time\":{\"created\":2000}},\"parts\":[{\"type\":\"tool\",\"tool\":\"shell\",\"state\":{\"status\":\"error\",\"input\":{\"command\":\"dotnet run --project app-fixture/App.csproj\"},\"output\":\"Tool execution aborted\"}}]}]";
            FakeHandler continueHandler = CreateHandler();
            continueHandler.Respond = (request, _) => request.Method == HttpMethod.Post
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))
                : request.Uri.AbsolutePath.StartsWith($"/session/{RootSessionId}/message/msg_", StringComparison.Ordinal)
                    ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
                    : Task.FromResult(JsonResponse(RouteJson(request, status: "absent", messages: abortedToolMessages)));
            using var continueHttp = new HttpClient(continueHandler);
            using var continueClient = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, continueHttp);
            RecoverySessionEvidence aborted = (await continueClient.ReadAsync(RootSessionId)).Evidence!;
            Require(aborted.HasErrorMarker && aborted.LatestErrorType == "ToolExecutionAborted",
                "The exact native tool-abort cancellation marker was not recognized.");
            OpenCodeRecoveryContinueResult acceptedContinue = await continueClient.ContinueAsync(RootSessionId, "Continue the same request.",
                OpenCodeRecoveryClient.CreateMessageId(), expectedUserMessageId: aborted.LatestUserMessageId,
                allowAbortedTurn: true, allowUntrackedWork: true, expectedStalledLaunchToolFingerprint: launch.StalledLaunchToolFingerprint);
            Require(acceptedContinue.IsAccepted, "The expected cancellation from an accepted app-launch abort did not permit same-request continuation.");
            OpenCodeRecoveryContinueResult unrelatedAbort = await continueClient.ContinueAsync(RootSessionId, "Do not continue unrelated error.",
                OpenCodeRecoveryClient.CreateMessageId(), expectedUserMessageId: aborted.LatestUserMessageId,
                allowAbortedTurn: true, allowUntrackedWork: true);
            Require(!unrelatedAbort.IsAccepted, "A tool-abort cancellation marker was allowed without the accepted launch-abort identity.");
        }
        finally
        {
            try { Directory.Delete(appDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task VerifyUntrackedWorkGuardsAsync()
    {
        FakeHandler handler = CreateHandler();
        handler.Respond = (request, _) => request.Method == HttpMethod.Post
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))
            : request.Uri.AbsolutePath.StartsWith($"/session/{RootSessionId}/message/msg_", StringComparison.Ordinal)
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
                : Task.FromResult(JsonResponse(RouteJson(request, status: "absent", messages: ActivityTurnMessages("unfinished work", "", "", "", ""), todos: "[]")));
        using var http = new HttpClient(handler);
        using var client = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, http);

        OpenCodeRecoveryContinueResult defaultRejected = await client.ContinueAsync(
            RootSessionId, "Do not continue without explicit opt-in", OpenCodeRecoveryClient.CreateMessageId(),
            expectedUserMessageId: "msg_current_user");
        Require(!defaultRejected.IsAccepted && defaultRejected.ErrorCode == "session_not_ready",
            "A no-todo current turn was allowed without explicit untracked-work recovery.");

        OpenCodeRecoveryContinueResult wrongUser = await client.ContinueAsync(
            RootSessionId, "Wrong request", OpenCodeRecoveryClient.CreateMessageId(),
            expectedUserMessageId: "msg_other", allowUntrackedWork: true);
        Require(!wrongUser.IsAccepted && wrongUser.ErrorCode == "user_message_changed",
            "Untracked work was allowed without an exact current user message.");

        OpenCodeRecoveryContinueResult allowed = await client.ContinueAsync(
            RootSessionId, "Continue this exact current request", OpenCodeRecoveryClient.CreateMessageId(),
            expectedUserMessageId: "msg_current_user", allowUntrackedWork: true);
        Require(allowed.IsAccepted, "Explicitly allowed, unfinished current-turn work without todos was not continued.");

        FakeHandler abortDefaultHandler = CreateHandler();
        abortDefaultHandler.Respond = (request, _) => request.Method == HttpMethod.Post
            ? Task.FromResult(JsonResponse("true"))
            : Task.FromResult(JsonResponse(RouteJson(request, status: "busy", messages: ActivityTurnMessages("unfinished work", "", "", "", ""), todos: "[]")));
        using var abortDefaultHttp = new HttpClient(abortDefaultHandler);
        using var abortDefaultClient = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, abortDefaultHttp);
        OpenCodeRecoveryActionResult abortDefault = await abortDefaultClient.AbortAsync(
            RootSessionId, expectedUserMessageId: "msg_current_user");
        Require(!abortDefault.IsAccepted && abortDefault.ErrorCode == "session_not_abortable",
            "Abort allowed untracked work without explicit opt-in.");

        FakeHandler abortAllowedHandler = CreateHandler();
        abortAllowedHandler.Respond = (request, _) => request.Method == HttpMethod.Post
            ? Task.FromResult(JsonResponse("true"))
            : Task.FromResult(JsonResponse(RouteJson(request, status: "busy", messages: ActivityTurnMessages("unfinished work", "", "", "", ""), todos: "[]")));
        using var abortAllowedHttp = new HttpClient(abortAllowedHandler);
        using var abortAllowedClient = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, abortAllowedHttp);
        OpenCodeRecoveryActionResult abortAllowed = await abortAllowedClient.AbortAsync(
            RootSessionId, expectedUserMessageId: "msg_current_user", allowUntrackedWork: true);
        Require(abortAllowed.IsAccepted, "Explicitly allowed, exact busy user work without todos was not abortable.");

        FakeHandler abortFinalHandler = CreateHandler();
        abortFinalHandler.Respond = (request, _) => request.Method == HttpMethod.Post
            ? Task.FromResult(JsonResponse("true"))
            : Task.FromResult(JsonResponse(RouteJson(request, status: "busy", messages: SuccessfulNoTodoMessages(), todos: "[]")));
        using var abortFinalHttp = new HttpClient(abortFinalHandler);
        using var abortFinalClient = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, abortFinalHttp);
        OpenCodeRecoveryActionResult abortFinal = await abortFinalClient.AbortAsync(
            RootSessionId, expectedUserMessageId: "msg_current_user", allowUntrackedWork: true);
        Require(!abortFinal.IsAccepted && abortFinal.ErrorCode == "session_not_abortable" &&
            abortFinalHandler.Requests.All(request => request.Method != HttpMethod.Post),
            "A no-todo turn with a successful final response was aborted.");
        FakeHandler finalHandler = CreateHandler();
        finalHandler.Respond = (request, _) => request.Method == HttpMethod.Post
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))
            : Task.FromResult(JsonResponse(RouteJson(request, status: "absent", messages: SuccessfulNoTodoMessages(), todos: "[]")));
        using var finalHttp = new HttpClient(finalHandler);
        using var finalClient = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, finalHttp);
        OpenCodeRecoveryContinueResult successfulFinal = await finalClient.ContinueAsync(
            RootSessionId, "Do not restart a completed turn", OpenCodeRecoveryClient.CreateMessageId(),
            expectedUserMessageId: "msg_current_user", allowUntrackedWork: true);
        Require(!successfulFinal.IsAccepted && successfulFinal.ErrorCode == "session_not_ready" &&
            finalHandler.Requests.All(request => request.Method != HttpMethod.Post),
            "A no-todo turn with a successful final response was restarted.");
    }

    private static async Task VerifyActivityFingerprintPreflightRacesAsync()
    {
        string messages = ActivityTurnMessages("unchanged", "", "", "", "");
        int readsSinceBaseline = -1;
        FakeHandler continueHandler = CreateHandler();
        continueHandler.Respond = (request, _) =>
        {
            if (request.Uri.AbsolutePath == $"/session/{RootSessionId}/message")
            {
                readsSinceBaseline++;
                messages = ActivityTurnMessages(readsSinceBaseline >= 2 ? "new activity" : "unchanged", "", "", "", "");
            }
            if (request.Uri.AbsolutePath.StartsWith($"/session/{RootSessionId}/message/msg_", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(JsonResponse(RouteJson(request, status: "absent", messages: messages, todos: "[]")));
        };
        using var continueHttp = new HttpClient(continueHandler);
        using var continueClient = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, continueHttp);
        RecoverySessionEvidence continueBaseline = (await continueClient.ReadAsync(RootSessionId)).Evidence!;
        readsSinceBaseline = 0;
        OpenCodeRecoveryContinueResult continuation = await continueClient.ContinueAsync(
            RootSessionId, "Continue only unchanged work", OpenCodeRecoveryClient.CreateMessageId(),
            expectedUserMessageId: "msg_current_user", allowUntrackedWork: true,
            expectedActivityFingerprint: continueBaseline.ActivityFingerprint);
        Require(!continuation.IsAccepted && continuation.ErrorCode == "progress_changed" &&
            continueHandler.Requests.All(request => request.Method != HttpMethod.Post),
            "A changed current-turn activity fingerprint did not block the continuation preflight.");

        messages = ActivityTurnMessages("unchanged", "", "", "", "");
        readsSinceBaseline = -1;
        FakeHandler abortHandler = CreateHandler();
        abortHandler.Respond = (request, _) =>
        {
            if (request.Uri.AbsolutePath == $"/session/{RootSessionId}/message")
            {
                readsSinceBaseline++;
                messages = ActivityTurnMessages(readsSinceBaseline >= 2 ? "new activity" : "unchanged", "", "", "", "");
            }
            if (request.Method == HttpMethod.Post)
                return Task.FromResult(JsonResponse("true"));
            return Task.FromResult(JsonResponse(RouteJson(request, status: "busy", messages: messages, todos: "[]")));
        };
        using var abortHttp = new HttpClient(abortHandler);
        using var abortClient = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, abortHttp);
        RecoverySessionEvidence abortBaseline = (await abortClient.ReadAsync(RootSessionId)).Evidence!;
        readsSinceBaseline = 0;
        OpenCodeRecoveryActionResult abort = await abortClient.AbortAsync(
            RootSessionId, expectedUserMessageId: "msg_current_user", allowUntrackedWork: true,
            expectedActivityFingerprint: abortBaseline.ActivityFingerprint);
        Require(!abort.IsAccepted && abort.ErrorCode == "progress_changed" &&
            abortHandler.Requests.All(request => request.Method != HttpMethod.Post),
            "A changed current-turn activity fingerprint did not block the abort preflight.");
    }

    private static string ActivityTurnMessages(string text, string reasoning, string toolStatus, string input, string output)
    {
        string tool = string.IsNullOrEmpty(toolStatus) ? "" : ", {\"type\":\"tool\",\"tool\":\"bash\",\"state\":{\"status\":" + JsonSerializer.Serialize(toolStatus) +
            ",\"input\":" + (input.Length == 0 ? "null" : input) + ",\"output\":" + JsonSerializer.Serialize(output) + "}}";
        string reasoningPart = reasoning.Length == 0 ? "" : ", {\"type\":\"reasoning\",\"text\":" + JsonSerializer.Serialize(reasoning) + "}";
        return "[{\"info\":{\"id\":\"msg_current_user\",\"sessionID\":\"" + RootSessionId + "\",\"role\":\"user\",\"time\":{\"created\":1000}},\"parts\":[{\"type\":\"text\",\"text\":\"request\"}]}," +
            "{\"info\":{\"id\":\"msg_current_assistant\",\"sessionID\":\"" + RootSessionId + "\",\"parentID\":\"msg_current_user\",\"role\":\"assistant\",\"time\":{\"created\":2000}},\"parts\":[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(text) + "}" + reasoningPart + tool + "]}]";
    }

    private static string SuccessfulNoTodoMessages() =>
        "[{\"info\":{\"id\":\"msg_current_user\",\"sessionID\":\"" + RootSessionId + "\",\"role\":\"user\",\"time\":{\"created\":1000}},\"parts\":[{\"type\":\"text\",\"text\":\"request\"}]}," +
        "{\"info\":{\"id\":\"msg_current_assistant\",\"sessionID\":\"" + RootSessionId + "\",\"parentID\":\"msg_current_user\",\"role\":\"assistant\",\"finish\":\"stop\",\"time\":{\"created\":2000,\"completed\":3000}},\"parts\":[{\"type\":\"text\",\"text\":\"verified done\"}]}]";
    private static async Task VerifyExpectedTurnAndLastMomentGuardAsync()
    {
        FakeHandler handler = CreateHandler();
        handler.Respond = async (request, cancellationToken) =>
        {
            if (request.Uri.AbsolutePath.StartsWith($"/session/{RootSessionId}/message/", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Post)
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            return JsonResponse(RouteJson(request, status: "absent", messages: ReadyMessagesJson()));
        };
        using var http = new HttpClient(handler);
        using var client = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, http);

        OpenCodeRecoveryContinueResult wrongTurn = await client.ContinueAsync(
            RootSessionId,
            "This must not send",
            OpenCodeRecoveryClient.CreateMessageId(),
            expectedUserMessageId: "msg_other_turn");
        Require(!wrongTurn.IsAccepted && wrongTurn.ErrorCode == "user_message_changed", "A changed latest user message did not disarm recovery.");

        OpenCodeRecoveryContinueResult guardChanged = await client.ContinueAsync(
            RootSessionId,
            "This must not send either",
            OpenCodeRecoveryClient.CreateMessageId(),
            expectedUserMessageId: "msg_user_2",
            canSend: () => false);
        Require(!guardChanged.IsAccepted && guardChanged.ErrorCode == "recovery_guard_changed", "The final recovery ownership guard was ignored.");
        Require(handler.Requests.All(request => request.Method != HttpMethod.Post), "A changed turn or false last-moment guard caused a prompt POST.");
    }

    private static async Task VerifyOnlyExpectedAbortMarkerCanBeClearedAsync()
    {
        string messageId = OpenCodeRecoveryClient.CreateMessageId();
        string messages = ReadyMessagesJson().Replace("msg_assistant_2", "msg_assistant_2", StringComparison.Ordinal)
            .Replace("last assistant response", "aborted", StringComparison.Ordinal)
            .Replace("\"role\":\"assistant\",\"time\":{\"created\":4000}", "\"role\":\"assistant\",\"time\":{\"created\":4000},\"error\":{\"name\":\"MessageAbortedError\"}", StringComparison.Ordinal);
        FakeHandler handler = CreateHandler();
        handler.Respond = (request, _) =>
        {
            if (request.Uri.AbsolutePath.StartsWith($"/session/{RootSessionId}/message/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (request.Method == HttpMethod.Post)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            return Task.FromResult(JsonResponse(RouteJson(request, status: "absent", messages: messages)));
        };
        using var http = new HttpClient(handler);
        using var client = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, http);

        OpenCodeRecoveryContinueResult result = await client.ContinueAsync(
            RootSessionId,
            "Continue after our confirmed abort",
            messageId,
            expectedUserMessageId: "msg_user_2",
            allowAbortedTurn: true);
        Require(result.IsAccepted, "An explicitly allowed, exact MessageAbortedError was not resumed.");

        FakeHandler unrelatedHandler = CreateHandler();
        unrelatedHandler.Respond = (request, _) =>
        {
            if (request.Uri.AbsolutePath.StartsWith($"/session/{RootSessionId}/message/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (request.Method == HttpMethod.Post)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            string unrelated = messages.Replace("MessageAbortedError", "ProviderError", StringComparison.Ordinal);
            return Task.FromResult(JsonResponse(RouteJson(request, status: "absent", messages: unrelated)));
        };
        using var unrelatedHttp = new HttpClient(unrelatedHandler);
        using var unrelatedClient = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, unrelatedHttp);
        OpenCodeRecoveryContinueResult rejected = await unrelatedClient.ContinueAsync(
            RootSessionId,
            "This error must remain visible",
            OpenCodeRecoveryClient.CreateMessageId(),
            expectedUserMessageId: "msg_user_2",
            allowAbortedTurn: true);
        Require(!rejected.IsAccepted && rejected.ErrorCode == "session_not_ready", "The aborted-turn exception suppressed a different error type.");
        Require(unrelatedHandler.Requests.All(request => request.Method != HttpMethod.Post), "An unrelated error type caused a prompt POST.");

        string appLaunchInput = "{\"command\":\"dotnet run --project app-fixture/App.csproj\"}";
        string launchFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes("shell\n" + appLaunchInput)));
        string mixedMessageAndToolErrors = messages.Replace(
            "\"text\":\"aborted\"}]",
            "\"text\":\"aborted\"},{\"type\":\"tool\",\"tool\":\"shell\",\"state\":{\"status\":\"error\",\"input\":{\"command\":\"unrelated\"},\"output\":\"permission denied\"}}]",
            StringComparison.Ordinal);
        await RequireAbortErrorSetRejectedAsync(mixedMessageAndToolErrors, expectedLaunchFingerprint: null,
            "A MessageAbortedError hid an unrelated tool error.");

        string abortAndProviderErrors = messages.Replace(
            "\"text\":\"aborted\"}]",
            "\"text\":\"aborted\"},{\"type\":\"tool\",\"tool\":\"shell\",\"state\":{\"status\":\"error\",\"input\":" + appLaunchInput + ",\"output\":\"Tool execution aborted\"}},{\"type\":\"tool\",\"tool\":\"shell\",\"state\":{\"status\":\"error\",\"input\":{\"command\":\"unrelated\"},\"output\":\"provider failed\"}}]",
            StringComparison.Ordinal);
        await RequireAbortErrorSetRejectedAsync(abortAndProviderErrors, launchFingerprint,
            "An expected abort tool error hid a second unrelated tool error.");
    }

    private static async Task RequireAbortErrorSetRejectedAsync(string messages, string? expectedLaunchFingerprint, string failureMessage)
    {
        FakeHandler handler = CreateHandler();
        handler.Respond = (request, _) =>
        {
            if (request.Uri.AbsolutePath.StartsWith($"/session/{RootSessionId}/message/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (request.Method == HttpMethod.Post)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            return Task.FromResult(JsonResponse(RouteJson(request, status: "absent", messages: messages)));
        };
        using var http = new HttpClient(handler);
        using var client = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, http);
        OpenCodeRecoveryContinueResult rejected = await client.ContinueAsync(
            RootSessionId,
            "This mixed error set must remain visible",
            OpenCodeRecoveryClient.CreateMessageId(),
            expectedUserMessageId: "msg_user_2",
            allowAbortedTurn: true,
            allowUntrackedWork: true,
            expectedStalledLaunchToolFingerprint: expectedLaunchFingerprint);
        Require(!rejected.IsAccepted && rejected.ErrorCode == "session_not_ready", failureMessage);
        Require(handler.Requests.All(request => request.Method != HttpMethod.Post), "A mixed abort/unrelated error set caused a prompt POST.");
    }

    private static async Task VerifyIncompleteTodoAndAbortGuardsAsync()
    {
        FakeHandler completed = CreateHandler();
        completed.Respond = (request, _) => Task.FromResult(JsonResponse(RouteJson(
            request,
            status: "absent",
            messages: ReadyMessagesJson(),
            todos: "[{\"content\":\"done\",\"status\":\"completed\",\"priority\":\"normal\"}]")));
        using var completedHttp = new HttpClient(completed);
        using var completedClient = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, completedHttp);
        OpenCodeRecoveryContinueResult noTodos = await completedClient.ContinueAsync(
            RootSessionId,
            "Do not send after all todos are done",
            OpenCodeRecoveryClient.CreateMessageId(),
            expectedUserMessageId: "msg_user_2");
        Require(!noTodos.IsAccepted && noTodos.ErrorCode == "session_not_ready", "A session with no incomplete todos was resumed.");

        FakeHandler abort = CreateHandler();
        abort.Respond = (request, _) => Task.FromResult(JsonResponse(RouteJson(
            request,
            status: "busy",
            messages: ReadyMessagesJson(),
            todos: "[{\"content\":\"done\",\"status\":\"completed\",\"priority\":\"normal\"}]")));
        using var abortHttp = new HttpClient(abort);
        using var abortClient = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, abortHttp);
        OpenCodeRecoveryActionResult notAbortable = await abortClient.AbortAsync(RootSessionId, expectedUserMessageId: "msg_user_2");
        Require(!notAbortable.IsAccepted && notAbortable.ErrorCode == "session_not_abortable", "Abort ignored the incomplete-todo requirement.");
        Require(abort.Requests.All(request => request.Method != HttpMethod.Post), "An abort POST was sent after all todos were complete.");
    }

    private static async Task VerifyAbortUsesNativeSameSessionEndpointAsync()
    {
        FakeHandler handler = CreateHandler();
        handler.Respond = (request, _) => request.Method == HttpMethod.Post
            ? Task.FromResult(JsonResponse("true"))
            : Task.FromResult(JsonResponse(RouteJson(request, status: "busy", messages: ReadyMessagesJson())));
        using var http = new HttpClient(handler);
        using var client = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, http);

        OpenCodeRecoveryActionResult result = await client.AbortAsync(RootSessionId);
        Require(result.IsAccepted, "The native abort response was not accepted.");
        Require(handler.Requests.Count(request => request.Method == HttpMethod.Post) == 1 && handler.Requests.Any(request => request.Uri.AbsolutePath == $"/session/{RootSessionId}/abort"), "Abort did not use the exact native session endpoint.");
    }

    private static async Task VerifyProgressFingerprintRaceGatesAsync()
    {
        FakeHandler continueHandler = CreateHandler();
        int messageReads = 0;
        continueHandler.Respond = (request, _) =>
        {
            string path = request.Uri.AbsolutePath;
            if (path.StartsWith($"/session/{RootSessionId}/message/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (path == $"/session/{RootSessionId}/message")
            {
                messageReads++;
                string current = messageReads >= 3
                    ? ReadyMessagesWithToolProgressJson()
                    : ReadyMessagesJson();
                return Task.FromResult(JsonResponse(RouteJson(request, status: "absent", messages: current)));
            }
            return Task.FromResult(JsonResponse(RouteJson(request, status: "absent", messages: ReadyMessagesJson())));
        };
        using var continueHttp = new HttpClient(continueHandler);
        using var continueClient = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, continueHttp);
        OpenCodeRecoveryReadResult baseline = await continueClient.ReadAsync(RootSessionId);
        Require(baseline.IsAvailable && baseline.Evidence is not null, "Could not establish continuation fingerprint baseline.");
        OpenCodeRecoveryContinueResult continuation = await continueClient.ContinueAsync(
            RootSessionId,
            "Continue only if the approved progress is still current",
            OpenCodeRecoveryClient.CreateMessageId(),
            expectedUserMessageId: "msg_user_2",
            expectedProgressFingerprint: baseline.Evidence!.ProgressFingerprint);
        Require(!continuation.IsAccepted && continuation.ErrorCode == "progress_changed", "Continue did not reject progress that changed during message-ID preflight.");
        Require(continueHandler.Requests.All(request => request.Method != HttpMethod.Post), "Continue POST was sent after its approved progress fingerprint changed.");

        FakeHandler abortHandler = CreateHandler();
        bool changed = false;
        abortHandler.Respond = (request, _) =>
        {
            if (request.Uri.AbsolutePath == $"/session/{RootSessionId}/message")
            {
                string current = changed
                    ? ReadyMessagesWithToolProgressJson()
                    : ReadyMessagesJson();
                return Task.FromResult(JsonResponse(RouteJson(request, status: "busy", messages: current)));
            }
            return Task.FromResult(JsonResponse(RouteJson(request, status: "busy", messages: ReadyMessagesJson())));
        };
        using var abortHttp = new HttpClient(abortHandler);
        using var abortClient = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, abortHttp);
        OpenCodeRecoveryReadResult abortBaseline = await abortClient.ReadAsync(RootSessionId);
        Require(abortBaseline.IsAvailable && abortBaseline.Evidence is not null, "Could not establish abort fingerprint baseline.");
        changed = true;
        OpenCodeRecoveryActionResult abort = await abortClient.AbortAsync(
            RootSessionId,
            expectedUserMessageId: "msg_user_2",
            expectedProgressFingerprint: abortBaseline.Evidence!.ProgressFingerprint);
        Require(!abort.IsAccepted && abort.ErrorCode == "progress_changed", "Abort did not reject progress newer than the controller-approved fingerprint.");
        Require(abortHandler.Requests.All(request => request.Method != HttpMethod.Post), "Abort POST was sent after substantive progress changed.");
    }

    private static async Task VerifyAbortResponseBodyTimeoutIsBoundedAsync()
    {
        FakeHandler handler = CreateHandler();
        handler.Respond = (request, _) => request.Method == HttpMethod.Post
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HangingReadStream()) })
            : Task.FromResult(JsonResponse(RouteJson(request, status: "busy", messages: ReadyMessagesJson())));
        using var http = new HttpClient(handler);
        using var client = new OpenCodeRecoveryClient(ServerUri, ProjectDirectory, http);

        DateTimeOffset started = DateTimeOffset.UtcNow;
        OpenCodeRecoveryActionResult result = await client.AbortAsync(RootSessionId, expectedUserMessageId: "msg_user_2");
        TimeSpan elapsed = DateTimeOffset.UtcNow - started;
        Require(result.Outcome == OpenCodeRecoveryOutcome.Ambiguous, "A hanging abort response body was not treated as ambiguous.");
        Require(elapsed < TimeSpan.FromSeconds(6), "A hanging abort response body exceeded the request deadline.");
        Require(handler.Requests.Count(request => request.Method == HttpMethod.Post) == 1, "A timed-out abort was resent.");
    }

    private static FakeHandler CreateHandler() => new()
    {
        Respond = (request, _) => Task.FromResult(JsonResponse(RouteJson(request)))
    };

    private static string RouteJson(
        CapturedRequest request,
        string? status = "busy",
        string? children = "[]",
        string? permissions = "[]",
        string? questions = "[]",
        bool includeRootInList = true,
        bool duplicateRootInList = false,
        string? detailDirectory = null,
        string? messages = null,
        string? todos = null)
    {
        string session = SessionJson(RootSessionId, detailDirectory ?? ProjectDirectory, parentId: null);
        if (!includeRootInList)
            session = "[]";
        else if (duplicateRootInList)
            session = "[" + SessionJson(RootSessionId, detailDirectory ?? ProjectDirectory, parentId: null) + "," + SessionJson(RootSessionId, detailDirectory ?? ProjectDirectory, parentId: null) + "]";
        else
            session = "[" + session + "]";

        string path = request.Uri.AbsolutePath;
        if (path == "/session") return session;
        if (path == $"/session/{RootSessionId}") return SessionJson(RootSessionId, detailDirectory ?? ProjectDirectory, parentId: null);
        if (path == $"/session/{RootSessionId}/children") return children ?? "[]";
        if (path.StartsWith("/session/", StringComparison.Ordinal) && path.EndsWith("/children", StringComparison.Ordinal)) return "[]";
        if (path == "/session/status")
        {
            if (status == "absent") return "{}";
            if (status == "retry") return "{\"" + RootSessionId + "\":{\"type\":\"retry\",\"attempt\":3,\"message\":\"Provider temporarily unavailable\",\"next\":123456789}}";
            return "{\"" + RootSessionId + "\":{\"type\":\"" + status + "\"},\"ses_unrelated\":{\"type\":\"busy\"}}";
        }
        if (path == $"/session/{RootSessionId}/todo") return todos ?? "[{\"content\":\"first\",\"status\":\"completed\",\"priority\":\"high\"},{\"content\":\"second\",\"status\":\"pending\",\"priority\":\"normal\"}]";
        if (path == $"/session/{RootSessionId}/message") return messages ?? MessagesJson();
        if (path == "/permission") return permissions ?? "[]";
        if (path == "/question") return questions ?? "[]";
        return "{}";
    }

    private static string SessionJson(string id, string directory, string? parentId) =>
        "{\"id\":\"" + id + "\",\"directory\":\"" + EscapeJson(directory) + "\",\"parentID\":" +
        (parentId is null ? "null" : "\"" + parentId + "\"") + ",\"time\":{\"created\":1000,\"updated\":2000}}";

    private static string MessagesJson() =>
        "[{\"info\":{\"id\":\"msg_user_1\",\"sessionID\":\"" + RootSessionId + "\",\"role\":\"user\",\"time\":{\"created\":1000}},\"parts\":[{\"type\":\"text\",\"text\":\"historic user prompt\"}]}," +
        "{\"info\":{\"id\":\"msg_assistant_1\",\"sessionID\":\"" + RootSessionId + "\",\"role\":\"assistant\",\"finish\":\"stop\",\"time\":{\"created\":2000,\"completed\":2500}},\"parts\":[{\"type\":\"text\",\"text\":\"intermediate response\"}]}," +
        "{\"info\":{\"id\":\"msg_user_2\",\"sessionID\":\"" + RootSessionId + "\",\"role\":\"user\",\"time\":{\"created\":3000}},\"parts\":[{\"type\":\"text\",\"text\":\"latest user prompt\"}]}," +
        "{\"info\":{\"id\":\"msg_assistant_2\",\"sessionID\":\"" + RootSessionId + "\",\"role\":\"assistant\",\"time\":{\"created\":4000},\"error\":{\"name\":\"ProviderError\"}},\"parts\":[{\"type\":\"text\",\"text\":\"current assistant progress\"},{\"type\":\"tool\",\"tool\":\"bash\",\"state\":{\"status\":\"running\",\"input\":{\"command\":\"build\"}}}]}]";

    private static string ReadyMessagesJson() =>
        "[{\"info\":{\"id\":\"msg_user_2\",\"sessionID\":\"" + RootSessionId + "\",\"role\":\"user\",\"time\":{\"created\":3000}},\"parts\":[{\"type\":\"text\",\"text\":\"existing user task\"}]}," +
        "{\"info\":{\"id\":\"msg_assistant_2\",\"sessionID\":\"" + RootSessionId + "\",\"role\":\"assistant\",\"time\":{\"created\":4000}},\"parts\":[{\"type\":\"text\",\"text\":\"last assistant response\"}]}]";

    private static string ReadyMessagesWithToolProgressJson() =>
        ReadyMessagesJson().Replace("{\"type\":\"text\",\"text\":\"last assistant response\"}",
            "{\"type\":\"text\",\"text\":\"last assistant response\"},{\"type\":\"tool\",\"tool\":\"bash\",\"state\":{\"status\":\"completed\",\"title\":\"build\",\"input\":{\"command\":\"dotnet test\"}}}",
            StringComparison.Ordinal);

    private static string EscapeJson(string value) => JsonSerializer.Serialize(value)[1..^1];

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static void VerifyServerNamedWaits()
    {
        static TimeSpan? Wait(string? text) => OpenCodeFailureClassifier.ServerRequestedWait(text);
        Require(Wait("요청이 많습니다. 60-120초 후 다시 시도하세요.") == TimeSpan.FromSeconds(120), "A Korean range did not use its upper end.");
        Require(Wait("Too many requests, retry after 60~120 seconds") == TimeSpan.FromSeconds(120), "An English range did not use its upper end.");
        Require(Wait("요청 제한: 60초~120초 대기") == TimeSpan.FromSeconds(120), "A range with a unit on both ends was misread.");
        Require(Wait("Rate limit exceeded. Try again in 2 minutes.") == TimeSpan.FromMinutes(2), "Minutes were misread.");
        Require(Wait("rate_limit: please wait 1.5분") == TimeSpan.FromSeconds(90), "A decimal minute was misread.");
        Require(Wait("429 Too Many Requests: retry in 500ms") == TimeSpan.FromMilliseconds(500), "Milliseconds were misread.");
        Require(Wait("Rate limited. Retry after 86400 seconds") == OpenCodeFailureClassifier.LongestNamedWait, "A named wait was not capped.");
        Require(Wait("Too many requests") is null && OpenCodeFailureClassifier.LooksLikeRateLimit("Too many requests"),
            "A rate limit without a time named one, or was not recognised.");
        Require(Wait("Request timed out after 600 seconds") is null, "A timeout without a rate-limit word was taken as a server wait.");
        Require(Wait("insufficient_quota: rate limit, retry after 60 seconds") is null, "A billing problem was taken as a wait.");
        Require(Wait("provider status update 3") is null && Wait(null) is null && Wait("") is null, "Plain status text named a wait.");
        Require(Wait("Run it only as a bounded 60–120 second smoke check.") is null, "A prompt without a rate-limit word named a wait.");

        // Only the time next to retry wording is the wait; quota windows are not (they held a terminal an hour each time).
        Require(Wait("Rate limit: 100 requests per 1 hour") is null, "A quota window was read as a wait.");
        Require(Wait("시간당 100회 요청 제한(1시간 기준)을 초과했습니다. 30분 후 다시 시도하세요.") == TimeSpan.FromMinutes(30),
            "A Korean quota window was read instead of the retry time.");
        Require(Wait("Rate limit of 60 requests per 1 minute exceeded, retry in 20 seconds") == TimeSpan.FromSeconds(20),
            "An English quota window was read instead of the retry time.");
        Require(Wait("요청 제한: 일일 한도 초과 (24시간마다 초기화)") is null, "A daily reset window was read as a wait.");
        Require(Wait("요청이 많아 잠시 후 다시 시도해 주세요 (예상 대기 시간: 90초)") == TimeSpan.FromSeconds(90), "A named waiting time was missed.");
        Require(Wait("rate limited, 60초 뒤에 다시 요청하세요") == TimeSpan.FromSeconds(60), "'뒤' was not read as a wait.");

        // The hold is the named time and a margin, the same day and night and on every repeat.
        TimeSpan named = TimeSpan.FromSeconds(120), margin = TimeSpan.FromSeconds(5);
        Require(OpenCodeFailureClassifier.ServerWaitHold(named) == named + margin &&
            OpenCodeFailureClassifier.ServerWaitHold(TimeSpan.FromMinutes(30)) == TimeSpan.FromMinutes(30) + margin,
            "A hold was not the server's named time.");

        // An overflowing context reported only in words is compacted like a structured overflow.
        static bool Overflow(string? text) => OpenCodeFailureClassifier.LooksLikeContextOverflow(text);
        Require(Overflow("This model's maximum context length is 32768 tokens. However, you requested 40000 tokens.") &&
            Overflow("the request exceeds the available context size, try increasing it") &&
            Overflow("input length exceeds the context length") &&
            Overflow("prompt is too long: 210000 tokens > 200000 maximum") &&
            Overflow("Error code: context_length_exceeded") &&
            Overflow("입력이 모델의 컨텍스트 길이를 초과했습니다") && Overflow("컨텍스트 한도 초과"),
            "A context overflow in words was not recognised.");
        Require(!Overflow("Too many requests") && !Overflow("invalid request: model not found") && !Overflow("Unauthorized: bad api key") &&
            !Overflow("Rate limit reached for tokens per min") && !Overflow(null) && !Overflow(""),
            "Another error was taken for a context overflow.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Body, string? Authorization);

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<CapturedRequest> _requests = new();
        public Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        public IReadOnlyList<CapturedRequest> Requests => _requests.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var captured = new CapturedRequest(request.Method, request.RequestUri!, body, request.Headers.Authorization?.ToString());
            _requests.Enqueue(captured);
            return await Respond(captured, cancellationToken);
        }
    }

    private sealed class HangingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => WaitAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static async ValueTask<int> WaitAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
