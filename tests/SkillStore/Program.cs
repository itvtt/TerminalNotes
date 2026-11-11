using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using TerminalNotes;
using Documents = System.Windows.Documents;
using ConfigFile = TerminalNotes.Services.OpenCodeConfigFile;

// The skill store end to end: the real relay (relay\relay.py) on a loopback port with its own .env and folders, the
// app's store client, and the store window's one-click apply/remove, versions and my own instructions in an isolated
// skills folder. 사용자 지시: 버전별 이력, 최신만 표시, 버전을 골라 적용, 기본 + 내 지침, 새 버전을 적용해도 내가 적은 것은 유지.
internal static class Program
{
    private static int _exitCode;

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                await RunAsync();
                Console.WriteLine("PASS skill store: one switch per skill with the rest in its ⋯ menu, versions (only the latest listed, each compared with the one before, any version applied), my additions kept across new versions (in one box; tabs only for a hand-edited skill), " +
                    "cards with the date and author, search, categories, the star first, 나만 보기 for the author only, deleting by its author or a store admin (test_07, who also adds versions to anyone's), plugins (a master shares, install/update/remove into the plugins folder, the user's own file kept), " +
                    "GitHub links followed by the relay and taken by the app, a task's own skills in its folder, " +
                    "version changes, removal and re-apply, hand edits taken over (below the marker as additions, elsewhere as my own text), " +
                    "my own full text with a backup when going back, key/name/author guards, the user's own same-name skill untouched, " +
                    "sharing from a dropped file with only the title typed (store name and shown title carry the account, SKILL.md frontmatter used), " +
                    "or from text in the 텍스트 tab (its heading gives the title, the chosen tab is what is shared), " +
                    "new versions from a file or from text that starts as the latest version by the one who shared it at the next or a larger chosen number, " +
                    "admin listing/restore/removal; loopback Python relay and temporary folders only.");
            }
            catch (Exception exception) { _exitCode = 1; Console.Error.WriteLine("FAIL: " + exception); }
            finally { app.Shutdown(_exitCode); }
        };
        app.Run();
        return _exitCode;
    }

    private const string Name = "impala-query";

    private static async Task RunAsync()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes skill store " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        string skills = Path.Combine(fixture, "opencode skills");
        AppContext.SetData("TerminalNotes.SkillsDirectory", skills);
        string plugins = Path.Combine(fixture, "opencode plugins");
        AppContext.SetData("TerminalNotes.PluginsDirectory", plugins);
        string favorites = Path.Combine(fixture, "skill-favorites.json");
        AppContext.SetData("TerminalNotes.SkillFavoritesPath", favorites);
        // The OpenCode config a task gets (its instruction files) stays in the fixture too.
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(fixture, "data"));
        // This PC's account is not a store admin here, whatever .env made the admins (the admin part below picks one).
        SkillStoreAdmins.AccountOverride = () => "DOMAIN\\not-a-store-admin";
        await CheckOpenCodeConfigAsync(fixture);
        int port = FreePort();
        const string key = "skill-store-test-key", admin = "skill-store-admin";
        // The relay reads no .env: this run's settings go in as --set.
        string[] settings = [$"RELAY_PORT={port}", $"RELAY_INGEST_KEY={key}", $"RELAY_ADMIN_PASSWORD={admin}", "RELAY_GITHUB_SYNC_SECONDS=2",
            $"RELAY_DATA_DIR={Path.Combine(fixture, "reports")}", $"RELAY_SKILLS_DIR={Path.Combine(fixture, "store")}", $"RELAY_PLUGINS_DIR={Path.Combine(fixture, "plugin-store")}",
            $"RELAY_ANNOUNCEMENTS_DIR={Path.Combine(fixture, "announcements")}", $"RELAY_LOG_FILE={Path.Combine(fixture, "relay.log")}"];
        var start = new ProcessStartInfo("python") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = fixture };
        foreach (string argument in new[] { "-X", "utf8", FindRelay() }.Concat(settings.SelectMany(setting => new[] { "--set", setting }))
            .Concat(["--bind", "127.0.0.1", "--quiet"])) start.ArgumentList.Add(argument);
        using var server = Process.Start(start)!;
        var relayOutput = new StringBuilder();
        server.OutputDataReceived += (_, e) => { lock (relayOutput) relayOutput.AppendLine(e.Data); };
        server.ErrorDataReceived += (_, e) => { lock (relayOutput) relayOutput.AppendLine(e.Data); };
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        SkillStoreDialog? dialog = null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            string relay = $"http://127.0.0.1:{port}";
            await Until(async () =>
            {
                if (server.HasExited) throw new InvalidOperationException("The relay exited: " + relayOutput);
                try { return await http.GetStringAsync(relay + "/health") == "ok"; }
                catch (HttpRequestException) { return false; }
            }, "the relay to start");
            var store = new SkillStoreClient(new Uri(relay), key, http);
            string SkillFile() => File.ReadAllText(Path.Combine(skills, Name, "SKILL.md"));
            InstalledSkills.Entry Local() => InstalledSkills.List()[Name];

            // Nothing shared yet: the window says so, and sharing is its one button; a build without a store address says that instead.
            var first = new SkillStoreDialog(store);
            Open(first);
            await Until(() => first.ShowsEmpty, "the empty store");
            Require(first.EmptyText == "아직 공유된 스킬이 없습니다." && Buttons(first).Count(button => button.Content as string == "+ 스킬 공유" && button.IsVisible) == 1,
                "The empty store did not offer sharing once: " + first.EmptyText);
            await SaveImage(first, "store-empty.png");
            first.Close();
            var offline = new SkillStoreDialog(null);
            Open(offline);
            await Until(() => offline.ShowsEmpty, "the store without an address");
            Require(offline.EmptyText.Contains("RELAY_HOST", StringComparison.Ordinal) && !Buttons(offline).Any(button => button.Content as string == "+ 스킬 공유" && button.IsVisible),
                "The store without an address did not say so, or offered sharing: " + offline.EmptyText);
            offline.Close();

            // Share v1, then list and download: the relay composes SKILL.md with OpenCode's frontmatter.
            Require(await store.PublishAsync(Name, "Impala로 데이터 조회", "데이터레이크를 조회할 때: \"Impala\"", "- Impala로 조회한다.", "alice", default) == 1,
                "The first share was not version 1.");
            var listed = await store.ListAsync(default);
            Require(listed.Count == 1 && listed[0] is { Name: Name, Title: "Impala로 데이터 조회", Author: "alice", Version: 1, VersionCount: 1 },
                "The shared skill was not listed at version 1.");
            var (downloaded, v1) = await store.GetAsync(Name, default);
            Require(v1.StartsWith("---\nname: impala-query\ndescription: \"데이터레이크를 조회할 때: \\\"Impala\\\"\"\n---\n\n# Impala로 데이터 조회\n\n- Impala로 조회한다.", StringComparison.Ordinal) &&
                downloaded.Sha256 == listed[0].Sha256, "The downloaded SKILL.md was not composed as expected: " + v1);

            // Guards: another author cannot replace it, a wrong key and a bad name are refused.
            await Throws(() => store.PublishAsync(Name, "덮어쓰기", "다른 사람", "x", "bob", default), HttpStatusCode.Conflict);
            await Throws(() => new SkillStoreClient(new Uri(relay), "wrong", http).ListAsync(default), HttpStatusCode.Unauthorized);
            await Throws(() => store.PublishAsync("Bad Name", "제목", "설명", "내용", "alice", default), HttpStatusCode.BadRequest);
            // A gateway in between that could not reach the relay (502) is named as such, with the relay's address.
            using (var gateway = new HttpClient(new StatusHandler(HttpStatusCode.BadGateway)))
            {
                string message = "";
                try { await new SkillStoreClient(new Uri("http://10.1.2.3:5080"), key, gateway).ListAsync(default); }
                catch (SkillStoreException exception) { message = exception.Message; }
                Require(message.Contains("10.1.2.3:5080", StringComparison.Ordinal) && message.Contains("502", StringComparison.Ordinal),
                    "A 502 from a gateway did not say the relay was not reached: " + message);
            }

            // The store window: one click on a skill's switch applies the latest version; the rest is in its ⋯ menu.
            dialog = new SkillStoreDialog(store);
            Open(dialog);
            await Until(() => Card(dialog, Name) is not null, "the store list");
            ToggleButton Switch(string name) => SwitchIn(dialog!, name);
            Button? Labeled(string name, string prefix) => Buttons(Card(dialog!, name)!).FirstOrDefault(button => (button.Content as string ?? "").StartsWith(prefix, StringComparison.Ordinal));
            List<string> MenuOf(string name) => Card(dialog!, name)!.ContextMenu.Items.OfType<MenuItem>().Select(item => (string)item.Header).ToList();
            Require(Switch(Name).IsChecked == false && MenuOf(Name).SequenceEqual(["내용 보기"]),
                "An unapplied skill was shown on, or its menu offered more than its content: " + string.Join(", ", MenuOf(Name)));
            Switch(Name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => InstalledSkills.List().TryGetValue(Name, out var entry) && entry is { FromStore: true, Applied: true, Version: 1 }, "the click to apply v1");
            Require(SkillFile() == v1.ReplaceLineEndings("\n"), "The applied SKILL.md differs from the store's v1.");
            string state = Path.Combine(skills, ".terminal-notes");
            Require(Directory.Exists(state) && Directory.GetFiles(state, "SKILL.md", SearchOption.AllDirectories).Length == 0,
                "The kept layers contain a SKILL.md, which OpenCode would load as another skill.");

            // My own additions go below the store version.
            const string mine = "- 결과는 CSV로도 저장한다.";
            InstalledSkills.SaveCustom(Name, mine);
            Require(SkillFile() == InstalledSkills.Compose(v1, mine) && SkillFile().EndsWith(mine + "\n", StringComparison.Ordinal) &&
                SkillFile().StartsWith(v1.ReplaceLineEndings("\n").TrimEnd('\n'), StringComparison.Ordinal), "My additions were not appended to the store version.");

            // The author keeps editing: v2 is listed alone, and taking it keeps my additions.
            Require(await store.PublishAsync(Name, "Impala로 데이터 조회", "데이터레이크를 조회할 때", "- Impala로만 조회한다.", "alice", default, note: "Hive 금지") == 2,
                "The author's change was not version 2.");
            listed = await store.ListAsync(default);
            Require(listed.Count == 1 && listed[0] is { Version: 2, VersionCount: 2, Note: "Hive 금지" }, "The store did not show only the latest version.");
            await dialog.RefreshAsync();
            var update = Labeled(Name, "업데이트");
            Require(update is not null && Switch(Name).IsChecked == true && MenuOf(Name).SequenceEqual(["내용 보기", "버전 기록", "내 지침"]),
                "An applied skill with a newer store version did not offer it, or its menu: " + string.Join(", ", MenuOf(Name)));
            // The card's badge shows the latest version; the one this PC has is in its tooltip and 업데이트's.
            var badge = VersionBadgeIn(dialog, Name);
            Require(badge is { Child: TextBlock { Text: "v2" } } && badge.ToolTip as string == "최신 v2 · 이 PC는 v1 적용 중" && update!.ToolTip as string == "v1 → v2",
                "The card did not show v2 with the applied v1: " + (badge?.Child as TextBlock)?.Text + " / " + badge?.ToolTip);
            await SaveImage(dialog, "store-update.png");
            update!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => SkillFile().Contains("Impala로만", StringComparison.Ordinal), "the new version");
            Require(SkillFile().EndsWith(mine + "\n", StringComparison.Ordinal) && Local() is { Version: 2, Mode: SkillMode.Store },
                "Applying the next version lost my additions.");
            await SaveImage(dialog, "store-applied-with-mine.png");
            var more = Buttons(Card(dialog, Name)!).Single(button => System.Windows.Automation.AutomationProperties.GetName(button).StartsWith("더 보기", StringComparison.Ordinal));
            var menu = Card(dialog, Name)!.ContextMenu;
            StoreLook.Open(menu, more);
            await SaveImage(menu, "store-menu.png");
            // ⋯ opens it right under the button, the right edges lined up.
            var corner = more.PointToScreen(new Point(more.ActualWidth, more.ActualHeight));
            Require(GetWindowRect(((System.Windows.Interop.HwndSource)PresentationSource.FromVisual(menu)).Handle, out var placed) &&
                Math.Abs(placed.Right - corner.X) <= 2 && placed.Top - corner.Y is >= 0 and <= 12,
                $"The menu did not open under ⋯: {placed.Left},{placed.Top}-{placed.Right} against {corner.X:0},{corner.Y:0}");
            menu.IsOpen = false;

            // Any version can be chosen; my additions stay on it too.
            var versions = await store.VersionsAsync(Name, default);
            Require(versions.Select(version => version.Version).SequenceEqual([2, 1]) && versions[0].Note == "Hive 금지", "The version history was not newest first.");

            // Each version but the first compares with the one before it: the removed and the added lines.
            var history = new SkillVersionsDialog(store, listed[0], versions, 2);
            Require(Buttons(history).Count(button => button.Content as string == "비교") == versions.Count - 1, "Every version but the first did not offer 비교.");
            Open(history);
            await SaveImage(history, "store-versions.png");
            history.Close();
            // Compared as Visual Studio Code does: side by side, each changed line facing its new form with only the changed
            // characters marked; ↑ ↓ go round the changes; narrower than 900 it is inline.
            var (_, v2) = await store.GetAsync(Name, default);
            var diff = new SkillDiffWindow("SKILL.md (v1) ↔ SKILL.md (v2)", v1, v2, "Impala로 데이터 조회-alice") { Owner = dialog, Width = 1100 };
            Open(diff);
            string Marked(SkillDiff.Side? side) => side is null ? "" : string.Join("|", side.Marks.Select(mark => side.Text.Substring(mark.Start, mark.Length)));
            var facing = diff.Rows.Where(row => row.Changed).ToList();
            Require(diff is { AddedCount: 2, RemovedCount: 2, SideBySide: true, ChangeCount: 2 } && facing.Count == 2 &&
                facing.All(row => row is { Before.Whole: false, After.Whole: false }) && Marked(facing[0].After) == "" &&
                Marked(facing[0].Before) == ": \\\"Impala\\\"" && Marked(facing[1].Before) == "" && Marked(facing[1].After) == "만" &&
                facing[1].Before!.Number == facing[1].After!.Number,
                "The v1 → v2 comparison did not face the changed lines with only their changed characters: " +
                string.Join(" / ", facing.Select(row => $"{row.Before?.Text}[{Marked(row.Before)}] → {row.After?.Text}[{Marked(row.After)}]")));
            await SaveImage(diff, "store-version-diff.png");
            var visited = new List<int?>();
            foreach (int direction in new[] { 1, 1, 1, -1 })
            {
                diff.GoToChange(direction);
                visited.Add(diff.CurrentChange);
            }
            Require(visited.SequenceEqual(new int?[] { 0, 1, 0, 1 }), "↑ ↓ did not go round the changes: " + string.Join(",", visited));
            diff.Width = 820;
            await Until(() => !diff.SideBySide, "the inline comparison in a narrow window");
            await SaveImage(diff, "store-version-diff-inline.png");
            diff.Close();
            Require(SkillDiff.Compare("a\nb\nc", "a\nB\nc\nd").Select(line => $"{line.Kind}:{line.Text}")
                .SequenceEqual(["Same:a", "Removed:b", "Added:B", "Same:c", "Added:d"]), "The line comparison changed.");
            // A line added above a changed one: the changed line still faces its new form, and the added one faces hatching.
            var aligned = SkillDiff.Align("a\nhello world\nc", "a\nnew line\nhello world!\nc");
            Require(aligned.Select(row => $"{row.Before?.Number}:{row.Before?.Text}|{row.After?.Number}:{row.After?.Text}|{(!row.Changed ? "same" : row.After?.Whole == true ? "whole" : "paired")}")
                .SequenceEqual(["1:a|1:a|same", ":|2:new line|whole", "2:hello world|3:hello world!|paired", "3:c|4:c|same"]),
                "The side-by-side alignment: " + string.Join(" / ", aligned.Select(row => $"{row.Before?.Text}→{row.After?.Text}")));
            // Lines only one side has face hatching; the similar ones still face each other.
            var padded = new SkillDiffWindow("SKILL.md (v2) ↔ SKILL.md (v3)", "# 조회\n\n- Impala로 조회한다.\n- Hive는 쓰지 않는다.\n- 결과는 표로 정리한다.\n",
                "# 조회\n\n- 접속 정보는 사용자가 준 값만 쓴다.\n- 비밀번호는 남기지 않는다.\n- Impala로만 조회한다.\n- 결과는 표로 정리하고 검증한다.\n") { Owner = dialog, Width = 1100 };
            Open(padded);
            Require(padded.Rows.Count(row => row.Before is null) == 2 && padded.Rows.Count(row => row.After is null) == 1 &&
                padded.Rows.Count(row => row is { Changed: true, Before.Whole: false }) == 2, "Lines only one side has did not face hatching.");
            await SaveImage(padded, "store-version-diff-hatch.png");
            padded.Close();
            // A long version opens at its first change, quickly.
            string longer = string.Join("\n", Enumerable.Range(1, 1500).Select(line => $"- 지침 {line}: Impala로 조회하고 결과를 표로 정리한다."));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var big = new SkillDiffWindow("SKILL.md (v1) ↔ SKILL.md (v2)", longer,
                longer.Replace("지침 700:", "지침 700 (수정):", StringComparison.Ordinal).Replace("지침 1400:", "규칙 1400:", StringComparison.Ordinal)) { Owner = dialog, Width = 1100 };
            Open(big);
            await Until(() => big.CurrentChange == 0, "the long comparison at its first change");
            Console.WriteLine($"TIMING 1500-line comparison shown in {watch.ElapsedMilliseconds} ms");
            Require(big.ChangeCount == 2, $"A long comparison found {big.ChangeCount} changes.");
            await SaveImage(big, "store-version-diff-long.png");
            big.Close();
            await dialog.ApplyAsync(listed[0], 1);
            Require(SkillFile() == InstalledSkills.Compose(v1, mine) && Local().Version == 1, "Choosing v1 did not apply v1 with my additions.");

            // A hand edit below the marker becomes my additions.
            File.AppendAllText(Path.Combine(skills, Name, "SKILL.md"), "- 표 머리글은 한국어로 쓴다.\n");
            await dialog.RefreshAsync();
            Require(InstalledSkills.ReadCustom(Name) == mine + "\n- 표 머리글은 한국어로 쓴다." && Local().Mode == SkillMode.Store,
                "A hand edit of my additions was not taken over: " + InstalledSkills.ReadCustom(Name));
            string allMine = InstalledSkills.ReadCustom(Name);

            // Removing keeps my additions; applying again brings them back on the latest version.
            Switch(Name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => !Directory.Exists(Path.Combine(skills, Name)), "the click to remove the skill");
            Require(InstalledSkills.ReadCustom(Name) == allMine && InstalledSkills.List()[Name] is { Applied: false, CustomLines: 2 } &&
                Switch(Name).IsChecked == false, "Removing the skill lost my additions or did not show it off.");
            Switch(Name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => File.Exists(Path.Combine(skills, Name, "SKILL.md")) && SkillFile().Contains("Impala로만", StringComparison.Ordinal), "applying again");
            Require(SkillFile().EndsWith(allMine + "\n", StringComparison.Ordinal), "Applying again did not bring my additions back.");

            // Editing the store part by hand makes it mine: no new version replaces it without asking, nothing is lost.
            File.WriteAllText(Path.Combine(skills, Name, "SKILL.md"), SkillFile().Replace("Impala로만", "Impala 또는 Trino로", StringComparison.Ordinal));
            string handEdited = SkillFile();
            Require(await store.PublishAsync(Name, "Impala로 데이터 조회", "데이터레이크를 조회할 때", "- Impala로만 조회하고 결과를 검증한다.", "alice", default, note: "검증") == 3,
                "The third share was not version 3.");
            await dialog.RefreshAsync();
            Require(Local() is { Mode: SkillMode.Local, Notice.Length: > 0 } && InstalledSkills.ReadLocal(Name) == handEdited &&
                Labeled(Name, "업데이트") is null, "A hand-edited store part was not taken over as my own text, or a new version was still offered.");
            int asked = 0;
            dialog.Confirm = _ => ++asked > 1;
            await dialog.ApplyAsync((await store.ListAsync(default))[0], 3);
            Require(asked == 1 && SkillFile() == handEdited, "Applying over my own text did not ask first, or went on after a refusal.");
            await dialog.ApplyAsync((await store.ListAsync(default))[0], 3);
            Require(asked == 2 && SkillFile().Contains("결과를 검증한다", StringComparison.Ordinal) && SkillFile().EndsWith(allMine + "\n", StringComparison.Ordinal) &&
                InstalledSkills.ReadLocal(Name) == handEdited && Directory.GetFiles(InstalledSkills.BackupDirectory(Name), "SKILL-*.md").Length == 1,
                "Going back to the store version lost my additions, my own text or its backup.");

            // My own full text: kept with its frontmatter; switching back keeps it for later.
            Expect<InvalidDataException>(() => InstalledSkills.SaveLocal(Name, "# 머리말 없음"), "Own text without frontmatter was accepted.");
            string own = "---\nname: impala-query\ndescription: 내 방식\n---\n\n# 내 방식\n\n- Trino로 조회한다.\n";
            InstalledSkills.SaveLocal(Name, own);
            Require(SkillFile() == own && Local().Mode == SkillMode.Local, "My own full text was not applied.");
            // Only a skill on my own text gets the tabs: that text, or back to the store version.
            var ownWindow = new SkillCustomizeDialog(Name, "Impala로 데이터 조회") { Owner = dialog };
            Open(ownWindow);
            Require(ownWindow is { ShowsTabs: true, LocalMode: true } && Descendants(ownWindow).OfType<RadioButton>().Count() == 2,
                "A skill on my own text did not open on it with the tabs.");
            await SaveImage(ownWindow, "store-customize-own.png");
            ownWindow.Close();
            InstalledSkills.UseStoreVersion(Name);
            Require(SkillFile().Contains("결과를 검증한다", StringComparison.Ordinal) && SkillFile().EndsWith(allMine + "\n", StringComparison.Ordinal) &&
                InstalledSkills.ReadLocal(Name) == own && Local().Mode == SkillMode.Store, "Going back to the store version lost something.");

            // The customize window edits the same layers.
            var customize = new SkillCustomizeDialog(Name, "Impala로 데이터 조회") { Owner = dialog };
            Open(customize);
            Require(customize.CustomText == allMine && customize is { LocalMode: false, ShowsTabs: false } && !Descendants(customize).OfType<RadioButton>().Any(),
                "The customize window did not show just my additions in one box.");
            await SaveImage(customize, "store-customize.png");
            customize.CustomText = allMine + "\n- 날짜는 yyyy-MM-dd로 쓴다.";
            Require(customize.SaveChanges(), "Saving my additions from the window failed.");
            Require(SkillFile().EndsWith("- 날짜는 yyyy-MM-dd로 쓴다.\n", StringComparison.Ordinal), "The window did not save my additions.");
            customize.Close();

            // Sharing from a file: only the title is typed; the store name and the shown title carry this PC's account.
            string user = Environment.UserName, owner = ShareSkillDialog.OwnerName(user);
            string drop = Path.Combine(fixture, "drop");
            Directory.CreateDirectory(drop);
            string guide = Path.Combine(drop, "trino-guide.md");
            File.WriteAllText(guide, "# Trino 가이드\n\n- Trino로 조회한다.\n");
            var share = new ShareSkillDialog(store) { Owner = dialog };
            Open(share);
            share.LoadDropped([guide, guide]);
            Require(share.ErrorText.Contains("하나만", StringComparison.Ordinal) && share.FileLabel is null, "Two dropped files were accepted.");
            share.LoadDropped([guide]);
            Require(share.FileLabel == "trino-guide.md" && share.TitleText == "Trino 가이드", "The dropped file did not give the title: " + share.TitleText);
            await SaveImage(share, "store-share-file.png");
            Require(await share.SubmitAsync(), "Sharing from a file failed: " + share.ErrorText);
            var trino = (await store.ListAsync(default)).Single(skill => skill.Title == "Trino 가이드");
            Require(trino is { Version: 1, Description: "Trino 가이드" } && trino.Name == "trino-guide-" + owner && trino.Author == user &&
                trino.DisplayTitle == "Trino 가이드-" + user && share.SharedTitle == trino.DisplayTitle, "The shared skill's name, author or description: " + trino);
            var (_, trinoText) = await store.GetAsync(trino.Name, default);
            Require(trinoText.Contains("# Trino 가이드\n\n- Trino로 조회한다.", StringComparison.Ordinal) && trinoText.Split("# Trino 가이드").Length == 2,
                "The file's own title line was not dropped, or the instructions are missing: " + trinoText);
            share.Close();
            await dialog.RefreshAsync();
            // The card: the title, the first line of the instructions (the description only repeats the title), and at the bottom
            // left the date and the author, and the version's badge right before the star.
            var trinoCard = Descendants(Card(dialog, trino.Name)!).OfType<TextBlock>().ToList();
            string byline = trino.ShownDate!.Value.ToLocalTime().ToString("yyyy-MM-dd") + " · " + user;
            Require(trinoCard[0].Text == "Trino 가이드" && trinoCard.Count(line => line.Text == "Trino 가이드") == 1 &&
                trinoCard.Any(line => line.Text == "Trino로 조회한다.") && trinoCard.Any(line => line.Text == byline),
                "The card's title, description or date · author: " + string.Join(" | ", trinoCard.Select(line => line.Text)));
            var trinoBadge = VersionBadgeIn(dialog, trino.Name);
            var trinoStar = Descendants(Card(dialog, trino.Name)!).OfType<ToggleButton>()
                .Single(button => System.Windows.Automation.AutomationProperties.GetName(button).StartsWith("즐겨찾기", StringComparison.Ordinal));
            var card = Card(dialog, trino.Name)!;
            double badgeRight = trinoBadge?.TranslatePoint(new Point(trinoBadge.ActualWidth, 0), card).X ?? double.NaN, starLeft = trinoStar.TranslatePoint(new Point(0, 0), card).X;
            Require(trinoBadge is { Child: TextBlock { Text: "v1" } } && badgeRight <= starLeft && starLeft - badgeRight < 16,
                $"The version badge is not right before the star: {badgeRight} / {starLeft}");
            await SaveImage(dialog, "store-cards.png");
            // × under the pointer, without moving the user's pointer: capturing the mouse makes the button the one it is over.
            var close = Buttons(dialog).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "닫기");
            if (System.Windows.Input.Mouse.Capture(close))
            {
                await Task.Delay(300);
                Require(close.IsMouseOver && close.Template.FindName("Chrome", close) is Border { Width: 32, Height: 32 } chip &&
                    chip.CornerRadius.TopLeft == 6, "× did not show its hover as a rounded square inside the title bar.");
                await SaveImage(dialog, "store-close-hover.png");
                System.Windows.Input.Mouse.Capture(null);
            }

            // Or written in the 텍스트 tab: its '# heading' gives the title, a dropped file brings the 파일 tab back, and the chosen
            // tab is what is shared.
            var typed = new ShareSkillDialog(store) { Owner = dialog };
            Open(typed);
            typed.TextMode = true;
            typed.TitleText = "스파크";
            Require(!await typed.SubmitAsync() && typed.ErrorText == "지침을 쓰거나 붙여 넣으세요.", "Empty text was accepted: " + typed.ErrorText);
            typed.TitleText = "";
            typed.SkillText = "# Spark 사용 안내\n\n- Spark 대신 Impala를 쓴다.\n";
            Require(typed.TitleText == "Spark 사용 안내", "The text's heading did not give the title: " + typed.TitleText);
            typed.LoadDropped([guide]);
            Require(!typed.TextMode && typed.FileLabel == "trino-guide.md" && typed.TitleText == "Trino 가이드", "A file dropped on the text tab was not shown in the file tab.");
            typed.TextMode = true;
            Require(typed.TitleText == "Spark 사용 안내" && typed.SkillText.StartsWith("# Spark", StringComparison.Ordinal), "Switching tabs lost the text or its title.");
            await SaveImage(typed, "store-share-text.png");
            Require(await typed.SubmitAsync(), "Sharing typed text failed: " + typed.ErrorText);
            typed.Close();
            var spark = (await store.ListAsync(default)).Single(skill => skill.Title == "Spark 사용 안내");
            var (_, sparkText) = await store.GetAsync(spark.Name, default);
            Require(spark.Name == "spark-" + owner && spark.Description == "Spark 사용 안내" &&
                sparkText.Contains("# Spark 사용 안내\n\n- Spark 대신 Impala를 쓴다.", StringComparison.Ordinal) && sparkText.Split("# Spark 사용 안내").Length == 2 &&
                !sparkText.Contains("Trino", StringComparison.Ordinal), "The typed skill was not shared as written: " + spark + " " + sparkText);

            // The same title again is refused (a new version goes through 수정); a SKILL.md folder's frontmatter gives the name and description.
            var again = new ShareSkillDialog(store);
            again.LoadFile(guide);
            Require(!await again.SubmitAsync() && again.ErrorText.Contains("이미 공유", StringComparison.Ordinal), "The same title was shared twice: " + again.ErrorText);
            string skillFolder = Path.Combine(drop, "impala-query");
            Directory.CreateDirectory(skillFolder);
            File.WriteAllText(Path.Combine(skillFolder, "SKILL.md"), "---\nname: impala-query\ndescription: \"데이터레이크를 조회할 때\"\n---\n\n# Impala 내 방식\n\n- Impala로 조회한다.\n");
            again.LoadFile(skillFolder);
            Require(again.TitleText == "Impala 내 방식" && again.FileLabel == Path.Combine("impala-query", "SKILL.md"), "A SKILL.md folder did not give its title.");
            Require(await again.SubmitAsync(), "Sharing a SKILL.md folder failed: " + again.ErrorText);
            var mineImpala = (await store.ListAsync(default)).Single(skill => skill.Title == "Impala 내 방식");
            Require(mineImpala is { Description: "데이터레이크를 조회할 때" } && mineImpala.Name == "impala-query-" + owner, "The SKILL.md frontmatter was not used: " + mineImpala);
            again.Close();

            // Files: Korean ANSI text is read, binaries and folders without SKILL.md are refused, and names stay unique.
            string ansi = Path.Combine(drop, "메모.txt");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            File.WriteAllBytes(ansi, Encoding.GetEncoding(949).GetBytes("- 한국어 ANSI 지침\r\n"));
            Require(SkillSourceFile.Read(ansi).Body == "- 한국어 ANSI 지침", "A Korean ANSI text file was not read.");
            File.WriteAllBytes(Path.Combine(drop, "image.png"), [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]);
            Expect<InvalidDataException>(() => SkillSourceFile.Read(Path.Combine(drop, "image.png")), "A binary file was accepted.");
            Expect<InvalidDataException>(() => SkillSourceFile.Read(drop), "A folder without SKILL.md was accepted.");
            Require(ShareSkillDialog.NameFor(SkillSourceFile.Read(ansi), "데이터 메모", "test_07", ["skill-test-07"]) == "skill-test-07-2" &&
                ShareSkillDialog.OwnerName("김철수") is { Length: 7 } hashed && hashed.StartsWith('u'), "The store name rules changed.");

            // Only the one who shared it sees 수정 and 삭제 (store admins aside); 수정 opens the file window at the next version, and
            // a larger number can be written.
            Require(MenuOf(trino.Name).SequenceEqual(["내용 보기", "수정", "비공개로 바꾸기", "삭제"]) && !MenuOf(Name).Contains("수정") && !MenuOf(Name).Contains("삭제") &&
                    !MenuOf(Name).Contains("비공개로 바꾸기"),
                "수정 and 삭제 are not limited to the one who shared the skill: " + string.Join(", ", MenuOf(trino.Name)));
            var upload = new ShareSkillDialog(store, trino) { Owner = dialog };
            Open(upload);
            Require(upload.NameLocked && upload.VersionText == "2" && upload.TitleText == "Trino 가이드", "The version window did not start at v2.");
            File.WriteAllText(guide, "# Trino 가이드\n\n- Trino로만 조회한다.\n");
            upload.LoadFile(guide);
            upload.NoteText = "Trino로만";
            await SaveImage(upload, "store-new-version.png");
            Require(await upload.SubmitAsync() && upload.SharedVersion == 2, "Uploading v2 from a file failed: " + upload.ErrorText);
            upload.Close();
            var raised = new ShareSkillDialog(store, (await store.ListAsync(default)).Single(skill => skill.Name == trino.Name));
            File.WriteAllText(guide, "# Trino 가이드\n\n- Trino로만 조회하고 검증한다.\n");
            raised.LoadFile(guide);
            raised.VersionText = "2";
            Require(!await raised.SubmitAsync() && raised.ErrorText.Contains("v2)보다 큰", StringComparison.Ordinal), "A version not above the latest was accepted: " + raised.ErrorText);
            raised.VersionText = "5";
            Require(await raised.SubmitAsync() && raised.SharedVersion == 5, "The author's own version number was not used: " + raised.ErrorText);
            var same = new ShareSkillDialog(store, (await store.ListAsync(default)).Single(skill => skill.Name == trino.Name));
            Require(same.VersionText == "6", "The next number after v5 was not 6.");
            same.LoadFile(guide);
            Require(!await same.SubmitAsync() && same.ErrorText.Contains("같아", StringComparison.Ordinal), "An unchanged file made a new version: " + same.ErrorText);
            Require((await store.VersionsAsync(trino.Name, default)).Select(version => version.Version).SequenceEqual([5, 2, 1]), "The kept versions are not 5, 2, 1.");

            // A new version can be written in the 텍스트 tab, which starts from the latest version's instructions.
            var edited = new ShareSkillDialog(store, (await store.ListAsync(default)).Single(skill => skill.Name == trino.Name)) { Owner = dialog };
            Open(edited);
            edited.TextMode = true;
            await Until(() => edited.SkillText.Length > 0, "the latest version's text");
            Require(edited.SkillText == "- Trino로만 조회하고 검증한다." && edited.TitleText == "Trino 가이드", "The text tab did not start from the latest version: " + edited.SkillText);
            Require(!await edited.SubmitAsync() && edited.ErrorText.Contains("같아", StringComparison.Ordinal), "Unchanged text made a new version: " + edited.ErrorText);
            edited.SkillText += "\n- 결과 행 수도 보고한다.";
            edited.NoteText = "행 수 보고";
            await SaveImage(edited, "store-new-version-text.png");
            Require(await edited.SubmitAsync() && edited.SharedVersion == 6, "A new version from the text tab failed: " + edited.ErrorText);
            edited.Close();
            var (_, v6) = await store.GetAsync(trino.Name, default);
            Require(v6.Contains("# Trino 가이드\n\n- Trino로만 조회하고 검증한다.\n- 결과 행 수도 보고한다.", StringComparison.Ordinal) && v6.Split("# Trino 가이드").Length == 2 &&
                (await store.VersionsAsync(trino.Name, default)).Select(version => version.Version).SequenceEqual([6, 5, 2, 1]), "The typed new version: " + v6);

            // Sharing starts in the 텍스트 tab; a category (the store's or a new one) and 나만 보기 go with it.
            var categorized = new ShareSkillDialog(store, null, ["데이터"]) { Owner = dialog };
            Open(categorized);
            Require(categorized.SourceTab == ShareSkillDialog.From.Text, "Sharing did not start in the 텍스트 tab.");
            categorized.SkillText = "# 보고서 양식\n\n- 표 머리글은 한국어로 쓴다.\n";
            categorized.CategoryText = "보고서";
            categorized.OriginText = "  bob ";
            categorized.PrivateOnly = true;
            await SaveImage(categorized, "store-share-options.png");
            Require(await categorized.SubmitAsync(), "Sharing with a category and 나만 보기 failed: " + categorized.ErrorText);
            categorized.Close();
            var report = (await store.ListAsync(default)).Single(skill => skill.Title == "보고서 양식");
            var someoneElse = new SkillStoreClient(new Uri(relay), key, http, "someone-else");
            Require(report is { Private: true, Category: "보고서", Origin: "bob", OriginLink: null } && (await someoneElse.ListAsync(default)).All(skill => skill.Name != report.Name),
                "A private skill was listed to another account, or lost its category: " + report);
            await dialog.RefreshAsync();
            // Skills shared without a category are 기타, the last tab.
            Require(dialog.Categories.SequenceEqual(["전체", "사용 중", "보고서", "기타"]) &&
                Descendants(Card(dialog, report.Name)!).OfType<TextBlock>().Any(mark => mark.Text == StoreLook.LockGlyph),
                "The category tabs, or the lock of a private skill: " + string.Join(", ", dialog.Categories));
            // 출처: a user id is only shown, a link opens, and only http(s) ever does: anyone writes it and a click starts it.
            TextBlock OriginLine(string skill) => Descendants(Card(dialog, skill)!).OfType<TextBlock>().Single(line => line.Inlines.FirstInline is Run { Text: "출처  " });
            Require(new Documents.TextRange(OriginLine(report.Name).ContentStart, OriginLine(report.Name).ContentEnd).Text == "출처  bob" &&
                !OriginLine(report.Name).Inlines.OfType<Hyperlink>().Any(), "A user id as 출처 was not shown as plain text.");
            foreach (var (written, opens) in new (string, string?)[]
            {
                ("https://github.com/bob/skills", "https://github.com/bob/skills"), ("github.com/bob", "https://github.com/bob"), ("www.example.com", "https://www.example.com/"),
                ("bob (https://github.com/bob).", "https://github.com/bob"), ("HTTP://EXAMPLE.COM/x", "http://example.com/x"),
                ("bob", null), ("john.doe", null), ("김철수", null), ("file:///C:/Windows/notepad.exe", null), ("ms-settings:", null), ("javascript:alert(1)", null),
                ("\\\\server\\share", null), ("C:\\Windows\\notepad.exe", null), ("", null), (null!, null),
            })
                Require(StoreSkill.LinkIn(written)?.AbsoluteUri == opens, $"The 출처 ‘{written}’ gave the link ‘{StoreSkill.LinkIn(written)}’.");
            // Changing it alone saves without a new version; a new version keeps it.
            var credited = new ShareSkillDialog(store, report) { Owner = dialog };
            Open(credited);
            credited.TextMode = true;
            await Until(() => credited.SkillText.Length > 0, "the latest version's text");
            Require(credited.OriginText == "bob", "수정 did not start with the 출처: " + credited.OriginText);
            credited.OriginText = "https://github.com/bob/skills";
            await SaveImage(credited, "store-share-origin.png");
            Require(await credited.SubmitAsync() && credited.SharedVersion == report.Version, "Changing the 출처 alone made a new version: " + credited.SharedVersion + " " + credited.ErrorText);
            credited.Close();
            report = (await store.ListAsync(default)).Single(skill => skill.Name == report.Name);
            Require(report is { Origin: "https://github.com/bob/skills", Version: 1 } && report.OriginLink == new Uri("https://github.com/bob/skills"), "The 출처 was not saved: " + report);
            await dialog.RefreshAsync();
            Uri? clicked = null;
            dialog.OpenLink = link => clicked = link;
            var originLink = OriginLine(report.Name).Inlines.OfType<Hyperlink>().Single();
            originLink.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            Require(clicked == report.OriginLink && originLink.Inlines.FirstInline is Run { Text: "github.com/bob/skills" },
                "The 출처 link did not open, or still shows its scheme: " + clicked);
            Require(Card(dialog, report.Name)!.ActualHeight == Card(dialog, trino.Name)!.ActualHeight, "A 출처 changed the height of the cards.");
            await SaveImage(dialog, "store-cards-origin.png");
            dialog.SearchText = "github.com/bob";
            Require(Cards(dialog).SequenceEqual([report.Name]), "Searching did not find the skill by its 출처: " + string.Join(", ", Cards(dialog)));
            dialog.SearchText = "";
            var versioned = new ShareSkillDialog(store, report) { Owner = dialog };
            Open(versioned);
            versioned.TextMode = true;
            await Until(() => versioned.SkillText.Length > 0, "the latest version's text");
            versioned.SkillText += "\n- 합계 행을 넣는다.";
            Require(await versioned.SubmitAsync() && versioned.SharedVersion == 2, "A new version with a 출처 failed: " + versioned.ErrorText);
            versioned.Close();
            report = (await store.ListAsync(default)).Single(skill => skill.Name == report.Name);
            Require(report is { Origin: "https://github.com/bob/skills", Version: 2 }, "A new version lost the 출처: " + report);
            // Narrowing: a category tab, the search box; a starred skill comes first.
            dialog.Category = "보고서";
            Require(Cards(dialog).SequenceEqual([report.Name]), "The category did not narrow the cards: " + string.Join(", ", Cards(dialog)));
            dialog.Category = "기타";
            Require(Cards(dialog).Contains(trino.Name) && !Cards(dialog).Contains(report.Name), "기타 did not hold the skills without a category: " + string.Join(", ", Cards(dialog)));
            dialog.Category = "";
            dialog.SearchText = "trino";
            Require(Cards(dialog).SequenceEqual([trino.Name]), "Searching did not find the one skill: " + string.Join(", ", Cards(dialog)));
            dialog.SearchText = "";
            var star = Descendants(Card(dialog, trino.Name)!).OfType<ToggleButton>()
                .Single(toggle => System.Windows.Automation.AutomationProperties.GetName(toggle).StartsWith("즐겨찾기", StringComparison.Ordinal));
            star.IsChecked = true;
            star.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            // Parts by category; in a part the applied skills come first, then a starred one.
            var applied = InstalledSkills.List().Values.Where(entry => entry.Applied).Select(entry => entry.Name).ToHashSet();
            dialog.Category = "기타";
            var order = Cards(dialog);
            int appliedHere = order.Count(applied.Contains);
            Require(applied.Count > 0 && order.Take(appliedHere).All(applied.Contains) && order[appliedHere] == trino.Name &&
                File.ReadAllText(favorites).Contains(trino.Name, StringComparison.Ordinal),
                "Applied skills were not first in their part, or a starred skill did not come next: " + string.Join(", ", order));
            dialog.Category = SkillStoreDialog.AppliedCategory;
            Require(Cards(dialog).ToHashSet().SetEquals(applied), "사용 중 did not show exactly the applied skills: " + string.Join(", ", Cards(dialog)));
            dialog.Category = "";
            var headings = Descendants(dialog).OfType<TextBlock>()
                .Where(text => System.Windows.Automation.AutomationProperties.GetAutomationId(text) == "SkillPartHeading").Select(text => text.Text).ToList();
            Require(headings.Take(2).SequenceEqual(["보고서 · 1", $"기타 · {order.Count}"]),
                "The parts were not one per category with its count: " + string.Join(" | ", headings));
            await SaveImage(dialog, "store-cards.png");

            // Its author deletes it from the ⋯ menu.
            Require(MenuOf(spark.Name).Last() == "삭제", "Its author was not offered 삭제: " + string.Join(", ", MenuOf(spark.Name)));
            dialog.Confirm = _ => true;
            await dialog.DeleteAsync(spark);
            Require((await store.ListAsync(default)).All(skill => skill.Name != spark.Name) && Card(dialog, spark.Name) is null,
                "The author could not delete their skill: " + dialog.StatusText);
            // A store admin (a master account, MASTER_USERS) changes and deletes anyone's: the new version is theirs, the
            // skill stays its author's.
            string keeperAccount = SkillStoreAdmins.AllowedUsers[0];
            const string bobs = "bob-notes";
            Require(await store.PublishAsync(bobs, "밥의 메모", "메모", "- 메모는 짧게 쓴다.", "bob", default) == 1, "bob's skill was not shared.");
            await dialog.RefreshAsync();
            Require(!MenuOf(bobs).Contains("수정") && !MenuOf(bobs).Contains("삭제"), "Someone else's skill offered 수정 or 삭제: " + string.Join(", ", MenuOf(bobs)));
            await Throws(() => store.PublishAsync(bobs, "밥의 메모", "메모", "- 다르게 쓴다.", user, default), HttpStatusCode.Conflict);
            SkillStoreAdmins.AccountOverride = () => "DOMAIN\\" + keeperAccount;
            await dialog.RefreshAsync();
            Require(MenuOf(bobs).Contains("수정") && MenuOf(bobs).Last() == "삭제", "The store admin was not offered 수정 and 삭제: " + string.Join(", ", MenuOf(bobs)));
            var keeper = new SkillStoreClient(new Uri(relay), key, http, keeperAccount);
            var byAdmin = new ShareSkillDialog(keeper, (await keeper.ListAsync(default)).Single(skill => skill.Name == bobs)) { Owner = dialog };
            Open(byAdmin);
            byAdmin.TextMode = true;
            await Until(() => byAdmin.SkillText.Length > 0, "bob's latest text");
            byAdmin.SkillText += "\n- 날짜를 붙인다.";
            Require(await byAdmin.SubmitAsync() && byAdmin.SharedVersion == 2 && byAdmin.SharedTitle == "밥의 메모-bob", "The store admin could not change bob's skill: " + byAdmin.ErrorText);
            byAdmin.Close();
            var bobsSkill = (await store.ListAsync(default)).Single(skill => skill.Name == bobs);
            var bobsVersions = await store.VersionsAsync(bobs, default);
            Require(bobsSkill is { Author: "bob", Version: 2 } && bobsVersions[0].Author == Environment.UserName && bobsVersions[1].Author == "bob",
                "The admin's version did not keep the skill bob's: " + bobsSkill + " / " + string.Join(", ", bobsVersions.Select(version => version.Author)));
            await dialog.RefreshAsync();
            await dialog.DeleteAsync((await store.ListAsync(default)).Single(skill => skill.Name == bobs));
            Require((await store.ListAsync(default)).All(skill => skill.Name != bobs), "The store admin could not delete bob's skill: " + dialog.StatusText);
            SkillStoreAdmins.AccountOverride = () => "DOMAIN\\not-a-store-admin";

            // Anyone may write (this account is no master). Who sees a skill is its author's: from the card, at any time, no new version.
            await dialog.RefreshAsync();
            Require(MenuOf(report.Name).Contains("수정") && MenuOf(report.Name).Contains("공개로 바꾸기") && MenuOf(report.Name).Last() == "삭제",
                "The author's menu on a private skill: " + string.Join(", ", MenuOf(report.Name)));
            await dialog.SetPrivateAsync(report, false);
            var opened = (await store.ListAsync(default)).Single(skill => skill.Name == report.Name);
            Require(opened is { Private: false, Version: 2, VersionCount: 2 } && (await someoneElse.ListAsync(default)).Any(skill => skill.Name == report.Name) &&
                MenuOf(report.Name).Contains("비공개로 바꾸기"), "Making it public: " + opened + " / " + dialog.StatusText);
            // The 나만 보기 switch alone is saved too, without a new version and without a failure message.
            var hideAgain = new ShareSkillDialog(store, opened) { Owner = dialog };
            Open(hideAgain);
            hideAgain.TextMode = true;
            await Until(() => hideAgain.SkillText.Length > 0, "the skill's latest text");
            hideAgain.PrivateOnly = true;
            Require(await hideAgain.SubmitAsync() && hideAgain.SharedVersion == 2, "Changing only 나만 보기 failed: " + hideAgain.ErrorText);
            hideAgain.Close();
            Require((await store.ListAsync(default)).Single(skill => skill.Name == report.Name) is { Private: true, Version: 2 }, "나만 보기 alone was not saved.");
            await dialog.RefreshAsync();

            // A master changes and deletes what others share, but who sees it stays theirs.
            var dana = new SkillStoreClient(new Uri(relay), key, http, "dana");
            const string danas = "dana-tips";
            await dana.PublishAsync(danas, "다나의 팁", "팁", "- 짧게 쓴다.", "dana", default);
            SkillStoreAdmins.AccountOverride = () => "DOMAIN\\" + keeperAccount;
            await dialog.RefreshAsync();
            Require(MenuOf(danas).Contains("수정") && !MenuOf(danas).Contains("비공개로 바꾸기"), "A master's menu on someone's skill: " + string.Join(", ", MenuOf(danas)));
            await Throws(() => keeper.SetPrivateAsync(danas, true, default), HttpStatusCode.Forbidden);
            var byMaster = new ShareSkillDialog(keeper, (await keeper.ListAsync(default)).Single(skill => skill.Name == danas)) { Owner = dialog };
            Open(byMaster);
            Require(!Descendants(byMaster).OfType<ToggleButton>().Single(toggle => System.Windows.Automation.AutomationProperties.GetName(toggle) == "나만 보기").IsVisible,
                "A master's window offered 나만 보기 on someone else's skill.");
            byMaster.Close();
            SkillStoreAdmins.AccountOverride = () => "DOMAIN\\not-a-store-admin";

            // The author makes it private: nobody else may use it, on a PC that already applied it either, in a task's own folder too.
            await dialog.RefreshAsync();
            await dialog.ApplyAsync((await store.ListAsync(default)).Single(skill => skill.Name == danas), null);
            await dialog.ApplyAsync((await store.ListAsync(default)).Single(skill => skill.Name == report.Name), null);
            InstalledSkills.SaveCustom(danas, "- 내 덧붙임");
            string skillTask = Path.Combine(fixture, "task with a skill");
            Directory.CreateDirectory(skillTask);
            string globalCopy = Path.Combine(skills, danas, "SKILL.md"), taskCopy = Path.Combine(InstalledSkills.ProjectDirectory(skillTask), danas, "SKILL.md");
            using (InstalledSkills.Scope(InstalledSkills.ProjectDirectory(skillTask)))
                InstalledSkills.Apply(await store.GetVersionAsync(danas, 1, apply: false, default));
            Require((await SkillWithdrawals.SweepAsync(store, [skillTask], default)).Count == 0 && File.Exists(globalCopy) && File.Exists(taskCopy), "A public skill was taken out.");
            await dana.SetPrivateAsync(danas, true, default);
            // Out of reach of the relay nothing is decided: the copies stay.
            var unreachable = new SkillStoreClient(new Uri($"http://127.0.0.1:{FreePort()}/"), key, http, user);
            bool noAnswer = false;
            try { await SkillWithdrawals.SweepAsync(unreachable, [skillTask], default); }
            catch (SkillStoreException) { noAnswer = true; }
            Require(noAnswer && File.Exists(globalCopy) && File.Exists(taskCopy), "An unreachable relay took a skill out.");
            var takenOut = await SkillWithdrawals.SweepAsync(store, [skillTask], default);
            Require(takenOut.SequenceEqual(["다나의 팁"]) && !File.Exists(globalCopy) && !File.Exists(taskCopy) && InstalledSkills.ReadCustom(danas).Contains("내 덧붙임", StringComparison.Ordinal) &&
                File.Exists(Path.Combine(skills, report.Name, "SKILL.md")), "The withdrawn skill's copies, my additions, or my own private skill: " + string.Join(", ", takenOut));
            await dialog.RefreshAsync();
            Require(!SwitchIn(dialog, danas).IsEnabled, "A withdrawn skill could be turned on again.");
            await Throws(() => store.GetVersionAsync(danas, 1, apply: true, default), HttpStatusCode.NotFound);
            // The window takes it out by itself too, and public again it can be applied; a deleted skill stays where it was applied.
            await dana.SetPrivateAsync(danas, false, default);
            await dialog.RefreshAsync();
            await dialog.ApplyAsync((await store.ListAsync(default)).Single(skill => skill.Name == danas), null);
            Require(File.Exists(globalCopy), "A skill public again could not be applied.");
            await dana.SetPrivateAsync(danas, true, default);
            await dialog.RefreshAsync();
            Require(!File.Exists(globalCopy) && dialog.StatusText.Contains("다나의 팁", StringComparison.Ordinal), "Opening the window did not take out a withdrawn skill: " + dialog.StatusText);
            await dana.SetPrivateAsync(danas, false, default);
            await dialog.RefreshAsync();
            await dialog.ApplyAsync((await store.ListAsync(default)).Single(skill => skill.Name == danas), null);
            await dana.DeleteAsync(danas, default);
            Require((await SkillWithdrawals.SweepAsync(store, [], default)).Count == 0 && File.Exists(globalCopy), "A deleted skill was taken out of a PC that applied it.");
            InstalledSkills.Remove(danas);
            InstalledSkills.Remove(report.Name);
            // Opening a skill window brings this PC up to date: the 스킬 button's for every task, a task's 스킬… for its own folder too.
            const string danasNext = "dana-next";
            await dana.PublishAsync(danasNext, "다나의 다음 팁", "팁", "- 표로 쓴다.", "dana", default);
            string nextInTask = Path.Combine(InstalledSkills.ProjectDirectory(skillTask), danasNext, "SKILL.md");
            async Task ApplyToTask()
            {
                using (InstalledSkills.Scope(InstalledSkills.ProjectDirectory(skillTask)))
                    InstalledSkills.Apply(await store.GetVersionAsync(danasNext, 1, apply: false, default));
            }
            await ApplyToTask();
            await dana.SetPrivateAsync(danasNext, true, default);
            var fromButton = new SkillStoreDialog(store, tasks: [skillTask]) { Owner = dialog };
            Open(fromButton);
            await Until(() => !File.Exists(nextInTask), "the 스킬 window to take the skill out of a task's folder");
            fromButton.Close();
            await dana.SetPrivateAsync(danasNext, false, default);
            await ApplyToTask();
            await dana.SetPrivateAsync(danasNext, true, default);
            var fromTask = new SkillStoreDialog(store, skillTask, "스킬 태스크") { Owner = dialog };
            Open(fromTask);
            await Until(() => !File.Exists(nextInTask), "a task's 스킬… window to take the skill out");
            fromTask.Close();
            // The app asks once when it starts, for whoever never opens a skill window: the global folder and the tasks it knows.
            await dana.SetPrivateAsync(danasNext, false, default);
            await dialog.RefreshAsync();
            await dialog.ApplyAsync((await store.ListAsync(default)).Single(skill => skill.Name == danasNext), null);
            string mainWorkspace = Path.Combine(fixture, "main workspace"), mainTask = Path.Combine(mainWorkspace, "task one");
            Directory.CreateDirectory(mainTask);
            var nextContent = await store.GetVersionAsync(danasNext, 1, apply: false, default);
            using (InstalledSkills.Scope(InstalledSkills.ProjectDirectory(mainTask))) InstalledSkills.Apply(nextContent);
            string nextGlobal = Path.Combine(skills, danasNext, "SKILL.md"), nextInMainTask = Path.Combine(InstalledSkills.ProjectDirectory(mainTask), danasNext, "SKILL.md");
            Require(File.Exists(nextGlobal) && File.Exists(nextInMainTask), "The skill was not applied for the app's start check.");
            await dana.SetPrivateAsync(danasNext, true, default);
            AppContext.SetData("TerminalNotes.ReportRelayUrl", relay);
            AppContext.SetData("TerminalNotes.ReportRelayKey", key);
            var main = new MainWindow(Path.Combine(fixture, "main-settings.json"), autoStartCodex: false, initialWorkspace: mainWorkspace)
            { Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
            const System.Reflection.BindingFlags Members = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            try
            {
                typeof(MainWindow).GetMethod("SetWorkMode", Members)!.Invoke(main, [false]);
                main.Show();
                await Until(() => !File.Exists(nextGlobal) && !File.Exists(nextInMainTask), "the app's check at its start");
                var shown = (TextBlock)main.FindName("StatusLabel");
                await Until(() => shown.Text.Contains("다나의 다음 팁", StringComparison.Ordinal), "the app saying what it took out");
            }
            finally
            {
                typeof(MainWindow).GetMethod("DisposeResources", Members)!.Invoke(main, null);
                main.Close();
                AppContext.SetData("TerminalNotes.ReportRelayUrl", null);
                AppContext.SetData("TerminalNotes.ReportRelayKey", null);
            }
            await dialog.RefreshAsync();

            // A GitHub (or GitHub Enterprise) link: the relay reads it, its heading gives the title, and the skill follows the file.
            string site = Path.Combine(fixture, "github");
            string linkedFile = Path.Combine(site, "api", "v3", "repos", "team", "skills", "contents", "sql", "SKILL.md");
            Directory.CreateDirectory(Path.GetDirectoryName(linkedFile)!);
            File.WriteAllText(linkedFile, "# SQL 규칙\n\n- 조인은 명시적으로 쓴다.\n");
            int githubPort = FreePort();
            var githubStart = new ProcessStartInfo("python") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-m", "http.server", githubPort.ToString(), "--bind", "127.0.0.1", "--directory", site }) githubStart.ArgumentList.Add(argument);
            using var github = Process.Start(githubStart)!;
            github.OutputDataReceived += (_, _) => { };
            github.ErrorDataReceived += (_, _) => { };
            github.BeginOutputReadLine();
            github.BeginErrorReadLine();
            try
            {
                await Until(async () =>
                {
                    try { using var ready = await http.GetAsync($"http://127.0.0.1:{githubPort}/"); return ready.IsSuccessStatusCode; }
                    catch (HttpRequestException) { return false; }
                }, "the GitHub stand-in");
                string link = $"http://127.0.0.1:{githubPort}/team/skills/blob/main/sql/SKILL.md";
                var linkShare = new ShareSkillDialog(store) { Owner = dialog };
                Open(linkShare);
                linkShare.SourceTab = ShareSkillDialog.From.GitHub;
                linkShare.LinkText = link;
                Require(await linkShare.LoadLinkAsync() && linkShare.TitleText == "SQL 규칙" && linkShare.LinkState.Contains("sql/SKILL.md", StringComparison.Ordinal),
                    "The GitHub link was not read: " + linkShare.LinkState);
                await SaveImage(linkShare, "store-share-github.png");
                Require(await linkShare.SubmitAsync(), "Sharing a GitHub link failed: " + linkShare.ErrorText);
                linkShare.Close();
                var sql = (await store.ListAsync(default)).Single(skill => skill.Title == "SQL 규칙");
                Require(sql.Source == link && sql.Name == "sql-" + owner, "The linked skill: " + sql);
                await dialog.ApplyAsync(sql, null);
                Require(File.ReadAllText(Path.Combine(skills, sql.Name, "SKILL.md")).Contains("조인은 명시적으로", StringComparison.Ordinal), "The linked skill was not applied.");
                // A change on GitHub: the relay makes a new version on its own, and the window takes it for the applied copy.
                File.WriteAllText(linkedFile, "# SQL 규칙\n\n- 조인은 명시적으로 쓴다.\n- 서브쿼리보다 CTE를 쓴다.\n");
                await Until(async () => (await store.ListAsync(default)).Single(skill => skill.Name == sql.Name).Version == 2, "the relay to follow the GitHub change");
                await dialog.RefreshAsync();
                Require(File.ReadAllText(Path.Combine(skills, sql.Name, "SKILL.md")).Contains("CTE를 쓴다", StringComparison.Ordinal) &&
                    dialog.StatusText.Contains("깃허브", StringComparison.Ordinal), "The applied linked skill did not follow GitHub: " + dialog.StatusText);
                Require((await store.VersionsAsync(sql.Name, default))[0].Note == "깃허브 변경 반영", "The GitHub version was not noted.");
            }
            finally
            {
                try { github.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }

            // A task's own skills: switched on into its folder (.opencode\skills), the global ones shown as on.
            string task = Path.Combine(fixture, "task one");
            Directory.CreateDirectory(task);
            var taskSkills = new SkillStoreDialog(store, task, "태스크 하나") { Owner = dialog };
            Open(taskSkills);
            await Until(() => Card(taskSkills, trino.Name) is not null, "the task's skill list");
            string sqlName = (await store.ListAsync(default)).Single(skill => skill.Title == "SQL 규칙").Name;
            Require(SwitchIn(taskSkills, sqlName) is { IsChecked: true, IsEnabled: true } && SwitchIn(taskSkills, trino.Name) is { IsChecked: false, IsEnabled: true },
                "A global skill was not shown as on for the task, or a store skill could not be switched on.");
            // A global skill turned off here stays global and is denied to this task's OpenCode only.
            string ConfigFor(string folder)
            {
                using var context = new TerminalNotes.Services.OpenCodeProjectContext(folder);
                return context.CreateProcessConfigContent(null);
            }
            SwitchIn(taskSkills, sqlName).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Require(SwitchIn(taskSkills, sqlName) is { IsChecked: false, IsEnabled: true } && File.Exists(Path.Combine(skills, sqlName, "SKILL.md")) &&
                TerminalNotes.Services.TaskSkillSwitches.Off(task).SequenceEqual([sqlName]) &&
                System.Text.Json.Nodes.JsonNode.Parse(ConfigFor(task))!["permission"]!["skill"]![sqlName]!.GetValue<string>() == "deny" &&
                !ConfigFor(fixture).Contains("\"skill\"", StringComparison.Ordinal),
                "Turning a global skill off for the task did not deny it there only: " + ConfigFor(task));
            await SaveImage(taskSkills, "store-task-global-off.png");
            SwitchIn(taskSkills, sqlName).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Require(SwitchIn(taskSkills, sqlName).IsChecked == true && TerminalNotes.Services.TaskSkillSwitches.Off(task).Count == 0 &&
                !File.Exists(TerminalNotes.Services.TaskSkillSwitches.FilePath(task)) && !ConfigFor(task).Contains("\"skill\"", StringComparison.Ordinal),
                "Turning the global skill back on for the task left it denied.");
            SwitchIn(taskSkills, trino.Name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            string taskFile = Path.Combine(task, ".opencode", "skills", trino.Name, "SKILL.md");
            await Until(() => File.Exists(taskFile), "the task's own skill");
            Require(!Directory.Exists(Path.Combine(skills, trino.Name)) && SwitchIn(taskSkills, trino.Name).IsChecked == true,
                "The task's skill went to the global folder.");
            await SaveImage(taskSkills, "store-task-skills.png");
            taskSkills.Close();
            Require(InstalledSkills.DirectoryPath == Path.GetFullPath(skills) && !InstalledSkills.List().ContainsKey(trino.Name),
                "Closing the task's skills did not go back to the global folder.");

            // The user's own skill with a store skill's name is never replaced or removed.
            string ownFolder = Path.Combine(skills, "own-skill");
            Directory.CreateDirectory(ownFolder);
            File.WriteAllText(Path.Combine(ownFolder, "SKILL.md"), "---\nname: own-skill\ndescription: mine\n---\nmine\n");
            await store.PublishAsync("own-skill", "다른 사람의 같은 이름", "같은 이름", "theirs", "carol", default);
            await dialog.RefreshAsync();
            var ownToggle = Switch("own-skill");
            Require(ownToggle is { IsEnabled: false, IsChecked: true } && Labeled("own-skill", "업데이트") is null,
                "The user's own same-name skill was offered for removal or update.");
            var ownStore = (await store.ListAsync(default)).Single(skill => skill.Name == "own-skill");
            await ThrowsIo(() => InstalledSkills.Install(ownStore, "theirs"));
            await ThrowsIo(() => InstalledSkills.Remove("own-skill"));
            Require(File.ReadAllText(Path.Combine(ownFolder, "SKILL.md")).Contains("mine", StringComparison.Ordinal), "The user's own skill was changed.");

            // The admin page lists, restores and removes; the applied copy stays removable from the window.
            var auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:" + admin)));
            async Task<HttpResponseMessage> Admin(HttpMethod method, string path, Dictionary<string, string>? form = null)
            {
                using var request = new HttpRequestMessage(method, relay + path) { Content = form is null ? null : new FormUrlEncodedContent(form) };
                request.Headers.Authorization = auth;
                if (method == HttpMethod.Post) request.Headers.Add("Origin", relay);
                return await http.SendAsync(request);
            }
            using (var page = await Admin(HttpMethod.Get, "/skills"))
                Require(page.IsSuccessStatusCode && (await page.Content.ReadAsStringAsync()).Contains("Impala로 데이터 조회", StringComparison.Ordinal), "The admin page did not list skills.");
            using (var anonymous = await http.GetAsync(relay + "/skills"))
                Require(anonymous.StatusCode == HttpStatusCode.Unauthorized, "The admin skill page opened without the password.");
            using (var restored = await Admin(HttpMethod.Post, $"/skills/{Name}/restore", new() { ["version"] = "1" }))
                Require((await store.ListAsync(default)).Single(skill => skill.Name == Name).Version == 4, "Restoring v1 did not publish it as v4.");
            using (var deleted = await Admin(HttpMethod.Post, $"/skills/{Name}/delete"))
                Require((await store.ListAsync(default)).All(skill => skill.Name != Name), "The admin could not remove a skill.");
            await dialog.RefreshAsync();
            Require(Card(dialog, Name) is not null && Switch(Name) is { IsEnabled: true, IsChecked: true },
                "An applied skill that left the store could not be removed.");
            Switch(Name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => !Directory.Exists(Path.Combine(skills, Name)), "removing the skill that left the store");
            Require(ShareSkillDialog.SuggestName("Impala 조회 Guide") == "impala-guide" && ShareSkillDialog.SuggestName("데이터 조회") == "",
                "The English name suggestion changed.");

            // 플러그인: a master shares one (code), 설치 puts it in OpenCode's plugins folder, a new version offers 업데이트, the
            // user's own file of the same name is never replaced, 제거 deletes only the store's file, and the author or a master deletes it.
            string master = SkillStoreAdmins.AllowedUsers[0];
            SkillStoreAdmins.AccountOverride = () => "DOMAIN\\" + master;
            var pluginStore = new PluginStoreDialog(store) { Owner = dialog };
            pluginStore.Confirm = _ => true;
            Open(pluginStore);
            await pluginStore.RefreshAsync();
            Require(Cards(pluginStore).Count == 0 && Buttons(pluginStore).Any(button => button.Content as string == "+ 플러그인 올리기" && button.IsVisible),
                "The empty plugin store, or its share button for a master.");
            string pluginFile = Path.Combine(fixture, "notify-done.ts");
            File.WriteAllText(pluginFile, "export const NotifyDone = async () => ({})\n");
            var sharePlugin = new SharePluginDialog(store) { Owner = pluginStore };
            Open(sharePlugin);
            sharePlugin.LoadFile(pluginFile);
            sharePlugin.DescriptionText = "작업이 끝나면 알림";
            Require(sharePlugin.TitleText == "notify-done" && sharePlugin.Extension == ".ts", "The plugin file did not fill the title: " + sharePlugin.TitleText);
            sharePlugin.TitleText = "끝나면 알림";
            await SaveImage(sharePlugin, "plugin-share.png");
            Require(await sharePlugin.SubmitAsync() && sharePlugin.SharedVersion == 1, "Sharing a plugin failed: " + sharePlugin.ErrorText);
            sharePlugin.Close();
            var notify = (await store.PluginsAsync(default)).Single();
            Require(notify is { Version: 1, Extension: ".ts", Title: "끝나면 알림" } && notify.Name == "notify-done-" + owner, "The shared plugin: " + notify);
            await pluginStore.RefreshAsync();
            Button? PluginButton(string name, string text) => Buttons(Card(pluginStore, name)!).FirstOrDefault(button => button.Content as string == text);
            Require(PluginButton(notify.Name, "설치") is { IsEnabled: true }, "The plugin card did not offer 설치.");
            PluginButton(notify.Name, "설치")!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            string installedFile = Path.Combine(plugins, notify.Name + ".ts");
            await Until(() => File.Exists(installedFile), "installing the plugin");
            Require(File.ReadAllText(installedFile) == "export const NotifyDone = async () => ({})\n" &&
                InstalledPlugins.List()[notify.Name] is { FromStore: true, Version: 1, Edited: false },
                "The installed plugin file or its record.");
            await Until(() => PluginButton(notify.Name, "제거") is not null, "the card to show 제거");
            var pluginHeadings = Descendants(pluginStore).OfType<TextBlock>().Where(text => text.Visibility == Visibility.Visible && text.Text.StartsWith("설치", StringComparison.Ordinal))
                .Select(text => text.Text).ToList();
            Require(pluginHeadings.SequenceEqual(["설치됨 · 1"]), "The installed part was not named: " + string.Join(" | ", pluginHeadings));
            // A new version (now .js): 업데이트 replaces the file.
            Require(await store.PublishPluginAsync(notify.Name, "끝나면 알림", "작업이 끝나면 알림", ".js", "export default async () => ({})\n", master, "js로", edit: true, default) == 2,
                "The plugin's v2 was not published.");
            await pluginStore.RefreshAsync();
            Require(PluginButton(notify.Name, "업데이트") is not null, "A newer plugin version did not offer 업데이트.");
            await SaveImage(pluginStore, "plugin-store.png");
            PluginButton(notify.Name, "업데이트")!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            string updatedFile = Path.Combine(plugins, notify.Name + ".js");
            await Until(() => File.Exists(updatedFile), "updating the plugin");
            Require(!File.Exists(installedFile) && InstalledPlugins.List()[notify.Name] is { Version: 2, File: var updated } && updated == notify.Name + ".js",
                "Updating did not replace the .ts file with the .js one.");
            // The user's own plugin file of a store plugin's name is never replaced or removed.
            File.WriteAllText(Path.Combine(plugins, "own-plugin.ts"), "// mine\n");
            await store.PublishPluginAsync("own-plugin", "같은 이름", "", ".ts", "// theirs\n", master, "", edit: false, default);
            await pluginStore.RefreshAsync();
            Require(PluginButton("own-plugin", "설치") is { IsEnabled: false }, "The user's own same-name plugin was offered for install.");
            var ownPlugin = (await store.PluginsAsync(default)).Single(plugin => plugin.Name == "own-plugin");
            await ThrowsIo(() => InstalledPlugins.Install(ownPlugin, "// theirs\n"));
            await ThrowsIo(() => InstalledPlugins.Remove("own-plugin"));
            Require(File.ReadAllText(Path.Combine(plugins, "own-plugin.ts")) == "// mine\n", "The user's own plugin was changed.");
            // 제거 deletes the store's file only; deleting from the store (a master) leaves installed copies alone.
            PluginButton(notify.Name, "제거")!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => !File.Exists(updatedFile), "removing the plugin");
            Require(!InstalledPlugins.List().ContainsKey(notify.Name) && File.Exists(Path.Combine(plugins, "own-plugin.ts")), "제거 left the plugin or took the user's file.");
            await pluginStore.DeleteAsync((await store.PluginsAsync(default)).Single(plugin => plugin.Name == notify.Name));
            Require((await store.PluginsAsync(default)).All(plugin => plugin.Name != notify.Name), "A master could not delete the plugin: " + pluginStore.StatusText);
            // From a GitHub (or GitHub Enterprise) link: the window reads the .ts file, the plugin follows it, and the installed copy
            // takes a change when the window opens.
            string pluginSite = Path.Combine(fixture, "github-plugins");
            string pluginSource = Path.Combine(pluginSite, "api", "v3", "repos", "team", "plugins", "contents", "notify.ts");
            Directory.CreateDirectory(Path.GetDirectoryName(pluginSource)!);
            File.WriteAllText(pluginSource, "export const Notify = async () => ({})\n");
            int pluginGitHubPort = FreePort();
            var pluginGitHubStart = new ProcessStartInfo("python") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-m", "http.server", pluginGitHubPort.ToString(), "--bind", "127.0.0.1", "--directory", pluginSite })
                pluginGitHubStart.ArgumentList.Add(argument);
            using var pluginGitHub = Process.Start(pluginGitHubStart)!;
            pluginGitHub.OutputDataReceived += (_, _) => { };
            pluginGitHub.ErrorDataReceived += (_, _) => { };
            pluginGitHub.BeginOutputReadLine();
            pluginGitHub.BeginErrorReadLine();
            try
            {
                await Until(async () =>
                {
                    try { using var ready = await http.GetAsync($"http://127.0.0.1:{pluginGitHubPort}/"); return ready.IsSuccessStatusCode; }
                    catch (HttpRequestException) { return false; }
                }, "the plugin GitHub stand-in");
                string pluginLink = $"http://127.0.0.1:{pluginGitHubPort}/team/plugins/blob/main/notify.ts";
                var linkPlugin = new SharePluginDialog(store) { Owner = pluginStore };
                Open(linkPlugin);
                linkPlugin.GitHubMode = true;
                linkPlugin.LinkText = pluginLink;
                Require(await linkPlugin.LoadLinkAsync() && linkPlugin.TitleText == "notify" && linkPlugin.LinkedCode.Contains("Notify", StringComparison.Ordinal),
                    "The plugin's GitHub link was not read: " + linkPlugin.ErrorText);
                await SaveImage(linkPlugin, "plugin-share-github.png");
                Require(await linkPlugin.SubmitAsync(), "Sharing a plugin from GitHub failed: " + linkPlugin.ErrorText);
                linkPlugin.Close();
                var linkedPlugin = (await store.PluginsAsync(default)).Single(plugin => plugin.Source == pluginLink);
                Require(linkedPlugin is { Version: 1, Extension: ".ts" } && linkedPlugin.Name == "notify-" + owner, "The linked plugin: " + linkedPlugin);
                await pluginStore.RefreshAsync();
                Require(Descendants(Card(pluginStore, linkedPlugin.Name)!).OfType<TextBlock>().Any(mark => mark.Text == StoreLook.LinkGlyph),
                    "The linked plugin's card has no link mark.");
                await pluginStore.InstallAsync(linkedPlugin);
                string linkedInstalled = Path.Combine(plugins, linkedPlugin.Name + ".ts");
                Require(File.ReadAllText(linkedInstalled).Contains("Notify", StringComparison.Ordinal), "The linked plugin was not installed.");
                // A change on GitHub: the relay makes v2 on its own, and opening the window updates the installed file.
                File.WriteAllText(pluginSource, "export const Notify = async () => ({ event: async () => {} })\n");
                await Until(async () => (await store.PluginsAsync(default)).Single(plugin => plugin.Name == linkedPlugin.Name).Version == 2, "the relay to follow the plugin's GitHub change");
                await pluginStore.RefreshAsync();
                Require(File.ReadAllText(linkedInstalled).Contains("event", StringComparison.Ordinal) && InstalledPlugins.List()[linkedPlugin.Name].Version == 2 &&
                    pluginStore.StatusText.Contains("깃허브", StringComparison.Ordinal), "The installed linked plugin did not follow GitHub: " + pluginStore.StatusText);
            }
            finally
            {
                try { pluginGitHub.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            // Someone who is not a master does not share plugins (the window hides it; the relay refuses when MASTER_USERS is set).
            SkillStoreAdmins.AccountOverride = () => "DOMAIN\\not-a-store-admin";
            var nonMaster = new PluginStoreDialog(store) { Owner = dialog };
            Open(nonMaster);
            Require(!Buttons(nonMaster).Any(button => button.Content as string == "+ 플러그인 올리기" && button.IsVisible), "A non-master saw 플러그인 올리기.");
            nonMaster.Close();
            pluginStore.Close();
        }
        finally
        {
            dialog?.Close();
            try { server.Kill(entireProcessTree: true); server.WaitForExit(5000); }
            catch (InvalidOperationException) { }
            try { Directory.Delete(fixture, recursive: true); }
            catch (IOException) { }
        }
    }

    // Test windows are shown without taking the focus from whatever the user is doing.
    // OpenCode's own settings in 설정: the file `opencode plugin -g` writes to, comments kept, nothing broken saved, and a file changed
    // meanwhile not overwritten. Which names OpenCode picks was seen on OpenCode 1.18.34 itself.
    private static async Task CheckOpenCodeConfigAsync(string fixture)
    {
        string folder = Path.Combine(fixture, "opencode config");
        AppContext.SetData("TerminalNotes.OpenCodeConfigDirectory", folder);
        string In(string name) => Path.Combine(folder, name);
        const TerminalNotes.Services.GlobalAgentInstructions.SaveResult Saved = TerminalNotes.Services.GlobalAgentInstructions.SaveResult.Saved,
            Conflict = TerminalNotes.Services.GlobalAgentInstructions.SaveResult.Conflict;
        Require(ConfigFile.FilePath == In("opencode.jsonc") && !Directory.Exists(folder), "A new settings file is not the one OpenCode creates: " + ConfigFile.FilePath);
        string start = ConfigFile.ReadCurrent(), edited = "{\n  // 내 플러그인\n  \"plugin\": [\"opencode-a\",],\n}\n";
        Require(start.Contains("opencode.ai/config.json", StringComparison.Ordinal) && ConfigFile.TrySave(start, edited) == Saved &&
            File.ReadAllText(ConfigFile.FilePath) == edited && File.ReadAllBytes(ConfigFile.FilePath)[0] == (byte)'{', "JSONC with a comment and a trailing comma was not saved as written.");
        Expect<InvalidOperationException>(() => ConfigFile.TrySave(edited, "{\n  \"plugin\": \n"), "Broken JSON was saved.");
        Expect<InvalidOperationException>(() => ConfigFile.TrySave(edited, "[]"), "A settings file that is not an object was saved.");
        Require(File.ReadAllText(ConfigFile.FilePath) == edited, "A refused save changed the file.");
        File.WriteAllText(ConfigFile.FilePath, "{ \"plugin\": [\"opencode-b\"] }");
        Require(ConfigFile.TrySave(edited, "{ }") == Conflict && File.ReadAllText(ConfigFile.FilePath).Contains("opencode-b", StringComparison.Ordinal),
            "A file changed while editing was overwritten.");
        File.WriteAllText(In("config.json"), "{ }");
        var editor = GlobalAgentInstructionsDialog.CreateOpenCodeConfig();
        Open(editor);
        Require(editor.PathText.Text == In("opencode.jsonc") && editor.InstructionsInput.Text.Contains("opencode-b", StringComparison.Ordinal) &&
            editor.DescriptionText.Text.Contains("config.json", StringComparison.Ordinal), "The editor did not open the file, or did not say what else is there: " + editor.DescriptionText.Text);
        await SaveImage(editor, "opencode-config.png");
        editor.Close();
        File.Delete(In("opencode.jsonc"));
        Require(ConfigFile.FilePath == In("config.json") && !ConfigFile.Description.Contains("config.json", StringComparison.Ordinal), "config.json alone is not the file.");
        File.WriteAllText(In("opencode.json"), "{ }");
        Require(ConfigFile.FilePath == In("opencode.json"), "opencode.json did not come before config.json.");
        Require(Directory.GetFiles(folder, "*.tmp").Length == 0, "Saving left a temporary file.");
    }

    private static void Open(Window window)
    {
        window.ShowActivated = false;
        window.Show();
    }

    // A screenshot for checking the window by eye, as Windows shows it (its frame too, even under other windows); kept outside
    // the deleted fixture. Nothing is drawn while the remote desktop session is disconnected.
    private static async Task SaveImage(Window window, string name)
    {
        window.UpdateLayout();
        if (Environment.GetEnvironmentVariable("TN_LAYOUT_DUMP") == "1") DumpLayout(window, name);
        await Task.Delay(250);
        // TN_RENDER_PREVIEW=1 draws the window's content itself, which works while the remote desktop session is disconnected.
        if (Environment.GetEnvironmentVariable("TN_RENDER_PREVIEW") == "1" && window.Content is FrameworkElement content)
        {
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight),
                96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(content);
            Save(bitmap, name);
            return;
        }
        Print(new System.Windows.Interop.WindowInteropHelper(window).Handle, name);
    }

    private static void Save(System.Windows.Media.Imaging.BitmapSource image, string name)
    {
        string folder = Path.Combine(Path.GetTempPath(), "TerminalNotes.SkillStore");
        Directory.CreateDirectory(folder);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(folder, name));
        encoder.Save(stream);
        Console.WriteLine("PREVIEW " + Path.Combine(folder, name));
    }

    // An open menu is a window of its own.
    private static async Task SaveImage(ContextMenu menu, string name)
    {
        await Task.Delay(400);
        if (PresentationSource.FromVisual(menu) is not System.Windows.Interop.HwndSource source) throw new InvalidOperationException("The menu is not open.");
        Print(source.Handle, name);
    }

    private static void Print(IntPtr handle, string name)
    {
        if (!GetWindowRect(handle, out var bounds)) throw new InvalidOperationException("The window has no bounds.");
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, width, height);
        IntPtr previous = SelectObject(memory, bitmap);
        try
        {
            if (!PrintWindow(handle, memory, 2)) throw new InvalidOperationException("The window could not be printed.");
            SelectObject(memory, previous);
            var image = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            string folder = Path.Combine(Path.GetTempPath(), "TerminalNotes.SkillStore");
            Directory.CreateDirectory(folder);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var stream = File.Create(Path.Combine(folder, name));
            encoder.Save(stream);
            Console.WriteLine("PREVIEW " + Path.Combine(folder, name));
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static void DumpLayout(Window window, string name)
    {
        Console.WriteLine("LAYOUT " + name + $" window {window.ActualWidth:0}x{window.ActualHeight:0}");
        void Walk(DependencyObject node, int depth)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
                if (child is FrameworkElement element && element.IsVisible)
                {
                    string? what = element switch
                    {
                        TextBlock text when text.Text.Length > 0 => "T \"" + (text.Text.Length > 60 ? text.Text[..60] : text.Text).Replace('\n', '⏎') +
                            "\" fs=" + text.FontSize + " fw=" + text.FontWeight + " fg=" + text.Foreground,
                        Button button => "B " + (button.Content as string ?? button.Content?.GetType().Name) + " style=" + (button.Style == null ? "-" : "set"),
                        TextBox box => "X len=" + box.Text.Length + " bg=" + box.Background,
                        System.Windows.Controls.Image => "I",
                        System.Windows.Shapes.Ellipse ellipse => "E fill=" + ellipse.Fill,
                        System.Windows.Shapes.Rectangle => "R",
                        Border border when border.Tag is string || border.BorderThickness.Left > 0 => "D bg=" + border.Background + " edge=" + border.BorderBrush + " r=" + border.CornerRadius.TopLeft,
                        _ => null,
                    };
                    if (what is not null)
                    {
                        var at = element.TransformToAncestor(window).Transform(new Point(0, 0));
                        Console.WriteLine($"  {new string(' ', Math.Min(depth, 30))}{at.X:0},{at.Y:0} {element.ActualWidth:0}x{element.ActualHeight:0} {what}");
                    }
                }
                Walk(child, depth + 1);
            }
        }
        Walk(window, 0);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr context, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr context);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr context);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr context, int width, int height);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr context, IntPtr item);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr context);

    private static Border? Card(Window dialog, string name) => Descendants(dialog).OfType<Border>().FirstOrDefault(border => border.Tag as string == name);

    // The cards in the order shown.
    private static List<string> Cards(Window dialog) =>
        Descendants(dialog).OfType<Border>().Where(border => border.Tag is string && border.ContextMenu is not null).Select(border => (string)border.Tag).ToList();

    // A card's switch (its star is a toggle too).
    private static ToggleButton SwitchIn(Window dialog, string name) => Descendants(Card(dialog, name)!).OfType<ToggleButton>()
        .Single(toggle => System.Windows.Automation.AutomationProperties.GetName(toggle).Contains("적용 · ", StringComparison.Ordinal));

    private static Border? VersionBadgeIn(Window dialog, string name) => Descendants(Card(dialog, name)!).OfType<Border>()
        .SingleOrDefault(border => System.Windows.Automation.AutomationProperties.GetName(border).StartsWith("버전 ", StringComparison.Ordinal));

    private static List<Button> Buttons(DependencyObject root) => Descendants(root).OfType<Button>().ToList();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject element) continue;
            yield return element;
            foreach (var descendant in Descendants(element)) yield return descendant;
        }
    }

    private static async Task Throws(Func<Task> action, HttpStatusCode status)
    {
        try { await action(); }
        catch (SkillStoreException exception) when (exception.Status == status && exception.Message.Length > 0) { return; }
        throw new InvalidOperationException("The store did not refuse with " + status + ".");
    }

    private static Task ThrowsIo(Action action)
    {
        try { action(); }
        catch (IOException) { return Task.CompletedTask; }
        throw new InvalidOperationException("The user's own skill was not protected.");
    }

    private static void Expect<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    // Answers every request with one status, as a proxy that cannot reach the relay does.
    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("<html><body>Bad Gateway</body></html>") });
    }

    private static async Task Until(Func<bool> condition, string what) => await Until(() => Task.FromResult(condition()), what);

    private static async Task Until(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Timed out waiting for " + what + ".");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindRelay()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            // The relay lives in the ApiServer project next to this one (Projects\ApiServer\server); an older checkout kept it in relay\.
            string candidate = Path.Combine(directory.FullName, "ApiServer", "server", "relay.py");
            if (File.Exists(candidate)) return candidate;
            candidate = Path.Combine(directory.FullName, "relay", "relay.py");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("ApiServer\\server\\relay.py (the API server project next to TerminalNotes) was not found above the test folder.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
