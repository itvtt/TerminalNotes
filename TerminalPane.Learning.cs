using TerminalNotes.Services;

namespace TerminalNotes;

public sealed partial class TerminalPane
{
    private string? _keptSessionId;
    private int? _keptProcessId;

    internal bool IsDisposed => _disposed;

    /// <summary>This terminal took back a server its agent kept working on while the app was closed.</summary>
    internal bool WasKeptRunning { get; private set; }

    /// <summary>The OpenCode session this terminal works in, for learning 나만의 스킬 from its finished work (null without one).</summary>
    internal (OpenCodeConversationClient Client, string SessionId)? LearningSource =>
        !_disposed && _headlessOpenCodeClient is { } client && _headlessSessionId is { Length: > 0 } session ? (client, session) : null;

    /// <summary>The app is closing while this task terminal's agent works: its server keeps running and is written down (KeptServers),
    /// so opening the task again takes it back. Only task terminals (numbered, with a session) are kept.</summary>
    /// <summary>This terminal's OpenCode server (for the agent board to tell it from agents in outside terminals).</summary>
    internal int? ServerProcessId => _headlessOpenCodeHost?.ProcessId is uint id ? (int)id : null;

    /// <summary>The server to search past conversations with, while it runs.</summary>
    internal Uri? SearchEndpoint => LearningSource is not null && _headlessOpenCodeHost?.IsRunning == true ? NativeOpenCodeEndpoint : null;

    /// <summary>A task terminal (numbered, with a session) whose server runs: one that can be kept when the app closes.</summary>
    internal bool CanKeepRunningAfterClose => !_disposed && _nativePaneKey is { } key && int.TryParse(key, out _) && _headlessSessionId is { Length: > 0 } &&
        NativeOpenCodeEndpoint is not null && _headlessOpenCodeHost is { IsRunning: true };

    internal bool KeepRunningAfterClose()
    {
        if (!CanKeepRunningAfterClose || _nativePaneKey is not { } key || _headlessSessionId is not { } session || NativeOpenCodeEndpoint is not { } endpoint ||
            _headlessOpenCodeHost is not { } host) return false;
        if (host.Detach() is not int processId) return false;
        KeptServers.Add(new KeptServers.Server(WorkingDirectory, key, endpoint.Port, processId, session, DateTimeOffset.UtcNow, KeptServers.StartTimeOf(processId)));
        return true;
    }
}
