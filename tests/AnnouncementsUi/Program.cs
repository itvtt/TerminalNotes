using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class Program
{
    private static int _failure;
    private static readonly BindingFlags HiddenInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { await VerifyAsync(); }
            catch (Exception error) { _failure = 1; Console.Error.WriteLine(error); }
            finally { app.Shutdown(); }
        };
        app.Run();
        return _failure;
    }

    private static async Task VerifyAsync()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes-announcements-ui-" + Guid.NewGuid().ToString("N"));
        string artifacts = Path.Combine(Path.GetTempPath(), "TerminalNotes-announcements-ui-images-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        Directory.CreateDirectory(artifacts);
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(fixture, "appdata"));
        using var lifetime = new CancellationTokenSource();
        var owner = new Window { Title = "Announcement test owner", Width = 500, Height = 400,
            Background = Brushes.Black, ShowInTaskbar = false };
        var ownerEditorStyle = new Style(typeof(TextBox));
        var ownerCaret = new SolidColorBrush(Color.FromRgb(230, 202, 149));
        var ownerSelection = new SolidColorBrush(Color.FromRgb(102, 82, 57));
        ownerEditorStyle.Setters.Add(new Setter(TextBox.CaretBrushProperty, ownerCaret));
        ownerEditorStyle.Setters.Add(new Setter(TextBox.SelectionBrushProperty, ownerSelection));
        ownerEditorStyle.Setters.Add(new Setter(TextBox.SelectionTextBrushProperty, Brushes.White));
        owner.Resources[typeof(TextBox)] = ownerEditorStyle;
        var windows = new List<Window>();
        try
        {
            owner.Show();
            string source = Path.Combine(fixture, "공지.md");
            const string original = "# 공지\n\n## 2026-10-19 안내\n모든 사용자가 읽을 수 있는 공지입니다.\n<script>alert('literal')</script>\n[링크](https://example.invalid)";
            File.WriteAllText(source, original);
            Assembly assembly = typeof(TerminalNotes.MainWindow).Assembly;
            Type serviceType = assembly.GetType("TerminalNotes.Services.AnnouncementService", true)!;
            Type dialogType = assembly.GetType("TerminalNotes.AnnouncementDialog", true)!;

            async Task<Window> OpenAsync(string account, string user)
            {
                object service = Activator.CreateInstance(serviceType, HiddenInstance, null,
                    [source, Path.Combine(fixture, user), (Func<string>)(() => account)], null)!;
                Task read = (Task)serviceType.GetMethod("ReadAsync", HiddenInstance)!.Invoke(service, [lifetime.Token])!;
                await read;
                object snapshot = read.GetType().GetProperty("Result")!.GetValue(read)!;
                var dialog = (Window)Activator.CreateInstance(dialogType, HiddenInstance, null,
                    [owner, service, snapshot, lifetime.Token], null)!;
                dialog.Width = dialog.MinWidth;
                dialog.Height = dialog.MinHeight;
                windows.Add(dialog);
                dialog.Show();
                await IdleAsync();
                return dialog;
            }

            Window reader = await OpenAsync(@"DOMAIN\reader", "reader-data");
            Require(Find(reader, "AnnouncementAdd") is null && Find(reader, "AnnouncementEdit") is null &&
                Find(reader, "AnnouncementSave") is null, "reader must not receive mutation controls");
            var readView = (FlowDocumentScrollViewer)Find(reader, "AnnouncementContent")!;
            Require(new TextRange(readView.Document.ContentStart, readView.Document.ContentEnd).Text.Contains("<script>"),
                "HTML is displayed as literal text");
            Require(!readView.Document.Blocks.OfType<Paragraph>().SelectMany(block => block.Inlines.OfType<Hyperlink>()).Any(),
                "Markdown never creates active external links");
            Require(!Directory.Exists(Path.Combine(fixture, "reader-data")), "dialog preview does not create real or test read-state files");
            VerifyVisibleButtons(reader);
            Capture(reader, Path.Combine(artifacts, "reader-minimum.png"));
            reader.Close();

            Window admin = await OpenAsync(@"DOMAIN\TEST_07", "editor-data");
            Require(Find(admin, "AnnouncementAdd")?.Visibility == Visibility.Visible &&
                Find(admin, "AnnouncementEdit")?.Visibility == Visibility.Visible, "authorized account has explicit add and edit buttons");
            VerifyVisibleButtons(admin);
            Capture(admin, Path.Combine(artifacts, "editor-read-minimum.png"));
            Click(admin, "AnnouncementAdd");
            var input = (TextBox)Find(admin, "AnnouncementEditor")!;
            Require(ReferenceEquals(input.Style, ownerEditorStyle) && ReferenceEquals(input.CaretBrush, ownerCaret) &&
                ReferenceEquals(input.SelectionBrush, ownerSelection), "editor inherits the owner's TextBox style and its caret/selection colors");
            string today = "## " + DateTime.Today.ToString("yyyy-MM-dd");
            Require(input.Visibility == Visibility.Visible && input.Text.StartsWith("# 공지\n\n" + today, StringComparison.Ordinal) &&
                input.Text.Contains(original["# 공지\n\n".Length..]) && input.Text.IndexOf(today, StringComparison.Ordinal) < input.Text.IndexOf("## 2026-10-19", StringComparison.Ordinal),
                "add retains the board and starts a dated Markdown entry on top (newest first)");
            Click(admin, "AnnouncementCancel");
            Require(File.ReadAllText(source) == original, "cancelled add preserves the source");
            Click(admin, "AnnouncementEdit");
            Require(input.Text == original, "edit starts from the actual displayed board");
            input.Text += "\n\n## 추가 공지\n저장 검증";
            VerifyVisibleButtons(admin);
            Capture(admin, Path.Combine(artifacts, "editor-edit-minimum.png"));

            bool declined = false;
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            var dismiss = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            dismiss.Tick += (_, _) =>
            {
                Window? confirmation = Application.Current.Windows.Cast<Window>().FirstOrDefault(window => window.Owner == admin);
                if (confirmation is not null && Find(confirmation, "AppMessageCancel") is Button cancel)
                {
                    dismiss.Stop(); declined = true;
                    cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else if (DateTime.UtcNow >= deadline) { dismiss.Stop(); confirmation?.Close(); }
            };
            dismiss.Start();
            admin.Close();
            dismiss.Stop();
            Require(declined && admin.IsVisible && input.Text.Contains("저장 검증"), "declining unsaved close retains the draft");
            Click(admin, "AnnouncementSave");
            await UntilAsync(() => input.Visibility == Visibility.Collapsed &&
                Find(admin, "AnnouncementAdd")?.Visibility == Visibility.Visible);
            Require(File.ReadAllText(source).Contains("저장 검증"), "save completes before reading the updated source");
            Require(File.ReadAllText(source).Contains(original), "save roundtrip preserves previous board content");
            VerifyVisibleButtons(admin);
            Capture(admin, Path.Combine(artifacts, "editor-saved-minimum.png"));
            admin.Close();
            Console.WriteLine("PASS: announcement reader/editor controls, literal rendering, minimum size, add/cancel, unsaved close and save roundtrip.");
            Console.WriteLine("PNG artifacts: " + artifacts);
        }
        finally
        {
            lifetime.Cancel(); // Cleanup never prompts about the isolated draft after a failed assertion.
            foreach (Window window in windows) window.Close();
            owner.Close();
            AppContext.SetData("TerminalNotes.DataDirectory", null);
            Directory.Delete(fixture, recursive: true);
        }
    }

    private static FrameworkElement? Find(DependencyObject element, string id)
    {
        if (element is FrameworkElement framework && AutomationProperties.GetAutomationId(framework) == id) return framework;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (Find(VisualTreeHelper.GetChild(element, index), id) is { } found) return found;
        return null;
    }

    private static void Click(Window window, string id) => ((Button)Find(window, id)!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void VerifyVisibleButtons(Window window)
    {
        window.UpdateLayout();
        foreach (string id in new[] { "AnnouncementAdd", "AnnouncementEdit", "AnnouncementSave", "AnnouncementCancel", "AnnouncementClose" })
        {
            if (Find(window, id) is not Button { IsVisible: true } button) continue;
            Point position = button.TranslatePoint(new Point(), window);
            Require(button.ActualWidth >= 60 && button.ActualHeight >= 25 && position.X >= 0 && position.Y >= 0 &&
                position.X + button.ActualWidth <= window.ActualWidth && position.Y + button.ActualHeight <= window.ActualHeight,
                id + " fits the minimum-size dialog");
        }
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static async Task IdleAsync() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new Exception("Announcement UI save timed out.");
            await Task.Delay(25);
        }
        await IdleAsync();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
