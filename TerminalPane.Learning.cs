using TerminalNotes.Services;

namespace TerminalNotes;

public sealed partial class TerminalPane
{
    internal bool IsDisposed => _disposed;

    // What Claude Code and Codex show while they wait for the user (measured 2026-11-12, Claude Code 2.1: the folder trust question
    // "Yes, I trust this folder … Enter to confirm · Esc to cancel"; permission prompts "Do you want to …").
    private static readonly string[] AskingMarkers =
        ["trust this folder", "Do you trust", "Yes, I trust", "Do you want to", "Enter to confirm", "Esc to cancel", "allow Codex to work"];

    /// <summary>A Claude Code or Codex terminal with a question on its screen: it waits for the user (blocked, not idle).</summary>
    internal bool IsAskingUser => IsTypedAgent && ScreenText() is { Length: > 0 } screen &&
        AskingMarkers.Any(marker => screen.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private bool _typedWorkSeen;

    /// <summary>A Claude Code or Codex terminal working, read from its screen: both show "esc to interrupt" while they work. Their screen
    /// keeps changing while idle (an animated status line, rotating hints), so output alone reads as always working (measured
    /// 2026-11-12: an idle Claude Code stayed '진행 중').</summary>
    internal bool IsTypedAgentWorking
    {
        get
        {
            bool working = IsTypedAgent && ScreenText().Contains("esc to interrupt", StringComparison.OrdinalIgnoreCase);
            if (working) _typedWorkSeen = true;
            return working;
        }
    }

    /// <summary>The Claude Code or Codex terminal worked since it started (idle now means done, not waiting).</summary>
    internal bool TypedAgentWorked => _typedWorkSeen;

    /// <summary>The terminal's screen as text (diagnostics and tests).</summary>
    internal string ScreenText()
    {
        if (_nativeTerminalEmulator?.GetSnapshot() is not { } snapshot) return "";
        var screen = new System.Text.StringBuilder();
        foreach (var line in snapshot.Lines)
        {
            foreach (var cell in line.Cells) screen.Append(cell.Text);
            screen.AppendLine();
        }
        return screen.ToString();
    }

    /// <summary>The OpenCode session this terminal works in, for learning 나만의 스킬 from its finished work (null without one).</summary>
    internal (OpenCodeConversationClient Client, string SessionId)? LearningSource =>
        !_disposed && _headlessOpenCodeClient is { } client && _headlessSessionId is { Length: > 0 } session ? (client, session) : null;

    /// <summary>This terminal's OpenCode server (for the agent board to tell it from agents in outside terminals).</summary>
    internal int? ServerProcessId => _headlessOpenCodeHost?.ProcessId is uint id ? (int)id : null;

    /// <summary>The server to search past conversations with, while it runs.</summary>
    internal Uri? SearchEndpoint => LearningSource is not null && _headlessOpenCodeHost?.IsRunning == true ? NativeOpenCodeEndpoint : null;
}
