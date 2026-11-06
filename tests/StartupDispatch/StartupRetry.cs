using System.Net;
using System.Net.Http;
using TerminalNotes.Services;

internal static partial class Program
{
    private static async Task TransientStartupSelectionAsync()
    {
        foreach (string failure in new[] { "503", "408", "429", "transport", "timeout" })
        {
            int reads = 0, selections = 0;
            var handler = new FakeHandler((request, _) =>
            {
                bool select = request.RequestUri!.AbsolutePath == "/tui/select-session";
                int attempt = select ? ++selections : ++reads;
                if (attempt == 1)
                {
                    if (failure == "transport") throw new HttpRequestException("Synthetic connection interruption.");
                    if (failure == "timeout") throw new TaskCanceledException("Synthetic transport timeout.");
                    return Task.FromResult(new HttpResponseMessage((HttpStatusCode)int.Parse(failure)));
                }
                return Task.FromResult(select ? Ok("true") : Ok(SessionJson(SessionId, "Startup task")));
            });
            using var client = NewClient(handler);
            await client.SelectStartupSessionAsync(SessionId);
            Require(reads == 2 && selections == 2, failure + " did not recover exact GET/selection once.");
            Require(handler.Requests.All(r => r.Path == "/session/" + SessionId || r.Path == "/tui/select-session"),
                "Connection recovery created a session or sent a prompt.");
        }
    }

    private static async Task StartupRetryBoundariesAsync()
    {
        foreach (string failure in new[] { "401", "403", "malformed", "wrong-session", "wrong-project", "false" })
        {
            var handler = new FakeHandler((request, _) =>
            {
                bool select = request.RequestUri!.AbsolutePath == "/tui/select-session";
                HttpResponseMessage response = failure switch
                {
                    "401" => new(HttpStatusCode.Unauthorized),
                    "403" => new(HttpStatusCode.Forbidden),
                    "malformed" => Ok("{"),
                    "wrong-session" => Ok(SessionJson("ses_other123", "Startup task")),
                    "wrong-project" => Ok(System.Text.Json.JsonSerializer.Serialize(new { id = SessionId, directory = Workspace + "-other", title = "Startup task" })),
                    _ => select ? Ok("false") : Ok(SessionJson(SessionId, "Startup task"))
                };
                return Task.FromResult(response);
            });
            using var client = NewClient(handler);
            await ThrowsAsync<OpenCodeAutomationException>(() => client.SelectStartupSessionAsync(SessionId));
            Require(handler.Requests.Count == (failure == "false" ? 2 : 1), failure + " was retried unsafely.");
        }

        var failing = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using (var client = NewClient(failing))
            await ThrowsAsync<OpenCodeAutomationException>(() => client.SelectStartupSessionAsync(SessionId));
        Require(failing.Requests.Count == 3, "Transient retries were not bounded to three attempts.");

        int creations = 0;
        var ambiguous = new FakeHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Ok("{}"));
            creations++;
            throw new HttpRequestException("Synthetic lost create response.");
        });
        using (var client = NewClient(ambiguous))
        {
            var failure = await ThrowsAsync<OpenCodeAutomationException>(() => client.CreateStartupSessionAsync("Startup task"));
            Require(failure.Code == "session_create_ambiguous" && creations == 1, "Ambiguous session creation was replayed.");
        }

        using var cancellation = new CancellationTokenSource();
        var cancelled = new FakeHandler((_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        using (var client = NewClient(cancelled))
            await ThrowsAsync<OperationCanceledException>(() => client.SelectStartupSessionAsync(SessionId, cancellation.Token));
        Require(cancelled.Requests.Count == 1, "Cancellation allowed another retry.");
        Console.WriteLine("PASS safe startup connection retry: transient GET/selection, bounded attempts, identity/auth/JSON guards, no create replay, cancellation.");
    }
}
