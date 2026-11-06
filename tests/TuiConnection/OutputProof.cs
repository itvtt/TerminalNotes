using System.Reflection;
using TerminalNotes;
using TerminalNotes.Services;

internal static partial class Program
{
    private static void VerifyTuiOutputProof()
    {
        Type proofType = typeof(TerminalPane).GetNestedType("TuiOutputProof", BindingFlags.NonPublic)!;
        using var session = new ConPtySession();
        (object Proof, TaskCompletionSource Completion) Create()
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            object proof = Activator.CreateInstance(proofType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [session, completion], null)!;
            return (proof, completion);
        }
        void VerifyChunks(IEnumerable<string> chunks)
        {
            var (proof, completion) = Create();
            foreach (string chunk in chunks)
            {
                Invoke(proof, "Observe", chunk);
                Require(!completion.Task.IsCompleted, "Terminal initialization/control payload falsely proved a rendered TUI.");
            }
            Invoke(proof, "Observe", "\u001b[32m실제 질문 화면\u001b[0m");
            Require(completion.Task.IsCompletedSuccessfully, "Rendered Unicode UI text did not prove the TUI view.");
        }

        string[] controls =
        [
            "\u001b[?25l\u001b[2J\u001b[H\u001b[0m\r\n",
            "\u001b]0;PowerShell startup title\a",
            "\u001b]2;Title payload is not screen content\u001b\\",
            "\u001bP1;2|Device payload\u001b\\",
            "\u001bXHidden SOS text\u001b\\\u001b^Hidden PM text\u001b\\\u001b_Hidden APC text\u001b\\",
            "\u001b(0\u001b)B\u001b7\u001b8\u001b#8",
            "\u009b?25l\u009d2;C1 title\u009c\u0090Device payload\u009c\u0098SOS\u009c\u009ePM\u009c\u009fAPC\u009c",
            "\r\n\t\0\u007f\u0085\u200b\u2060\ufeff "
        ];
        foreach (string control in controls)
        {
            VerifyChunks(control.Select(value => value.ToString()));
            for (int split = 0; split <= control.Length; split++)
                VerifyChunks([control[..split], control[split..]]);
        }
        // Even long unterminated string payloads must remain non-rendered until ST.
        VerifyChunks(["\u001b]2;", new string('x', 65_536), "\u001b", "\\"]);
        var (emojiProof, emojiCompletion) = Create();
        Invoke(emojiProof, "Observe", "\ud83d");
        Require(!emojiCompletion.Task.IsCompleted, "An incomplete Unicode scalar proved a visible frame.");
        Invoke(emojiProof, "Observe", "\ude80");
        Require(emojiCompletion.Task.IsCompletedSuccessfully, "A supplementary Unicode UI character was ignored.");
        Console.WriteLine("PASS TUI output proof: fragmented CSI/OSC/control strings, title payloads, whitespace and Unicode rendering.");
    }
}
