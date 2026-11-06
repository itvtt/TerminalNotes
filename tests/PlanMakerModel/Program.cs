using System.Net;
using System.Text;
using System.Text.Json;
using TerminalNotes.Services;

const string sourceSession = "ses_source";
const string alias = "enterprise-gateway/deployment/lantern-v7";
JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

using var transport = new ModelTransport();
using var http = new HttpClient(transport);
using var client = new OpenCodeConversationClient(new Uri("http://127.0.0.1:43129/"), Path.GetTempPath(), http);
JsonElement agents = Parse("[{\"name\":\"plan\",\"model\":{\"providerID\":\"agent-hub\",\"modelID\":\"separate-alias\"}}]");
transport.Config = "{\"model\":\"" + alias + "\"}";
transport.Messages = "[{\"info\":{\"role\":\"assistant\",\"sessionID\":\"ses_source\",\"providerID\":\"original-provider-name\",\"modelID\":\"original-model-name\"}}]";
string? snapshot = await client.ReadPlanMakerModelAsync(sourceSession, agents, CancellationToken.None);
Require(snapshot == alias, "Configured alias must override plan-agent model and ignore assistant model names.");
await client.SendPlanMakerAsync("ses_plan", "initial", "msg_initial", CancellationToken.None, snapshot);
transport.Config = "{\"model\":\"enterprise-gateway/changed-later\"}";
await client.SendPlanMakerAsync("ses_plan", "follow-up", "msg_followup", CancellationToken.None, snapshot);
Require(transport.Prompts.Count == 2, "Initial and follow-up must each submit exactly once.");
foreach (JsonElement prompt in transport.Prompts)
{
    Require(prompt.GetProperty("model").GetProperty("providerID").GetString() == "enterprise-gateway" &&
        prompt.GetProperty("model").GetProperty("modelID").GetString() == "deployment/lantern-v7",
        "Initial and follow-up must preserve exact aliases, including slashes in model IDs.");
    Require(prompt.GetProperty("agent").GetString() == "plan" && !prompt.TryGetProperty("tools", out _),
        "Model preservation must retain plan-agent behavior and session-only tool permissions.");
}

transport.Messages = "[{\"info\":{\"role\":\"user\",\"sessionID\":\"ses_source\",\"model\":{\"providerID\":\"request-hub\",\"modelID\":\"exact-user-alias\"}}},{\"info\":{\"role\":\"user\",\"sessionID\":\"ses_other\",\"model\":{\"providerID\":\"other-hub\",\"modelID\":\"other-alias\"}}}]";
Require(await client.ReadPlanMakerModelAsync(sourceSession, agents, CancellationToken.None) == "request-hub/exact-user-alias",
    "Exact source user-request model must win over defaults and unrelated sessions.");
transport.Messages = "[]";
transport.ConfigUnavailable = true;
Require(await client.ReadPlanMakerModelAsync(sourceSession, agents, CancellationToken.None) == "agent-hub/separate-alias",
    "An unavailable config endpoint may use only the explicitly configured plan-agent IDs.");
transport.ConfigUnavailable = false;
transport.Config = "{\"model\":\"Friendly Display Name\"}";
try
{
    _ = await client.ReadPlanMakerModelAsync(sourceSession, agents, CancellationToken.None);
    throw new Exception("Malformed explicit configuration must not silently select a plan-agent alias.");
}
catch (InvalidOperationException error)
{
    Require(error.Message.Contains("provider/model", StringComparison.Ordinal), "Malformed configuration must explain the required model form.");
}
transport.Config = "{}";
try
{
    _ = await client.ReadPlanMakerModelAsync(sourceSession, Parse("[{\"name\":\"plan\",\"model\":\"Friendly Display Name\"}]"), CancellationToken.None);
    throw new Exception("Malformed explicit plan-agent configuration must not silently use the native default.");
}
catch (InvalidOperationException error)
{
    Require(error.Message.Contains("provider/model", StringComparison.Ordinal), "Malformed plan-agent configuration must explain the required model form.");
}
Require(transport.Prompts.Count == 2, "Malformed explicit models must fail before submission.");

JsonElement nativeAgents = Parse("[{\"name\":\"plan\"}]");
transport.Messages = "[]";
string? nativeModel = await client.ReadPlanMakerModelAsync(sourceSession, nativeAgents, CancellationToken.None);
Require(nativeModel is null, "A fresh source session with empty config and no plan-agent model must allow OpenCode's native default.");
await client.SendPlanMakerAsync("ses_plan", "native initial", "msg_native", CancellationToken.None, nativeModel);
Require(transport.Prompts.Count == 3 && !transport.Prompts[^1].TryGetProperty("model", out _),
    "The native-default initial prompt must omit model instead of blocking submission.");
transport.Messages = "[{\"info\":{\"role\":\"assistant\",\"sessionID\":\"ses_source\",\"providerID\":\"original-provider-name\",\"modelID\":\"original-model-name\"}}]";
Require(await client.ReadPlanMakerModelAsync(sourceSession, nativeAgents, CancellationToken.None) is null,
    "Assistant model names must never override the native default.");

const string nativeAlias = "native-hub/deployment/native-alias";
JsonElement NativeMessage(string role, string session, string message) => JsonSerializer.SerializeToElement(new[]
{
    new { info = new { role, sessionID = session, id = message, model = new { providerID = "native-hub", modelID = "deployment/native-alias" } } }
});
Require(OpenCodeConversationClient.ReadPlanMakerRequestModel(NativeMessage("assistant", "ses_plan", "msg_native"), "ses_plan", "msg_native") is null &&
    OpenCodeConversationClient.ReadPlanMakerRequestModel(NativeMessage("user", "ses_other", "msg_native"), "ses_plan", "msg_native") is null &&
    OpenCodeConversationClient.ReadPlanMakerRequestModel(NativeMessage("user", "ses_plan", "msg_other"), "ses_plan", "msg_native") is null,
    "Native model capture must require the exact user-message ID, planning session, and user role.");
Require(OpenCodeConversationClient.ReadPlanMakerRequestModel(Parse("[]"), "ses_plan", "msg_native") is null &&
    OpenCodeConversationClient.ReadPlanMakerRequestModel(Parse("[{\"info\":{\"role\":\"user\",\"sessionID\":\"ses_plan\",\"id\":\"msg_native\"}}]"), "ses_plan", "msg_native") is null,
    "Missing native user-model metadata must preserve native default selection.");
nativeModel ??= OpenCodeConversationClient.ReadPlanMakerRequestModel(NativeMessage("user", "ses_plan", "msg_native"), "ses_plan", "msg_native");
Require(nativeModel == nativeAlias, "Native selection must retain its exact provider and model alias.");
transport.Config = "{\"model\":\"enterprise-gateway/changed-later\"}";
nativeModel ??= OpenCodeConversationClient.ReadPlanMakerRequestModel(Parse("[{\"info\":{\"role\":\"user\",\"sessionID\":\"ses_plan\",\"id\":\"msg_other\",\"model\":{\"providerID\":\"changed-hub\",\"modelID\":\"changed-alias\"}}}]"), "ses_plan", "msg_other");
await client.SendPlanMakerAsync("ses_plan", "native follow-up", "msg_native_followup", CancellationToken.None, nativeModel);
Require(transport.Prompts.Count == 4 &&
    transport.Prompts[^1].GetProperty("model").GetProperty("providerID").GetString() == "native-hub" &&
    transport.Prompts[^1].GetProperty("model").GetProperty("modelID").GetString() == "deployment/native-alias",
    "Follow-up must retain the captured native alias after common configuration changes.");
Console.WriteLine("PASS PlanMaker model: exact user/config/agent aliases, native default omission, exact native user-model capture, stable follow-up identity, malformed/display-name rejection; fake HTTP only.");

sealed class ModelTransport : HttpMessageHandler
{
    internal string Config = "{}", Messages = "[]";
    internal bool ConfigUnavailable;
    internal readonly List<JsonElement> Prompts = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string path = request.RequestUri!.AbsolutePath;
        string body;
        HttpStatusCode status = HttpStatusCode.OK;
        if (request.Method == HttpMethod.Get && path == "/session/ses_source/message") body = Messages;
        else if (request.Method == HttpMethod.Get && path == "/config")
        {
            body = ConfigUnavailable ? "{}" : Config;
            if (ConfigUnavailable) status = HttpStatusCode.NotFound;
        }
        else if (request.Method == HttpMethod.Post && path == "/session/ses_plan/prompt_async")
        {
            using JsonDocument document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Prompts.Add(document.RootElement.Clone());
            body = ""; status = HttpStatusCode.NoContent;
        }
        else throw new InvalidOperationException("Unexpected fake request: " + path);
        return new HttpResponseMessage(status) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
