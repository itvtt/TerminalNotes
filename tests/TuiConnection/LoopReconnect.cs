using TerminalNotes;
using TerminalNotes.Services;
using TerminalNotes.TerminalEngine;

internal static partial class Program
{
    private static async Task VerifyLoopReconnectAsync(TerminalPane pane, string fixture,
        Func<int> launches, Action<bool> failLaunch)
    {
        object? previous = typeof(TerminalPane).GetField("_autoResume",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(pane);
        using var client = new OpenCodeRecoveryClient(new Uri("http://127.0.0.1:43129/"), fixture);
        using var controller = new OpenCodeAutoResumeController(fixture, null, client, () => false);
        controller.SetEnabled(true);
        // Seed only authorized unfinished-work state. The controller is never started;
        // every attach uses this fixture's existing fake selection transport.
        Set(controller, "_hasUnfinishedWork", true);
        Set(pane, "_autoResume", controller);
        try
        {
            failLaunch(true);
            Set(pane, "_tuiReconnectAttempts", 2);
            Set(pane, "_tuiConnectionStage", "running");
            int before = launches();
            ConPtySession current = Field<ConPtySession>(pane, "_session");
            Invoke(pane, "HandleTerminalSessionExit", current, 0);
            await UntilAsync(() => Field<int>(pane, "_tuiReconnectAttempts") >= 5);
            failLaunch(false);
            await UntilAsync(() => pane.IsReady && !Field<bool>(pane, "_tuiReconnectScheduled"));
            Require(launches() >= before + 4,
                "Authorized unfinished work did not reconnect after a clean exit and more than two failed attaches.");

            // Stop while the next retry is delayed must veto the pending attach.
            current = Field<ConPtySession>(pane, "_session");
            await (Task)Invoke(current, "StopAndWaitAsync")!;
            Invoke(pane, "HandleTerminalSessionExit", current, 0);
            int beforeStop = launches();
            controller.Suspend("fixture explicit Stop");
            await Task.Delay(150);
            Require(launches() == beforeStop && !Field<bool>(pane, "_tuiReconnectScheduled"),
                "An explicit Stop allowed a delayed automatic display reconnect.");
        }
        finally
        {
            typeof(TerminalPane).GetField("_autoResume",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(pane, previous);
            failLaunch(false);
        }
    }
}
