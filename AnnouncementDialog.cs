using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using TerminalNotes.Services;

namespace TerminalNotes;

internal sealed class AnnouncementDialog : Window
{
    private readonly AnnouncementService _service;
    private readonly CancellationToken _lifetime;
    private AnnouncementSnapshot _snapshot;
    private readonly Grid _body = new();
    private readonly FlowDocumentScrollViewer _reader = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Background = Paint("#1A1A1A") };
    private readonly TextBox _editor = new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed,
        Background = Paint("#121212"), Foreground = Paint("#F2F2F2"), BorderBrush = Paint("#E5C07B"),
        Padding = new Thickness(12), FontSize = 14, MaxLength = AnnouncementService.MaximumBytes };
    private readonly TextBlock _feedback = new() { Foreground = Paint("#FFCA80"), TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 10, 0, 0) };
    // Whether other PCs see this board (shared, not yet shared, server unreachable, this PC only).
    private readonly TextBlock _share = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -8, 0, 12) };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private Button? _add;
    private Button? _edit;
    private Button? _save;
    private Button? _cancel;
    private bool _editing;
    private bool _saving;
    internal event Action<AnnouncementSnapshot>? SnapshotDisplayed;

    internal AnnouncementDialog(Window owner, AnnouncementService service, AnnouncementSnapshot snapshot, CancellationToken lifetime)
    {
        Owner = owner;
        _service = service;
        _snapshot = snapshot;
        _lifetime = lifetime;
        Title = "공지";
        Width = 720; Height = 560; MinWidth = 360; MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Paint("#1A1A1A"); Foreground = Paint("#F2F2F2");
        FontFamily = new FontFamily(new Uri("pack://application:,,,/TerminalNotes;component/"), "./Assets/Fonts/#Pretendard, Segoe UI, Malgun Gothic"); FontSize = 14; TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        AutomationProperties.SetAutomationId(this, "AnnouncementDialog");
        AppMessageDialog.ApplyOwnerScrollBarStyle(this, owner);
        if (owner.TryFindResource(typeof(TextBox)) is Style editorStyle) _editor.Style = editorStyle;
        void SetFallback(DependencyProperty property, string color)
        {
            if (DependencyPropertyHelper.GetValueSource(_editor, property).BaseValueSource is
                BaseValueSource.Default or BaseValueSource.DefaultStyle or BaseValueSource.DefaultStyleTrigger)
                _editor.SetCurrentValue(property, Paint(color));
        }
        SetFallback(TextBox.CaretBrushProperty, "#E5C07B");
        SetFallback(TextBox.SelectionBrushProperty, "#5C5C5C");
        SetFallback(TextBox.SelectionTextBrushProperty, "#FFFFFF");
        SourceInitialized += (_, _) => AppMessageDialog.ApplyFrame(this);
        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = new TextBlock { Text = "공지", FontSize = 19, FontWeight = FontWeights.SemiBold,
            Foreground = Paint("#E5C07B"), Margin = new Thickness(0, 0, 0, 14) };
        if (service.CanEdit) title.ToolTip = service.SourceLabel;
        AutomationProperties.SetAutomationId(_share, "AnnouncementShareStatus");
        layout.Children.Add(new StackPanel { Children = { title, _share } });
        _body.Children.Add(_reader); _body.Children.Add(_editor);
        AutomationProperties.SetAutomationId(_reader, "AnnouncementContent");
        AutomationProperties.SetAutomationId(_editor, "AnnouncementEditor");
        Grid.SetRow(_body, 1); layout.Children.Add(_body);
        Grid.SetRow(_feedback, 2); layout.Children.Add(_feedback);
        Grid.SetRow(_actions, 3); layout.Children.Add(_actions);
        if (service.CanEdit)
        {
            _add = MakeButton("추가", "AnnouncementAdd", (_, _) => BeginEdit(append: true));
            _edit = MakeButton("수정", "AnnouncementEdit", (_, _) => BeginEdit(append: false));
            _save = MakeButton("저장", "AnnouncementSave", Save_Click);
            _cancel = MakeButton("취소", "AnnouncementCancel", (_, _) => SetEditing(false));
            _save.Visibility = _cancel.Visibility = Visibility.Collapsed;
        }
        MakeButton("닫기", "AnnouncementClose", (_, _) => Close());
        Content = new Border { BorderBrush = Paint("#E5C07B"), BorderThickness = new Thickness(1), Child = layout };
        RenderSnapshot();
        ContentRendered += (_, _) => SnapshotDisplayed?.Invoke(_snapshot);
        Closing += ConfirmDiscard;
    }

    private Button MakeButton(string text, string id, RoutedEventHandler handler)
    {
        var button = new Button { Content = text, MinWidth = 64, Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(8, 12, 0, 0), Style = AppMessageDialog.GoldButtonStyle() };
        AutomationProperties.SetAutomationId(button, id);
        button.Click += handler;
        _actions.Children.Add(button);
        return button;
    }

    private void RenderSnapshot()
    {
        string? share = _service.ShareStatus(_snapshot);
        _share.Text = share ?? "";
        _share.Visibility = share is null ? Visibility.Collapsed : Visibility.Visible;
        _share.Foreground = Paint(share?.StartsWith('⚠') == true ? "#FFCA80" : "#BDBDBD");
        var document = new FlowDocument { PagePadding = new Thickness(12), FontFamily = FontFamily,
            FontSize = 14, Foreground = Foreground, Background = Background };
        if (!_snapshot.HasContent) document.Blocks.Add(new Paragraph(new Run("등록된 공지가 없습니다.")));
        else
        {
            // Render only literal text and Markdown headings. No HTML, URI navigation or fetched images.
            foreach (string line in NewestFirst(_snapshot.Text).Split('\n'))
            {
                int heading = 0;
                while (heading < line.Length && heading < 6 && line[heading] == '#') heading++;
                bool isHeading = heading > 0 && heading < line.Length && line[heading] == ' ';
                var paragraph = new Paragraph(new Run(isHeading ? line[(heading + 1)..] : line))
                    { Margin = new Thickness(0, 0, 0, isHeading ? 12 : 5) };
                if (isHeading) { paragraph.FontWeight = FontWeights.SemiBold; paragraph.FontSize = heading == 1 ? 22 : 18; paragraph.Foreground = Paint("#E5C07B"); }
                document.Blocks.Add(paragraph);
            }
        }
        _reader.Document = document;
    }

    private void BeginEdit(bool append)
    {
        if (!_service.CanEdit) return;
        if (append)
        {
            // New notices go on top: after the board's title/preamble, before the first dated entry.
            var (preamble, sections) = SplitSections(_snapshot.Text);
            string entry = "## " + DateTime.Today.ToString("yyyy-MM-dd") + "\n\n";
            string head = preamble.TrimEnd();
            string before = head.Length == 0 ? "" : head + "\n\n";
            string rest = string.Join("\n\n", sections.Select(section => section.TrimEnd()));
            _editor.Text = before + entry + (rest.Length == 0 ? "" : "\n\n" + rest);
            SetEditing(true);
            _editor.CaretIndex = before.Length + entry.Length;
        }
        else
        {
            _editor.Text = _snapshot.Text;
            SetEditing(true);
            _editor.CaretIndex = _editor.Text.Length;
        }
        _editor.Focus();
    }

    // Splits the board into the text before the first "## " entry and the entries themselves.
    private static (string Preamble, List<string> Sections) SplitSections(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var preamble = new List<string>();
        var sections = new List<string>();
        List<string>? current = null;
        foreach (string line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (current is not null) sections.Add(string.Join("\n", current));
                current = [line];
            }
            else if (current is null) preamble.Add(line);
            else current.Add(line);
        }
        if (current is not null) sections.Add(string.Join("\n", current));
        return (string.Join("\n", preamble), sections);
    }

    // Shows the newest notice first: dated entries ("## yyyy-MM-dd ...") by date descending;
    // when any entry has no date the file order is simply reversed (entries were appended over time).
    internal static string NewestFirst(string text)
    {
        var (preamble, sections) = SplitSections(text);
        if (sections.Count < 2) return text.Replace("\r\n", "\n");
        static DateTime? DateOf(string section)
        {
            string heading = section.Split('\n')[0][3..].Trim();
            return heading.Length >= 10 && DateTime.TryParseExact(heading[..10], "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) ? date : null;
        }
        var dated = sections.Select((section, index) => (Section: section, Index: index, Date: DateOf(section))).ToList();
        var ordered = dated.All(item => item.Date is not null)
            ? dated.OrderByDescending(item => item.Date).ThenByDescending(item => item.Index).Select(item => item.Section)
            : dated.AsEnumerable().Reverse().Select(item => item.Section);
        string head = preamble.TrimEnd();
        return (head.Length == 0 ? "" : head + "\n\n") + string.Join("\n\n", ordered.Select(section => section.TrimEnd()));
    }

    private void SetEditing(bool editing)
    {
        _editing = editing;
        _feedback.Text = "";
        _reader.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        _editor.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        if (_add is null) return;
        _add.Visibility = _edit!.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        _save!.Visibility = _cancel!.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        _saving = true;
        _actions.IsEnabled = false;
        _editor.IsReadOnly = true;
        try
        {
            AnnouncementSnapshot saved = await _service.SaveAsync(_snapshot, _editor.Text, _lifetime);
            if (_lifetime.IsCancellationRequested || !IsVisible) return;
            _snapshot = saved;
            RenderSnapshot();
            SetEditing(false);
            ApplicationDiagnostics.Record("announcement_saved", "announcements");
            await Dispatcher.InvokeAsync(() =>
            {
                if (IsVisible && !_editing) SnapshotDisplayed?.Invoke(saved);
            }, DispatcherPriority.ContextIdle);
        }
        catch (OperationCanceledException) { if (!_lifetime.IsCancellationRequested) _feedback.Text = "저장 시간이 초과되었습니다. 다시 시도해 주세요."; }
        catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or System.Text.EncoderFallbackException)
        {
            // The relay's own words say why (server unreachable, an earlier relay, a wrong key, not an editor there);
            // a local file's system message would only confuse.
            _feedback.Text = error is AnnouncementConflictException or System.IO.InvalidDataException || _service.UsesRelay ? error.Message :
                "공지를 저장할 수 없습니다. 계정과 파일 권한을 확인해 주세요.";
            ApplicationDiagnostics.Record("announcement_save_failed", "announcements", error is AnnouncementConflictException ? "conflict" : "unavailable");
        }
        finally { _saving = false; _actions.IsEnabled = true; _editor.IsReadOnly = false; }
    }

    private void ConfirmDiscard(object? sender, CancelEventArgs e)
    {
        if (_lifetime.IsCancellationRequested) return;
        if (_saving) { e.Cancel = true; return; }
        if (_editing && _editor.Text != _snapshot.Text &&
            !AppMessageDialog.Confirm(this, "공지", "수정 중인 내용을 닫으시겠습니까?", "닫기"))
            e.Cancel = true;
    }

    private static Brush Paint(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
}
