using TerminalNotes.Services;

namespace TerminalNotes;

public sealed partial class TerminalPane
{
    private string? _keptSessionId;
    private int? _keptProcessId;

    /// <summary>The OpenCode session this terminal works in, for learning 나만의 스킬 from its finished work (null without one).</summary>
    internal (OpenCodeConversationClient Client, string SessionId)? LearningSource =>
        !_disposed && _headlessOpenCodeClient is { } client && _headlessSessionId is { Length: > 0 } session ? (client, session) : null;

    /// <summary>The app is closing while this task terminal's agent works: its server keeps running and is written down (KeptServers),
    /// so opening the task again takes it back. Only task terminals (numbered, with a session) are kept.</summary>
    internal bool KeepRunningAfterClose()
    {
        if (_disposed || _nativePaneKey is not { } key || !int.TryParse(key, out _) || _headlessSessionId is not { Length: > 0 } session ||
            NativeOpenCodeEndpoint is not { } endpoint || _headlessOpenCodeHost is not { IsRunning: true } host) return false;
        if (host.Detach() is not int processId) return false;
        KeptServers.Add(new KeptServers.Server(WorkingDirectory, key, endpoint.Port, processId, session, DateTimeOffset.UtcNow, KeptServers.StartTimeOf(processId)));
        return true;
    }
}
