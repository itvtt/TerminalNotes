using TerminalNotes.Services;

namespace TerminalNotes;

public sealed partial class TerminalPane
{
    /// <summary>The OpenCode session this terminal works in, for learning 나만의 스킬 from its finished work (null without one).</summary>
    internal (OpenCodeConversationClient Client, string SessionId)? LearningSource =>
        !_disposed && _headlessOpenCodeClient is { } client && _headlessSessionId is { Length: > 0 } session ? (client, session) : null;
}
