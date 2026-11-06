using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
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

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string evidence = Path.Combine(Path.GetTempPath(), "TerminalNotes PlanMaker layout " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(evidence, "app-data"));
        var owner = new Window();
        try
        {
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XElement resources = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"))
                .Root!.Element(presentation + "Window.Resources")!;
            resources.Name = presentation + "ResourceDictionary";
            resources.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
            owner.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
            owner.FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
            owner.FontSize = 14;
            owner.ShowActivated = false;
            owner.ShowInTaskbar = false;
            owner.WindowStartupLocation = WindowStartupLocation.Manual;
            owner.Left = -10000; owner.Top = -10000; owner.Width = 1; owner.Height = 1;
            owner.Show(); owner.Hide();
            var dialog = new PlanMakerDialog(owner, evidence, "# Reports page\n\n## Goal\nA clear report with filters.\n\n## Acceptance\nExported rows match the selected filters.", CancellationToken.None);
            var draft = Field<TextBox>(dialog, "_draft");
            var status = Field<TextBlock>(dialog, "_status");
            var badge = Field<TextBlock>(dialog, "_statusLabel");
            var root = (Border)dialog.Content;
            var primarySample = new Button { Style = (Style)owner.FindResource("PrimaryButton") };
            Color primaryInk = ((SolidColorBrush)primarySample.Foreground).Color;
            Color[] primarySurfaces = primarySample.Background is GradientBrush primaryGradient
                ? primaryGradient.GradientStops.Select(stop => stop.Color).ToArray()
                : [((SolidColorBrush)primarySample.Background).Color];
            Require(((SolidColorBrush)owner.FindResource("PrimaryBrush")).Color == Color.FromRgb(216, 188, 130) &&
                primarySurfaces.All(surface =>
                    (Math.Max(Luminance(primaryInk), Luminance(surface)) + 0.05) / (Math.Min(Luminance(primaryInk), Luminance(surface)) + 0.05) >= 4.5),
                "Metallic gold primary actions must keep readable ink across every gradient stop.");
            Require(dialog.Background is LinearGradientBrush { IsFrozen: true },
                "Planning backdrop must use a static frozen gradient.");
            var chrome = WindowChrome.GetWindowChrome(dialog);
            Require(dialog.WindowStyle == WindowStyle.SingleBorderWindow && dialog.ResizeMode == ResizeMode.CanResize &&
                dialog.ShowInTaskbar &&
                chrome.CaptionHeight == 36 && chrome.ResizeBorderThickness == SystemParameters.WindowResizeBorderThickness &&
                !chrome.UseAeroCaptionButtons, "Native resize/drag chrome was not configured.");
            foreach (string id in new[] { "PlanMakerMinimize", "PlanMakerMaximizeRestore", "PlanMakerClose" })
            {
                Button button = Descendants(root).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == id);
                Require(button.ToolTip is not null && !string.IsNullOrEmpty(AutomationProperties.GetName(button)) &&
                    WindowChrome.GetIsHitTestVisibleInChrome(button), "Caption control accessibility/hit-testing is missing.");
            }
            foreach ((string text, string label) in new[]
            {
                ("플랜메이커 전용 터미널 연결 중…", "연결 중"),
                ("메모리 여유를 기다리는 중 · 여유 900 MiB", "실행 대기"),
                ("◌ 플랜 만들기 · OpenCode 질문에 답해주세요", "답변 대기"),
                ("플랜메이커 · AI가 질문을 멈췄습니다. 질문 계속 또는 플랜으로 돌아가기를 선택하세요.", "질문 일시정지"),
                ("질문 마무리 중… · 현재 답변 반영을 기다린 뒤 질문을 마칩니다.", "마무리 중"),
                ("플랜메이커 · 현재 답변 반영을 기다린 뒤 질문을 마칩니다.", "마무리 중"),
                ("질문을 마쳤습니다 · 플랜 초안은 유지합니다.", "✓ 플랜 완료"),
                ("플랜메이커 오류 · OpenCode 모델 제공자가 플랜 요청을 거부했습니다.", "제공자 거부"),
                ("플랜메이커 오류 · 설정된 모델을 사용할 수 없습니다.", "오류")
            })
            {
                Call(dialog, "SetPlanningStatus", text);
                Require(status.Text == text && badge.Text == label, "Status text or phase badge changed unexpectedly.");
            }
            Call(dialog, "SetPlanningStatus", "◌ 플랜 만들기 · OpenCode 질문에 답해주세요");
            var footerButtons = new[] { Field<Button>(dialog, "_retry"), Field<Button>(dialog, "_recommendedAnswers"),
                Field<Button>(dialog, "_continue"), Field<Button>(dialog, "_finish") };
            Require(Descendants(root).OfType<Button>().Count(button => button.Content as string == "플랜으로 돌아가기") == 1 &&
                !Descendants(root).OfType<Button>().Any(button => button.Content as string == "질문 마치기") &&
                ReferenceEquals(Descendants(root).OfType<Button>().Single(button => button.Content as string == "플랜으로 돌아가기"), footerButtons[^1]),
                "Finish and return must share exactly one visible primary action.");
            // Measure the widest footer state, including failure/recovery controls, at every size.
            foreach ((double width, double height, string name) in new[] { (1200d, 720d, "normal"), (800d, 500d, "compact"), (800d, 440d, "minimum") })
            {
                foreach (Button button in footerButtons) button.Visibility = Visibility.Visible;
                Field<Button>(dialog, "_recommendedAnswers").Content = "Auto Recommand";
                root.Width = width; root.Height = height;
                root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
                Point left = draft.TranslatePoint(new Point(0, 0), root);
                Point right = dialog.Pane.TranslatePoint(new Point(0, 0), root);
                Require(left.X + draft.ActualWidth < right.X && Math.Abs(left.Y - right.Y) < 1 &&
                    Math.Abs(draft.ActualHeight - dialog.Pane.ActualHeight) < 1 && draft.ActualHeight > 200 &&
                    right.X + dialog.Pane.ActualWidth <= width, "Plan/terminal geometry overlaps or clips at " + name);
                Color surface = ((SolidColorBrush)draft.Background).Color;
                Color ink = ((SolidColorBrush)draft.Foreground).Color;
                Require(draft.IsReadOnly && surface == Color.FromRgb(0x12, 0x12, 0x12) &&
                    (Luminance(ink) + 0.05) / (Luminance(surface) + 0.05) >= 4.5,
                    "Readonly preview must have the dark PlanMaker surface and readable text contrast.");
                double previousRight = 0;
                foreach (Button button in footerButtons)
                {
                    Point position = button.TranslatePoint(new Point(0, 0), root);
                    Require(position.X >= previousRight && position.X + button.ActualWidth <= width &&
                        position.Y + button.ActualHeight <= height && position.Y >= left.Y + draft.ActualHeight,
                        "Interview footer controls overlap the draft or clip at " + name);
                    previousRight = position.X + button.ActualWidth;
                }
                // Screenshots show the actual inactive mode after the widest-control geometry check.
                Field<Button>(dialog, "_retry").Visibility = Visibility.Collapsed;
                Call(dialog, "UpdateInterviewActions");
                root.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(evidence, name + ".png")); encoder.Save(output);
            }
            Field<Button>(dialog, "_retry").Visibility = Visibility.Collapsed;
            Field<Button>(dialog, "_continue").Visibility = Visibility.Collapsed;
            root.ClearValue(FrameworkElement.WidthProperty); root.ClearValue(FrameworkElement.HeightProperty);
            dialog.ShowActivated = false;
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Left = -10000; dialog.Top = -10000;
            dialog.Show();
            Button maximize = Descendants(root).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "PlanMakerMaximizeRestore");
            maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => dialog.WindowState == WindowState.Maximized);
            Require(AutomationProperties.GetName(maximize) == "이전 크기로 복원", "Maximized caption did not switch to restore.");
            dialog.UpdateLayout();
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            IntPtr handle = new System.Windows.Interop.WindowInteropHelper(dialog).Handle;
            Require(GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor), "Could not verify the actual monitor work area.");
            DpiScale dpi = VisualTreeHelper.GetDpi(dialog);
            double workWidth = (monitor.Work.Right - monitor.Work.Left) / dpi.DpiScaleX;
            double workHeight = (monitor.Work.Bottom - monitor.Work.Top) / dpi.DpiScaleY;
            Require(root.ActualWidth <= workWidth + 20 && root.ActualHeight <= workHeight + 20,
                $"Maximized dialog exceeds its monitor work area: {root.ActualWidth}x{root.ActualHeight}, work {workWidth}x{workHeight}.");
            maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => dialog.WindowState == WindowState.Normal);
            Require(AutomationProperties.GetName(maximize) == "최대화", "Restored caption did not switch to maximize.");
            Button minimize = Descendants(root).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "PlanMakerMinimize");
            minimize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => dialog.WindowState == WindowState.Minimized);
            SystemCommands.RestoreWindow(dialog);
            PumpUntil(() => dialog.WindowState == WindowState.Normal);
            dialog.Hide();
            // Exercise the actual async retry method with an inert pane: startup/config are recreated,
            // but ownership loss and cancellation cannot launch replacement work.
            string before = draft.Text;
            TerminalPane originalPane = dialog.Pane;
            var retryTask = (Task)Call(dialog, "RetryPlanAsync", "captured prompt", (Func<bool>)(() => true), (Action<string>)(_ => { }))!;
            PumpUntil(() => retryTask.IsCompleted);
            retryTask.GetAwaiter().GetResult();
            Require(originalPane.Stopped && originalPane.Disposed && TerminalPane.MaximumActive == 1 &&
                dialog.Pane.ResourceRunOnceEnabled && draft.Text == before && dialog.Error is not null &&
                Field<Button>(dialog, "_retry").Visibility == Visibility.Visible,
                "Retry lost draft/config-refresh isolation or did not preserve explicit retry after error.");
            TerminalPane secondPane = dialog.Pane;
            ((Task)Call(dialog, "RetryPlanAsync", "captured prompt", (Func<bool>)(() => false), (Action<string>)(_ => { }))!).GetAwaiter().GetResult();
            Require(dialog.Pane == secondPane && secondPane.Stopped && secondPane.Disposed && draft.Text == before,
                "Ownership loss started a replacement planner or discarded the draft.");
            dialog.Pane.Dispose();
            ((Task)Call(dialog, "CleanUpAsync")!).GetAwaiter().GetResult();
            dialog.Close();
            VerifyConfirmedDraftRetry(owner, evidence);
            VerifyIdempotentDraftRetry(owner, evidence);
            VerifyInterviewActions(owner, evidence);
            VerifyFinishReturnFailures(owner, evidence);
            VerifyRecommendedAnswers(owner, evidence);
            VerifyRecommendedCompletionWithoutDecisions(owner, evidence);
            VerifyRecommendedCompletionWhenConverged(owner, evidence);
            VerifyAutoRecommandAllowedUsersOnly(owner, evidence);
            VerifyVisibleErrorsAndRetryBudget(owner, evidence);
            VerifyCloseAfterSynchronousCleanup(owner, evidence);
            SettingsEditorChecks.Verify(owner, evidence);
            owner.Close();
            Console.WriteLine("PASS isolated dialog layout, status phases, fresh-pane retry, repeated/cumulative/subset/original-echo draft merges, finish/continue/recommended-answer controls, latest draft and ownership/cancellation guards.");
            Console.WriteLine("Screenshots (inert terminal stand-in; no OpenCode or live app): " + evidence);
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine("FAIL " + exception); return 1; }
    }

    private static void PumpUntil(Func<bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Isolated UI attempt timed out.");
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
    }
    private static void VerifyConfirmedDraftRetry(Window owner, string evidence)
    {
        const string original = "# Existing user plan\nPreserve original requirements.";
        const string confirmed = "## Confirmed earlier answer\nExport must include CSV filters.";
        const string earlyRetry = "## New answer\nReports should refresh.";
        const string finalRetry = "## New answer\nReports should refresh every minute.";
        const string secondRetry = "## Another answer\nExports must retain active filters.";
        var dialog = new PlanMakerDialog(owner, evidence, original, CancellationToken.None);
        string mainDraft = original;
        var applied = new List<string>();
        void ApplyAsMainWindow(string generated)
        {
            mainDraft = PlanMakerWorkflow.MergePlanAdditions(original, generated,
                "## 플랜메이커 · 확인된 요구사항");
            applied.Add(generated);
        }
        try
        {
            PlanMakerInterviewControl? failedControl = null;
            TerminalPane.PlanOverride = (_, draft) =>
            {
                failedControl = Field<PlanMakerInterviewControl>(dialog, "_interview");
                draft(confirmed);
                return Task.FromException<string>(new InvalidOperationException("Error after confirmed answer"));
            };
            ((Task)Call(dialog, "PlanAsync", "original captured prompt", (Func<bool>)(() => true), (Action<string>)ApplyAsMainWindow)!).GetAwaiter().GetResult();
            Require(dialog.Error is not null && mainDraft.Contains(confirmed, StringComparison.Ordinal),
                "First failed attempt did not retain its confirmed answer.");
            TerminalPane.PlanOverride = (prompt, draft) =>
            {
                var freshControl = Field<PlanMakerInterviewControl>(dialog, "_interview");
                failedControl!.RequestFinish();
                Require(!ReferenceEquals(failedControl, freshControl) && !freshControl.IsFinishRequested &&
                    !Field<Button>(dialog, "_finish").IsEnabled,
                    "Retry reused the failed interview control or enabled finish before a live draft.");
                Require(prompt.Contains("original captured prompt", StringComparison.Ordinal) && prompt.Contains(confirmed, StringComparison.Ordinal),
                    "Fresh retry omitted original intent or confirmed answer context.");
                Require(prompt.Contains("같은 제목과 상위 제목을 그대로 사용", StringComparison.Ordinal) &&
                    prompt.Contains("새 조건만 간결하게 작성", StringComparison.Ordinal) &&
                    !prompt.Contains("기존 확인 내용과 구분", StringComparison.Ordinal),
                    "Retry did not request concise new conditions under the existing heading path.");
                draft(earlyRetry);
                draft(finalRetry);
                return Task.FromException<string>(new InvalidOperationException("Second error after revised answer"));
            };
            ((Task)Call(dialog, "RetryPlanAsync", "original captured prompt", (Func<bool>)(() => true), (Action<string>)ApplyAsMainWindow)!).GetAwaiter().GetResult();
            Require(dialog.Error is not null && mainDraft.Contains(confirmed, StringComparison.Ordinal) &&
                mainDraft.Contains(finalRetry, StringComparison.Ordinal), "Second failed attempt discarded confirmed additions.");
            TerminalPane.PlanOverride = (prompt, draft) =>
            {
                Require(prompt.Contains(confirmed, StringComparison.Ordinal) && prompt.Contains(finalRetry, StringComparison.Ordinal),
                    "Repeated retry omitted accumulated confirmed context.");
                draft(secondRetry);
                return Task.FromResult(secondRetry);
            };
            ((Task)Call(dialog, "RetryPlanAsync", "original captured prompt", (Func<bool>)(() => true), (Action<string>)ApplyAsMainWindow)!).GetAwaiter().GetResult();
            Require(dialog.PlanningCompleted && dialog.Error is null && mainDraft.StartsWith(original, StringComparison.Ordinal) &&
                mainDraft.Contains(confirmed, StringComparison.Ordinal) && mainDraft.Contains(finalRetry, StringComparison.Ordinal) &&
                mainDraft.Contains(secondRetry, StringComparison.Ordinal) &&
                applied.Skip(1).All(value => value.StartsWith(confirmed, StringComparison.Ordinal)) &&
                !mainDraft.Contains(earlyRetry + "\n", StringComparison.Ordinal) &&
                mainDraft.Split(confirmed, StringSplitOptions.None).Length == 2 &&
                mainDraft.Split(finalRetry, StringSplitOptions.None).Length == 2 &&
                mainDraft.Split(secondRetry, StringSplitOptions.None).Length == 2 &&
                Field<TextBox>(dialog, "_draft").Text == applied[^1],
                "Successful retry lost prior answers or duplicated per-attempt provisional drafts.");
            // A user edit conflict must reject the callback before mutating the retained preview.
            string retained = Field<TextBox>(dialog, "_draft").Text;
            TerminalPane.PlanOverride = (_, draft) =>
            {
                draft("## Rejected new draft");
                return Task.FromResult("## Rejected new draft");
            };
            ((Task)Call(dialog, "RetryPlanAsync", "original captured prompt", (Func<bool>)(() => true),
                (Action<string>)(_ => throw new InvalidOperationException("User edit conflict")))!).GetAwaiter().GetResult();
            Require(dialog.Error == "User edit conflict" && Field<TextBox>(dialog, "_draft").Text == retained &&
                Field<string>(dialog, "_confirmedGeneratedDraft") == retained,
                "Rejected main-document conflict changed the confirmed preview.");
            TerminalPane beforeCancellation = dialog.Pane;
            var stopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TerminalPane.StopOverride = () => stopGate.Task;
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(
                System.Windows.Threading.Dispatcher.CurrentDispatcher));
            var canceledRetry = (Task)Call(dialog, "RetryPlanAsync", "original captured prompt", (Func<bool>)(() => true),
                (Action<string>)(_ => throw new InvalidOperationException("Canceled retry applied a draft")))!;
            Field<CancellationTokenSource>(dialog, "_cancellation").Cancel();
            stopGate.SetResult();
            PumpUntil(() => canceledRetry.IsCompleted);
            SynchronizationContext.SetSynchronizationContext(previousContext);
            canceledRetry.GetAwaiter().GetResult();
            Require(dialog.Pane == beforeCancellation && beforeCancellation.Disposed &&
                Field<TextBox>(dialog, "_draft").Text == retained,
                "Cancellation during retry cleanup launched a replacement or changed the retained draft.");
        }
        finally
        {
            TerminalPane.PlanOverride = null;
            TerminalPane.StopOverride = null;
            dialog.Pane.Dispose();
            ((Task)Call(dialog, "CleanUpAsync")!).GetAwaiter().GetResult();
            dialog.Close();
        }
    }

    private static void VerifyIdempotentDraftRetry(Window owner, string evidence)
    {
        const string originalDetail = "- CSV columns `Order ID` and `Total (KRW)`; maximum 5000 rows; exclude canceled orders; path `C:\\Reports\\daily.csv`; keep `amount >= 0`.";
        const string original = "# Confirmed Plan\r\n\r\n## Export\r\n" + originalDetail +
            "\r\n\r\n## Existing checks\r\n- Shared check.\r\n  \r\n";
        const string confirmed = "# Confirmed Plan\n\n## Export\n- Keep filters.\n\n## Display\n- Shared check.";
        const string cumulative = "# Confirmed Plan\n\n## Export\n- Keep filters.\n- Refresh every minute.\n\n## Display\n- Shared check.";
        const string subset = "# Confirmed Plan\n\n## Export\n- Keep filters.";
        var dialog = new PlanMakerDialog(owner, evidence, original, CancellationToken.None);
        string mainDraft = original;
        int callbacks = 0;
        void ApplyAsMainWindow(string generated)
        {
            mainDraft = PlanMakerWorkflow.MergePlanAdditions(original, generated,
                "## 플랜메이커 · 확인된 요구사항");
            callbacks++;
        }
        void Run(bool retry) => ((Task)Call(dialog, retry ? "RetryPlanAsync" : "PlanAsync",
            "captured prompt", (Func<bool>)(() => true), (Action<string>)ApplyAsMainWindow)!).GetAwaiter().GetResult();
        int Count(string text) => mainDraft.Split(text, StringSplitOptions.None).Length - 1;
        try
        {
            TerminalPane.PlanOverride = (_, draft) =>
            {
                draft(confirmed);
                return Task.FromException<string>(new InvalidOperationException("Retain confirmed answers"));
            };
            Run(false);
            string firstDraft = mainDraft;
            string firstPreview = Field<TextBox>(dialog, "_draft").Text;
            Require(dialog.Error is not null && mainDraft.Contains(originalDetail, StringComparison.Ordinal) &&
                mainDraft.Contains("## Existing checks\r\n- Shared check.\r\n  \r\n", StringComparison.Ordinal) &&
                Count("# Confirmed Plan") == 1 && Count("## Export") == 1 && Count("- Keep filters.") == 1 &&
                Count("- Shared check.") == 2 && mainDraft.IndexOf("- Keep filters.", StringComparison.Ordinal) <
                    mainDraft.IndexOf("## Existing checks", StringComparison.Ordinal),
                "The first confirmed answer was not grouped with existing export details or lost original whitespace or a check in another section.");
            TerminalPane.PlanOverride = (_, draft) =>
            {
                draft(confirmed);
                draft(confirmed);
                return Task.FromException<string>(new InvalidOperationException("Repeated old delta"));
            };
            Run(true);
            Require(mainDraft == firstDraft && Field<TextBox>(dialog, "_draft").Text == firstPreview && callbacks == 3,
                "Retrying the same old delta duplicated confirmed requirements or retry headings.");
            TerminalPane.PlanOverride = (_, draft) =>
            {
                draft(cumulative);
                draft(cumulative);
                return Task.FromResult(cumulative);
            };
            Run(true);
            Require(dialog.PlanningCompleted && dialog.Error is null && mainDraft.Contains(originalDetail, StringComparison.Ordinal) &&
                Count("# Confirmed Plan") == 1 && Count("## Export") == 1 && Count("## Display") == 1 &&
                Count("- Keep filters.") == 1 && Count("- Refresh every minute.") == 1 && Count("- Shared check.") == 2 &&
                mainDraft.IndexOf("- Refresh every minute.", StringComparison.Ordinal) < mainDraft.IndexOf("## Existing checks", StringComparison.Ordinal),
                "A cumulative retry duplicated old blocks or lost a changed requirement under a repeated section title.");
            string accumulated = mainDraft;
            TerminalPane.PlanOverride = (_, draft) => { draft(subset); return Task.FromResult(subset); };
            Run(true);
            Require(mainDraft == accumulated && Field<TextBox>(dialog, "_draft").Text.Contains("- Refresh every minute.", StringComparison.Ordinal),
                "A retry subset duplicated old answers or discarded an earlier confirmed requirement.");
            TerminalPane.PlanOverride = (_, draft) =>
            {
                string echo = original + "\n\n" + Field<string>(dialog, "_confirmedGeneratedDraft");
                draft(echo);
                return Task.FromResult(echo);
            };
            Run(true);
            Require(mainDraft == accumulated && Count("# Confirmed Plan") == 1 && Count("## Export") == 1 &&
                Count(originalDetail) == 1 && Count("- Keep filters.") == 1 &&
                Count("- Refresh every minute.") == 1 && Count("- Shared check.") == 2,
                "A provider echo duplicated the original user plan or confirmed requirements.");
        }
        finally
        {
            TerminalPane.PlanOverride = null;
            dialog.Pane.Dispose();
            ((Task)Call(dialog, "CleanUpAsync")!).GetAwaiter().GetResult();
            dialog.Close();
        }
    }

    private static void VerifyInterviewActions(Window owner, string evidence)
    {
        var dialog = new PlanMakerDialog(owner, evidence, "# Original plan", CancellationToken.None);
        var finish = Field<Button>(dialog, "_finish");
        var resume = Field<Button>(dialog, "_continue");
        var latest = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        PlanMakerInterviewControl? control = null;
        Action<string>? publish = null;
        Action<string>? phase = null;
        string applied = "";
        bool owned = true;
        int attempts = 0;
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(
            System.Windows.Threading.Dispatcher.CurrentDispatcher));
        try
        {
            dialog.ShowActivated = false;
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Left = -10000; dialog.Top = -10000;
            dialog.Show();
            Require(!finish.IsEnabled && resume.Visibility == Visibility.Collapsed,
                "Interview actions were available before a confirmed live draft.");
            finish.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            TerminalPane.InterviewOverride = (_, draft, reportPhase, _, interview) =>
            {
                attempts++;
                control = interview;
                publish = draft;
                phase = reportPhase;
                Require(control is not null && !control.IsFinishRequested && !finish.IsEnabled,
                    "A new interview inherited finish state or allowed premature finish.");
                draft("## Confirmed answer\nCSV export.");
                control!.SetAwaitingContinuation(true);
                reportPhase("플랜메이커 · AI가 질문을 멈췄습니다. 질문 계속 또는 플랜으로 돌아가기를 선택하세요.");
                return latest.Task;
            };
            var operation = (Task)Call(dialog, "PlanAsync", "original wish", (Func<bool>)(() => owned),
                (Action<string>)(draft => applied = draft))!;
            SetField(dialog, "_operation", operation);
            Require(finish.IsEnabled && resume.Visibility == Visibility.Visible && !dialog.PlanningCompleted,
                "A paused live interview did not offer finish and continue.");
            TerminalPane samePane = dialog.Pane;
            owned = false;
            finish.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            resume.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!control!.IsFinishRequested && !control.HasContinuationRequest,
                "Interview controls ignored loss of main-document ownership.");
            owned = true;
            resume.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(control.HasContinuationRequest && resume.Visibility == Visibility.Collapsed &&
                dialog.Pane == samePane && attempts == 1 && !operation.IsCompleted,
                "Continue restarted the planner or left duplicate continuation available.");
            control.SetAwaitingContinuation(false);
            phase!("다음 질문을 준비 중…");
            foreach (string pending in new[] { "answer submission", "recommendation ranking", "unsafe native operation" })
            {
                control.SetCanRequestFinish(false);
                phase("플랜메이커 · 응답 확인 중");
                Require(!finish.IsEnabled, "Return was enabled during " + pending + ".");
                finish.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(!control.IsFinishRequested && !Field<bool>(dialog, "_returnAfterFinish") && dialog.IsVisible,
                    "Pending " + pending + " was bypassed by a synthetic click.");
            }
            control.SetCanRequestFinish(true);
            phase("플랜메이커 · 답변 확인 완료");
            finish.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(control.IsFinishRequested && !finish.IsEnabled && !operation.IsCompleted &&
                !dialog.PlanningCompleted && !Field<CancellationTokenSource>(dialog, "_cancellation").IsCancellationRequested,
                "Finish canceled work or completed before current-answer cleanup.");
            Require(dialog.IsVisible && Field<bool>(dialog, "_returnAfterFinish"),
                "The combined action closed before the latest draft was returned.");
            publish!("## Confirmed answer\nCSV export must preserve active filters.");
            latest.SetResult(applied);
            PumpUntil(() => operation.IsCompleted);
            operation.GetAwaiter().GetResult();
            Require(dialog.PlanningCompleted && dialog.Error is null && applied.Contains("active filters", StringComparison.Ordinal) &&
                Field<TextBox>(dialog, "_draft").Text == applied && !finish.IsEnabled &&
                resume.Visibility == Visibility.Collapsed && Field<TextBlock>(dialog, "_statusLabel").Text == "✓ 플랜 완료",
                "Finish lost the latest answer or left interview actions active.");
            PumpUntil(() => Field<TaskCompletionSource>(dialog, "_closed").Task.IsCompleted);
            Require(!dialog.IsVisible && samePane.Disposed && Field<TextBox>(dialog, "_draft").Text == applied,
                "One finish/return click did not close after applying the latest unsaved draft.");
        }
        finally
        {
            latest.TrySetResult(applied);
            TerminalPane.InterviewOverride = null;
            var cleanup = (Task)Call(dialog, "CleanUpAsync")!;
            PumpUntil(() => cleanup.IsCompleted);
            cleanup.GetAwaiter().GetResult();
            dialog.Close();
        }
        var closing = new PlanMakerDialog(owner, evidence, "# Original plan", CancellationToken.None);
        try
        {
            TerminalPane.InterviewOverride = async (_, draft, _, token, _) =>
            {
                draft("## Confirmed before close");
                await Task.Delay(Timeout.Infinite, token);
                return "unexpected";
            };
            var operation = (Task)Call(closing, "PlanAsync", "wish", (Func<bool>)(() => true), (Action<string>)(_ => { }))!;
            SetField(closing, "_operation", operation);
            var recommended = Field<Button>(closing, "_recommendedAnswers");
            recommended.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var closingControl = Field<PlanMakerInterviewControl>(closing, "_interview");
            Require(closingControl.RecommendedAnswersEnabled, "Close fixture could not enable recommended answers.");
            var args = new System.ComponentModel.CancelEventArgs();
            Call(closing, "OnClosing", closing, args);
            PumpUntil(() => operation.IsCompleted && closing.Pane.Disposed);
            Require(args.Cancel && !closing.PlanningCompleted &&
                Field<TextBox>(closing, "_draft").Text == "## Confirmed before close" &&
                Field<CancellationTokenSource>(closing, "_cancellation").IsCancellationRequested &&
                !recommended.IsEnabled && !closingControl.RecommendedAnswersEnabled,
                "Close did not cancel the interview and preserve its confirmed draft.");
        }
        finally
        {
            TerminalPane.InterviewOverride = null;
            var cleanup = (Task)Call(closing, "CleanUpAsync")!;
            PumpUntil(() => cleanup.IsCompleted);
            cleanup.GetAwaiter().GetResult();
            closing.Close();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static void VerifyFinishReturnFailures(Window owner, string evidence)
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(
            System.Windows.Threading.Dispatcher.CurrentDispatcher));
        try
        {
            foreach (string outcome in new[] { "provider-error", "empty-result", "cancellation", "ownership-loss", "apply-conflict", "natural-completion" })
            {
                const string confirmed = "## Confirmed\nKeep existing filters.";
                var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var dialog = new PlanMakerDialog(owner, evidence, "# Original plan", CancellationToken.None)
                {
                    ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000, Top = -10000
                };
                string applied = "";
                bool owned = true, conflict = false;
                try
                {
                    dialog.Show();
                    TerminalPane.InterviewOverride = (_, publish, _, _, _) => { publish(confirmed); return result.Task; };
                    var operation = (Task)Call(dialog, "PlanAsync", "wish", (Func<bool>)(() => owned),
                        (Action<string>)(draft =>
                        {
                            if (conflict) throw new InvalidOperationException("Concurrent main-plan edit");
                            applied = draft;
                        }))!;
                    SetField(dialog, "_operation", operation);
                    var finish = Field<Button>(dialog, "_finish");
                    Require(finish.IsEnabled && finish.Content as string == "플랜으로 돌아가기", "Combined action was unavailable.");
                    if (outcome != "natural-completion") finish.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (outcome == "ownership-loss") owned = false;
                    if (outcome == "apply-conflict") conflict = true;
                    if (outcome == "provider-error") result.SetException(new InvalidOperationException("Provider unavailable"));
                    else if (outcome == "cancellation") result.SetCanceled();
                    else result.SetResult(outcome == "empty-result" ? "" : "## Confirmed\nKeep existing filters and CSV export.");
                    PumpUntil(() => operation.IsCompleted);
                    operation.GetAwaiter().GetResult();
                    if (outcome == "natural-completion")
                    {
                        Require(dialog.PlanningCompleted && dialog.IsVisible && finish.IsEnabled &&
                            applied.Contains("CSV export", StringComparison.Ordinal),
                            "Natural completion closed without a user return action or disabled the completed draft.");
                        owned = false;
                        finish.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Require(dialog.IsVisible, "Completed return ignored main-document ownership loss.");
                        owned = true;
                        finish.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        PumpUntil(() => Field<TaskCompletionSource>(dialog, "_closed").Task.IsCompleted);
                        Require(!dialog.IsVisible && dialog.Pane.Disposed,
                            "Completed draft did not return with one click.");
                    }
                    else
                    {
                        Require(!dialog.PlanningCompleted && dialog.IsVisible && !dialog.Pane.Disposed &&
                            !Field<TaskCompletionSource>(dialog, "_closed").Task.IsCompleted &&
                            !Field<bool>(dialog, "_returnAfterFinish") && !finish.IsEnabled &&
                            applied == confirmed && Field<TextBox>(dialog, "_draft").Text == confirmed,
                            "Finish/return lost the confirmed draft or closed after " + outcome + ".");
                        Require(outcome is "cancellation" or "ownership-loss" ? dialog.Error is null : dialog.Error is not null,
                            "Finish/return changed error semantics after " + outcome + ".");
                    }
                }
                finally
                {
                    result.TrySetCanceled();
                    TerminalPane.InterviewOverride = null;
                    var cleanup = (Task)Call(dialog, "CleanUpAsync")!;
                    PumpUntil(() => cleanup.IsCompleted);
                    cleanup.GetAwaiter().GetResult();
                    dialog.Close();
                }
            }
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void VerifyRecommendedAnswers(Window owner, string evidence)
    {
        const string original = "# Retained original draft";
        var dialog = new PlanMakerDialog(owner, evidence, original, CancellationToken.None);
        var recommended = Field<Button>(dialog, "_recommendedAnswers");
        var finish = Field<Button>(dialog, "_finish");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        PlanMakerInterviewControl? control = null;
        Action<string>? publish = null;
        Action<string>? phase = null;
        bool owned = true;
        int attempts = 0, applied = 0;
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(
            System.Windows.Threading.Dispatcher.CurrentDispatcher));
        try
        {
            dialog.ShowActivated = false;
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Left = -10000; dialog.Top = -10000;
            dialog.Show();
            var recommendationStatus = Field<TextBlock>(dialog, "_recommendationStatus");
            var indicator = Field<System.Windows.Shapes.Path>(dialog, "_recommendationIndicator");
            var rotation = (RotateTransform)indicator.RenderTransform;
            void RequireState(string expected, string title)
            {
                Require(Field<string>(dialog, "_recommendationVisualState") == expected &&
                    recommendationStatus.Text.Contains(title, StringComparison.Ordinal) &&
                    AutomationProperties.GetName(recommendationStatus).Contains(title, StringComparison.Ordinal),
                    "Recommendation state must be readable and accessible: " + expected);
                Require(Field<bool>(dialog, "_recommendationAnimationActive") ==
                    ((expected == "submitting" || expected == "processing") && dialog.IsVisible &&
                        dialog.WindowState != WindowState.Minimized && SystemParameters.ClientAreaAnimation) &&
                    rotation.HasAnimatedProperties == Field<bool>(dialog, "_recommendationAnimationActive"),
                    "Recommendation activity animation did not match actual submission: " + expected);
            }
            void Capture(string name)
            {
                var root = (Border)dialog.Content;
                root.Measure(new Size(1200, 720)); root.Arrange(new Rect(0, 0, 1200, 720)); root.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1200, 720, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(evidence, name + ".png")); encoder.Save(output);
            }
            Require(!recommended.IsEnabled && recommended.Content as string == "Auto Recommand" &&
                AutomationProperties.GetAutomationId(recommended) == "PlanMakerRecommendedAnswers" &&
                AutomationProperties.GetName(recommended).Contains("꺼짐", StringComparison.Ordinal) &&
                AutomationProperties.GetHelpText(recommended).Contains("Auto Recommand", StringComparison.Ordinal) &&
                AutomationProperties.GetHelpText(recommended).Contains("설정된 모델로 추천을 보완", StringComparison.Ordinal) &&
                !recommended.IsDefault && !recommended.IsCancel,
                "Recommended-answer control must default off with accessible guidance and preserve Enter/Escape behavior.");
            RequireState("manual", "Auto Recommand OFF · 직접 답변");
            Capture("recommendation-off");
            recommended.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            TerminalPane.ReadyOverride = () => ready.Task;
            TerminalPane.InterviewOverride = (_, draft, reportPhase, _, interview) =>
            {
                attempts++;
                control = interview;
                publish = draft;
                phase = reportPhase;
                Require(control is not null && !control.RecommendedAnswersEnabled && recommended.IsEnabled && !finish.IsEnabled,
                    "A ready interview inherited recommended mode or required a current question to enable it.");
                return result.Task;
            };
            var operation = (Task)Call(dialog, "PlanAsync", "wish", (Func<bool>)(() => owned),
                (Action<string>)(_ => applied++))!;
            SetField(dialog, "_operation", operation);
            recommended.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!recommended.IsEnabled && !Field<PlanMakerInterviewControl>(dialog, "_interview").RecommendedAnswersEnabled,
                "Recommended mode was enabled while the pane was loading.");
            ready.SetResult();
            PumpUntil(() => control is not null);
            recommended.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(control!.RecommendedAnswersEnabled && recommended.Content as string == "Auto Recommand" &&
                AutomationProperties.GetName(recommended).Contains("켜짐", StringComparison.Ordinal) &&
                Field<TextBlock>(dialog, "_status").Text.Contains("설정된 모델로 추천을 보완", StringComparison.Ordinal) &&
                Field<TextBox>(dialog, "_draft").Text == original && applied == 0 &&
                !control.IsFinishRequested && !control.HasContinuationRequest && !operation.IsCompleted && !dialog.PlanningCompleted,
                "Enabling recommended mode changed the draft, finished the interview, or omitted its visible state.");
            RequireState("waiting", "Auto Recommand ON · 다음 질문 대기");
            Capture("recommendation-waiting");
            IInputElement? focused = System.Windows.Input.Keyboard.FocusedElement;
            phase!("플랜메이커 · 현재 질문의 선택지와 추천 이유를 확인하는 중입니다.");
            RequireState("processing", "추천 확인 중");
            phase("플랜메이커 · 추천 답변을 보내기 전에 현재 질문을 확인합니다.");
            RequireState("processing", "추천 확인 중");
            phase!("플랜메이커 · 추천 답변을 선택하고 전송 중입니다.");
            RequireState("submitting", "답변 전송 중");
            if (SystemParameters.ClientAreaAnimation) PumpUntil(() => rotation.Angle > 5);
            Capture("recommendation-submitting");
            phase("플랜메이커 · 추천 답변 전송: Keep filters");
            RequireState("waiting", "다음 질문 대기");
            phase("◌ 플랜 만들기 · 답변을 반영하는 중");
            RequireState("processing", "다음 질문 준비 중");
            Capture("recommendation-processing");
            dialog.Hide();
            RequireState("processing", "다음 질문 준비 중");
            dialog.Show();
            RequireState("processing", "다음 질문 준비 중");
            dialog.WindowState = WindowState.Minimized;
            RequireState("processing", "다음 질문 준비 중");
            dialog.WindowState = WindowState.Normal;
            RequireState("processing", "다음 질문 준비 중");
            phase("플랜메이커 오류 · 설정된 모델을 사용할 수 없습니다.");
            RequireState("error", "오류");
            phase("플랜메이커를 중단했습니다 · 반영된 초안은 유지합니다.");
            RequireState("stopped", "중단");
            phase("플랜메이커 · 명확한 추천 선택지가 없어 직접 답변을 기다립니다.");
            RequireState("manual-required", "직접 답변 필요");
            Capture("recommendation-manual-required");
            phase("플랜메이커 · 입력 중에는 추천 답변을 보내지 않습니다.");
            RequireState("input", "직접 입력 중");
            phase("플랜메이커 · 승인을 기다리는 동안 추천 답변을 보내지 않습니다.");
            RequireState("permission", "승인 대기");
            dialog.Pane.IsWaitingForResources = true;
            phase("플랜메이커 · 추천 답변을 선택하고 전송 중입니다.");
            RequireState("resources", "자원");
            dialog.Pane.IsWaitingForResources = false;
            phase("질문에 답해주세요");
            Require(System.Windows.Input.Keyboard.FocusedElement == focused,
                "Recommendation status updates moved keyboard focus away from the current IME/input target.");
            owned = false;
            phase!("질문에 답해주세요");
            recommended.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!recommended.IsEnabled && control.RecommendedAnswersEnabled,
                "Recommended control ignored loss of document ownership.");
            owned = true;
            phase("질문에 답해주세요");
            recommended.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!control.RecommendedAnswersEnabled && recommended.Content as string == "Auto Recommand" &&
                AutomationProperties.GetName(recommended).Contains("꺼짐", StringComparison.Ordinal) &&
                Field<TextBlock>(dialog, "_status").Text.Contains("직접 답변 모드", StringComparison.Ordinal),
                "Recommended mode did not return to manual answers.");
            recommended.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            result.SetException(new InvalidOperationException("Isolated interview failure"));
            PumpUntil(() => operation.IsCompleted);
            operation.GetAwaiter().GetResult();
            Require(dialog.Error is not null && !recommended.IsEnabled && !control.RecommendedAnswersEnabled,
                "A failed attempt left recommended answers enabled.");
            RequireState("error", "오류");
            PlanMakerInterviewControl failed = control;
            control = null;
            result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            TerminalPane.ReadyOverride = null;
            var retry = (Task)Call(dialog, "RetryPlanAsync", "wish", (Func<bool>)(() => owned),
                (Action<string>)(_ => applied++))!;
            SetField(dialog, "_operation", retry);
            Require(control is not null && !ReferenceEquals(failed, control) && !control.RecommendedAnswersEnabled && attempts == 2,
                "Retry did not create a fresh interview with recommended mode off.");
            recommended.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            publish!("## Confirmed answer\nKeep filters.");
            finish.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            recommended.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(control!.IsFinishRequested && !control.RecommendedAnswersEnabled && !recommended.IsEnabled &&
                recommended.Content as string == "Auto Recommand" && !retry.IsCompleted,
                "Finishing did not disable recommended mode while waiting for the current answer.");
            result.SetResult("## Confirmed answer\nKeep filters.");
            PumpUntil(() => retry.IsCompleted);
            retry.GetAwaiter().GetResult();
            Require(dialog.PlanningCompleted && dialog.Error is null && !recommended.IsEnabled &&
                Field<TextBox>(dialog, "_draft").Text.Contains("Keep filters.", StringComparison.Ordinal),
                "Recommended mode interfered with finish completion or the retained draft.");
            PumpUntil(() => Field<TaskCompletionSource>(dialog, "_closed").Task.IsCompleted);
            Require(!dialog.IsVisible && dialog.Pane.Disposed, "Recommended completion did not return automatically after finish.");
            RequireState("stopped", "Auto Recommand 중단");
        }
        finally
        {
            ready.TrySetResult();
            result.TrySetResult("## Retained answer");
            TerminalPane.ReadyOverride = null;
            TerminalPane.InterviewOverride = null;
            var cleanup = (Task)Call(dialog, "CleanUpAsync")!;
            PumpUntil(() => cleanup.IsCompleted);
            cleanup.GetAwaiter().GetResult();
            dialog.Close();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    // Auto Recommand is offered only to LoopAutoRecommandUsers.AllowedUsers: other accounts never see it or turn it on.
    private static void VerifyAutoRecommandAllowedUsersOnly(Window owner, string evidence)
    {
        try
        {
            // The first master account of this build (MASTER_USERS, default test_01), in other letter case.
            string master = LoopAutoRecommandUsers.AllowedUsers[0].ToUpperInvariant();
            foreach (string account in new[] { @"CORP\someone", @"CORP\" + master })
            {
                bool allowed = account.EndsWith(master, StringComparison.Ordinal);
                LoopAutoRecommandUsers.AccountOverride = () => account;
                var dialog = new PlanMakerDialog(owner, evidence, "# Original plan", CancellationToken.None);
                try
                {
                    var control = new PlanMakerInterviewControl();
                    control.SetRecommendedAnswersEnabled(true);
                    Require(Field<Button>(dialog, "_recommendedAnswers").Visibility == (allowed ? Visibility.Visible : Visibility.Collapsed) &&
                        control.RecommendedAnswersEnabled == allowed,
                        allowed ? "An allowed account lost Auto Recommand." : "Another account saw or turned on Auto Recommand.");
                }
                finally { dialog.Close(); }
            }
            Console.WriteLine("PASS Auto Recommand allowed users: hidden and never enabled for other accounts, shown for a master account.");
        }
        finally { LoopAutoRecommandUsers.AccountOverride = null; }
    }

    // Auto Recommand keeps asking for improvements, so it must also end on its own: answers that stop changing the plan.
    private static void VerifyRecommendedCompletionWhenConverged(Window owner, string evidence)
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(
            System.Windows.Threading.Dispatcher.CurrentDispatcher));
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new PlanMakerDialog(owner, evidence, "# Original plan", CancellationToken.None)
        {
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000
        };
        PlanMakerInterviewControl? control = null;
        Action<string>? phase = null;
        try
        {
            dialog.Show();
            TerminalPane.InterviewOverride = (_, publish, reportPhase, _, interview) =>
            {
                control = interview;
                phase = reportPhase;
                publish("## Confirmed\nCSV export.");
                return result.Task;
            };
            var operation = (Task)Call(dialog, "PlanAsync", "wish", (Func<bool>)(() => true), (Action<string>)(_ => { }))!;
            SetField(dialog, "_operation", operation);
            PumpUntil(() => control is not null);
            Field<Button>(dialog, "_recommendedAnswers").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(control!.RecommendedAnswersEnabled, "Auto Recommand did not turn on.");
            for (int i = 0; i < PlanMakerInterviewControl.AutoAnswersWithoutChangeLimit - 1; i++) control.RecordRecommendedAnswer();
            phase!("플랜메이커 · 추천 답변 전송: 반복 예약 기능 추가");
            Require(control.RecommendedAnswersEnabled && !control.IsFinishRequested,
                "Auto Recommand finished before its answers stopped changing the plan.");
            control.RecordRecommendedAnswer();
            phase!("플랜메이커 · " + control.AutoStopReason + " 자동 추천을 마무리합니다.");
            Require(control.IsFinishRequested && !control.RecommendedAnswersEnabled && dialog.CompletedWithoutRemainingDecisions &&
                Field<TextBlock>(dialog, "_status").Text.Contains("3번 연속", StringComparison.Ordinal),
                "Converged Auto Recommand did not finish the interview with its reason.");
            result.SetResult("## Confirmed\nCSV export.");
            PumpUntil(() => dialog.PlanningCompleted);
            Require(dialog.Error is null && dialog.IsVisible && Field<TextBlock>(dialog, "_statusLabel").Text == "✓ 플랜 완료" &&
                Field<TextBlock>(dialog, "_status").Text.Contains("3번 연속", StringComparison.Ordinal),
                "Converged Auto Recommand did not show completion and why it stopped.");
            Console.WriteLine("PASS converged Auto Recommand: finishes after 3 unchanged answers, keeps the dialog open and says why.");
        }
        finally
        {
            result.TrySetCanceled();
            TerminalPane.InterviewOverride = null;
            var cleanup = (Task)Call(dialog, "CleanUpAsync")!;
            PumpUntil(() => cleanup.IsCompleted);
            cleanup.GetAwaiter().GetResult();
            dialog.Close();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static void VerifyRecommendedCompletionWithoutDecisions(Window owner, string evidence)
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(
            System.Windows.Threading.Dispatcher.CurrentDispatcher));
        try
        {
            foreach (bool hasDraft in new[] { true, false })
            {
                var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var dialog = new PlanMakerDialog(owner, evidence, "# Original plan", CancellationToken.None)
                {
                    ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000, Top = -10000
                };
                PlanMakerInterviewControl? control = null;
                Action<string>? phase = null;
                try
                {
                    Require(dialog.Owner is null, "PlanMaker must stay modeless and unowned so other tasks remain usable.");
                    dialog.Show();
                    Require(owner.IsEnabled, "PlanMaker disabled the main window.");
                    TerminalPane.InterviewOverride = (_, publish, reportPhase, _, interview) =>
                    {
                        control = interview;
                        phase = reportPhase;
                        if (hasDraft) publish("## Confirmed\nCSV export.");
                        return result.Task;
                    };
                    var operation = (Task)Call(dialog, "PlanAsync", "wish", (Func<bool>)(() => true), (Action<string>)(_ => { }))!;
                    SetField(dialog, "_operation", operation);
                    PumpUntil(() => control is not null);
                    var recommended = Field<Button>(dialog, "_recommendedAnswers");
                    Brush offBackground = recommended.Background;
                    recommended.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(control!.RecommendedAnswersEnabled && recommended.Content as string == "Auto Recommand" &&
                        recommended.Background is SolidColorBrush { Color: var on } && on == Color.FromRgb(0x2E, 0x8B, 0x66),
                        "Active Auto Recommand was not visually distinct.");
                    control.SetAwaitingContinuation(true);
                    phase!("플랜메이커 · AI가 질문을 멈췄습니다. 질문 계속 또는 플랜으로 돌아가기를 선택하세요.");
                    Require(!control.RecommendedAnswersEnabled && ReferenceEquals(recommended.Background, offBackground),
                        "Auto Recommand stayed on without remaining decisions.");
                    if (hasDraft)
                    {
                        Require(control.IsFinishRequested && !Field<bool>(dialog, "_returnAfterFinish") &&
                            dialog.CompletedWithoutRemainingDecisions, "No remaining decisions did not complete the interview.");
                        result.SetResult("## Confirmed\nCSV export.");
                        PumpUntil(() => dialog.PlanningCompleted);
                        PumpUntil(() => Field<Button>(dialog, "_finish").IsEnabled);
                        Require(dialog.Error is null && dialog.IsVisible && !Field<TaskCompletionSource>(dialog, "_closed").Task.IsCompleted &&
                            Field<TextBlock>(dialog, "_statusLabel").Text == "✓ 플랜 완료" && Field<Button>(dialog, "_finish").IsEnabled,
                            "A completed Auto Recommand interview must stay open, show completion and offer the return action.");
                    }
                    else
                        Require(!control.IsFinishRequested && !dialog.CompletedWithoutRemainingDecisions && dialog.IsVisible &&
                            Field<TextBlock>(dialog, "_status").Text.Contains("Auto Recommand 해제", StringComparison.Ordinal),
                            "An empty interview finished or did not tell the user Auto Recommand was turned off.");
                }
                finally
                {
                    result.TrySetCanceled();
                    TerminalPane.InterviewOverride = null;
                    var cleanup = (Task)Call(dialog, "CleanUpAsync")!;
                    PumpUntil(() => cleanup.IsCompleted);
                    cleanup.GetAwaiter().GetResult();
                    dialog.Close();
                }
            }
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void VerifyVisibleErrorsAndRetryBudget(Window owner, string evidence)
    {
        var dialog = new PlanMakerDialog(owner, evidence, "# Original draft", CancellationToken.None)
        {
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 140, Top = 140, Width = 800, Height = 500
        };
        int calls = 0;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(
            System.Windows.Threading.Dispatcher.CurrentDispatcher));
        try
        {
            dialog.Show();
            var presenter = Field<StatusNotificationPresenter>(dialog, "_statusNotifications");
            StatusNotificationWindow? Notice() => Field<StatusNotificationWindow?>(presenter, "_window");
            Call(dialog, "SetPlanningStatus", "플랜메이커 전용 터미널 연결 중…");
            Call(dialog, "SetPlanningResult", "플랜 초안에 반영했습니다 · 오류 프로젝트");
            Require(Notice() is null && Field<TextBlock>(dialog, "_status").Text.Contains("반영했습니다", StringComparison.Ordinal),
                "Routine planner progress/success opened a popup or lost its inline status.");
            dialog.Pane.PublishUserWarning("붙여넣기는 200,000자 이하로 나눠주세요.");
            Require(Notice() is { IsVisible: true } && Field<TextBlock>(dialog, "_status").Text.Contains("200,000", StringComparison.Ordinal),
                "Initial planner pane warnings were not routed to a visible planner-owned popup.");
            Notice()!.Close();
            Call(dialog, "SetPlanningResult", "플랜메이커를 중단했습니다 · 반영된 초안은 유지합니다.");
            Require(!Notice()!.IsVisible, "A normal planner stop reopened a dismissed warning.");
            Task Run(string prompt)
            {
                var operation = (Task)Call(dialog, "PlanAsync", prompt, (Func<bool>)(() => true), (Action<string>)(_ => { }))!;
                SetField(dialog, "_operation", operation);
                PumpUntil(() => operation.IsCompleted);
                operation.GetAwaiter().GetResult();
                return operation;
            }
            TerminalPane.InterviewOverride = (_, _, _, _, _) =>
            {
                calls++;
                return Task.FromException<string>(new InvalidOperationException("Unexpected provider response 418"));
            };
            Run("Build reports");
            Require(dialog.Error == "Unexpected provider response 418" && Notice() is { IsVisible: true } notice && notice.Owner == dialog,
                "Actual provider errors without warning keywords must open a notification above the planner.");
            Notice()!.Close();
            TerminalPane failedPane = dialog.Pane;
            var retry = (Task)Call(dialog, "RetryPlanAsync", "Build reports", (Func<bool>)(() => true), (Action<string>)(_ => { }))!;
            SetField(dialog, "_operation", retry);
            PumpUntil(() => retry.IsCompleted);
            retry.GetAwaiter().GetResult();
            Require(Notice()!.IsVisible, "Explicit retry returning the same provider failure did not reopen its notice.");
            Notice()!.Close();
            dialog.Pane.PublishUserWarning("터미널 입력: 클립보드에 접근할 수 없습니다.");
            Require(!ReferenceEquals(failedPane, dialog.Pane) && Notice()!.IsVisible &&
                Field<TextBlock>(dialog, "_status").Text.Contains("클립보드", StringComparison.Ordinal),
                "Replacement planner pane lost the UserWarning subscription after retry.");
            Notice()!.Close();
            Run("  ");
            Require(calls == 2 && dialog.Error!.Contains("입력해주세요", StringComparison.Ordinal) && Notice()!.IsVisible,
                "Empty input reached the provider or its error stayed inline.");
            const string retained = "## Confirmed\nKeep every confirmed condition.";
            SetField(dialog, "_confirmedGeneratedDraft", retained);
            int promptLength = PlanMakerWorkflow.ExpandedPromptLength - retained.Length;
            Run(new string('p', promptLength));
            Require(calls == 2 && dialog.Error!.Contains("전체 요청", StringComparison.Ordinal) && Notice()!.IsVisible &&
                Field<string>(dialog, "_confirmedGeneratedDraft") == retained && Field<TextBox>(dialog, "_draft").Text == "# Original draft",
                "Retry envelope discarded confirmed conditions or failed to count its instructions before native submission.");
            TerminalPane.InterviewOverride = (_, _, _, _, _) => { calls++; return Task.FromResult(""); };
            Run("Build reports");
            Require(calls == 3 && dialog.Error!.Contains("반환하지 않았습니다", StringComparison.Ordinal) &&
                !dialog.PlanningCompleted && Notice()!.IsVisible,
                "An empty model response was accepted or its retryable error stayed inline.");
        }
        finally
        {
            TerminalPane.InterviewOverride = null;
            var cleanup = (Task)Call(dialog, "CleanUpAsync")!;
            PumpUntil(() => cleanup.IsCompleted);
            cleanup.GetAwaiter().GetResult();
            dialog.Close();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static void VerifyCloseAfterSynchronousCleanup(Window owner, string evidence)
    {
        var dialog = new PlanMakerDialog(owner, evidence, "# Retained draft", CancellationToken.None)
        {
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000
        };
        int closed = 0;
        dialog.Closed += (_, _) => closed++;
        dialog.Show();
        dialog.Close();
        PumpUntil(() => closed == 1);
        Require(!dialog.IsVisible && dialog.Pane.Disposed &&
            Field<TaskCompletionSource>(dialog, "_closed").Task.IsCompleted &&
            Field<TextBox>(dialog, "_draft").Text == "# Retained draft",
            "Synchronous planner cleanup failed to close the actual visible dialog and retain its draft.");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void SetField(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name,
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(target, args);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static double Luminance(Color color)
    {
        static double Linear(byte channel) { double value = channel / 255d; return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4); }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { internal int Size; internal NativeRect Monitor, Work; internal int Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}

namespace TerminalNotes
{
    // The production dialog is compiled unchanged. This stand-in never starts a process,
    // loads a WebView or calls a provider; it verifies only layout and retry ownership.
    internal sealed class TerminalPane : Grid, IDisposable
    {
        private static int _active;
        internal static int MaximumActive { get; private set; }
        internal static Func<string, Action<string>, Task<string>>? PlanOverride { get; set; }
        internal static Func<string, Action<string>, Action<string>, CancellationToken, PlanMakerInterviewControl?, Task<string>>? InterviewOverride { get; set; }
        internal bool Stopped { get; private set; }
        internal bool Disposed { get; private set; }
        internal bool ResourceRunOnceEnabled { get; private set; }
        internal bool IsWaitingForResources { get; set; }
        internal string? ResourceWaitReason => null;
        internal bool CanStartNativeAutomation => true;
        internal bool IsRunning => !Stopped;
        internal (bool Failed, string Reason) NativeAutomationDiagnostic => (false, "inert");
        internal event Action<string>? StatusChanged { add { } remove { } }
        internal event Action<string>? UserWarning;
        internal void PublishUserWarning(string message) => UserWarning?.Invoke(message);
        internal TerminalPane(string project, string startupCommand, bool protectTuiViewport, string paneKey, bool useProjectContext)
        {
            MaximumActive = Math.Max(MaximumActive, ++_active);
            Background = new SolidColorBrush(Color.FromRgb(21, 23, 27));
            Children.Add(new TextBlock { Text = "Isolated layout preview · no OpenCode started\n\nWhat should the reports page include?\n\n› Filters and CSV export\n\nAnswer in the planning terminal", Foreground = Brushes.WhiteSmoke,
                Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 14 });
        }
        internal void SetAutoResumeEnabled(bool value) { }
        internal void EnableResourceAdmissionRunOnce() => ResourceRunOnceEnabled = true;
        internal Task WaitForResourceAdmissionAsync(CancellationToken token) => Task.CompletedTask;
        internal static Func<Task>? ReadyOverride;
        internal Task EnsureReadyAsync(CancellationToken token) => ReadyOverride?.Invoke() ?? Task.CompletedTask;
        internal Task<string> CreatePlanWithQuestionsAsync(string prompt, Func<bool> owned, Action<string> phase, CancellationToken token,
            Action<string> draft, PlanMakerInterviewControl? interview = null) =>
            InterviewOverride?.Invoke(prompt, draft, phase, token, interview) ??
            PlanOverride?.Invoke(prompt, draft) ?? Task.FromException<string>(new InvalidOperationException("Isolated provider error"));
        internal static Func<Task>? StopOverride;
        internal Task StopForTerminalLifecycleAsync() { Stopped = true; return StopOverride?.Invoke() ?? Task.CompletedTask; }
        public void Dispose() { if (Disposed) return; Disposed = true; _active--; }
    }
}

namespace TerminalNotes.Services
{
    internal static class TerminalExecutionTiming
    {
        internal static TimeSpan StartupReadyTimeout => TimeSpan.FromSeconds(1);
    }
}
