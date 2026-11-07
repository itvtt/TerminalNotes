using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

internal static partial class FakeNativeOpenCodeServer
{
    private sealed partial class FakeServer
    {
        private sealed record PlanningQuestion(string Id, string SessionId, int Step, string FirstAnswer, string Acceptance);
        private sealed class PlanningInterview
        {
            public string LatestRequestId = "";
            public string Purpose = "";
            public string Acceptance = "";
            public string Detail = "";
            public int Round;
        }
        private readonly Dictionary<string, PlanningQuestion> _planningQuestions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PlanningInterview> _planningInterviews = new(StringComparer.Ordinal);

        private async Task<bool> HandlePlanMakerRequestAsync(HttpListenerContext context, string path)
        {
            if (!File.Exists(Path.Combine(Environment.CurrentDirectory, ".fixture-plan-maker"))) return false;
            if (path == "/session" && context.Request.HttpMethod == "GET")
            {
                object[] sessions;
                lock (_gate) sessions = _sessions.Values.Select(session => session.ToInfo()).ToArray();
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, sessions);
                return true;
            }
            if (path == "/experimental/tool/ids")
            {
                await WriteJsonAsync(context.Response, File.Exists(Path.Combine(Environment.CurrentDirectory, ".fixture-plan-maker-no-tools"))
                    ? HttpStatusCode.NotFound : HttpStatusCode.OK, new[] { "question", "read", "edit", "write", "bash", "task", "custom_dangerous" });
                return true;
            }
            if (path == "/agent")
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new[] { new { name = "plan" }, new { name = "build" } });
                return true;
            }
            if (path == "/config")
            {
                bool nativeDefault = File.Exists(Path.Combine(Environment.CurrentDirectory, ".fixture-plan-maker-native-model"));
                object config = nativeDefault ? _planningInterviews.Count == 0 ? new { } : new { model = "shared/changed-after-initial" }
                    : new { model = "shared/planning-alias" };
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, config);
                return true;
            }
            if (path == "/question")
            {
                object[] questions;
                lock (_gate) questions = _planningQuestions.Values.Select(QuestionPayload).ToArray();
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, questions);
                return true;
            }
            if (path == "/tui/select-session" && context.Request.HttpMethod == "POST" &&
                File.Exists(Path.Combine(Environment.CurrentDirectory, ".fixture-hold-planner-restore")))
            {
                using JsonDocument body = await ReadBodyAsync(context.Request);
                await File.WriteAllTextAsync(Path.Combine(Environment.CurrentDirectory, ".fixture-planner-restore-waiting"),
                    body.RootElement.GetProperty("sessionID").GetString());
                while (File.Exists(Path.Combine(Environment.CurrentDirectory, ".fixture-hold-planner-restore"))) await Task.Delay(25);
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, true);
                return true;
            }
            string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 3 && segments[0] == "session" && segments[2] == "abort")
            {
                lock (_gate)
                    foreach (string id in _planningQuestions.Values.Where(question => question.SessionId == segments[1]).Select(question => question.Id).ToArray()) _planningQuestions.Remove(id);
                return false;
            }
            if (segments.Length == 3 && segments[0] == "question" && segments[2] == "reply")
            {
                PlanningQuestion? question;
                lock (_gate) _planningQuestions.Remove(segments[1], out question);
                if (question is null) { await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new { error = "no question" }); return true; }
                using JsonDocument body = await ReadBodyAsync(context.Request);
                string answer = body.RootElement.GetProperty("answers")[0][0].GetString()!;
                FakeSession session;
                lock (_gate) session = _sessions[question.SessionId];
                PlanningInterview interview = _planningInterviews[session.Id];
                Broadcast("question.replied", new { requestID = question.Id, sessionID = session.Id });
                if (question.Step == 1)
                {
                    interview.Purpose = answer;
                    AskPlanningQuestion(session, 2, answer);
                }
                else if (question.Step == 2)
                {
                    interview.Acceptance = answer;
                    CompletePlanningSession(session, question.FirstAnswer, answer);
                }
                else
                {
                    interview.Detail = answer;
                    string holdAnswer = Path.Combine(Environment.CurrentDirectory, ".fixture-plan-maker-hold-answer");
                    if (File.Exists(holdAnswer))
                    {
                        lock (_gate) session.Status = "busy";
                        Broadcast("session.status", new { sessionID = session.Id, status = new { type = "busy" } });
                        await File.WriteAllTextAsync(Path.Combine(Environment.CurrentDirectory, ".fixture-plan-maker-answer-busy"), answer);
                        while (File.Exists(holdAnswer)) await Task.Delay(25);
                    }
                    // Later provider drafts deliberately omit the earlier confirmed requirements.
                    CompletePlanningSession(session, "", "", answer);
                }
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, true);
                return true;
            }
            if (segments.Length == 3 && segments[0] == "session" && segments[2] == "prompt_async")
            {
                FakeSession session;
                lock (_gate) session = _sessions[segments[1]];
                // Leave the body intact for the ordinary coding fixture handler.
                if (!session.Title.Contains("플랜 만들기", StringComparison.Ordinal)) return false;
                using JsonDocument body = await ReadBodyAsync(context.Request);
                if (!body.RootElement.TryGetProperty("agent", out var agent) || agent.GetString() != "plan") return false;
                if (body.RootElement.TryGetProperty("tools", out _))
                    throw new InvalidDataException("The planner must not overwrite durable session permissions with legacy tools.");
                bool nativeDefault = File.Exists(Path.Combine(Environment.CurrentDirectory, ".fixture-plan-maker-native-model"));
                bool initialNative = nativeDefault && !_planningInterviews.ContainsKey(session.Id);
                bool hasModel = body.RootElement.TryGetProperty("model", out var model);
                if (initialNative ? hasModel : !hasModel ||
                    model.GetProperty("providerID").GetString() != (nativeDefault ? "native-shared" : "shared") ||
                    model.GetProperty("modelID").GetString() != (nativeDefault ? "deployment/native-default" : "planning-alias"))
                    throw new InvalidDataException("The planner must omit an unconfigured initial model and preserve the exact configured or native-selected alias on follow-up.");
                string trace = Environment.GetEnvironmentVariable("TN_WORKFLOW_TRACE")!;
                File.WriteAllText(Path.Combine(trace, "plan-maker-" + Guid.NewGuid().ToString("N") + ".json"), body.RootElement.GetRawText(), new UTF8Encoding(false));
                VerifyPlanningPermissions(session);
                string messageId = body.RootElement.GetProperty("messageID").GetString()!;
                string prompt = body.RootElement.GetProperty("parts")[0].GetProperty("text").GetString()!;
                PlanningInterview interview;
                lock (_gate)
                {
                    if (!_planningInterviews.TryGetValue(session.Id, out interview!))
                        _planningInterviews.Add(session.Id, interview = new PlanningInterview());
                    if (session.Messages.Any(message => message.Id == messageId))
                        throw new InvalidDataException("Planner continuation reused a native request message ID.");
                    interview.LatestRequestId = messageId;
                    interview.Round++;
                }
                await File.AppendAllTextAsync(Path.Combine(Environment.CurrentDirectory, ".fixture-plan-maker-prompts"),
                    session.Id + " " + messageId + "\n");
                long created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                object userInfo = nativeDefault
                    ? new { id = messageId, sessionID = session.Id, role = "user", time = new { created }, model = new { providerID = "native-shared", modelID = "deployment/native-default" } }
                    : new { id = messageId, sessionID = session.Id, role = "user", time = new { created } };
                var user = new FakeMessage(messageId, userInfo, new[] { new { type = "text", text = prompt } });
                lock (_gate) { session.Messages.Add(user); session.Status = "busy"; }
                Broadcast("message.updated", new { info = user.Info, parts = user.Parts });
                Broadcast("session.status", new { sessionID = session.Id, status = new { type = "busy" } });
                if (interview.Round == 1) AskPlanningQuestion(session, 1, "");
                else if (interview.Round == 2) AskPlanningQuestion(session, 3, interview.Purpose);
                else if (interview.Round <= 4) CompletePlanningSession(session, "", "", interview.Detail);
                else AskPlanningQuestion(session, 4, interview.Purpose);
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                context.Response.Close();
                return true;
            }
            return false;
        }

        private object QuestionPayload(PlanningQuestion question) => new
        {
            id = question.Id, sessionID = question.SessionId,
            questions = new[] { new { header = "확인", question = question.Step switch
                {
                    1 => "어떤 결과를 원하시나요?",
                    2 => question.FirstAnswer + "의 완료 조건은 무엇인가요?",
                    3 => question.FirstAnswer + " / " + question.Acceptance + "에서 추가로 확인할 제약은 무엇인가요?",
                    _ => question.FirstAnswer + " / " + question.Acceptance + "에 더 추가할 요구사항은 무엇인가요?"
                },
                options = new[] { new { label = "직접 입력", description = "원하는 내용을 답해주세요." } }, custom = true } }
        };

        private void AskPlanningQuestion(FakeSession session, int step, string firstAnswer)
        {
            VerifyPlanningPermissions(session);
            // Native tool turns can already be completed while their question remains unanswered.
            string messageId = "msg_" + Guid.NewGuid().ToString("N");
            long completed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            PlanningInterview interview = _planningInterviews[session.Id];
            string parentId = interview.LatestRequestId;
            var assistant = new FakeMessage(messageId, new { id = messageId, sessionID = session.Id, parentID = parentId, role = "assistant", time = new { created = completed, completed }, finish = "tool-calls" },
                new[] { new { type = "text", text = step == 1 ? "확인 질문을 준비했습니다." : step == 2 ?
                    "<<<TERMINALNOTES_PLAN>>>\n# Confirmed Plan\n\nPurpose: " + firstAnswer + "\n<<<END_TERMINALNOTES_PLAN>>>" : "확인 질문을 계속합니다." } });
            lock (_gate) { session.Messages.Add(assistant); session.Status = "idle"; }
            var question = new PlanningQuestion("que_" + Guid.NewGuid().ToString("N"), session.Id, step, firstAnswer, interview.Acceptance);
            lock (_gate) _planningQuestions.Add(question.Id, question);
            Broadcast("question.asked", QuestionPayload(question));
        }

        private void CompletePlanningSession(FakeSession session, string purpose, string criteria, string? detail = null)
        {
            VerifyPlanningPermissions(session);
            string id = "msg_" + Guid.NewGuid().ToString("N");
            long created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string parentId = _planningInterviews[session.Id].LatestRequestId;
            string plan = "# Confirmed Plan\n\n" +
                (detail is null ? "Purpose: " + purpose + "\n\nAcceptance: " + criteria : "Constraint: " + detail);
            if (detail is not null && File.Exists(Path.Combine(Environment.CurrentDirectory, ".fixture-plan-maker-cumulative")))
            {
                PlanningInterview interview = _planningInterviews[session.Id];
                plan = "# Confirmed Plan\n\nPurpose: " + interview.Purpose + "\n\nAcceptance: " + interview.Acceptance +
                    "\n\nConstraint: " + detail;
            }
            string echoPath = Path.Combine(Environment.CurrentDirectory, ".fixture-plan-maker-echo");
            if (File.Exists(echoPath)) plan = File.ReadAllText(echoPath) + "\n\n" + plan;
            var assistant = new FakeMessage(id, new { id, sessionID = session.Id, parentID = parentId, role = "assistant", time = new { created, completed = created + 1 }, finish = "stop" },
                new[] { new { type = "text", text = "<<<TERMINALNOTES_PLAN>>>\n" + plan +
                    "\n<<<END_TERMINALNOTES_PLAN>>>" } });
            lock (_gate) { session.Messages.Add(assistant); session.Status = "idle"; }
            Broadcast("message.updated", new { info = assistant.Info, parts = assistant.Parts });
            Broadcast("session.status", new { sessionID = session.Id, status = new { type = "idle" } });
            Broadcast("session.idle", new { sessionID = session.Id });
        }

        private static void VerifyPlanningPermissions(FakeSession session)
        {
            JsonElement rules = session.Permission ?? throw new InvalidDataException("No durable planner permissions.");
            if (rules.GetArrayLength() != 2 || rules[0].GetProperty("permission").GetString() != "*" ||
                rules[0].GetProperty("action").GetString() != "deny" || rules[1].GetProperty("permission").GetString() != "question" ||
                rules[1].GetProperty("action").GetString() != "allow") throw new InvalidDataException("Planner permissions changed during questions.");
        }
    }
}
