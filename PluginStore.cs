using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace TerminalNotes;

// 플러그인 (사용자 지시: 스킬스토어 옆에 플러그인 버튼, 스킬스토어처럼 쓰되 적용·해제가 아니라 설치·제거). An OpenCode plugin is
// code: one .ts or .js file that OpenCode finds in its plugins folder (~/.config/opencode/plugins, checked with OpenCode 1.18.34)
// and runs when it starts. Installing puts the store's file there, removing deletes it; both reach the next OpenCode terminal.
// The store is the relay's /api/plugins; only master accounts (MASTER_USERS) share plugins, since they run on every PC.
// A plugin shared from a GitHub (or GitHub Enterprise) link follows that file: the relay makes a new version when it changes, and
// this window takes it for the plugins installed from the store when it opens.

/// <summary>A plugin in the store at its latest version; <see cref="Code"/> only when one plugin is fetched.</summary>
internal sealed record StorePlugin
{
    public string Name { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Author { get; init; } = "";
    public int Version { get; init; } = 1;
    public string Extension { get; init; } = ".ts";
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public int Downloads { get; init; }
    public string? Note { get; init; }
    public string Sha256 { get; init; } = "";
    public string? Code { get; init; }
    /// <summary>The GitHub file it follows (relay 2.9), and why the relay last could not read it.</summary>
    public string? Source { get; init; }
    public string? SourceError { get; init; }

    /// <summary>When it was shared, or last changed once it has newer versions.</summary>
    public DateTimeOffset? ShownDate => Version > 1 ? UpdatedAt ?? CreatedAt : CreatedAt ?? UpdatedAt;
    public string DisplayTitle => StoreSkill.Shown(Title, Author);
}

/// <summary>Plugins in OpenCode's global plugins folder. The store's carry a record in plugins\.terminal-notes\&lt;name&gt;.json
/// (JSON, which OpenCode does not load); any other .ts/.js file there is the user's own and is never replaced or removed.</summary>
internal static class InstalledPlugins
{
    internal sealed record Entry(string Name, string File, bool FromStore, int Version, string? Title, bool Edited);
    private sealed record State(string Name, string Title, int Version, string File, string Sha256, DateTimeOffset InstalledAt);

    private static readonly string[] Extensions = [".ts", ".js"];
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // OpenCode reads <XDG_CONFIG_HOME or ~/.config>/opencode/plugins/*.ts|*.js when it starts; tests point this elsewhere.
    internal static string DirectoryPath => AppContext.GetData("TerminalNotes.PluginsDirectory") is string test
        ? Path.GetFullPath(test)
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
            ? config : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "opencode", "plugins");

    private static string StatePath(string name) => Path.Combine(DirectoryPath, ".terminal-notes", name + ".json");

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    internal static Dictionary<string, Entry> List()
    {
        var found = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (!Directory.Exists(DirectoryPath)) return found;
        string states = Path.Combine(DirectoryPath, ".terminal-notes");
        if (Directory.Exists(states))
            foreach (string path in Directory.EnumerateFiles(states, "*.json"))
            {
                State? state;
                try { state = JsonSerializer.Deserialize<State>(File.ReadAllText(path)); }
                catch (JsonException) { continue; }
                if (state is null || !InstalledSkills.ValidName(state.Name)) continue;
                string file = Path.Combine(DirectoryPath, state.File);
                // Its file deleted by hand: no longer installed.
                if (!File.Exists(file)) { File.Delete(path); continue; }
                found[state.Name] = new Entry(state.Name, state.File, true, state.Version, state.Title,
                    !string.Equals(Sha(File.ReadAllBytes(file)), state.Sha256, StringComparison.OrdinalIgnoreCase));
            }
        foreach (string path in Directory.EnumerateFiles(DirectoryPath))
        {
            string extension = Path.GetExtension(path).ToLowerInvariant(), name = Path.GetFileNameWithoutExtension(path);
            if (Extensions.Contains(extension) && !found.Values.Any(entry => string.Equals(entry.File, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)))
                found.TryAdd(name, new Entry(name, Path.GetFileName(path), false, 0, null, false));
        }
        return found;
    }

    /// <summary>Puts a store plugin's code in OpenCode's plugins folder (replacing the version installed before).</summary>
    internal static void Install(StorePlugin plugin, string code)
    {
        if (!InstalledSkills.ValidName(plugin.Name)) throw new InvalidDataException("플러그인 이름이 올바르지 않습니다.");
        var current = List().GetValueOrDefault(plugin.Name);
        if (current is { FromStore: false }) throw new IOException("이 PC에서 직접 만든 같은 이름의 플러그인이 있습니다.");
        string extension = Extensions.Contains(plugin.Extension) ? plugin.Extension : ".ts", file = plugin.Name + extension;
        Directory.CreateDirectory(Path.Combine(DirectoryPath, ".terminal-notes"));
        string target = Path.Combine(DirectoryPath, file);
        if (current is not null && !string.Equals(current.File, file, StringComparison.OrdinalIgnoreCase))
            File.Delete(Path.Combine(DirectoryPath, current.File));
        else if (current is null && File.Exists(target)) throw new IOException("이 PC에서 직접 만든 같은 이름의 플러그인 파일이 있습니다.");
        byte[] data = Utf8.GetBytes(code.ReplaceLineEndings("\n"));
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temporary, data);
        File.Move(temporary, target, overwrite: true);
        File.WriteAllText(StatePath(plugin.Name), JsonSerializer.Serialize(new State(plugin.Name, plugin.Title, plugin.Version, file, Sha(data), DateTimeOffset.UtcNow)));
    }

    /// <summary>Deletes a store plugin's file (only one the store installed).</summary>
    internal static void Remove(string name)
    {
        var current = List().GetValueOrDefault(name);
        if (current is not { FromStore: true }) throw new IOException("스토어에서 설치한 플러그인이 아닙니다.");
        File.Delete(Path.Combine(DirectoryPath, current.File));
        File.Delete(StatePath(name));
    }
}

/// <summary>플러그인: the store's plugins as cards, the installed ones on top; 설치 puts one in OpenCode's plugins folder, 제거 takes
/// it out. Master accounts share plugins; the author or a master changes or deletes one (⋯).</summary>
internal sealed class PluginStoreDialog : Window
{
    private const string NoStore = "이 빌드에는 스토어 주소가 없습니다(.env의 RELAY_HOST).";
    private readonly SkillStoreClient? _store;
    private readonly UniformGrid _installed = new() { Columns = 3, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(18, 0, 18, 6) };
    private readonly UniformGrid _others = new() { Columns = 3, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(18, 4, 18, 10) };
    private readonly TextBlock _installedHeading = new(), _othersHeading = new();
    private readonly TextBox _search = new() { Tag = "검색", MaxLength = 80, Width = 240, Padding = new Thickness(11, 6, 11, 6), FontSize = 13 };
    private readonly TextBlock _status = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MinHeight = 18, Margin = new Thickness(24, 0, 24, 14) };
    private readonly TextBlock _empty = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 420,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Button _refresh = new() { Content = StoreLook.Glyph(StoreLook.RefreshGlyph, 14), ToolTip = "새로 고침 (F5)", Margin = new Thickness(0, 0, 4, 0) };
    private readonly Button _share = new() { Content = "+ 플러그인 올리기", MinHeight = 30, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0) };
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<StorePlugin> _plugins = [];
    private string? _failure;
    private bool _busy;

    /// <summary>Asks before installing code, replacing a hand-edited file, or deleting; tests answer it.</summary>
    internal Func<string, bool> Confirm { get; set; }

    internal PluginStoreDialog(SkillStoreClient? store)
    {
        _store = store;
        Confirm = message => AppMessageDialog.Confirm(this, "플러그인", message, "확인");
        Title = "플러그인";
        var screen = SystemParameters.WorkArea;
        Width = Math.Min(1080, screen.Width - 40); Height = Math.Min(740, screen.Height - 40); MinWidth = 520; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        _share.SetResourceReference(StyleProperty, "OutlineButton");
        // Plugins run as code on every PC that installs them: master accounts share them.
        _share.Visibility = store is not null && SkillStoreAdmins.Allowed ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_share, "플러그인 올리기");
        _refresh.SetResourceReference(StyleProperty, "IconButton");
        _refresh.Visibility = store is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(_refresh, "새로 고침");
        AutomationProperties.SetName(_search, "플러그인 검색");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        foreach (var heading in new[] { _installedHeading, _othersHeading })
        {
            heading.FontSize = 13;
            heading.Margin = new Thickness(24, 8, 24, 4);
            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        }
        _othersHeading.Margin = new Thickness(24, 18, 24, 4);
        var tools = new DockPanel { Margin = new Thickness(24, 14, 24, 6) };
        DockPanel.SetDock(_search, Dock.Left);
        tools.Children.Add(_search);
        tools.Children.Add(new Border());
        var parts = new StackPanel();
        foreach (UIElement part in new UIElement[] { _installedHeading, _installed, _othersHeading, _others }) parts.Children.Add(part);
        var scroll = new ScrollViewer { Content = parts, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.SizeChanged += (_, e) => _installed.Columns = _others.Columns = Math.Max(1, (int)((e.NewSize.Width - 36) / 330));
        var center = new Grid();
        center.Children.Add(scroll);
        center.Children.Add(_empty);
        var body = new DockPanel();
        DockPanel.SetDock(tools, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        body.Children.Add(tools);
        body.Children.Add(_status);
        body.Children.Add(center);
        StoreLook.Dress(this, "플러그인", body, null, _refresh, _share);
        _share.Click += async (_, _) => await ShareAsync(null);
        _refresh.Click += async (_, _) => await RefreshAsync();
        _search.TextChanged += (_, _) => Render();
        KeyDown += async (_, e) => { if (e.Key == Key.F5) await RefreshAsync(); };
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _lifetime.Cancel();
    }

    internal string StatusText => _status.Text;
    internal string SearchText { get => _search.Text; set => _search.Text = value; }

    internal async Task RefreshAsync()
    {
        if (_busy) return;
        SetBusy(true, "불러오는 중…");
        string? failure = null;
        try { _plugins = _store is null ? [] : await _store.PluginsAsync(_lifetime.Token); }
        catch (SkillStoreException exception) { failure = exception.Status == HttpStatusCode.NotFound
            ? "중계 서버가 플러그인을 아직 지원하지 않습니다(relay.py 2.8 이상 필요)." : exception.Message; }
        catch (OperationCanceledException) { return; }
        _failure = failure;
        SetBusy(false, "");
        if (failure is null) await UpdateLinkedAsync();
        Render();
    }

    // Plugins that follow a GitHub file stay current here: a newer store version of one installed from the store is taken at once
    // (a file changed by hand on this PC is left alone).
    private async Task UpdateLinkedAsync()
    {
        if (_store is null) return;
        var installed = TryList();
        int updated = 0;
        foreach (var plugin in _plugins.Where(plugin => plugin.Source is { Length: > 0 }))
        {
            if (installed.GetValueOrDefault(plugin.Name) is not { FromStore: true, Edited: false } local || plugin.Version <= local.Version) continue;
            try
            {
                var latest = await _store.GetPluginAsync(plugin.Name, install: true, _lifetime.Token);
                InstalledPlugins.Install(latest, latest.Code!);
                updated++;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception exception) when (exception is SkillStoreException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { }
        }
        if (updated > 0) ShowStatus($"깃허브에서 바뀐 플러그인 {updated}개를 업데이트했습니다. 새로 여는 OpenCode 터미널부터 실행됩니다.");
    }

    private Dictionary<string, InstalledPlugins.Entry> TryList()
    {
        try { return InstalledPlugins.List(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowStatus("플러그인 폴더를 읽지 못했습니다 · " + exception.Message);
            return [];
        }
    }

    private void Render()
    {
        _installed.Children.Clear();
        _others.Children.Clear();
        var installed = TryList();
        string query = _search.Text.Trim();
        bool Matches(StorePlugin plugin) => query.Length == 0 || new[] { plugin.Title, plugin.Description, plugin.Author, plugin.Name }
            .Any(text => text.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        foreach (var plugin in _plugins.Where(Matches).OrderByDescending(plugin => plugin.ShownDate))
        {
            var local = installed.GetValueOrDefault(plugin.Name);
            (local is { FromStore: true } ? _installed : _others).Children.Add(Card(plugin.Title, plugin.Description, plugin.Name, local, plugin));
        }
        // Installed from the store but gone from it (or the store unreachable): still removable.
        if (query.Length == 0)
            foreach (var local in installed.Values.Where(entry => entry.FromStore && _plugins.All(plugin => plugin.Name != entry.Name)).OrderBy(entry => entry.Name))
                _installed.Children.Add(Card(local.Title ?? local.Name, "스토어 목록에 없음", local.Name, local, null));
        bool split = _installed.Children.Count > 0;
        _installedHeading.Text = $"설치됨 · {_installed.Children.Count}";
        _othersHeading.Text = $"설치 안 됨 · {_others.Children.Count}";
        _installed.Visibility = _installedHeading.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
        _othersHeading.Visibility = split && _others.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        bool any = _installed.Children.Count + _others.Children.Count > 0;
        string? problem = _store is null ? NoStore : _failure;
        _empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (!any) _empty.Text = problem ?? (_plugins.Count > 0 ? "찾는 플러그인이 없습니다." : "아직 올라온 플러그인이 없습니다.");
        else if (problem is not null && _status.Text.Length == 0) ShowStatus(problem);
    }

    // A card: the title and the version's badge; a short description; at the bottom left the date and the author in gold, at the
    // right 업데이트 when the store has a newer version, ⋯, and 설치 or 제거.
    private Border Card(string title, string description, string name, InstalledPlugins.Entry? local, StorePlugin? plugin)
    {
        bool installed = local is { FromStore: true }, own = local is { FromStore: false };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock { Text = title, FontSize = 14.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 42, VerticalAlignment = VerticalAlignment.Center, ToolTip = name });
        if (plugin is { Source.Length: > 0 } linked)
        {
            var mark = StoreLook.Glyph(StoreLook.LinkGlyph, 12);
            mark.Margin = new Thickness(6, 0, 0, 0);
            mark.ToolTip = "깃허브 파일을 따라 자동 업데이트\n" + linked.Source + (linked.SourceError is { Length: > 0 } error ? "\n" + error : "");
            mark.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            AutomationProperties.SetName(mark, "깃허브 연결");
            Grid.SetColumn(mark, 1);
            head.Children.Add(mark);
        }
        if (plugin is not null)
        {
            var badge = Badge($"v{plugin.Version}" + (plugin.Extension == ".js" ? " · js" : ""),
                installed && local!.Version != plugin.Version ? $"최신 v{plugin.Version} · 이 PC는 v{local.Version} 설치됨" : $"최신 v{plugin.Version}");
            Grid.SetColumn(badge, 2);
            head.Children.Add(badge);
        }
        layout.Children.Add(head);
        string? notice = own ? "이 PC에서 직접 만든 같은 이름의 플러그인이 있습니다." : local is { Edited: true } ? "이 PC에서 고친 파일입니다." : null;
        if ((notice ?? description).Length > 0)
        {
            var detail = new TextBlock { Text = notice ?? description, FontSize = 13, LineHeight = 19, TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 57, Margin = new Thickness(0, 8, 0, 0) };
            detail.SetResourceReference(TextBlock.ForegroundProperty, notice is null ? "TextSecondary" : "Accent");
            Grid.SetRow(detail, 1);
            layout.Children.Add(detail);
        }
        var foot = new Grid { Margin = new Thickness(0, 10, -6, -4) };
        foot.ColumnDefinitions.Add(new ColumnDefinition());
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string byline = string.Join(" · ", new[] { plugin?.ShownDate?.ToLocalTime().ToString("yyyy-MM-dd"), plugin?.Author }.Where(part => !string.IsNullOrEmpty(part)));
        if (byline.Length > 0)
        {
            var meta = new TextBlock { Text = byline, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            meta.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            foot.Children.Add(meta);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (installed && plugin is not null && plugin.Version > local!.Version)
        {
            var update = new Button { Content = "업데이트", ToolTip = $"v{local.Version} → v{plugin.Version}", VerticalAlignment = VerticalAlignment.Center };
            update.SetResourceReference(StyleProperty, "AccentButton");
            update.Click += async (_, _) => await InstallAsync(plugin);
            actions.Children.Add(update);
        }
        var menu = Menu(name, title, plugin);
        if (menu.Items.Count > 0)
        {
            var more = new Button { Content = StoreLook.Glyph(StoreLook.MoreGlyph, 16), Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            more.SetResourceReference(StyleProperty, "IconButton");
            AutomationProperties.SetName(more, "더 보기 · " + title);
            more.Click += (_, _) => StoreLook.Open(menu, more);
            actions.Children.Add(more);
        }
        var main = new Button { Content = installed ? "제거" : "설치", MinWidth = 64, Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = installed || plugin is not null && !own, ToolTip = own ? "이 PC에서 직접 만든 같은 이름의 플러그인이 있습니다." : null };
        main.SetResourceReference(StyleProperty, installed ? "QuietButton" : "OutlineButton");
        ToolTipService.SetShowOnDisabled(main, true);
        AutomationProperties.SetName(main, (installed ? "제거 · " : "설치 · ") + title);
        main.Click += async (_, _) =>
        {
            if (installed) await RemoveAsync(name, title);
            else if (plugin is not null) await InstallAsync(plugin);
        };
        actions.Children.Add(main);
        Grid.SetColumn(actions, 1);
        foot.Children.Add(actions);
        Grid.SetRow(foot, 2);
        layout.Children.Add(foot);
        var card = new Border { Child = layout, Tag = name, CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14, 14, 10),
            Margin = new Thickness(6), MinHeight = 136, BorderThickness = new Thickness(1), ContextMenu = menu.Items.Count > 0 ? menu : null };
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
        var badge = new Border { Child = label, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Padding = new Thickness(7, 1, 7, 1),
            Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
        badge.SetResourceReference(Border.BackgroundProperty, "SurfaceElevated");
        badge.SetResourceReference(Border.BorderBrushProperty, "ButtonLine");
        AutomationProperties.SetName(badge, "버전 " + text);
        return badge;
    }

    private ContextMenu Menu(string name, string title, StorePlugin? plugin)
    {
        var menu = StoreLook.Menu();
        if (plugin is null || _store is null) return menu;
        StoreLook.Item(menu, "코드 보기", async () => await ShowCodeAsync(plugin));
        // Its author, or a master account whoever wrote it, changes and deletes it.
        if (string.Equals(plugin.Author, Environment.UserName, StringComparison.OrdinalIgnoreCase) || SkillStoreAdmins.Allowed)
        {
            StoreLook.Item(menu, "수정", async () => await ShareAsync(plugin));
            StoreLook.Item(menu, "삭제", async () => await DeleteAsync(plugin));
        }
        return menu;
    }

    /// <summary>Installs (or updates to) the store's latest version. Code runs inside OpenCode, so a first install asks.</summary>
    internal async Task InstallAsync(StorePlugin plugin)
    {
        if (_busy || _store is null) return;
        var local = TryList().GetValueOrDefault(plugin.Name);
        if (local is null && !Confirm($"‘{plugin.DisplayTitle}’을(를) 설치할까요?\n플러그인은 OpenCode 안에서 코드로 실행됩니다. 새로 여는 OpenCode 터미널부터 적용됩니다."))
            return;
        if (local is { Edited: true } && !Confirm($"‘{plugin.DisplayTitle}’ 파일을 이 PC에서 고쳤습니다. 스토어의 v{plugin.Version}으로 바꿀까요?")) return;
        SetBusy(true, $"‘{plugin.DisplayTitle}’ 설치 중…");
        string message;
        try
        {
            var latest = await _store.GetPluginAsync(plugin.Name, install: true, _lifetime.Token);
            InstalledPlugins.Install(latest, latest.Code!);
            message = $"‘{latest.DisplayTitle}’ v{latest.Version}을(를) 설치했습니다. 새로 여는 OpenCode 터미널부터 실행됩니다.";
        }
        catch (OperationCanceledException) { return; }
        catch (Exception exception) when (exception is SkillStoreException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { message = "설치하지 못했습니다 · " + exception.Message; }
        SetBusy(false, message);
        Render();
    }

    internal Task RemoveAsync(string name, string title)
    {
        if (_busy) return Task.CompletedTask;
        var local = TryList().GetValueOrDefault(name);
        if (local is { Edited: true } && !Confirm($"‘{title}’ 파일을 이 PC에서 고쳤습니다. 제거할까요?")) return Task.CompletedTask;
        try
        {
            InstalledPlugins.Remove(name);
            ShowStatus($"‘{title}’을(를) 제거했습니다. 새로 여는 OpenCode 터미널부터 빠집니다.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { ShowStatus("제거하지 못했습니다 · " + exception.Message); }
        Render();
        return Task.CompletedTask;
    }

    private async Task ShowCodeAsync(StorePlugin plugin)
    {
        if (_busy || _store is null) return;
        SetBusy(true, "코드를 불러오는 중…");
        StorePlugin latest;
        try { latest = await _store.GetPluginAsync(plugin.Name, install: false, _lifetime.Token); SetBusy(false, ""); }
        catch (OperationCanceledException) { return; }
        catch (Exception exception) when (exception is SkillStoreException or JsonException) { SetBusy(false, exception.Message); return; }
        SkillTextWindow.Show(this, "코드", $"{latest.DisplayTitle} v{latest.Version} · {latest.Name}{latest.Extension}", latest.Code ?? "");
    }

    internal async Task DeleteAsync(StorePlugin plugin)
    {
        if (_busy || _store is null) return;
        if (!Confirm($"‘{plugin.DisplayTitle}’을(를) 스토어에서 삭제할까요?\n이미 설치한 PC의 플러그인은 그대로 남습니다.")) return;
        SetBusy(true, $"‘{plugin.DisplayTitle}’ 삭제 중…");
        string message;
        try
        {
            await _store.DeletePluginAsync(plugin.Name, _lifetime.Token);
            message = $"‘{plugin.DisplayTitle}’을(를) 삭제했습니다.";
        }
        catch (OperationCanceledException) { return; }
        catch (SkillStoreException exception) { message = "삭제하지 못했습니다 · " + exception.Message; }
        SetBusy(false, "");
        await RefreshAsync();
        ShowStatus(message);
    }

    private async Task ShareAsync(StorePlugin? existing)
    {
        if (_busy || _store is null) return;
        var dialog = new SharePluginDialog(_store, existing, _plugins.Select(plugin => plugin.Name).ToList()) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await RefreshAsync();
        ShowStatus(existing is null ? $"‘{dialog.SharedTitle}’을(를) 올렸습니다." : $"‘{dialog.SharedTitle}’ v{dialog.SharedVersion}을(를) 올렸습니다.");
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        _share.IsEnabled = _refresh.IsEnabled = !busy;
        foreach (var card in _installed.Children.OfType<Border>().Concat(_others.Children.OfType<Border>())) card.IsEnabled = !busy;
        ShowStatus(status);
    }

    private void ShowStatus(string message) => _status.Text = message;
}

/// <summary>Shares a plugin (title, a line on what it does, and its code: typed or pasted, a .ts/.js file opened or dropped, or a
/// GitHub link to its file, which the plugin then follows), or a new version of one, which starts from its latest code.</summary>
internal sealed class SharePluginDialog : Window
{
    private readonly SkillStoreClient _store;
    private readonly StorePlugin? _existing;
    private readonly IReadOnlyList<string> _taken;
    private readonly string _user = Environment.UserName;
    private readonly TextBox _title = new() { MaxLength = 80, Tag = "제목" };
    private readonly TextBox _description = new() { MaxLength = 500, Tag = "무엇을 하는 플러그인인지 (선택)", Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBox _code = new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = StoreLook.Mono, FontSize = 13, Padding = new Thickness(12, 10, 12, 10),
        Tag = "플러그인 코드(.ts 또는 .js)를 붙여 넣거나 파일을 여세요" };
    private readonly Button _open = new() { Content = "파일 열기…" };
    private readonly TextBlock _kind = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly TextBox _note = new() { MaxLength = 200, Tag = "바뀐 점 (선택)", Margin = new Thickness(0, 12, 0, 0) };
    private readonly RadioButton _codeTab = new() { Content = "코드", IsChecked = true };
    private readonly RadioButton _gitHubTab = new() { Content = "깃허브" };
    private readonly TextBox _link = new() { MaxLength = 500, Tag = "깃허브 링크 (.ts 또는 .js 파일)" };
    private readonly TextBlock _linkState = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 10, 2, 10) };
    private readonly TextBox _linkedCode = new() { IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = StoreLook.Mono, FontSize = 12.5, Padding = new Thickness(12, 10, 12, 10) };
    private readonly DockPanel _codePanel = new(), _gitHubPanel = new();
    private readonly System.Windows.Threading.DispatcherTimer _linkPause = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private string _linkedFrom = "", _linkedExtension = ".ts";
    private readonly TextBlock _error = new();
    private readonly Button _submit = new() { Content = "올리기", MinWidth = 88 };
    private string _extension = ".ts";
    private string _fileStem = "";
    private bool _busy;

    internal string SharedTitle { get; private set; } = "";
    internal int SharedVersion { get; private set; }

    internal SharePluginDialog(SkillStoreClient store, StorePlugin? existing = null, IReadOnlyList<string>? taken = null)
    {
        _store = store;
        _existing = existing;
        _taken = taken ?? [];
        Title = existing is null ? "플러그인 올리기" : "새 버전 올리기";
        Width = 680; MinWidth = 500; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        _kind.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        if (existing is not null)
        {
            _title.Text = existing.Title;
            _description.Text = existing.Description;
            _extension = existing.Extension;
        }
        AutomationProperties.SetName(_code, "플러그인 코드");
        AutomationProperties.SetName(_description, "설명");
        var form = new StackPanel { Margin = new Thickness(24, 20, 24, 0) };
        form.Children.Add(_title);
        form.Children.Add(_description);
        var tabs = StoreLook.Tabs(_codeTab, _gitHubTab);
        tabs.Margin = new Thickness(0, 14, 0, 10);
        foreach (var tab in new[] { _codeTab, _gitHubTab }) tab.Checked += (_, _) => ShowSource();
        _gitHubTab.Click += (_, _) => _link.Focus();
        form.Children.Add(tabs);
        // 코드: the file button and the box; 깃허브: the link, what was read, and the code it gave (read-only). The tab not shown
        // is only hidden, so switching keeps the window's size.
        var codeRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        _open.SetResourceReference(StyleProperty, "QuietButton");
        DockPanel.SetDock(_open, Dock.Left);
        codeRow.Children.Add(_open);
        codeRow.Children.Add(_kind);
        DockPanel.SetDock(codeRow, Dock.Top);
        _codePanel.Children.Add(codeRow);
        _codePanel.Children.Add(_code);
        AutomationProperties.SetName(_link, "깃허브 링크");
        AutomationProperties.SetName(_linkedCode, "깃허브 파일의 코드");
        _linkState.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DockPanel.SetDock(_link, Dock.Top);
        DockPanel.SetDock(_linkState, Dock.Top);
        _gitHubPanel.Children.Add(_link);
        _gitHubPanel.Children.Add(_linkState);
        _gitHubPanel.Children.Add(_linkedCode);
        var source = new Grid { Height = 300 };
        source.Children.Add(_codePanel);
        source.Children.Add(_gitHubPanel);
        form.Children.Add(source);
        // A link is read once typing pauses.
        _link.TextChanged += (_, _) =>
        {
            _linkPause.Stop();
            if (_link.Text.Trim() != _linkedFrom) { _linkedFrom = ""; _linkedCode.Text = ""; _linkState.Text = ""; }
            if (_link.Text.Trim().Length > 0) _linkPause.Start();
        };
        _linkPause.Tick += async (_, _) =>
        {
            _linkPause.Stop();
            await LoadLinkAsync();
        };
        Closed += (_, _) => _linkPause.Stop();
        if (existing is not null) form.Children.Add(_note);
        var cancel = new Button { Content = "취소", IsCancel = true, MinWidth = 88 };
        _submit.SetResourceReference(StyleProperty, "PrimaryButton");
        if (existing is not null) _submit.Content = "새 버전 올리기";
        var footer = StoreLook.Footer(_error, cancel, _submit);
        var body = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);
        body.Children.Add(form);
        StoreLook.Dress(this, Title, body, existing is null ? null : $"{existing.DisplayTitle} · 지금 v{existing.Version}");
        ShowKind();
        if (existing?.Source is { Length: > 0 } followed)
        {
            _link.Text = followed;
            _gitHubTab.IsChecked = true;
        }
        ShowSource();
        _code.TextChanged += (_, _) => { if (!_busy) _error.Text = ""; };
        _open.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Title = "플러그인 파일", Filter = "플러그인 (*.ts, *.js)|*.ts;*.js|모든 파일 (*.*)|*.*" };
            if (picker.ShowDialog(this) == true) LoadFile(picker.FileName);
        };
        AllowDrop = true;
        PreviewDragOver += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; } };
        PreviewDrop += (_, e) =>
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
            e.Handled = true;
            _codeTab.IsChecked = true;
            if (paths.Length == 1) LoadFile(paths[0]);
            else _error.Text = "파일은 하나만 끌어 놓으세요.";
        };
        _submit.Click += async (_, _) => { if (await SubmitAsync()) DialogResult = true; };
        Loaded += async (_, _) =>
        {
            (existing is null ? _title : _note).Focus();
            // A new version starts from the latest code.
            if (existing is null) return;
            try { _code.Text = (await _store.GetPluginAsync(existing.Name, install: false, CancellationToken.None)).Code ?? ""; }
            catch (Exception exception) when (exception is SkillStoreException or JsonException) { _error.Text = exception.Message; }
        };
    }

    internal string TitleText { get => _title.Text; set => _title.Text = value; }
    internal string DescriptionText { get => _description.Text; set => _description.Text = value; }
    internal string CodeText { get => _code.Text; set => _code.Text = value; }
    internal string NoteText { get => _note.Text; set => _note.Text = value; }
    internal string Extension => _extension;
    internal string ErrorText => _error.Text;
    internal bool GitHubMode { get => _gitHubTab.IsChecked == true; set => (value ? _gitHubTab : _codeTab).IsChecked = true; }
    internal string LinkText { get => _link.Text; set => _link.Text = value; }
    internal string LinkedCode => _linkedCode.Text;

    private void ShowKind() => _kind.Text = _extension == ".js" ? "JavaScript (.js)" : "TypeScript (.ts)";

    private void ShowSource()
    {
        bool gitHub = _gitHubTab.IsChecked == true;
        _codePanel.Visibility = gitHub ? Visibility.Hidden : Visibility.Visible;
        _gitHubPanel.Visibility = gitHub ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>Reads the link's .ts/.js file through the relay; its file name gives the title until one is typed.</summary>
    internal async Task<bool> LoadLinkAsync()
    {
        string link = _link.Text.Trim();
        if (link.Length == 0) return false;
        if (link == _linkedFrom) return true;
        _linkState.Text = "깃허브에서 읽는 중…";
        try
        {
            var (code, extension) = await _store.FetchPluginSourceAsync(link, CancellationToken.None);
            if (_link.Text.Trim() != link) return false;
            _linkedFrom = link;
            _linkedExtension = extension;
            _linkedCode.Text = code;
            string file = Uri.TryCreate(link, UriKind.Absolute, out var address) ? Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(address.AbsolutePath)) : "";
            if (_existing is null && _title.Text.Trim().Length == 0 && file.Length > 0) _title.Text = file;
            if (_existing is null && _fileStem.Length == 0) _fileStem = file;
            _linkState.Text = $"읽었습니다 · {code.Split('\n').Length}줄 · {extension} · 깃허브 파일이 바뀌면 새 버전이 됩니다.";
            _error.Text = "";
            return true;
        }
        catch (SkillStoreException exception)
        {
            if (_link.Text.Trim() == link) _linkState.Text = exception.Message;
            return false;
        }
    }

    /// <summary>A .ts or .js file's code; its name gives the store name and, until one is typed, the title.</summary>
    internal void LoadFile(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".ts" or ".js")) { _error.Text = "플러그인은 .ts 또는 .js 파일입니다."; return; }
        try
        {
            if (new FileInfo(path).Length > 300_000) { _error.Text = "파일이 너무 큽니다(300KB까지)."; return; }
            _code.Text = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { _error.Text = "파일을 읽지 못했습니다 · " + exception.Message; return; }
        _extension = extension;
        _fileStem = Path.GetFileNameWithoutExtension(path);
        if (_title.Text.Trim().Length == 0) _title.Text = _fileStem;
        ShowKind();
        _error.Text = "";
    }

    /// <summary>A new plugin's store name: the file's or the title's English words ("plugin" when there are none), then this PC's
    /// account; numbered when the store already has that name.</summary>
    internal static string NameFor(string fileStem, string title, string user, IEnumerable<string> taken)
    {
        string owner = ShareSkillDialog.OwnerName(user);
        string stem = new[] { ShareSkillDialog.SuggestName(fileStem), ShareSkillDialog.SuggestName(title) }.FirstOrDefault(candidate => candidate.Length > 0) ?? "plugin";
        if (stem.EndsWith("-" + owner, StringComparison.Ordinal)) stem = stem[..^(owner.Length + 1)];
        int room = 64 - owner.Length - 5;
        if (stem.Length > room) stem = stem[..room].TrimEnd('-');
        var used = new HashSet<string>(taken, StringComparer.Ordinal);
        string name = stem + "-" + owner;
        for (int repeat = 2; used.Contains(name); repeat++) name = $"{stem}-{owner}-{repeat}";
        return name;
    }

    internal async Task<bool> SubmitAsync()
    {
        if (_busy) return false;
        bool gitHub = _gitHubTab.IsChecked == true;
        if (gitHub && !await LoadLinkAsync())
        {
            _error.Text = _link.Text.Trim().Length == 0 ? "깃허브 링크를 넣으세요." : "깃허브 링크를 읽지 못했습니다.";
            return false;
        }
        string title = _title.Text.Trim(), description = _description.Text.Trim(), note = _note.Text.Trim();
        string code = gitHub ? _linkedCode.Text : _code.Text, extension = gitHub ? _linkedExtension : _extension;
        // Shared from a link, the plugin follows it; from code, it stops following one it had.
        string? source = gitHub ? _linkedFrom : _existing?.Source is { Length: > 0 } ? "" : null;
        string? problem = title.Length == 0 ? "제목을 쓰세요." : code.Trim().Length == 0 ? "코드를 붙여 넣거나 파일을 여세요." :
            code.Length > 300_000 ? "코드가 너무 깁니다(30만 자까지)." : null;
        if (problem is not null) { _error.Text = problem; return false; }
        _busy = true;
        _submit.IsEnabled = false;
        _error.Text = "올리는 중…";
        try
        {
            if (_existing is { } plugin)
            {
                int published = await _store.PublishPluginAsync(plugin.Name, title, description, extension, code, _user, note, edit: true, CancellationToken.None, source);
                if (published <= plugin.Version)
                {
                    // Only the link changed (started or stopped following): saved, no new version.
                    if (source is not null && source != (plugin.Source ?? "")) { SharedVersion = plugin.Version; SharedTitle = StoreSkill.Shown(title, plugin.Author); _error.Text = ""; return true; }
                    _error.Text = $"코드가 지금 버전(v{plugin.Version})과 같아 새 버전을 만들지 않았습니다.";
                    return false;
                }
                SharedVersion = published;
                SharedTitle = StoreSkill.Shown(title, plugin.Author);
            }
            else
            {
                var taken = _taken.ToList();
                for (int attempt = 1; ; attempt++)
                {
                    string name = NameFor(_fileStem, title, _user, taken);
                    try
                    {
                        SharedVersion = await _store.PublishPluginAsync(name, title, description, extension, code, _user, "", edit: false, CancellationToken.None, source);
                        break;
                    }
                    // Another PC took the name a moment ago: the next number.
                    catch (SkillStoreException exception) when (exception.Status == HttpStatusCode.Conflict && attempt < 3) { taken.Add(name); }
                }
                SharedTitle = StoreSkill.Shown(title, _user);
            }
            _error.Text = "";
            return true;
        }
        catch (SkillStoreException exception) { _error.Text = exception.Message; return false; }
        finally { _busy = false; _submit.IsEnabled = true; }
    }
}
