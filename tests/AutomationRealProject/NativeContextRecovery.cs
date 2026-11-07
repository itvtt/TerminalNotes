using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerminalNotes.Services;

internal static class NativeContextRecovery
{
    // Explicit opt-in: --native-context-recovery uses the real native executable
    // and a localhost fake model. It supplies protocol/controller evidence only,
    // never real-model quality or overnight reliability evidence.
    private static readonly string Native = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"npm\node_modules\opencode-ai\node_modules\opencode-windows-x64\bin\opencode.exe");
    private const string Provider = "fixture";
    private const string Model = "context-fixture";
    private const string SummaryModel = "summary-fixture";
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static async Task<int> RunAsync()
    {
        string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string evidence = Path.Combine(project, "artifacts/context-recovery-20261015/native-local-fixture", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        string isolated = Path.Combine(evidence, "isolated");
        Directory.CreateDirectory(isolated);
        using var fixture = new ModelFixture(FreePort());
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        Process? native = null;
        int? nativeProcessId = null;
        var logs = new ConcurrentQueue<string>();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(isolated, "AGENTS.md"), "This is an isolated local protocol fixture. Use no tools. Return the requested text.");
            var config = new
            {
                model = Provider + "/" + Model, small_model = Provider + "/" + Model,
                enabled_providers = new[] { Provider }, autoupdate = false, snapshot = false,
                permission = "deny", compaction = new { auto = true, prune = false },
                agent = new { title = new { disable = true }, compaction = new { model = Provider + "/" + SummaryModel } },
                provider = new Dictionary<string, object>
                {
                    [Provider] = new
                    {
                        npm = "@ai-sdk/openai-compatible", name = "Local test fixture only",
                        options = new { baseURL = fixture.Uri.AbsoluteUri + "v1", apiKey = "fixture-no-credentials" },
                        models = new Dictionary<string, object>
                        {
                            [Model] = new { name = "Context fixture", limit = new { context = 65536, output = 2048 }, tool_call = false },
                            [SummaryModel] = new { name = "Summary fixture", limit = new { context = 131072, output = 2048 }, tool_call = false }
                        }
                    }
                }
            };
            string configPath = Path.Combine(isolated, "opencode.json");
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, Pretty));
            int port = FreePort();
            var start = new ProcessStartInfo(Native) { WorkingDirectory = isolated, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "serve", "--pure", "--hostname", "127.0.0.1", "--port", port.ToString(), "--print-logs", "--log-level", "DEBUG" }) start.ArgumentList.Add(arg);
            foreach (string key in start.Environment.Keys.Where(k => k.Contains("API_KEY", StringComparison.OrdinalIgnoreCase) || k.StartsWith("OPENCODE_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("AWS_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("AZURE_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("GOOGLE_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
            foreach (string kind in new[] { "CONFIG", "DATA", "STATE", "CACHE" }) start.Environment["XDG_" + kind + "_HOME"] = Path.Combine(isolated, kind.ToLowerInvariant());
            start.Environment["HOME"] = isolated;
            start.Environment["USERPROFILE"] = isolated;
            start.Environment["OPENCODE_CONFIG"] = configPath;
            start.Environment["OPENCODE_DISABLE_MODELS_FETCH"] = "true";
            start.Environment["OPENCODE_DISABLE_AUTOUPDATE"] = "true";
            start.Environment["OPENCODE_DISABLE_LSP_DOWNLOAD"] = "true";
            start.Environment["OPENCODE_DISABLE_CLAUDE_CODE"] = "true";
            start.Environment["OPENCODE_DISABLE_EXTERNAL_SKILLS"] = "true";
            start.Environment["OPENCODE_DISABLE_DEFAULT_PLUGINS"] = "true";
            // Prevent accidental network access by any model/metadata fallback.
            start.Environment["HTTP_PROXY"] = "http://127.0.0.1:1";
            start.Environment["HTTPS_PROXY"] = "http://127.0.0.1:1";
            start.Environment["ALL_PROXY"] = "http://127.0.0.1:1";
            start.Environment["NO_PROXY"] = "127.0.0.1,localhost";
            native = new Process { StartInfo = start };
            native.OutputDataReceived += (_, e) => { if (e.Data is not null) logs.Enqueue(e.Data); };
            native.ErrorDataReceived += (_, e) => { if (e.Data is not null) logs.Enqueue(e.Data); };
            Require(native.Start(), "Native process did not start.");
            nativeProcessId = native.Id;
            native.BeginOutputReadLine(); native.BeginErrorReadLine();
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(60) };
            await WaitReadyAsync(http, native, deadline.Token);
            JsonNode provider = await GetAsync(http, "provider", deadline.Token);
            await WriteAsync(evidence, "providers.json", provider);
            Require(provider["all"]!.AsArray().Count == 1 && provider["all"]![0]!["id"]!.GetValue<string>() == Provider, "Native provider list contains a nonfixture provider.");
            await VerifyManualCompactionAsync(http, fixture, isolated, evidence, deadline.Token);
            await VerifyOverflowAsync(http, fixture, isolated, evidence, seedHistory: false, deadline.Token);
            await VerifyOverflowAsync(http, fixture, isolated, evidence, seedHistory: true, deadline.Token);
            await VerifyControllerAsync(http, fixture, isolated, evidence, failSummary: false, deadline.Token);
            await VerifyControllerAsync(http, fixture, isolated, evidence, failSummary: true, deadline.Token);
            await VerifyLateBoundNativeAsync(http, fixture, isolated, evidence, deadline.Token);
            await VerifyOwnedHostRestartAsync(http, fixture, isolated, evidence, async () =>
            {
                native!.Kill(entireProcessTree: true);
                await native.WaitForExitAsync(deadline.Token);
                native.Dispose();
                native = StartNative(start, logs);
                nativeProcessId = native.Id;
                await WaitReadyAsync(http, native, deadline.Token);
            }, deadline.Token);
            await VerifyControllerAsync(http, fixture, isolated, evidence, failSummary: false, deadline.Token, streamOverload: true);
            await WriteAsync(evidence, "result.json", new { passed = true, nativeVersion = "1.18.34", scope = "real native plus localhost fake model; production recovery client/controller, exact final-response recognition, next Loop review and user-stop fence; no real-model quality claim", fakeModelRequestCount = fixture.Requests.Count, providerOnly = Provider });
            Console.WriteLine("PASS real native + localhost fake model integration: " + evidence);
            return 0;
        }
        catch (Exception ex)
        {
            await WriteAsync(evidence, "failure.json", new { error = ex.ToString() });
            Console.Error.WriteLine("FAIL " + ex + " Evidence: " + evidence);
            return 1;
        }
        finally
        {
            if (nativeProcessId is not null && native is { HasExited: false }) { native.Kill(entireProcessTree: true); await native.WaitForExitAsync(); }
            await WriteAsync(evidence, "cleanup.json", new { processId = nativeProcessId, ownNativeProcessExited = nativeProcessId is null || native!.HasExited, unrelatedProcessesTouched = false });
            native?.Dispose();
            await File.WriteAllLinesAsync(Path.Combine(evidence, "native.log"), logs);
            await WriteAsync(evidence, "model-requests.json", fixture.Requests.ToArray());
        }
    }

    private static async Task VerifyManualCompactionAsync(HttpClient http, ModelFixture fixture, string isolated, string evidence, CancellationToken token)
    {
        JsonNode createdSession = await PostAsync(http, "session", new { title = "manual compaction fixture" }, token);
        Require(createdSession["version"]?.GetValue<string>() == "1.18.34", "This fixture requires installed OpenCode 1.18.34.");
        string session = createdSession["id"]!.GetValue<string>();
        await PostAsync(http, $"session/{session}/message", Prompt("Original pinned request: preserve the requested multiline task.\nImplement the fixture and verify it."), token);
        await WriteAsync(evidence, "manual-before.json", await GetAsync(http, $"session/{session}/message", token));
        fixture.FailNext = true;
        JsonNode failedResponse = await PostAsync(http, $"session/{session}/summarize", new { providerID = Provider, modelID = Model, auto = false }, token);
        JsonNode failed = await GetAsync(http, $"session/{session}/message", token);
        await WriteAsync(evidence, "manual-failed.json", new { response = failedResponse, messages = failed });
        Require(failedResponse.ToJsonString() == "true", "Expected native summarize to return true even on summary model failure.");
        Require(failed.AsArray().Any(m => m?["info"]?["summary"]?.GetValue<bool>() == true && m["info"]?["error"]?["name"]?.GetValue<string>() == "ContextOverflowError"), "Native provider overflow was not normalized to a persisted summary ContextOverflowError.");
        await PostAsync(http, $"session/{session}/summarize", new { providerID = Provider, modelID = Model, auto = false }, token);
        JsonNode compacted = await GetAsync(http, $"session/{session}/message", token);
        await WriteAsync(evidence, "manual-compacted.json", compacted);
        Require(compacted.AsArray().Count(m => m?["info"]?["role"]?.GetValue<string>() == "user" && m["parts"]!.AsArray().Any(p => p?["type"]?.GetValue<string>() == "compaction")) >= 2, "Fresh compaction user markers missing after retry.");
        Require(compacted.AsArray().Any(m => m?["info"]?["summary"]?.GetValue<bool>() == true && m["info"]?["error"] is null && m["info"]?["finish"]?.GetValue<string>() == "stop"), "Successful persisted summary missing.");
        Require(compacted.AsArray().Where(m => m?["info"]?["summary"]?.GetValue<bool>() == true).All(m => m!["info"]!["modelID"]!.GetValue<string>() == SummaryModel), "Native compaction agent model did not override request model.");
        using var client = new OpenCodeRecoveryClient(http.BaseAddress!, isolated);
        var read = await client.ReadAsync(session, cancellationToken: token);
        await WriteAsync(evidence, "manual-production-client-read.json", read);
        Require(read.IsAvailable, "Production client could not read native session after compaction: " + read.Error);
        await PostAsync(http, $"session/{session}/message", Prompt("Continue the original pinned request after confirmed compaction."), token);
        JsonNode continued = await GetAsync(http, $"session/{session}/message", token);
        await WriteAsync(evidence, "manual-continued.json", continued);
        Require(continued.AsArray().Last()?["info"]?["finish"]?.GetValue<string>() == "stop", "Native continuation did not finish.");
    }

    private static async Task VerifyOverflowAsync(HttpClient http, ModelFixture fixture, string isolated, string evidence, bool seedHistory, CancellationToken token)
    {
        string session = (await PostAsync(http, "session", new { title = "context overflow fixture" }, token))["id"]!.GetValue<string>();
        if (seedHistory) await PostAsync(http, $"session/{session}/message", Prompt("Earlier completed request, unrelated to the pinned request."), token);
        fixture.FailNext = true;
        object prompt = Prompt("Original overflow request\nPreserve this exact task across native recovery.");
        string originalId = JsonSerializer.SerializeToNode(prompt)!["messageID"]!.GetValue<string>();
        await PostAsync(http, $"session/{session}/message", prompt, token);
        JsonNode messages = await GetAsync(http, $"session/{session}/message", token);
        string label = seedHistory ? "overflow-replay" : "overflow-synthetic";
        await WriteAsync(evidence, label + "-messages.json", messages);
        Require(messages.AsArray().Any(m => m?["parts"]!.AsArray().Any(p => p?["type"]?.GetValue<string>() == "compaction" && p["auto"]?.GetValue<bool>() == true && p["overflow"]?.GetValue<bool>() == true) == true), "Native overflow did not produce automatic compaction marker.");
        Require(messages.AsArray().Any(m => m?["info"]?["summary"]?.GetValue<bool>() == true && m["info"]?["finish"]?.GetValue<string>() == "stop"), "Native automatic summary failed.");
        if (seedHistory)
        {
            Require(messages.AsArray().Count(m => m?["parts"]!.AsArray().Any(p => p?["metadata"]?["terminalnotes"]?["requestID"]?.GetValue<string>() == originalId) == true) == 2, "Native replay did not retain the original request's opaque metadata marker.");
        }
        else Require(messages.AsArray().Any(m => m?["parts"]!.AsArray().Any(p => p?["synthetic"]?.GetValue<bool>() == true && p["metadata"]?["compaction_continue"]?.GetValue<bool>() == true) == true), "Native generated continuation marker missing.");
        Require(messages.AsArray().Last()?["info"]?["finish"]?.GetValue<string>() == "stop", "Native did not finish continuation automatically.");
        using var client = new OpenCodeRecoveryClient(http.BaseAddress!, isolated);
        await WriteAsync(evidence, label + "-production-client-read.json", await client.ReadAsync(session, cancellationToken: token));
    }

    private static object Prompt(string text)
    {
        string messageID = OpenCodeRecoveryClient.CreateMessageId();
        return new { messageID, model = new { providerID = Provider, modelID = Model }, parts = new[] { new { type = "text", text, metadata = new { terminalnotes = new { requestID = messageID } } } } };
    }

    private static async Task VerifyControllerAsync(HttpClient http, ModelFixture fixture, string isolated, string evidence, bool failSummary, CancellationToken token, bool streamOverload = false)
    {
        string label = streamOverload ? "controller-stream503" : failSummary ? "controller-summary-retry" : "controller-native-replay";
        string session = (await PostAsync(http, "session", new { title = label }, token))["id"]!.GetValue<string>();
        await PostAsync(http, $"session/{session}/message", Prompt("Earlier completed request; the next request is independent."), token);
        string plan = Path.Combine(isolated, label + "-plan.md");
        const string instruction = "Original controller request\nPreserve this multiline instruction across all native compaction and replay chains.";
        await File.WriteAllTextAsync(plan, "# Native local fixture\n" + instruction, token);
        using var recorder = new RecordingHandler();
        using var clientHttp = new HttpClient(recorder) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new OpenCodeRecoveryClient(http.BaseAddress!, isolated, clientHttp);
        var clock = new FixtureClock();
        using var controller = new OpenCodeAutoResumeController(isolated, plan, client, () => true, clock);
        using var eventStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var eventResponse = streamOverload ? await http.GetAsync("event", HttpCompletionOption.ResponseHeadersRead, token) : null;
        var nativeEvents = new ConcurrentQueue<JsonNode>();
        Task eventRead = eventResponse is null ? Task.CompletedTask : CaptureEventsAsync(eventResponse, nativeEvents, eventStop.Token);
        var diagnostics = new ConcurrentQueue<object>();
        controller.DiagnosticEmitted += diagnostic => diagnostics.Enqueue(diagnostic);
        controller.SetEnabled(true);
        object prompt = Prompt(instruction);
        string original = JsonSerializer.SerializeToNode(prompt)!["messageID"]!.GetValue<string>();
        controller.ObserveSubmittedPrompt(instruction, session, original);
        fixture.FailureCount = streamOverload ? 0 : failSummary ? 2 : 1;
        fixture.StreamOverloadNext = streamOverload;
        fixture.HttpOverloadCount = streamOverload ? 1 : 0;
        TaskCompletionSource? releaseStreamError = streamOverload ? fixture.HoldStreamError() : null;
        var release = fixture.HoldNextRequest();
        Task<JsonNode> send = PostAsync(http, $"session/{session}/message", prompt, token);
        try
        {
            await fixture.RequestHeld!.Task.WaitAsync(token);
            controller.ConfirmApiSubmission(session, original);
            for (int i = 0; i < 80 && controller.VerifiedContinuationSessionId != session; i++)
            { await controller.TickAsync(); await Task.Delay(25, token); }
            Require(controller.VerifiedContinuationSessionId == session, "Production controller did not bind native original request: " + controller.Status);
            release.TrySetResult();
            if (streamOverload)
            {
                await fixture.PartialStreamSent!.Task.WaitAsync(token);
                await Task.Delay(700, token);
                await controller.TickAsync();
                JsonNode partial = await GetAsync(http, $"session/{session}/message", token);
                await WriteAsync(evidence, label + "-partial.json", partial);
                await WriteAsync(evidence, label + "-partial-read.json", await client.ReadAsync(session, token));
                Require(!controller.HasVerifiedCompletion && partial.AsArray().Last()?["info"]?["finish"]?.GetValue<string>() != "stop", "An unfinished partial stream was accepted as completion.");
                await WriteAsync(evidence, label + "-partial-events.json", nativeEvents.ToArray());
                Require(nativeEvents.Any(e => e.ToJsonString().Contains("Fixture partial response before backend overload.", StringComparison.Ordinal)), "Native did not emit the partial assistant text before the overload.");
                releaseStreamError!.TrySetResult();
                for (int i = 0; i < 80 && !send.IsCompleted; i++)
                {
                    var observed = await client.ReadAsync(session, token);
                    if (observed.Evidence?.Status == OpenCodeRecoveryStatus.Retry)
                    {
                        await WriteAsync(evidence, label + "-native-retry-read.json", observed);
                        break;
                    }
                    await Task.Delay(20, token);
                }
            }
            await send;
            JsonNode initial = await GetAsync(http, $"session/{session}/message", token);
            await WriteAsync(evidence, label + "-before-ticks.json", initial);
            for (int i = 0; i < (streamOverload ? 60 : 20); i++)
            {
                await controller.TickAsync();
                var observed = await client.ReadAsync(session, token);
                if (observed.Evidence?.Status is not (OpenCodeRecoveryStatus.Busy or OpenCodeRecoveryStatus.Retry))
                    clock.Advance(TimeSpan.FromMinutes(2));
                await Task.Delay(150, token);
            }
            JsonNode final = await GetAsync(http, $"session/{session}/message", token);
            await WriteAsync(evidence, label + "-messages.json", final);
            await WriteAsync(evidence, label + "-http.json", recorder.Requests.ToArray());
            await WriteAsync(evidence, label + "-state.json", new { controller.Status, controller.IsRecoveryPaused, controller.VerifiedContinuationSessionId, controller.HasVerifiedCompletion, diagnostics = diagnostics.ToArray() });
            Require(!controller.IsRecoveryPaused && controller.VerifiedContinuationSessionId == session, "Production controller lost native request ownership: " + controller.Status);
            Require(final.AsArray().Last()?["info"]?["finish"]?.GetValue<string>() == "stop", "Production recovery did not reach a successful native response: " + controller.Status);
            int compactions = recorder.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/summarize", StringComparison.Ordinal));
            int continues = recorder.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/prompt_async", StringComparison.Ordinal));
            Require(compactions == (failSummary ? 1 : 0), "Unexpected production summary POST count: " + compactions);
            Require(streamOverload ? continues <= 1 : continues == (failSummary ? 1 : 0), "Unexpected production continuation POST count: " + continues);
            if (streamOverload)
            {
                Require(!final.AsArray().Any(m => m?["parts"]!.AsArray().Any(p => p?["type"]?.GetValue<string>() == "compaction") == true), "An overload without context overflow triggered compaction.");
                Require(fixture.HttpOverloadCount == 0, "The follow-up HTTP503 model request was not exercised.");
            }
            string? retained = typeof(OpenCodeAutoResumeController).GetField("_lastUserInstruction", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(controller) as string;
            Require(retained == instruction, "Production controller overwrote the original multiline instruction.");
            if (streamOverload)
                await VerifyLoopAfterRecoveryAsync(http, fixture, client, controller, recorder, clock, session, original, instruction, evidence, token);
        }
        finally
        {
            release.TrySetResult(); releaseStreamError?.TrySetResult();
            eventStop.Cancel();
            try { await eventRead; } catch (OperationCanceledException) { }
            if (streamOverload) await WriteAsync(evidence, label + "-events.json", nativeEvents.ToArray());
        }
    }

    private static async Task VerifyLoopAfterRecoveryAsync(HttpClient http, ModelFixture fixture, OpenCodeRecoveryClient client,
        OpenCodeAutoResumeController controller, RecordingHandler recorder, FixtureClock clock, string session,
        string original, string instruction, string evidence, CancellationToken token)
    {
        const string label = "controller-stream503-loop-review";
        var completed = await client.ReadAsync(session, token);
        await controller.TickAsync();
        await WriteAsync(evidence, label + "-completed-read.json", completed);
        Require(completed.IsAvailable && completed.Evidence is { Status: OpenCodeRecoveryStatus.Idle, HasSuccessfulFinalResponse: true, TotalTodoCount: 0 } final && final.LatestUserMessageId == original,
            "The production client did not recognize the exact recovered final response before Loop review.");
        // HasVerifiedCompletion deliberately requires confirmed todo completion;
        // no-todo native responses instead authorize the separate Loop review path.
        Require(!controller.HasVerifiedCompletion, "The no-todo fixture unexpectedly claimed todo-verified completion.");
        int before = recorder.Requests.Count(r => r.Method == "POST");
        Require(before == 0, "The controller submitted work before the recovered final response.");
        controller.SetContinuousImprovement(true, until: null);
        await controller.TickAsync();
        clock.Advance(TimeSpan.FromSeconds(35));
        var releaseReview = fixture.HoldNextRequest();
        try
        {
            for (int i = 0; i < 80 && recorder.Requests.Count(r => r.Method == "POST") == before; i++)
            { await controller.TickAsync(); await Task.Delay(25, token); }
            await fixture.RequestHeld!.Task.WaitAsync(token);
            var mutations = recorder.Requests.Where(r => r.Method == "POST").ToArray();
            Require(mutations.Length == 1 && mutations[0].Path == $"/session/{session}/prompt_async", "Loop did not submit exactly one next review to the original session.");
            JsonNode reviewing = await GetAsync(http, $"session/{session}/message", token);
            Require(reviewing.AsArray().Last(m => m?["info"]?["role"]?.GetValue<string>() == "user")?["parts"]!.AsArray().Any(p => p?["text"]?.GetValue<string>().StartsWith("Loop ", StringComparison.Ordinal) == true) == true,
                "The next Loop submission was not an improvement review.");
            string? retained = typeof(OpenCodeAutoResumeController).GetField("_lastUserInstruction", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(controller) as string;
            Require(retained == instruction, "Loop review replaced the original user instruction.");
            await WriteAsync(evidence, label + "-messages.json", reviewing);
            controller.ObserveLocalInput("\x03");
            controller.SetContinuousImprovement(false, until: null);
            releaseReview.TrySetResult();
            for (int i = 0; i < 40; i++)
            {
                JsonNode settled = await GetAsync(http, $"session/{session}/message", token);
                if (settled.AsArray().Last()?["info"]?["finish"]?.GetValue<string>() == "stop") break;
                await Task.Delay(100, token);
            }
            for (int i = 0; i < 4; i++) { clock.Advance(TimeSpan.FromMinutes(10)); await controller.TickAsync(); await Task.Delay(25, token); }
            Require(recorder.Requests.Count(r => r.Method == "POST") == before + 1, "A stopped Loop submitted more work.");
            await WriteAsync(evidence, label + "-http.json", recorder.Requests.ToArray());
            await WriteAsync(evidence, label + "-state.json", new { exactRecoveredFinalRecognized = true, todoVerifiedCompletionApplicable = false, nextReviewSubmitted = true, originalInstructionRetained = retained == instruction, userStopPreventedFurtherPosts = true, controller.Status, controller.IsRecoveryPaused });
        }
        finally { releaseReview.TrySetResult(); }
    }

    private static async Task CaptureEventsAsync(HttpResponseMessage response, ConcurrentQueue<JsonNode> events, CancellationToken token)
    {
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
        while (await reader.ReadLineAsync(token) is { } line)
            if (line.StartsWith("data:", StringComparison.Ordinal) && JsonNode.Parse(line[5..].Trim()) is { } data) events.Enqueue(data);
    }

    private sealed class FixtureClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        private long _timestamp;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan duration) { _now += duration; _timestamp += duration.Ticks; }
    }

    private static async Task VerifyLateBoundNativeAsync(HttpClient http, ModelFixture fixture, string isolated, string evidence, CancellationToken token)
    {
        const string label = "controller-late-bind-native-replay";
        string session = (await PostAsync(http, "session", new { title = label }, token))["id"]!.GetValue<string>();
        await PostAsync(http, $"session/{session}/message", Prompt("Earlier completed request for the late receipt fixture."), token);
        const string instruction = "Original late binding request\nPreserve this task when native replay finishes before the first controller poll.";
        string plan = Path.Combine(isolated, label + "-plan.md");
        await File.WriteAllTextAsync(plan, "# Late receipt fixture\n" + instruction, token);
        using var recorder = new RecordingHandler();
        using var clientHttp = new HttpClient(recorder) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new OpenCodeRecoveryClient(http.BaseAddress!, isolated, clientHttp);
        using var controller = new OpenCodeAutoResumeController(isolated, plan, client, () => true, new FixtureClock());
        controller.SetEnabled(true);
        object prompt = Prompt(instruction);
        string original = JsonSerializer.SerializeToNode(prompt)!["messageID"]!.GetValue<string>();
        controller.ObserveSubmittedPrompt(instruction, session, original);
        fixture.FailureCount = 1;
        await PostAsync(http, $"session/{session}/message", prompt, token);
        JsonNode final = await GetAsync(http, $"session/{session}/message", token);
        Require(final.AsArray().Last()?["info"]?["finish"]?.GetValue<string>() == "stop", "Native late-binding fixture did not complete before controller observation.");
        controller.ConfirmApiSubmission(session, original);
        for (int i = 0; i < 80 && controller.VerifiedContinuationSessionId != session; i++)
        { await controller.TickAsync(); await Task.Delay(25, token); }
        await WriteAsync(evidence, label + "-messages.json", final);
        await WriteAsync(evidence, label + "-http.json", recorder.Requests.ToArray());
        await WriteAsync(evidence, label + "-state.json", new { controller.Status, controller.IsRecoveryPaused, controller.VerifiedContinuationSessionId, controller.HasVerifiedCompletion });
        Require(controller.VerifiedContinuationSessionId == session && !controller.IsRecoveryPaused, "Accepted original receipt did not bind its completed native replay chain: " + controller.Status);
        Require(recorder.Requests.All(r => r.Method != "POST"), "Late binding resubmitted work already completed by native recovery.");
        string? retained = typeof(OpenCodeAutoResumeController).GetField("_lastUserInstruction", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(controller) as string;
        Require(retained == instruction, "Late binding replaced the original multiline instruction.");
    }

    private static async Task VerifyOwnedHostRestartAsync(HttpClient http, ModelFixture fixture, string isolated, string evidence, Func<Task> restartHost, CancellationToken token)
    {
        const string label = "controller-owned-host-restart";
        string session = (await PostAsync(http, "session", new { title = label }, token))["id"]!.GetValue<string>();
        await PostAsync(http, $"session/{session}/message", Prompt("Earlier completed request for restart fixture."), token);
        const string instruction = "Original restart request\nRetain this exact instruction after the owned native host dies during compaction.";
        string plan = Path.Combine(isolated, label + "-plan.md");
        await File.WriteAllTextAsync(plan, "# Restart fixture\n" + instruction, token);
        using var oldClient = new OpenCodeRecoveryClient(http.BaseAddress!, isolated);
        var clock = new FixtureClock();
        using var oldController = new OpenCodeAutoResumeController(isolated, plan, oldClient, () => true, clock);
        oldController.SetEnabled(true);
        object prompt = Prompt(instruction);
        string original = JsonSerializer.SerializeToNode(prompt)!["messageID"]!.GetValue<string>();
        oldController.ObserveSubmittedPrompt(instruction, session, original);
        fixture.FailureCount = 1;
        var releaseOriginal = fixture.HoldNextRequest();
        Task<JsonNode> send = PostAsync(http, $"session/{session}/message", prompt, token);
        TaskCompletionSource? releaseSummary = null;
        try
        {
            await fixture.RequestHeld!.Task.WaitAsync(token);
            oldController.ConfirmApiSubmission(session, original);
            for (int i = 0; i < 80 && oldController.VerifiedContinuationSessionId != session; i++)
            { await oldController.TickAsync(); await Task.Delay(25, token); }
            Require(oldController.VerifiedContinuationSessionId == session, "Restart fixture did not bind original native request.");
            releaseSummary = fixture.HoldNextRequest();
            Task summaryHeld = fixture.RequestHeld!.Task;
            releaseOriginal.TrySetResult();
            await summaryHeld.WaitAsync(token);
            await oldController.TickAsync();
            OpenCodeErrorRestart snapshot = oldController.GetConfirmedRecoverySnapshot() ?? throw new InvalidOperationException("Pending native summary was not exportable for exact-session reconciliation.");
            Require(snapshot.ReconcileOnly && snapshot.ContextRecovery is not null, "Pending summary snapshot lost its reconciliation reservation.");
            await WriteAsync(evidence, label + "-before-exit.json", await GetAsync(http, $"session/{session}/message", token));
            oldController.Dispose();
            await restartHost();
            releaseSummary.TrySetResult();
            try { await send; } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
            JsonNode restoredSession = await GetAsync(http, $"session/{session}", token);
            Require(restoredSession["id"]?.GetValue<string>() == session, "Restart did not reconnect the original persisted session.");
            using var recorder = new RecordingHandler();
            using var clientHttp = new HttpClient(recorder) { Timeout = Timeout.InfiniteTimeSpan };
            using var newClient = new OpenCodeRecoveryClient(http.BaseAddress!, isolated, clientHttp);
            using var newController = new OpenCodeAutoResumeController(isolated, plan, newClient, () => true, clock);
            newController.SetEnabled(true);
            Require(newController.RestoreContextRecovery(snapshot, previousOwnedHostStopped: true), "Restarted controller rejected the confirmed original-session reservation.");
            Require(recorder.Requests.All(r => r.Method != "POST"), "Restoring a recovery reservation submitted a new startup prompt.");
            for (int i = 0; i < 50; i++)
            {
                await newController.TickAsync();
                var observed = await newClient.ReadAsync(session, token);
                if (observed.Evidence?.Status is not (OpenCodeRecoveryStatus.Busy or OpenCodeRecoveryStatus.Retry))
                    clock.Advance(TimeSpan.FromMinutes(2));
                await Task.Delay(150, token);
            }
            JsonNode final = await GetAsync(http, $"session/{session}/message", token);
            await WriteAsync(evidence, label + "-messages.json", final);
            await WriteAsync(evidence, label + "-http.json", recorder.Requests.ToArray());
            await WriteAsync(evidence, label + "-state.json", new { newController.Status, newController.IsRecoveryPaused, newController.VerifiedContinuationSessionId });
            Require(newController.VerifiedContinuationSessionId == session && !newController.IsRecoveryPaused, "Restarted controller lost original session ownership: " + newController.Status);
            Require(final.AsArray().Last()?["info"]?["finish"]?.GetValue<string>() == "stop", "Restarted native compaction did not finish: " + newController.Status);
            Require(recorder.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/summarize", StringComparison.Ordinal)) == 1, "Owned host exit caused a duplicate or missing compaction retry.");
            Require(recorder.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/prompt_async", StringComparison.Ordinal)) == 1, "Owned host exit caused a duplicate or missing continuation.");
            Require(!recorder.Requests.Any(r => r.Method == "POST" && r.Path == "/session"), "Recovery created a replacement session.");
            string? retained = typeof(OpenCodeAutoResumeController).GetField("_lastUserInstruction", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(newController) as string;
            Require(retained == instruction, "Owned host restart overwrote the original multiline instruction.");
        }
        finally { releaseOriginal.TrySetResult(); releaseSummary?.TrySetResult(); }
    }

    private static Process StartNative(ProcessStartInfo start, ConcurrentQueue<string> logs)
    {
        var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) logs.Enqueue(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) logs.Enqueue(e.Data); };
        Require(process.Start(), "Replacement native process did not start.");
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        return process;
    }

    private sealed record NativeRequest(string Method, string Path, JsonNode? Body);
    private sealed class RecordingHandler : DelegatingHandler
    {
        public ConcurrentQueue<NativeRequest> Requests { get; } = new();
        public RecordingHandler() : base(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue(new(request.Method.Method, request.RequestUri!.AbsolutePath, string.IsNullOrEmpty(body) ? null : JsonNode.Parse(body)));
            return await base.SendAsync(request, cancellationToken);
        }
    }
    private static async Task<JsonNode> GetAsync(HttpClient http, string path, CancellationToken token)
    {
        using var response = await http.GetAsync(path, token);
        string body = await response.Content.ReadAsStringAsync(token);
        Require(response.IsSuccessStatusCode, $"GET {path} {response.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }
    private static async Task<JsonNode> PostAsync(HttpClient http, string path, object body, CancellationToken token)
    {
        using var response = await http.PostAsync(path, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), token);
        string result = await response.Content.ReadAsStringAsync(token);
        Require(response.IsSuccessStatusCode, $"POST {path} {response.StatusCode}: {result}");
        return JsonNode.Parse(result)!;
    }
    private static async Task WaitReadyAsync(HttpClient http, Process native, CancellationToken token)
    {
        for (int i = 0; i < 120; i++)
        {
            Require(!native.HasExited, "Native exited with code " + (native.HasExited ? native.ExitCode : -1));
            try { using var response = await http.GetAsync("doc", token); if (response.IsSuccessStatusCode) return; } catch (HttpRequestException) { }
            await Task.Delay(250, token);
        }
        throw new TimeoutException("Native readiness timed out.");
    }
    private static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static Task WriteAsync(string dir, string name, object? data) => File.WriteAllTextAsync(Path.Combine(dir, name), JsonSerializer.Serialize(data, Pretty));
    private static void Require(bool pass, string message) { if (!pass) throw new InvalidOperationException(message); }

    private sealed class ModelFixture : IDisposable
    {
        private readonly HttpListener _listener = new();
        public Uri Uri { get; }
        public ConcurrentQueue<object> Requests { get; } = new();
        public int FailureCount { get; set; }
        public bool StreamOverloadNext { get; set; }
        public int HttpOverloadCount { get; set; }
        public bool FailNext { get => FailureCount > 0; set => FailureCount = value ? 1 : 0; }
        private TaskCompletionSource? _hold;
        private TaskCompletionSource? _streamErrorHold;
        public TaskCompletionSource? PartialStreamSent { get; private set; }
        public TaskCompletionSource HoldStreamError()
        {
            PartialStreamSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _streamErrorHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public TaskCompletionSource? RequestHeld { get; private set; }
        public TaskCompletionSource HoldNextRequest()
        {
            RequestHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public ModelFixture(int port)
        {
            Uri = new Uri($"http://127.0.0.1:{port}/"); _listener.IgnoreWriteExceptions = true; _listener.Prefixes.Add(Uri.AbsoluteUri); _listener.Start();
            _ = ServeAsync();
        }
        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); } catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }
                try
                {
                    string body = await new StreamReader(context.Request.InputStream).ReadToEndAsync();
                    JsonNode? request = JsonNode.Parse(body);
                    Requests.Enqueue(new { path = context.Request.RawUrl, body = request });
                    if (_hold is { } hold) { _hold = null; RequestHeld!.TrySetResult(); await hold.Task; }
                    string? requestedModel = request?["model"]?.GetValue<string>();
                    Require(context.Request.Url!.IsLoopback && requestedModel is Model or SummaryModel, "Unexpected model destination.");
                    if (StreamOverloadNext)
                    {
                        StreamOverloadNext = false;
                        context.Response.ContentType = "text/event-stream";
                        string partial = JsonSerializer.Serialize(new { id = "chatcmpl-overload", @object = "chat.completion.chunk", created = 1700000000, model = requestedModel, choices = new[] { new { index = 0, delta = new { role = "assistant", content = "Fixture partial response before backend overload." }, finish_reason = (string?)null } } });
                        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("data: " + partial + "\n\n"));
                        await context.Response.OutputStream.FlushAsync();
                        PartialStreamSent?.TrySetResult();
                        if (_streamErrorHold is { } streamErrorHold) { _streamErrorHold = null; await streamErrorHold.Task; }
                        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("data: {\"error\":{\"message\":\"503 backend overloaded\",\"type\":\"server_error\",\"code\":\"503\"}}\n\ndata: [DONE]\n\n"));
                    }
                    else if (HttpOverloadCount > 0)
                    {
                        HttpOverloadCount--;
                        context.Response.StatusCode = 503;
                        context.Response.Headers["Retry-After"] = "0";
                        context.Response.ContentType = "application/json";
                        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"backend overloaded\",\"type\":\"server_error\",\"code\":\"503\"}}"));
                    }
                    else if (FailureCount > 0)
                    {
                        FailureCount--; context.Response.StatusCode = 400; context.Response.ContentType = "application/json";
                        byte[] error = Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"maximum context length exceeded\",\"type\":\"invalid_request_error\",\"code\":\"context_length_exceeded\"}}");
                        await context.Response.OutputStream.WriteAsync(error);
                    }
                    else
                    {
                        bool summary = requestedModel == SummaryModel;
                        string text = summary ? "Fixture summary: the original multiline task remains incomplete. Implement the fixture and verify it." : "Fixture response completed.";
                        context.Response.ContentType = "text/event-stream";
                        string chunk = JsonSerializer.Serialize(new { id = "chatcmpl-fixture", @object = "chat.completion.chunk", created = 1700000000, model = requestedModel, choices = new[] { new { index = 0, delta = new { role = "assistant", content = text }, finish_reason = (string?)null } } });
                        string end = JsonSerializer.Serialize(new { id = "chatcmpl-fixture", @object = "chat.completion.chunk", created = 1700000000, model = requestedModel, choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }, usage = new { prompt_tokens = 120, completion_tokens = 20, total_tokens = 140 } });
                        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("data: " + chunk + "\n\ndata: " + end + "\n\ndata: [DONE]\n\n"));
                    }
                }
                catch (Exception ex) { context.Response.StatusCode = 500; Requests.Enqueue(new { error = ex.Message }); }
                finally { context.Response.Close(); }
            }
        }
        public void Dispose() { _listener.Stop(); _listener.Close(); }
    }
}
