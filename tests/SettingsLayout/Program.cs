using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TerminalNotes;

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int exitCode = 1;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            string artifacts = Path.Combine(Path.GetTempPath(), "TerminalNotes.SettingsLayout", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(artifacts);
            TerminalProfilesDialog? settings = null;
            try
            {
                string missingWorkspace = Path.Combine(artifacts, "workspace-not-created");
                settings = new TerminalProfilesDialog(missingWorkspace, true, diagnosticSettingsPath: Path.Combine(artifacts, "settings.json"));
                ShowVirtual(settings);
                await Flush();
                var scroll = (ScrollViewer)settings.FindName("SettingsScrollViewer");
                var workspace = (TextBox)settings.FindName("DefaultWorkspaceInput");
                var toggle = (CheckBox)settings.FindName("GlobalAutoModeToggle");
                var footer = (StackPanel)settings.FindName("SettingsFooter");
                Check(settings.Width >= 600 && settings.Width <= 700 && settings.SizeToContent == SizeToContent.Height && settings.ActualHeight >= settings.MinHeight, "vertical settings width with height fitted to its content");
                Check(Grid.GetRow((FrameworkElement)settings.FindName("ToolsColumn")) == 1, "tools and instructions sit below the work settings in one column");
                Check(scroll.ScrollableHeight < 1, "all normal settings are visible without scrolling");
                Check(settings.MaxWidth < double.PositiveInfinity && settings.MaxHeight < double.PositiveInfinity,
                    "settings size is bounded by the current monitor work area");
                CheckFooter(settings, footer);
                CheckLabel(settings, "EditGlobalInstructionsButton", "전역 지침 편집");
                CheckLabel(settings, "EditPlanMakerInstructionsButton", "플랜메이커 지침 편집");
                CheckLabel(settings, "EditOpenCodeConfigButton", "OpenCode 설정 편집");
                string[] settingsText = TextBlocks(settings).Select(text => text.Text).ToArray();
                Check(settingsText.Contains("OpenCode 설치") &&
                    !settingsText.Any(text => text.Contains("WSL", StringComparison.OrdinalIgnoreCase)),
                    "installation label identifies OpenCode without requiring WSL");
                SaveImage(settings, Path.Combine(artifacts, "settings-normal.png"));

                // A user drag-resize switches SizeToContent to Manual; do the same before resizing in code.
                settings.SizeToContent = SizeToContent.Manual;
                settings.Width = settings.MinWidth;
                settings.Height = settings.MinHeight;
                await Flush();
                Check(scroll.ScrollableHeight > 0, $"minimum settings scroll instead of clipping (window {settings.ActualWidth:0}x{settings.ActualHeight:0}, extent {scroll.ExtentHeight:0}, viewport {scroll.ViewportHeight:0})");
                Check(Grid.GetRow((FrameworkElement)settings.FindName("ToolsColumn")) == 1, "minimum settings keep the single column");
                CheckFooter(settings, footer);
                scroll.ScrollToBottom();
                await Flush();
                CheckVisibleInScroll((Button)settings.FindName("AnalyzeLogsButton"), scroll);
                CheckVisibleInScroll((Button)settings.FindName("EditOpenCodeConfigButton"), scroll);
                CheckFooter(settings, footer);
                SaveImage(settings, Path.Combine(artifacts, "settings-minimum.png"));
                Check(workspace.Text == missingWorkspace && toggle.IsChecked == true, "resizing preserves unsaved settings");
                Check(!Directory.Exists(missingWorkspace) && !File.Exists(Path.Combine(artifacts, "settings.json")),
                    "opening and resizing do not create workspace or settings");
                Check(!((FrameworkElement)settings.FindName("FirstRunWorkModeSection")).IsVisible &&
                    !((RadioButton)settings.FindName("BasicWorkModeRadio")).IsVisible &&
                    !((RadioButton)settings.FindName("AdvancedWorkModeRadio")).IsVisible,
                    "regular settings keep first-run mode options and descriptions hidden");
                await VerifyFirstRunModeOptionsAsync(artifacts);

                settings.Width = 840;
                settings.Height = 720;
                scroll.ScrollToTop();
                var installer = (Button)settings.FindName("OpenAutoInstallButton");
                var status = (TextBlock)settings.FindName("AutoInstallStatusText");
                AutoInstall.Reset(installed: false);
                installer.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!installer.IsEnabled && AutoInstall.RunCount == 1, "installer runs once and disables duplicate clicks");
                typeof(TerminalProfilesDialog).GetMethod("OpenAutoInstallButton_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(settings, [installer, new RoutedEventArgs()]);
                Check(AutoInstall.RunCount == 1, "busy guard rejects duplicate installer invocation");
                AutoInstall.Complete();
                await Until(() => installer.IsEnabled);
                Check(AutoInstall.CheckCount == 1 && status.Text.Contains("실행 확인 실패") && !status.Text.Contains("실행 확인 완료"),
                    "exit zero does not imply installed; post-install detection determines status");
                Check(workspace.Text == missingWorkspace && toggle.IsChecked == true, "installer preserves unsaved settings");
                AutoInstall.Reset(installed: true);
                installer.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                AutoInstall.Complete();
                await Until(() => installer.IsEnabled);
                Check(AutoInstall.CheckCount == 1 && status.Text.Contains("실행 확인 완료"), "verified installation is reported");
                AutoInstall.Reset(installed: true);
                installer.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                AutoInstall.Complete(AutoInstallStatus.Failed, "외부 설치 도구 컴파일 실패");
                await Until(() => installer.IsEnabled);
                Check(AutoInstall.CheckCount == 1 && status.Text.Contains("외부 설치 도구 컴파일 실패") &&
                    status.Text.Contains("실행 확인 완료"), "failed installer remains visible when OpenCode was already available");

                using (var editor = new WindowScope(new GlobalAgentInstructionsDialog()))
                {
                    CheckEditor(editor.Window, "전역 지침");
                    SaveImage(editor.Window, Path.Combine(artifacts, "global-instructions.png"));
                }
                using (var editor = new WindowScope(GlobalAgentInstructionsDialog.CreatePlanMaker()))
                {
                    CheckEditor(editor.Window, "플랜메이커 지침");
                    SaveImage(editor.Window, Path.Combine(artifacts, "planmaker-instructions.png"));
                }
                using (var editor = new WindowScope(GlobalAgentInstructionsDialog.CreateOpenCodeConfig()))
                {
                    CheckEditor(editor.Window, "OpenCode 설정");
                    SaveImage(editor.Window, Path.Combine(artifacts, "opencode-config.png"));
                }
                Check(GlobalAgentInstructions.SaveCount == 0, "opening instruction editors never writes instructions");
                AutoInstall.Reset(installed: false);
                installer.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                settings.Close();
                await Until(() => AutoInstall.CancellationObserved);
                Check(AutoInstall.CheckCount == 0, "closing settings cancels installer and prevents a later recheck");
                Console.WriteLine($"PASS {_checks} settings layout, naming, installer detection, cancellation and no-write checks");
                Console.WriteLine("Screenshots: " + artifacts);
                exitCode = 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); }
            finally { settings?.Close(); app.Shutdown(); }
        }));
        app.Run();
        return exitCode;
    }

    private static async Task VerifyFirstRunModeOptionsAsync(string artifacts)
    {
        // First run asks only for the work folder; it always starts in 일반 mode with Auto Mode on.
        foreach (bool initialBasic in new[] { true, false })
        {
            string workspace = Path.Combine(artifacts, initialBasic ? "first-run-basic" : "first-run-advanced");
            var firstRun = new TerminalProfilesDialog(workspace, false, firstRun: true,
                diagnosticSettingsPath: Path.Combine(artifacts, "first-run-settings.json"), basicWorkMode: initialBasic);
            try
            {
                ShowVirtual(firstRun);
                await Flush();
                var input = (TextBox)firstRun.FindName("DefaultWorkspaceInput");
                var browse = (Button)firstRun.FindName("WorkspaceBrowseButton");
                var hint = (FrameworkElement)firstRun.FindName("WorkspaceFirstRunHint");
                var auto = (CheckBox)firstRun.FindName("GlobalAutoModeToggle");
                Check(input.IsVisible && browse.IsVisible && hint.IsVisible && input.Text == @"C:\opencode",
                    "first-run setup shows the work folder prefilled with C:\\opencode and the browse button");
                Check(!((FrameworkElement)firstRun.FindName("FirstRunWorkModeSection")).IsVisible &&
                    !((FrameworkElement)firstRun.FindName("AutoModeSection")).IsVisible &&
                    !((FrameworkElement)firstRun.FindName("ToolsColumn")).IsVisible &&
                    !((FrameworkElement)firstRun.FindName("TerminalAgentSection")).IsVisible,
                    "first-run setup hides mode choice, Auto Mode, tools and per-terminal agents");
                Check(firstRun.UseBasicWorkMode && !firstRun.UsePlanWorkMode && auto.IsChecked == true,
                    "first-run setup starts in 일반 mode with Auto Mode on whatever the previous mode was");
                Check(firstRun.Width <= 600, "first-run setup is a narrow window");
                SaveImage(firstRun, Path.Combine(artifacts, initialBasic ? "first-run-basic.png" : "first-run-advanced.png"));
                Check(!Directory.Exists(workspace) && !File.Exists(Path.Combine(artifacts, "first-run-settings.json")),
                    "opening first-run setup does not save settings or create its workspace");
            }
            finally { firstRun.Close(); }
        }
    }

    private static void ShowVirtual(Window window)
    {
        window.Opacity = 0;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Show();
        window.UpdateLayout();
    }

    private static void CheckEditor(GlobalAgentInstructionsDialog editor, string title)
    {
        Check(editor.Title == title && ((TextBlock)editor.FindName("CaptionTitle")).Text == title &&
            ((TextBlock)editor.FindName("HeadingText")).Text == title, title + " uses consistent titles");
        Check(AutomationProperties.GetName((TextBox)editor.FindName("InstructionsInput")) == title + " 내용",
            title + " uses consistent accessible name");
    }

    private static void CheckLabel(TerminalProfilesDialog settings, string name, string label)
    {
        var button = (Button)settings.FindName(name);
        // Editor buttons read "지침 편집" or "설정 편집" beside their own heading; tooltip and accessibility keep the full name.
        string visible = !name.StartsWith("Edit", StringComparison.Ordinal) ? label :
            name.EndsWith("InstructionsButton", StringComparison.Ordinal) ? "지침 편집" : name.EndsWith("ConfigButton", StringComparison.Ordinal) ? "설정 편집" : label;
        Check(Equals(button.Content, visible) && Equals(button.ToolTip, label) && AutomationProperties.GetName(button) == label,
            label + " label, tooltip and accessibility agree");
    }

    private static IEnumerable<TextBlock> TextBlocks(DependencyObject parent)
    {
        if (parent is TextBlock text) yield return text;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (TextBlock child in TextBlocks(VisualTreeHelper.GetChild(parent, index)))
                yield return child;
    }

    private static void CheckFooter(Window window, FrameworkElement footer)
    {
        var bounds = footer.TransformToAncestor(window).TransformBounds(new Rect(footer.RenderSize));
        Check(bounds.Top >= 0 && bounds.Bottom <= window.ActualHeight && footer.ActualHeight > 30,
            "save and cancel footer stays completely visible");
        var credit = (FrameworkElement)window.FindName("CreditText");
        var creditBounds = credit.TransformToAncestor(window).TransformBounds(new Rect(credit.RenderSize));
        Check(creditBounds.Top >= bounds.Bottom - 0.5 && creditBounds.Bottom <= window.ActualHeight,
            "the credit sits faintly on the bottom edge, below the buttons");
    }

    private static void CheckVisibleInScroll(FrameworkElement element, ScrollViewer scroll)
    {
        var bounds = element.TransformToAncestor(scroll).TransformBounds(new Rect(element.RenderSize));
        Check(bounds.Top >= -1 && bounds.Bottom <= scroll.ActualHeight + 1 && bounds.Right <= scroll.ActualWidth + 1,
            element.Name + " can be fully reached by scrolling");
    }

    private static void SaveImage(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        double width = content.ActualWidth + content.Margin.Left + content.Margin.Right;
        double height = content.ActualHeight + content.Margin.Top + content.Margin.Bottom;
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(content), null,
                new Rect(content.Margin.Left, content.Margin.Top, content.ActualWidth, content.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static async Task Flush() => await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    private static async Task Until(Func<bool> predicate)
    {
        for (int attempt = 0; attempt < 200 && !predicate(); attempt++) await Task.Delay(10);
        Check(predicate(), "asynchronous operation settles");
        await Flush();
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }
    private sealed class WindowScope : IDisposable
    {
        public GlobalAgentInstructionsDialog Window { get; }
        public WindowScope(GlobalAgentInstructionsDialog window) { Window = window; ShowVirtual(window); }
        public void Dispose() => Window.Close();
    }
}

namespace TerminalNotes
{
    // Isolated seams deliberately prevent production settings, instructions, installers or app startup from running.
    internal static class MainWindow { public static string DefaultSettingsPath => "unused-settings.json"; }
    internal sealed class DiagnosticLogAnalysisDialog : Window { public DiagnosticLogAnalysisDialog(string path) { } }
    public enum AutoInstallStatus { NotConnected, Completed, Failed }
    public sealed record AutoInstallResult(AutoInstallStatus Status, string Message);
    public sealed record AutoInstallDetectionResult(bool IsInstalled, string Message);
    internal static class AutoInstall
    {
        private static TaskCompletionSource<AutoInstallResult> _completion = new();
        private static bool _installed;
        public static int RunCount, CheckCount;
        public static bool CancellationObserved;
        public static void Reset(bool installed)
        {
            _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _installed = installed;
            RunCount = CheckCount = 0;
            CancellationObserved = false;
        }
        public static void Complete(AutoInstallStatus status = AutoInstallStatus.Completed, string message = "설치 도구 종료") =>
            _completion.SetResult(new(status, message));
        public static async Task<AutoInstallResult> RunAsync(CancellationToken token)
        {
            RunCount++;
            try { return await _completion.Task.WaitAsync(token); }
            catch (OperationCanceledException) { CancellationObserved = true; throw; }
        }
        public static Task<AutoInstallDetectionResult> CheckAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            CheckCount++;
            return Task.FromResult(new AutoInstallDetectionResult(_installed, _installed ? "OpenCode 테스트 준비" : "OpenCode 실행 확인 실패"));
        }
    }
    internal static class GlobalAgentInstructions
    {
        public enum SaveResult { Saved, Conflict, Busy }
        public static int SaveCount;
        public static string FilePath => "isolated-global-instructions.md";
        public static string ReadCurrent() => "기존 전역 지침 내용";
        public static SaveResult TrySave(string expected, string contents) { SaveCount++; return SaveResult.Saved; }
    }
}

namespace TerminalNotes.Services
{
    internal static class PlanMakerInstructions
    {
        public static string FilePath => "isolated-planmaker-instructions.md";
        public static int MaximumLength => 16384;
        public static string ReadCurrent() => "기존 플랜메이커 지침 내용";
        public static GlobalAgentInstructions.SaveResult TrySave(string expected, string contents) => GlobalAgentInstructions.TrySave(expected, contents);
    }

    internal static class OpenCodeConfigFile
    {
        internal const string Title = "OpenCode 설정";
        internal static string Description => "";
        internal static string FilePath => "isolated-opencode.jsonc";
        internal static string ReadCurrent() => "{ }";
        internal static GlobalAgentInstructions.SaveResult TrySave(string expected, string contents) => GlobalAgentInstructions.TrySave(expected, contents);
    }
}
