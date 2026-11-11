using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace TerminalNotes;

// 나만의 스킬 in the skill window: the last entry of the left list. On top its count, how much of the limits it uses, 자동으로 배우기,
// 보관함 and + 새 스킬; then the curator's merge proposals; then the cards (newest first; a learned one is marked 자동) with on/off
// and ⋯ (수정, 이전 버전, 보관, 삭제). 보관함 lists the archived skills with 복원 and 삭제.
internal sealed partial class SkillStoreDialog
{
    private bool _showArchive;

    /// <summary>Replaces the editor (tests): gets the skill to edit (null for a new one), returns the saved skill or null.</summary>
    internal Func<MySkills.Skill?, MySkills.Skill?>? EditMySkill { get; set; }

    private void RenderMine(string query)
    {
        _empty.Visibility = Visibility.Collapsed;
        List<MySkills.Skill> skills;
        MySkills.Usage usage;
        MySkills.Settings settings;
        try
        {
            skills = MySkills.List();
            usage = MySkills.Measure();
            settings = MySkills.ReadSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowStatus("나만의 스킬 폴더를 읽지 못했습니다 · " + exception.Message);
            return;
        }
        // The bar: name and count, the limits, and what can be done.
        var bar = new DockPanel { Margin = new Thickness(18, 2, 12, 10), LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var learnLabel = new TextBlock { Text = "자동으로 배우기", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        learnLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        var learn = new ToggleButton { IsChecked = settings.AutoLearn, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0),
            ToolTip = "작업을 마치면 다시 쓸 만한 절차를 나만의 스킬로 정리합니다." };
        learn.SetResourceReference(StyleProperty, "StoreSwitch");
        AutomationProperties.SetName(learn, "자동으로 배우기");
        learn.Click += (_, _) => MySkills.UpdateSettings(current => current with { AutoLearn = learn.IsChecked == true });
        actions.Children.Add(learnLabel);
        actions.Children.Add(learn);
        if (usage.Archived > 0 || _showArchive)
        {
            var archive = new Button { Content = _showArchive ? "나만의 스킬" : $"보관함 {usage.Archived}", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            archive.SetResourceReference(StyleProperty, "QuietButton");
            AutomationProperties.SetAutomationId(archive, "MySkillArchive");
            archive.Click += (_, _) => { _showArchive = !_showArchive; Render(); };
            actions.Children.Add(archive);
        }
        var add = new Button { Content = "+ 새 스킬", VerticalAlignment = VerticalAlignment.Center };
        add.SetResourceReference(StyleProperty, "AccentButton");
        AutomationProperties.SetAutomationId(add, "MySkillAdd");
        add.Click += (_, _) => Edit(null);
        actions.Children.Add(add);
        DockPanel.SetDock(actions, Dock.Right);
        bar.Children.Add(actions);
        var heading = new TextBlock { Text = _showArchive ? $"보관함 · {usage.Archived}" : $"{MySkills.Category} · {skills.Count}", FontSize = 13.5,
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        AutomationProperties.SetAutomationId(heading, "SkillPartHeading");
        // How much of the limits is used: what is on goes into every request, so that is the one that matters.
        var limits = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = $"켜짐 {usage.Applied}/{MySkills.MaxApplied} · 전체 {usage.Count}/{MySkills.MaxKept} · {Size(usage.Bytes + usage.ArchiveBytes)}",
            ToolTip = $"켜 둔 스킬은 요청마다 설명이 들어가므로 {MySkills.MaxApplied}개까지 켭니다.\n" +
                      $"자동으로 배운 스킬은 {MySkills.UnusedOff.Days}일 동안 안 쓰면 꺼지고, 꺼진 채 {MySkills.UnusedArchive.Days}일이 지나면 보관함(압축)으로 갑니다.\n" +
                      $"보관함은 {MySkills.MaxArchiveBytes / 1024 / 1024} MB를 넘으면 오래된 것부터 지웁니다. 버전은 최근 {MySkills.KeptVersions}개만 남깁니다." };
        limits.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        AutomationProperties.SetAutomationId(limits, "MySkillUsage");
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(heading);
        title.Children.Add(limits);
        bar.Children.Add(title);
        _sections.Children.Add(bar);

        bool Matches(MySkills.Skill skill) => query.Length == 0 ||
            new[] { skill.Title, skill.Description, skill.Name, skill.Body }.Any(text => text.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        if (_showArchive)
        {
            var archived = MySkills.Archived().Where(Matches).ToList();
            var list = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            foreach (var skill in archived) list.Children.Add(ArchivedRow(skill));
            if (archived.Count == 0) list.Children.Add(Note(query.Length > 0 ? "찾는 스킬이 없습니다." : "보관한 스킬이 없습니다."));
            _sections.Children.Add(list);
            return;
        }
        foreach (var proposal in settings.Proposals.Where(proposal => proposal.Names.All(name => skills.Any(skill => skill.Name == name))))
            _sections.Children.Add(ProposalRow(proposal, skills));
        var shown = skills.Where(Matches).ToList();
        var grid = new UniformGrid { Columns = _columns, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 4) };
        foreach (var skill in shown) grid.Children.Add(MyCard(skill));
        _sections.Children.Add(grid);
        if (shown.Count == 0)
            _sections.Children.Add(Note(query.Length > 0 ? "찾는 스킬이 없습니다."
                : "아직 나만의 스킬이 없습니다. 작업을 마치면 다시 쓸 만한 절차를 자동으로 배우고, + 새 스킬로 직접 만들 수도 있습니다."));
    }

    private static string Size(long bytes) => bytes < 1024 * 1024 ? $"{Math.Max(1, (bytes + 1023) / 1024)} KB" : $"{bytes / 1024.0 / 1024:0.0} MB";

    private static TextBlock Note(string text)
    {
        var note = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(18, 8, 18, 8) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return note;
    }

    private Border MyCard(MySkills.Skill skill)
    {
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var head = new DockPanel();
        var marks = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        if (skill.Learned) marks.Children.Add(Badge("자동", "작업에서 자동으로 배운 스킬"));
        if (skill.Version > 1) marks.Children.Add(Badge($"v{skill.Version}", "고친 횟수만큼 버전이 올라갑니다."));
        DockPanel.SetDock(marks, Dock.Right);
        head.Children.Add(marks);
        head.Children.Add(new TextBlock { Text = skill.Title, FontSize = 14.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 42, VerticalAlignment = VerticalAlignment.Center, ToolTip = skill.Name });
        layout.Children.Add(head);
        if (skill.Description.Length > 0)
        {
            var detail = new TextBlock { Text = skill.Description, FontSize = 13, LineHeight = 19, TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 57, Margin = new Thickness(0, 8, 0, 0) };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            Grid.SetRow(detail, 1);
            layout.Children.Add(detail);
        }
        var foot = new DockPanel { Margin = new Thickness(0, 10, -6, -4) };
        var menu = StoreLook.Menu();
        StoreLook.Item(menu, "수정", () => Edit(skill));
        if (MySkills.Versions(skill.Name).Count > 0) StoreLook.Item(menu, "이전 버전", () => ShowMyVersions(skill));
        StoreLook.Item(menu, "보관", () => Change(() => MySkills.Archive(skill.Name), $"'{skill.Title}'을(를) 보관함으로 옮겼습니다."));
        StoreLook.Item(menu, "삭제", () =>
        {
            if (Confirm($"'{skill.Title}'을(를) 지울까요? 이전 버전도 함께 지워집니다.")) Change(() => MySkills.Delete(skill.Name), $"'{skill.Title}'을(를) 지웠습니다.");
        });
        var more = new Button { Content = StoreLook.Glyph(StoreLook.MoreGlyph, 16), Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        more.SetResourceReference(StyleProperty, "IconButton");
        AutomationProperties.SetName(more, "더 보기 · " + skill.Title);
        more.Click += (_, _) => StoreLook.Open(menu, more);
        var toggle = new ToggleButton { IsChecked = skill.Applied, Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = skill.Applied ? "켜짐 · 에이전트가 필요할 때 씁니다." : "꺼짐" };
        toggle.SetResourceReference(StyleProperty, "StoreSwitch");
        AutomationProperties.SetName(toggle, "켜기 · " + skill.Title);
        toggle.Click += (_, _) => Change(() => MySkills.SetApplied(skill.Name, toggle.IsChecked == true), null);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(more);
        actions.Children.Add(toggle);
        DockPanel.SetDock(actions, Dock.Right);
        foot.Children.Add(actions);
        string where = skill.Project is { Length: > 0 } project ? Path.GetFileName(project.TrimEnd('\\', '/')) : "";
        var meta = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Text = string.Join(" · ", new[] { skill.Updated.ToLocalTime().ToString("yyyy-MM-dd"), where, skill.Uses > 0 ? $"사용 {skill.Uses}회" : "" }.Where(part => part.Length > 0)),
            ToolTip = skill.LastUsed is { } used ? "마지막 사용 " + used.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "아직 쓰지 않음" };
        meta.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        foot.Children.Add(meta);
        Grid.SetRow(foot, 2);
        layout.Children.Add(foot);
        var card = new Border { Child = layout, Tag = skill.Name, CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14, 14, 10),
            Margin = new Thickness(6), MinHeight = 136, BorderThickness = new Thickness(1), ContextMenu = menu };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "ButtonLine");
        card.MouseEnter += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, "ButtonLineHover");
        card.MouseLeave += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, "ButtonLine");
        return card;
    }

    private static Border Badge(string text, string tip)
    {
        var label = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var tag = new Border { Child = label, CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(4, 0, 0, 0),
            BorderThickness = new Thickness(1), ToolTip = tip, VerticalAlignment = VerticalAlignment.Center };
        tag.SetResourceReference(Border.BorderBrushProperty, "ButtonLine");
        return tag;
    }

    // A merge the curator proposes: which skills, why, and its merged text to look at before merging.
    private Border ProposalRow(MySkills.MergeProposal proposal, List<MySkills.Skill> skills)
    {
        string names = string.Join(" + ", proposal.Names.Select(name => "'" + (skills.FirstOrDefault(skill => skill.Name == name)?.Title ?? name) + "'"));
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines.Add(new System.Windows.Documents.Run("정리 제안  ") { FontWeight = FontWeights.SemiBold });
        text.Inlines.Add(new System.Windows.Documents.Run($"{names} → '{proposal.Title}'" + (proposal.Reason.Length > 0 ? " · " + proposal.Reason : "")));
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var look = new Button { Content = "내용 보기", Margin = new Thickness(8, 0, 0, 0) };
        var merge = new Button { Content = "합치기", Margin = new Thickness(8, 0, 0, 0) };
        var dismiss = new Button { Content = "그대로 두기", Margin = new Thickness(8, 0, 0, 0) };
        look.SetResourceReference(StyleProperty, "QuietButton");
        merge.SetResourceReference(StyleProperty, "AccentButton");
        dismiss.SetResourceReference(StyleProperty, "QuietButton");
        look.Click += (_, _) => SkillTextWindow.Show(this, "정리 제안", proposal.Title, proposal.Body);
        merge.Click += (_, _) => Change(() => MySkills.AcceptProposal(proposal), $"'{proposal.Title}'로 합쳤습니다 · 나머지는 보관함에 있습니다.");
        dismiss.Click += (_, _) => Change(() => MySkills.DismissProposal(proposal), null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(look);
        buttons.Children.Add(merge);
        buttons.Children.Add(dismiss);
        var row = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(text);
        var frame = new Border { Child = row, CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 10, 10), Margin = new Thickness(18, 0, 6, 8),
            BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BorderBrushProperty, "Accent");
        AutomationProperties.SetAutomationId(frame, "MySkillProposal");
        return frame;
    }

    private Border ArchivedRow(MySkills.Skill skill)
    {
        var title = new TextBlock { Text = skill.Title, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var when = new TextBlock { Text = skill.LastTouched.ToLocalTime().ToString("yyyy-MM-dd") + (skill.Uses > 0 ? $" · 사용 {skill.Uses}회" : "") +
            (skill.Description.Length > 0 ? " · " + skill.Description : ""), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        when.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var text = new StackPanel();
        text.Children.Add(title);
        text.Children.Add(when);
        var restore = new Button { Content = "복원", Margin = new Thickness(8, 0, 0, 0) };
        var delete = new Button { Content = "삭제", Margin = new Thickness(8, 0, 0, 0) };
        restore.SetResourceReference(StyleProperty, "QuietButton");
        delete.SetResourceReference(StyleProperty, "QuietButton");
        restore.Click += (_, _) => Change(() => MySkills.Restore(skill.Name), $"'{skill.Title}'을(를) 꺼진 채로 되돌렸습니다.");
        delete.Click += (_, _) =>
        {
            if (Confirm($"보관한 '{skill.Title}'을(를) 지울까요?")) Change(() => MySkills.DeleteArchived(skill.Name), null);
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(restore);
        buttons.Children.Add(delete);
        var row = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(text);
        var frame = new Border { Child = row, CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 8, 10, 8), Margin = new Thickness(6, 0, 6, 6),
            BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BackgroundProperty, "Surface");
        frame.SetResourceReference(Border.BorderBrushProperty, "ButtonLine");
        return frame;
    }

    private void Edit(MySkills.Skill? skill)
    {
        MySkills.Skill? saved;
        if (EditMySkill is { } edit) saved = edit(skill);
        else
        {
            var editor = new MySkillEditorDialog(skill) { Owner = this };
            saved = editor.ShowDialog() == true ? editor.Saved : null;
        }
        if (saved is null) return;
        ShowStatus(skill is null ? $"'{saved.Title}'을(를) 만들었습니다." : $"'{saved.Title}'을(를) 고쳤습니다 · v{saved.Version}");
        Render();
    }

    private void ShowMyVersions(MySkills.Skill skill)
    {
        var versions = MySkills.Versions(skill.Name);
        SkillTextWindow.Show(this, "이전 버전", skill.Title, string.Join("\n\n", versions.Select(version => $"── v{version.Version} ──\n{version.Body}")));
    }

    // A change to the library, then the list again; a refusal (a limit) is shown, not thrown.
    private void Change(Action change, string? done)
    {
        try
        {
            change();
            if (done is not null) ShowStatus(done);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowStatus(exception.Message);
        }
        Render();
    }
}

/// <summary>Writing or editing a 나만의 스킬: its title, when it is used (the description the agent picks it by) and its
/// instructions, with the length limit shown as it is typed.</summary>
internal sealed class MySkillEditorDialog : Window
{
    private readonly MySkills.Skill? _skill;
    private readonly TextBox _title = new() { MaxLength = MySkills.MaxTitle, Tag = "제목" };
    private readonly TextBox _description = new() { MaxLength = MySkills.MaxDescription, Tag = "언제 쓰는지 한 줄 (에이전트가 이것을 보고 고릅니다)", Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBox _body = new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = StoreLook.Mono, FontSize = 13, Padding = new Thickness(12, 10, 12, 10), Height = 300, Margin = new Thickness(0, 10, 0, 0),
        Tag = "지침 · 단계, 명령, 확인 방법, 주의점" };
    private readonly TextBlock _count = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 2, 0) };
    private readonly TextBlock _error = new();

    internal MySkills.Skill? Saved { get; private set; }

    internal MySkillEditorDialog(MySkills.Skill? skill)
    {
        _skill = skill;
        Title = skill is null ? "새 나만의 스킬" : "나만의 스킬 수정";
        Width = 640; MinWidth = 480; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        _count.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        AutomationProperties.SetName(_title, "제목");
        AutomationProperties.SetName(_description, "언제 쓰는지");
        AutomationProperties.SetName(_body, "지침");
        if (skill is not null)
        {
            _title.Text = skill.Title;
            _description.Text = skill.Description;
            _body.Text = skill.Body.Replace("\n", "\r\n");
        }
        _body.TextChanged += (_, _) => Count();
        Count();
        var form = new StackPanel { Margin = new Thickness(24, 20, 24, 0) };
        form.Children.Add(_title);
        form.Children.Add(_description);
        form.Children.Add(_body);
        form.Children.Add(_count);
        var cancel = new Button { Content = "취소", IsCancel = true, MinWidth = 88 };
        var save = new Button { Content = "저장", MinWidth = 88, IsDefault = false };
        save.SetResourceReference(StyleProperty, "PrimaryButton");
        save.Click += (_, _) =>
        {
            try
            {
                Saved = MySkills.Save(_skill?.Name, _title.Text, _description.Text, _body.Text);
                DialogResult = true;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                _error.Text = exception.Message;
            }
        };
        var footer = StoreLook.Footer(_error, cancel, save);
        var body = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);
        body.Children.Add(form);
        StoreLook.Dress(this, Title, body, skill is null ? null : $"{skill.Name} · v{skill.Version}");
        Loaded += (_, _) => (skill is null ? _title : _body).Focus();
    }

    private void Count()
    {
        int length = _body.Text.Replace("\r\n", "\n").Trim().Length;
        _count.Text = $"{length:N0} / {MySkills.MaxBodyChars:N0}자";
        _count.SetResourceReference(TextBlock.ForegroundProperty, length > MySkills.MaxBodyChars ? "Accent" : "Muted");
    }
}
