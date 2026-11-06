using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using TerminalNotes.Services;

namespace TerminalNotes;

// Plan cleanup in a window of its own (2026-11-06 사용자: "플랜정리가 너무 오래걸리고 진척사항을 파악할수 없으니 별도의 모달창을
// 띄워서 진행하게해"): percentage and bar, the current step, elapsed time and steps, every step so far with its time, and 정리 중단.
// When it ends the same window shows the outcome: size, every removed item with its reason, whether plan.md was saved.
// It stays above the main window but does not lock it (2026-11-06 사용자: "플랜정리도 여러개 동시 실행가능하지?"): several
// tasks' cleanups run at once, one window each, set off from each other. Neutral black surfaces with the bright gold accent only.
public partial class MainWindow
{
    private sealed class PlanCleanupDialog : Window
    {
        private static readonly Brush Surface = Frozen("#161616"), Edge = Frozen("#3D3D3D"), Track = Frozen("#333333"),
            Gold = Frozen("#E5C07B"), Text = Frozen("#F2F2F2"), Muted = Frozen("#B4B4B4"), Done = Frozen("#A7E8BD");
        private readonly OrganizeProgress _progress;
        private readonly Action _cancel;
        private readonly TextBlock _percent, _phase, _facts, _result;
        private readonly ColumnDefinition _barDone, _barLeft;
        private readonly StackPanel _steps, _removed;
        private readonly Border _resultPanel;
        private readonly ScrollViewer _removedScroll;
        private readonly Button _action;
        private bool _cancelRequested;
        private readonly Func<bool> _appClosing;
        internal bool IsFinished { get; private set; }
        internal string Outcome => _result.Text;

        internal PlanCleanupDialog(Window owner, string task, string heading, OrganizeProgress progress, Action cancel,
            Func<bool> appClosing, int offset)
        {
            _progress = progress;
            _cancel = cancel;
            _appClosing = appClosing;
            Owner = owner;
            // Several at once: each later one sits a step lower and to the right of the centred first one.
            if (offset > 0) Loaded += (_, _) => { Left += 30 * offset; Top += 30 * offset; };
            Title = "플랜 정리 · " + task;
            Width = 560;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = Surface;
            Foreground = Text;
            FontFamily = new FontFamily(new Uri("pack://application:,,,/TerminalNotes;component/"), "./Assets/Fonts/#Pretendard, Segoe UI, Malgun Gothic");
            FontSize = 14;
            if (owner.TryFindResource("DocumentActionButton") is Style buttons) Resources[typeof(Button)] = buttons;

            var content = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
            var header = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            header.Inlines.Add(new System.Windows.Documents.Run("플랜 정리"));
            header.Inlines.Add(new System.Windows.Documents.Run(" · " + task) { Foreground = Muted, FontWeight = FontWeights.Normal });
            content.Children.Add(header);
            if (heading != "플랜 정리")
                content.Children.Add(new TextBlock { Text = heading, Foreground = Muted, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 0) });

            var top = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = true };
            _percent = new TextBlock { FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = Gold, MinWidth = 86, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(_percent, Dock.Left);
            top.Children.Add(_percent);
            _phase = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            top.Children.Add(_phase);
            content.Children.Add(top);

            var bar = new Grid { Height = 6, Margin = new Thickness(0, 10, 0, 0), Background = Track };
            _barDone = new ColumnDefinition();
            _barLeft = new ColumnDefinition();
            bar.ColumnDefinitions.Add(_barDone);
            bar.ColumnDefinitions.Add(_barLeft);
            bar.Children.Add(new Border { Background = Gold });
            content.Children.Add(bar);

            _facts = new TextBlock { Foreground = Muted, FontSize = 12.5, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            content.Children.Add(_facts);
            content.Children.Add(new TextBlock
            {
                Text = "OpenCode는 답을 다 쓴 뒤 한꺼번에 보내므로, 그 전까지는 걸린 시간과 분량으로 진행률을 추정합니다. 정리하는 동안 플랜은 바뀌지 않습니다.",
                Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap
            });

            content.Children.Add(new TextBlock { Text = "진행 단계", FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 14, 0, 6) });
            _steps = new StackPanel();
            content.Children.Add(new Border
            {
                BorderBrush = Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6),
                Child = new ScrollViewer { MaxHeight = 170, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _steps }
            });

            var resultBody = new StackPanel();
            _result = new TextBlock { TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetLiveSetting(_result, AutomationLiveSetting.Polite);
            resultBody.Children.Add(_result);
            _removed = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            _removedScroll = new ScrollViewer { MaxHeight = 170, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _removed, Visibility = Visibility.Collapsed };
            resultBody.Children.Add(_removedScroll);
            _resultPanel = new Border
            {
                BorderBrush = Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 12, 0, 0), Child = resultBody, Visibility = Visibility.Collapsed
            };
            content.Children.Add(_resultPanel);

            _action = new Button { Content = "정리 중단", MinWidth = 96, Padding = new Thickness(12, 7, 12, 7), IsCancel = true,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            AutomationProperties.SetName(_action, "플랜 정리 중단");
            _action.Click += (_, _) => { if (IsFinished) Close(); else RequestCancel(); };
            content.Children.Add(_action);

            Content = new Border { Child = content, BorderBrush = Edge, BorderThickness = new Thickness(1), Background = Surface };
            Closing += OnClosing;
            Update();
        }

        private static Brush Frozen(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }

        private void RequestCancel()
        {
            if (IsFinished || _cancelRequested) return;
            if (!AppMessageDialog.Confirm(this, "플랜 정리 중단", "플랜 정리를 중단할까요?\n\n플랜은 정리 전 그대로 둡니다.", "중단")) return;
            _cancelRequested = true;
            _action.IsEnabled = false;
            _action.Content = "중단하는 중…";
            _cancel();
            Update();
        }

        // Closing while it runs means stopping it; the window stays until the cleanup has really ended. When the app itself
        // closes, the cleanup stops and the window goes without asking.
        private void OnClosing(object? sender, CancelEventArgs e)
        {
            if (IsFinished) return;
            if (_appClosing())
            {
                _cancel();
                return;
            }
            e.Cancel = true;
            RequestCancel();
        }

        internal void Update()
        {
            if (IsFinished) return;
            int percent = Math.Clamp(_progress.Percent, 0, 100);
            ShowPercent(percent);
            _phase.Text = _cancelRequested ? "중단하는 중… 진행 중인 모델 요청을 멈추고 있습니다." : _progress.Phase;
            var elapsed = _progress.Elapsed;
            _facts.Text = $"경과 {(int)elapsed.TotalMinutes}분 {elapsed.Seconds:00}초 · 원문 {_progress.PlanLength:N0}자 · 정리 단계 {_progress.StepsDone}/{_progress.Steps} 완료" +
                (_progress.Calling ? $" · 이번 요청 대기 {(int)_progress.CallElapsed.TotalMinutes}분 {_progress.CallElapsed.Seconds:00}초 · 받은 응답 {_progress.ReceivedChars:N0}자" : "") +
                (_progress.PlanLength > 6000 ? " · 큰 플랜은 몇 분 걸릴 수 있습니다" : "");
            RenderSteps(finished: false);
        }

        private void ShowPercent(int percent)
        {
            _percent.Text = percent + "%";
            _barDone.Width = new GridLength(Math.Max(0.001, percent), GridUnitType.Star);
            _barLeft.Width = new GridLength(Math.Max(0.001, 100 - percent), GridUnitType.Star);
            AutomationProperties.SetName(this, $"플랜 정리 {percent}%");
        }

        private void RenderSteps(bool finished)
        {
            var history = _progress.History;
            while (_steps.Children.Count > history.Count) _steps.Children.RemoveAt(_steps.Children.Count - 1);
            for (int i = 0; i < history.Count; i++)
            {
                bool current = i == history.Count - 1 && !finished;
                var (at, phase) = history[i];
                string text = $"{(int)at.TotalMinutes:00}:{at.Seconds:00}   {(current ? "●" : "✓")}  {phase}";
                if (i < _steps.Children.Count && _steps.Children[i] is TextBlock line)
                {
                    line.Text = text;
                    line.Foreground = current ? Gold : Muted;
                }
                else _steps.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 2), Foreground = current ? Gold : Muted, TextWrapping = TextWrapping.Wrap });
            }
        }

        // The cleanup has ended: what happened, every removed item, and the close button.
        internal void ShowOutcome(string message, bool succeeded, IReadOnlyList<PlanMakerWorkflow.CondenseRemoval> removed)
        {
            if (IsFinished) return;
            IsFinished = true;
            if (succeeded) ShowPercent(100);
            _phase.Text = succeeded ? "정리를 마쳤습니다." : _cancelRequested ? "정리를 중단했습니다." : "정리하지 못했습니다.";
            var elapsed = _progress.Elapsed;
            _facts.Text = $"걸린 시간 {(int)elapsed.TotalMinutes}분 {elapsed.Seconds:00}초 · 정리 단계 {_progress.StepsDone}/{_progress.Steps}";
            RenderSteps(finished: true);
            _result.Text = message;
            _result.Foreground = succeeded ? Text : Gold;
            _removed.Children.Clear();
            if (removed.Count > 0)
            {
                _removed.Children.Add(new TextBlock { Text = $"지운 항목 {removed.Count}개", FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = Done, Margin = new Thickness(0, 4, 0, 4) });
                foreach (var item in removed)
                    _removed.Children.Add(new TextBlock
                    {
                        Text = "– " + item.Text + (item.Reason.Length > 0 ? "  ·  " + item.Reason : ""),
                        FontSize = 12.5, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1)
                    });
            }
            _removedScroll.Visibility = removed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _resultPanel.Visibility = Visibility.Visible;
            _action.Content = "닫기";
            _action.IsEnabled = true;
            _action.IsDefault = true;
            AutomationProperties.SetName(_action, "플랜 정리 창 닫기");
            if (IsVisible) _action.Focus();
        }
    }
}
