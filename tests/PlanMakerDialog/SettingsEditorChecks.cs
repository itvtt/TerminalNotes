using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Xml.Linq;
using TerminalNotes;
using TerminalNotes.Services;

internal static class SettingsEditorChecks
{
    internal static void Verify(Window owner, string evidence)
    {
        var global = new GlobalAgentInstructionsDialog { Owner = owner };
        var planner = GlobalAgentInstructionsDialog.CreatePlanMaker();
        planner.Owner = owner;
        try
        {
            Require(global.Title == "전역 지침" && global.CaptionTitle.Text == global.Title && global.HeadingText.Text == global.Title &&
                global.DescriptionText.Visibility == Visibility.Visible && global.DescriptionText.Text == "모든 프로젝트에 적용합니다." &&
                AutomationProperties.GetName(global.InstructionsInput) == "전역 지침 내용" &&
                global.InstructionsInput.MaxLength == 0 && global.PathText.Text == GlobalAgentInstructions.FilePath &&
                global.SaveStatusText.Text.Contains("새 OpenCode 요청부터", StringComparison.Ordinal),
                "Default editor no longer routes to the existing global instructions.");
            Require(planner.Title == "플랜메이커 지침" && planner.CaptionTitle.Text == planner.Title && planner.HeadingText.Text == planner.Title &&
                planner.PathText.Text == PlanMakerInstructions.FilePath && planner.InstructionsInput.Text == PlanMakerInstructions.DefaultContents &&
                planner.InstructionsInput.MaxLength == PlanMakerInstructions.MaximumLength &&
                planner.DescriptionText.Text == "질문 방식과 플랜 작성 기준을 설정합니다." &&
                planner.SaveStatusText.Text.Contains("새로 시작하는 플랜메이커 대화부터", StringComparison.Ordinal) &&
                AutomationProperties.GetName(planner.InstructionsInput) == "플랜메이커 지침 내용" &&
                !File.Exists(PlanMakerInstructions.FilePath),
                "Planner editor metadata/defaults are wrong or opening it wrote a guidance file.");
            global.InstructionsInput.Text = "# Existing global instructions\nKeep project guidance separate.";
            Save(global);
            string globalContents = File.ReadAllText(GlobalAgentInstructions.FilePath);
            Require(globalContents == global.InstructionsInput.Text && !File.Exists(PlanMakerInstructions.FilePath),
                "Global editor wrote the planner guidance file.");
            planner.InstructionsInput.Text = "# Custom planner guidance\nAsk about acceptance criteria first.";
            Save(planner);
            Require(File.ReadAllText(PlanMakerInstructions.FilePath) == planner.InstructionsInput.Text &&
                File.ReadAllText(GlobalAgentInstructions.FilePath) == globalContents &&
                planner.SaveStatusLabel.Text == "저장 완료" &&
                planner.SaveStatusText.Text.Contains("새로 시작하는 플랜메이커 대화부터", StringComparison.Ordinal),
                "Planner save changed global instructions or lost the new-interview application timing.");
            const string external = "# External edit\nPreserve another editor's changes.";
            const string local = "# Unsaved local draft\nKeep my question style.";
            File.WriteAllText(PlanMakerInstructions.FilePath, external);
            planner.InstructionsInput.Text = local;
            Save(planner);
            Require(planner.SaveStatusLabel.Text == "저장 충돌" && planner.InstructionsInput.Text == local &&
                File.ReadAllText(PlanMakerInstructions.FilePath) == external,
                "Planner save conflict overwrote the external file or discarded local text.");
            planner.InstructionsInput.Text = new string('x', PlanMakerInstructions.MaximumLength + 1);
            Save(planner);
            Require(planner.SaveStatusLabel.Text == "저장 실패" && File.ReadAllText(PlanMakerInstructions.FilePath) == external,
                "Oversized guidance was saved or the editor did not report the service limit.");
            planner.InstructionsInput.Text = local + "\n" + new string('W', 700) + "\n" + local;
            Save(planner);
            RenderEditor(global, evidence, "global-instructions-minimum");
            RenderEditor(planner, evidence, "plan-maker-instructions-minimum");
            VerifySettingsLayout(evidence);
            VerifyOpenCodeInstructions(owner, evidence);
            VerifyNonMasterInstructions();
            Console.WriteLine("PASS actual global/planner editor routing, saves, external conflicts, length errors, wrap/chrome/footer and settings button.");
        }
        finally { global.Close(); planner.Close(); }
    }

    // Accounts other than the master accounts use the shipped Terminal Notes instructions even when the files are edited by hand;
    // the master accounts keep their edits.
    private static void VerifyNonMasterInstructions()
    {
        string global = GlobalAgentInstructions.FilePath;
        string planner = PlanMakerInstructions.FilePath;
        var reverted = new List<string>();
        void Record(string backup) { lock (reverted) reverted.Add(backup); }
        GlobalAgentInstructions.EditsReverted += Record;
        IDisposable? guard = null;
        try
        {
            InstructionEditors.AccountOverride = () => @"PC\test_01";
            Require(GlobalAgentInstructions.GuardDefault() is null, "A master account got the default-only guard.");
            File.WriteAllText(global, "# master edit");
            File.WriteAllText(planner, "# master planner edit");
            GlobalAgentInstructions.EnsureFile();
            Require(File.ReadAllText(global) == "# master edit" && PlanMakerInstructions.ReadCurrent() == "# master planner edit" && reverted.Count == 0,
                "A master account's own instructions were replaced.");

            File.Delete(global);
            GlobalAgentInstructions.EnsureFile();
            byte[] defaults = File.ReadAllBytes(global);

            InstructionEditors.AccountOverride = () => @"PC\someone";
            File.WriteAllText(global, "# my own rules\nUse English.");
            GlobalAgentInstructions.EnsureFile();
            Require(File.ReadAllBytes(global).AsSpan().SequenceEqual(defaults) && reverted.Count == 1 &&
                File.ReadAllText(reverted[0]) == "# my own rules\nUse English." && reverted[0].Contains(".before-default-restore.", StringComparison.Ordinal),
                "Another account's hand-edited global instructions were used, or the edit was not kept in a backup.");
            Require(PlanMakerInstructions.ReadCurrent() == PlanMakerInstructions.DefaultContents && File.ReadAllText(planner) == "# master planner edit",
                "Another account used an edited plan maker instructions file.");

            guard = GlobalAgentInstructions.GuardDefault();
            Require(guard is not null, "Another account got no guard while the app runs.");
            File.WriteAllText(global, "# edited while running");
            Require(WaitUntil(() => File.Exists(global) && File.ReadAllBytes(global).AsSpan().SequenceEqual(defaults)) && WaitUntil(() => reverted.Count == 2),
                "An edit made while the app runs was not put back to the default.");
            File.Delete(global);
            Require(WaitUntil(() => File.Exists(global) && File.ReadAllBytes(global).AsSpan().SequenceEqual(defaults)),
                "A deleted global instructions file was not recreated with the default.");
        }
        finally
        {
            guard?.Dispose();
            GlobalAgentInstructions.EditsReverted -= Record;
            InstructionEditors.AccountOverride = null;
        }
    }

    private static bool WaitUntil(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            try { if (condition()) return true; }
            catch (IOException) { }
            Thread.Sleep(50);
        }
        return false;
    }

    // OpenCode 지침 edits OpenCode's own AGENTS.md (an isolated config folder here, never the real ~/.config/opencode).
    private static void VerifyOpenCodeInstructions(Window owner, string evidence)
    {
        object? previous = AppContext.GetData("TerminalNotes.OpenCodeConfigDirectory");
        string folder = Path.Combine(evidence, "opencode-config");
        AppContext.SetData("TerminalNotes.OpenCodeConfigDirectory", folder);
        try
        {
            string agents = Path.Combine(folder, "AGENTS.md");
            Require(OpenCodeAgentInstructions.FilePath == agents && OpenCodeAgentInstructions.ReadCurrent() == "",
                "OpenCode 지침 is not OpenCode's AGENTS.md in its config folder.");
            var editor = GlobalAgentInstructionsDialog.CreateOpenCodeInstructions();
            editor.Owner = owner;
            try
            {
                Require(editor.Title == "OpenCode 지침" && editor.PathText.Text == agents && editor.InstructionsInput.Text == "" &&
                    AutomationProperties.GetName(editor.InstructionsInput) == "OpenCode 지침 내용" &&
                    editor.DescriptionText.Text.Contains("Terminal Notes 밖", StringComparison.Ordinal),
                    "OpenCode 지침 editor metadata is wrong.");
                Save(editor);
                Require(editor.SaveStatusLabel.Text == "저장 완료" && !File.Exists(agents),
                    "Saving an unchanged empty OpenCode 지침 created AGENTS.md (it would hide OpenCode's CLAUDE.md fallback).");
                editor.InstructionsInput.Text = "# 내 OpenCode 지침\n- 한국어로 답한다.";
                Save(editor);
                Require(editor.SaveStatusLabel.Text == "저장 완료" && File.ReadAllText(agents) == editor.InstructionsInput.Text,
                    "OpenCode 지침 was not saved to AGENTS.md.");
                const string outside = "# Edited outside\nKeep it.";
                File.WriteAllText(agents, outside);
                editor.InstructionsInput.Text = "# Local draft";
                Save(editor);
                Require(editor.SaveStatusLabel.Text == "저장 충돌" && File.ReadAllText(agents) == outside && editor.InstructionsInput.Text == "# Local draft",
                    "OpenCode 지침 overwrote a file changed outside the editor.");
                Require(!File.Exists(GlobalAgentInstructions.FilePath) || !File.ReadAllText(GlobalAgentInstructions.FilePath).Contains("Local draft") &&
                    (!File.Exists(PlanMakerInstructions.FilePath) || !File.ReadAllText(PlanMakerInstructions.FilePath).Contains("Local draft")),
                    "OpenCode 지침 wrote a Terminal Notes instruction file.");
                RenderEditor(editor, evidence, "opencode-instructions-minimum");
            }
            finally { editor.Close(); }
        }
        finally { AppContext.SetData("TerminalNotes.OpenCodeConfigDirectory", previous); }
    }

    private static void Save(GlobalAgentInstructionsDialog dialog) => typeof(GlobalAgentInstructionsDialog)
        .GetMethod("SaveInstructionsButton_Click", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!
        .Invoke(dialog, [dialog.SaveInstructionsButton, new RoutedEventArgs()]);

    private static void RenderEditor(GlobalAgentInstructionsDialog dialog, string evidence, string name)
    {
        Require(WindowChrome.GetWindowChrome(dialog).CaptionHeight == 36 && dialog.ShowInTaskbar,
            "Instruction editor custom caption behavior changed.");
        var root = (FrameworkElement)dialog.Content;
        root.Width = 560; root.Height = 420;
        root.Measure(new Size(560, 420)); root.Arrange(new Rect(0, 0, 560, 420)); root.UpdateLayout();
        Point editor = dialog.InstructionsInput.TranslatePoint(new Point(), root);
        Point save = dialog.SaveInstructionsButton.TranslatePoint(new Point(), root);
        Require(dialog.InstructionsInput.TextWrapping == TextWrapping.Wrap &&
            dialog.InstructionsInput.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled &&
            dialog.InstructionsInput.ActualHeight > 100 && editor.X + dialog.InstructionsInput.ActualWidth <= 560 &&
            save.Y >= editor.Y + dialog.InstructionsInput.ActualHeight &&
            save.X + dialog.SaveInstructionsButton.ActualWidth <= 560 && save.Y + dialog.SaveInstructionsButton.ActualHeight <= 420,
            "Wrapped instruction editor or footer is clipped at the minimum window size.");
        Render(root, evidence, name, 560, 420);
    }

    private static void VerifySettingsLayout(string evidence)
    {
        XElement source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TerminalProfilesDialog.xaml")).Root!;
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement button = source.Descendants(presentation + "Button").Single(element =>
            (string?)element.Attribute(x + "Name") == "EditPlanMakerInstructionsButton");
        Require((string?)button.Attribute("Content") == "지침 편집" &&
            (string?)button.Attribute("AutomationProperties.Name") == "플랜메이커 지침 편집" &&
            (string?)button.Attribute("Click") == "EditPlanMakerInstructionsButton_Click",
            "Settings planner editor button is not connected to its handler.");
        source.Attribute(x + "Class")!.Remove();
        source.Attribute("Icon")!.Remove();
        // Loaded without code-behind: drop every event handler (Click, the two-column SizeChanged, Checked).
        foreach (string handler in new[] { "Click", "SizeChanged", "Checked" })
            foreach (XAttribute attribute in source.Descendants().Attributes(handler).ToList()) attribute.Remove();
        var settings = (Window)XamlReader.Parse(source.ToString());
        try
        {
            var root = (FrameworkElement)settings.Content;
            var edit = (Button)settings.FindName("EditPlanMakerInstructionsButton");
            var save = (Button)settings.FindName("SaveButton");
            settings.Content = null;
            var frame = new Border { Background = settings.Background, Child = root };
            settings.Content = frame;
            frame.Measure(new Size(480, 400)); frame.Arrange(new Rect(0, 0, 480, 400)); frame.UpdateLayout();
            edit.BringIntoView(); frame.UpdateLayout();
            Point position = edit.TranslatePoint(new Point(), root);
            Point footer = save.TranslatePoint(new Point(), root);
            Require(position.X + edit.ActualWidth <= root.ActualWidth && footer.Y + save.ActualHeight <= root.ActualHeight,
                "Settings planner editor button or fixed footer is clipped at minimum width.");
            Render(frame, evidence, "settings-plan-maker-minimum", 480, 400);
        }
        finally { settings.Close(); }
    }

    private static void Render(FrameworkElement root, string evidence, string name, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(Path.Combine(evidence, name + ".png"));
        encoder.Save(output);
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
