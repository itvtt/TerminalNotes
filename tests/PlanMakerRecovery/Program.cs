using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TerminalNotes;
using TerminalNotes.Services;

internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks;

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int result = 1;
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(); result = 0; }
            catch (Exception exception) { Console.Error.WriteLine("FAIL " + exception); }
            finally { app.Shutdown(); }
        };
        app.Run();
        return result;
    }

    private static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "TerminalNotes PlanMakerRecovery " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        object? previousData = AppContext.GetData("TerminalNotes.DataDirectory");
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(root, "data"));
        MainWindow? window = null;
        try
        {
            window = new MainWindow(Path.Combine(root, "settings.json"), autoStartCodex: false);
            // Create only its HWND: Loaded/startup never runs, so no scheduler, PTY or model can start.
            new WindowInteropHelper(window).EnsureHandle();
            VerifyUserWarningRouting(root);

            var shortPlan = await Fixture.CreateAsync(window, root, "short", "# Goal\nKeep the existing goal.", "Add CSV export.");
            using (var modal = new ModalDriver(accept: false))
            {
                string? prompt = shortPlan.Prepare();
                Check(prompt is not null && prompt.Contains(shortPlan.Wish, StringComparison.Ordinal) &&
                    prompt.Contains(shortPlan.OriginalDraft, StringComparison.Ordinal), "Short request lost user input.");
                Check(modal.Count == 0, "Short requests must not ask for organization approval.");
                shortPlan.AssertUnchanged();
            }

            // A long plan alone no longer needs approval: PlanMaker receives a bounded outline of it.
            var longOnly = await Fixture.CreateAsync(window, root, "long-plan", LongPlan(), "Add CSV export.");
            using (var modal = new ModalDriver(accept: false))
            {
                string? prompt = longOnly.Prepare();
                Check(prompt is not null && modal.Count == 0 && prompt.Length <= 32 * 1024 &&
                    prompt.Contains("(긴 플랜이라 항목을 줄여 보여 줍니다", StringComparison.Ordinal) && prompt.Contains(longOnly.Wish, StringComparison.Ordinal),
                    "A long plan must yield a bounded prompt without asking for organization.");
                longOnly.AssertUnchanged();
            }

            // Organization approval remains for a request that is itself larger than the normal limit.
            var canceled = await Fixture.CreateAsync(window, root, "cancel", LongPlan(), BigWish);
            using (var modal = new ModalDriver(accept: false))
            {
                Check(canceled.Prepare() is null, "Cancel must stop prompt preparation.");
                modal.AssertClickedOnce();
                canceled.AssertUnchanged();
            }

            var accepted = await Fixture.CreateAsync(window, root, "accept-readonly", LongPlan(), BigWish);
            File.SetAttributes(accepted.PlanPath, File.GetAttributes(accepted.PlanPath) | FileAttributes.ReadOnly);
            try
            {
                using var modal = new ModalDriver(accept: true);
                string prompt = accepted.Prepare() ?? throw new Exception("Accepted recovery returned no prompt.");
                modal.AssertClickedOnce();
                Check(prompt.Length > 32 * 1024 && prompt.Length <= 256 * 1024, "Expanded recovery did not retain a prompt above the old limit.");
                string organized = Property<string>(accepted.Edit, "Draft");
                Check(organized != accepted.OriginalDraft, "Accepted recovery did not organize the unsaved draft.");
                Check(Property<string>(accepted.Session, "InitialDraft") == organized && accepted.Editor.Text == organized,
                    "Session and visible editor did not receive the same organized draft.");
                // The host-side draft keeps every requirement; the model sees a bounded outline of it.
                for (int index = 0; index < 600; index++)
                    Check(organized.Contains(Requirement(index), StringComparison.Ordinal), "Recovery clipped distinct requirement " + index);
                Check(organized.Contains(CodeBlock, StringComparison.Ordinal), "Recovery changed a fenced code block.");
                Check(organized.Contains("- Tail condition must survive.", StringComparison.Ordinal) &&
                    prompt.Contains(BigWish, StringComparison.Ordinal), "Recovery clipped the final condition or the request.");
                // One live condition/category plus the same literal text inside the preserved code fence.
                Check(Count(organized, "- Repeated acceptance condition.") == 2 && Count(organized, "## Delivery") == 2,
                    "Accepted recovery failed to consolidate duplicate categories/conditions.");
                accepted.AssertDiskAndWishUnchanged();
                Check(ReferenceEquals(accepted.OriginalSnapshot, Property<object>(accepted.Edit, "Snapshot")),
                    "Unsaved recovery replaced the disk snapshot.");
                Check(File.GetAttributes(accepted.PlanPath).HasFlag(FileAttributes.ReadOnly), "Recovery changed file permissions.");
            }
            finally { File.SetAttributes(accepted.PlanPath, FileAttributes.Normal); }

            foreach (string mutation in new[] { "draft", "snapshot", "ownership" })
            {
                var stale = await Fixture.CreateAsync(window, root, "stale-" + mutation, LongPlan(), BigWish);
                bool owns = true;
                string newerDraft = stale.OriginalDraft + "\n- New edit made while confirmation was open.";
                var newerSnapshot = new TabDocumentSnapshot(stale.PlanPath, "A refreshed disk snapshot.", true);
                using var modal = new ModalDriver(accept: true, beforeClick: () =>
                {
                    if (mutation == "draft") { SetProperty(stale.Edit, "Draft", newerDraft); stale.Editor.Text = newerDraft; }
                    if (mutation == "snapshot") SetProperty(stale.Edit, "Snapshot", newerSnapshot);
                    if (mutation == "ownership") owns = false;
                });
                Exception? failure = Capture(() => stale.Prepare(() => owns));
                modal.AssertClickedOnce();
                Check(mutation == "ownership" ? failure is OperationCanceledException : failure is InvalidOperationException,
                    "Stale " + mutation + " was not rejected: " + failure?.GetType().Name);
                Check(Property<string>(stale.Edit, "Draft") == (mutation == "draft" ? newerDraft : stale.OriginalDraft),
                    "Recovery overwrote the newer " + mutation + " state.");
                Check(Property<string>(stale.Session, "InitialDraft") == stale.OriginalDraft, "Rejected recovery mutated the captured session draft.");
                Check(ReferenceEquals(Property<object>(stale.Edit, "Snapshot"), mutation == "snapshot" ? newerSnapshot : stale.OriginalSnapshot),
                    "Rejected recovery overwrote the latest document snapshot.");
                stale.AssertDiskAndWishUnchanged();
            }

            var oversized = await Fixture.CreateAsync(window, root, "native-limit", "## Delivery\n- Keep me.\n## Delivery\n- Keep me.", new string('W', 256 * 1024));
            using (var modal = new ModalDriver(accept: true))
            {
                Exception? failure = Capture(() => oversized.Prepare());
                modal.AssertClickedOnce();
                Check(failure is InvalidOperationException && failure.GetType().Name == "PlanMakerPromptTooLongException",
                    "A full prompt above the native limit was not rejected with the typed length error.");
                oversized.AssertUnchanged();
            }

            Check(((TabControl)window.FindName("TerminalTabs")).Items.Count == 0 && !(bool)Field(window, "_windowInitialized")!,
                "The isolated recovery tests unexpectedly started the application or created a terminal.");
            Console.WriteLine($"PASS {_checks} checks: real MainWindow recovery, cancel, expanded read-only draft, stale draft/snapshot/ownership, native limit; no model/PTY/live-app access.");
        }
        finally
        {
            if (window is not null) { Call(window, "DisposeResources"); window.Close(); }
            AppContext.SetData("TerminalNotes.DataDirectory", previousData);
        }
    }

    private const string CodeBlock = "```text\n## Delivery\n- Repeated acceptance condition.\n```";
    private static readonly string BigWish = "Keep this unsaved wish and every existing condition. " + new string('W', 40 * 1024);
    private static void VerifyUserWarningRouting(string root)
    {
        using var pane = new TerminalPane(root, useProjectContext: false);
        var statuses = new List<string>();
        var warnings = new List<string>();
        pane.StatusChanged += statuses.Add;
        Action<string> warning = warnings.Add;
        pane.UserWarning += warning;
        Call(pane, "ReportUserWarning", "Visible input warning.");
        Check(warnings.SequenceEqual(new[] { "Visible input warning." }) && statuses.Count == 0,
            "A handled user warning must be delivered exactly once without duplicate background status.");
        pane.UserWarning -= warning;
        Call(pane, "ReportUserWarning", "Fallback warning.");
        Check(warnings.Count == 1 && statuses.SequenceEqual(new[] { "Fallback warning." }) && !pane.IsRunning,
            "A user warning without a subscriber must retain the status fallback without starting a terminal.");
    }
    private static string Requirement(int index) => $"- Requirement {index:D4}: preserve condition {index:D4} and its exact evidence, boundary and acceptance criteria.";
    private static string LongPlan() => "# Existing plan\n\n## Delivery\n" + string.Join("\n", Enumerable.Range(0, 600).Select(Requirement)) +
        "\n- Repeated acceptance condition.\n\n" + CodeBlock + "\n\n## Delivery\n- Repeated acceptance condition.\n- Tail condition must survive.\n";

    private sealed class Fixture(MainWindow window, object session, object edit, object input, string planPath,
        byte[] disk, string originalDraft, string wish, object originalSnapshot)
    {
        internal object Session => session;
        internal object Edit => edit;
        internal string PlanPath => planPath;
        internal string OriginalDraft => originalDraft;
        internal string Wish => wish;
        internal object OriginalSnapshot => originalSnapshot;
        internal TextBox Editor => (TextBox)Field(window, "_tabDocumentEditor")!;

        internal static async Task<Fixture> CreateAsync(MainWindow window, string root, string name, string draft, string wish)
        {
            string project = Path.Combine(root, name);
            string path = TerminalTabDocumentStore.GetProjectPlanPath(project);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "# Saved disk plan\r\nThis exact disk document must remain untouched.\r\n");
            object session = await LoadAsync(window, project, path);
            object edit = Property<object>(session, "Edit");
            SetProperty(edit, "Draft", draft);
            session = await LoadAsync(window, project, path);
            Type inputType = typeof(MainWindow).GetNestedType("ProjectInputDraft", BindingFlags.NonPublic)!;
            object input = Activator.CreateInstance(inputType, nonPublic: true)!;
            inputType.GetField("Text")!.SetValue(input, wish);
            inputType.GetField("Revision")!.SetValue(input, 7L);
            ((IDictionary)Field(window, "_projectInputDrafts")!).Add(project, input);
            SetField(window, "_selectedProjectPath", project);
            SetField(window, "_activeTabDocumentPath", path);
            Call(window, "ShowTabDocumentDraft", edit);
            Call(window, "UpdateProjectInputComposer");
            return new(window, session, edit, input, path, File.ReadAllBytes(path), draft, wish, Property<object>(edit, "Snapshot"));
        }

        private static async Task<object> LoadAsync(MainWindow window, string project, string path)
        {
            var task = (Task)Call(window, "LoadAutomationPlanAsync", null, project, 1, path, false)!;
            await task;
            return task.GetType().GetProperty("Result")!.GetValue(task)!;
        }

        internal string? Prepare(Func<bool>? owns = null) => (string?)Call(window, "PreparePlanMakerPrompt", session, wish, "Ask one concrete question at a time.", owns ?? (() => true));
        internal void AssertDiskAndWishUnchanged()
        {
            Check(disk.SequenceEqual(File.ReadAllBytes(planPath)), "Prompt preparation modified the saved disk plan.");
            Check((string)input.GetType().GetField("Text")!.GetValue(input)! == wish &&
                (long)input.GetType().GetField("Revision")!.GetValue(input)! == 7L &&
                ((TextBox)Field(window, "_projectInputEditor")!).Text == wish,
                "Prompt preparation changed the unsent wish or revision.");
        }
        internal void AssertUnchanged()
        {
            AssertDiskAndWishUnchanged();
            Check(Property<string>(edit, "Draft") == originalDraft && Property<string>(session, "InitialDraft") == originalDraft && Editor.Text == originalDraft,
                "Canceled/rejected prompt preparation modified the unsaved draft.");
            Check(ReferenceEquals(originalSnapshot, Property<object>(edit, "Snapshot")), "Canceled/rejected preparation changed the snapshot.");
        }
    }

    private sealed class ModalDriver : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private Exception? _failure;
        private readonly DateTime _deadline = DateTime.UtcNow.AddSeconds(10);
        internal int Count { get; private set; }
        internal ModalDriver(bool accept, Action? beforeClick = null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(20) };
            _timer.Tick += (_, _) =>
            {
                Window? dialog = Application.Current.Windows.Cast<Window>().SingleOrDefault(item => AutomationProperties.GetAutomationId(item) == "AppMessageDialog");
                if (dialog is null) return;
                try
                {
                    if (DateTime.UtcNow > _deadline) throw new TimeoutException("The recovery confirmation could not be exercised.");
                    _timer.Stop();
                    Button button = Descendants(dialog).OfType<Button>().Single(item =>
                        AutomationProperties.GetAutomationId(item) == (accept ? "AppMessageConfirm" : "AppMessageCancel"));
                    beforeClick?.Invoke();
                    Count++;
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception exception) { _failure = exception; _timer.Stop(); dialog.Close(); }
            };
            _timer.Start();
        }
        internal void AssertClickedOnce()
        {
            if (_failure is not null) ExceptionDispatchInfo.Capture(_failure).Throw();
            Check(Count == 1, "Recovery must show exactly one usable confirmation.");
        }
        public void Dispose() => _timer.Stop();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static int Count(string text, string value) => (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, PrivateInstance)!.GetValue(instance);
    private static void SetField(object instance, string name, object? value) => instance.GetType().GetField(name, PrivateInstance)!.SetValue(instance, value);
    private static T Property<T>(object instance, string name) => (T)instance.GetType().GetProperty(name)!.GetValue(instance)!;
    private static void SetProperty(object instance, string name, object value) => instance.GetType().GetProperty(name)!.SetValue(instance, value);
    private static object? Call(object instance, string name, params object?[] args)
    {
        try { return instance.GetType().GetMethod(name, PrivateInstance)!.Invoke(instance, args); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        { ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
    }
    private static Exception? Capture(Action action) { try { action(); return null; } catch (Exception exception) { return exception; } }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); _checks++; }
}
