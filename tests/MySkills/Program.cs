using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TerminalNotes;
using Application = System.Windows.Application;

// 나만의 스킬: the library (add, edit with versions, on/off into OpenCode's folder, archive, delete), the limits that keep it small,
// the curator's rules, what learning reads from a session and the answers it accepts, and the skill window's 나만의 스킬 view.
internal static class Program
{
    private static int _failures;

    [STAThread]
    private static int Main()
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(); }
            catch (Exception ex) { _failures++; Console.WriteLine("FAIL " + ex); }
            finally { app.Shutdown(); }
        };
        app.Run();
        Console.WriteLine(_failures == 0 ? "PASS my skills: library, limits, curator, learning formats, window" : $"FAIL my skills ({_failures})");
        return _failures;
    }

    private static void Assert(bool condition, string name)
    {
        if (condition) return;
        _failures++;
        Console.WriteLine("FAIL " + name);
    }

    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); Assert(false, name + " (no refusal)"); }
        catch (T) { }
    }

    private static async Task RunAsync()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes my skills " + Guid.NewGuid().ToString("N"));
        string opencode = Path.Combine(fixture, "opencode-skills");
        AppContext.SetData("TerminalNotes.SkillsDirectory", opencode);
        AppContext.SetData("TerminalNotes.MySkillsDirectory", Path.Combine(fixture, "my-skills"));
        AppContext.SetData("TerminalNotes.SkillFavoritesPath", Path.Combine(fixture, "favorites.json"));
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(fixture, "data"));
        Directory.CreateDirectory(opencode);
        try
        {
            Library(opencode);
            Curator(opencode);
            Learning();
            await Window();
        }
        finally
        {
            try { Directory.Delete(fixture, true); } catch { }
        }
    }

    private static void Library(string opencode)
    {
        // Add: a "my-" name from the proposed one, on, and in OpenCode's folder with frontmatter.
        var skill = MySkills.Save(MySkills.NameFor("WPF Release Check!", "WPF 릴리스 확인"), "WPF 릴리스 확인", "WPF 앱을 릴리스할 때", "1. 버전을 올린다.\n2. 빌드한다.", learned: true, project: @"C:\work\app");
        Assert(skill.Name == "my-wpf-release-check" && skill.Applied && skill.Learned && skill.Version == 1, "a learned skill is added on: " + skill.Name);
        string deployed = Path.Combine(opencode, skill.Name, "SKILL.md");
        Assert(File.Exists(deployed) && File.ReadAllText(deployed).StartsWith("---\nname: my-wpf-release-check\ndescription: \"WPF 앱을 릴리스할 때\"\n---\n\n# WPF 릴리스 확인\n\n1. 버전을 올린다.", StringComparison.Ordinal),
            "an applied skill is in OpenCode's folder with its frontmatter");
        Assert(MySkills.NameFor("wpf-release-check", "x") == "my-wpf-release-check-2", "a taken name gets a number");
        Assert(MySkills.NameFor("", "한글 제목").StartsWith("my-skill-", StringComparison.Ordinal), "a title without letters gets a made-up name");

        // Edit: a new version, the earlier text kept; unchanged text keeps the version.
        var edited = MySkills.Save(skill.Name, "WPF 릴리스 확인", "WPF 앱을 릴리스할 때", "1. 버전을 올린다.\n2. 빌드한다.\n3. 테스트한다.");
        Assert(edited.Version == 2 && edited.Learned && edited.Project == @"C:\work\app", "editing makes v2 and keeps where it came from");
        Assert(MySkills.Versions(skill.Name) is [(1, "1. 버전을 올린다.\n2. 빌드한다.")], "v1 is kept");
        for (int i = 0; i < 5; i++) MySkills.Save(skill.Name, "WPF 릴리스 확인", "WPF 앱을 릴리스할 때", "단계 " + i);
        Assert(MySkills.Versions(skill.Name).Count == MySkills.KeptVersions, "only the last versions are kept: " + MySkills.Versions(skill.Name).Count);
        Assert(MySkills.Save(skill.Name, "WPF 릴리스 확인", "WPF 앱을 릴리스할 때", "단계 4").Version == MySkills.Get(skill.Name)!.Version, "the same text is not a new version");
        Assert(File.ReadAllText(deployed).Contains("단계 4", StringComparison.Ordinal), "OpenCode's copy follows the edit");

        // Refusals: empty, too long.
        Throws<ArgumentException>(() => MySkills.Save(null, "", "", "x"), "a skill without a title");
        Throws<ArgumentException>(() => MySkills.Save(null, "제목", "", ""), "a skill without instructions");
        Throws<ArgumentException>(() => MySkills.Save(null, "제목", "", new string('가', MySkills.MaxBodyChars + 1)), "instructions over the limit");

        // Off and on: OpenCode's copy goes and comes back; a folder that is not ours is left alone.
        MySkills.SetApplied(skill.Name, false);
        Assert(!Directory.Exists(Path.Combine(opencode, skill.Name)) && !MySkills.Get(skill.Name)!.Applied, "off takes it out of OpenCode's folder");
        MySkills.SetApplied(skill.Name, true);
        Assert(File.Exists(deployed), "on puts it back");
        Directory.CreateDirectory(Path.Combine(opencode, "my-hand-made"));
        File.WriteAllText(Path.Combine(opencode, "my-hand-made", "SKILL.md"), "---\nname: my-hand-made\ndescription: x\n---\n");
        MySkills.Curate();
        Assert(File.Exists(Path.Combine(opencode, "my-hand-made", "SKILL.md")), "a skill folder that is not ours is never removed");

        // Uses are counted.
        MySkills.RecordUses(["my-wpf-release-check", "my-wpf-release-check", "impala-query"]);
        Assert(MySkills.Get(skill.Name) is { Uses: 2, LastUsed: not null }, "uses of my skills are counted, store skills ignored");

        // Archive and back (off), then delete.
        MySkills.Archive(skill.Name);
        Assert(MySkills.Get(skill.Name) is null && !Directory.Exists(Path.Combine(opencode, skill.Name)) && MySkills.Archived().Any(archived => archived.Name == skill.Name),
            "archiving moves it into the archive and out of OpenCode's folder");
        MySkills.Restore(skill.Name);
        Assert(MySkills.Get(skill.Name) is { Applied: false, Uses: 2 } restored && restored.Body == "단계 4" && MySkills.Archived().Count == 0,
            "restoring brings it back off, with its text and uses");
        MySkills.Delete(skill.Name);
        Assert(MySkills.Get(skill.Name) is null && MySkills.List().Count == 0, "deleting removes it");
    }

    private static void Curator(string opencode)
    {
        var now = DateTimeOffset.UtcNow;
        // Learned and unused for a month: off. Off and unused for three months: archived. Written by hand: stays on.
        var old = MySkills.Save(null, "오래된 배움", "", "본문", learned: true, now: now - TimeSpan.FromDays(40));
        var older = MySkills.Save(null, "아주 오래된 배움", "", "본문", learned: true, now: now - TimeSpan.FromDays(120));
        MySkills.SetApplied(older.Name, false);
        var mine = MySkills.Save(null, "직접 쓴 스킬", "", "본문", now: now - TimeSpan.FromDays(200));
        var fresh = MySkills.Save(null, "새 배움", "", "본문", learned: true, now: now);
        var result = MySkills.Curate(now);
        Assert(result.TurnedOff.Contains(old.Name) && !MySkills.Get(old.Name)!.Applied && !Directory.Exists(Path.Combine(opencode, old.Name)),
            "a learned skill unused for 30 days is turned off");
        Assert(result.Archived.Contains(older.Name) && MySkills.Get(older.Name) is null, "a skill off and unused for 90 days is archived");
        Assert(MySkills.Get(mine.Name)!.Applied && MySkills.Get(fresh.Name)!.Applied, "hand-written and recently learned skills stay on");
        // Turning one on by hand starts its month again.
        MySkills.SetApplied(old.Name, true);
        Assert(MySkills.Curate(now).TurnedOff.Count == 0 && MySkills.Get(old.Name)!.Applied, "a skill turned on by hand is not turned off at once");

        // At most MaxApplied on: new ones beyond come in off, and turning one more on is refused.
        foreach (var skill in MySkills.List()) MySkills.Delete(skill.Name);
        for (int i = 0; i < MySkills.MaxApplied + 2; i++) MySkills.Save(null, $"스킬 {i}", "", "본문 " + i, learned: true);
        var all = MySkills.List();
        Assert(all.Count(skill => skill.Applied) == MySkills.MaxApplied, $"only {MySkills.MaxApplied} are on: {all.Count(skill => skill.Applied)}");
        Throws<InvalidOperationException>(() => MySkills.SetApplied(all.First(skill => !skill.Applied).Name, true), "one more on than the limit");
        var usage = MySkills.Measure();
        Assert(usage.Count == MySkills.MaxApplied + 2 && usage.Applied == MySkills.MaxApplied && usage.Bytes > 0, "the usage is measured");

        // Merge proposals: the first becomes the merged text, the others go to the archive.
        var a = all[0];
        var b = all[1];
        var proposal = new MySkills.MergeProposal([a.Name, b.Name], "합친 스킬", "둘을 합침", "합친 본문", "같은 일");
        MySkills.UpdateSettings(settings => settings with { Proposals = [proposal] });
        var merged = MySkills.AcceptProposal(proposal);
        Assert(merged.Name == a.Name && merged.Title == "합친 스킬" && merged.Version == 2 && MySkills.Get(b.Name) is null &&
            MySkills.Archived().Any(archived => archived.Name == b.Name) && MySkills.ReadSettings().Proposals.Count == 0, "accepting a merge proposal");
        foreach (var skill in MySkills.List()) MySkills.Delete(skill.Name);
        foreach (var skill in MySkills.Archived()) MySkills.DeleteArchived(skill.Name);
        Assert(MySkills.List().Count == 0 && MySkills.Archived().Count == 0, "everything can be deleted");
    }

    private static void Learning()
    {
        // A session read back: what was asked, said and run, failures, skills used; only after the last learned message.
        string json = """
        [
          {"info":{"id":"m1","role":"user"},"parts":[{"type":"text","text":"빌드가 깨져. 고쳐줘"}]},
          {"info":{"id":"m2","role":"assistant"},"parts":[
            {"type":"tool","tool":"skill","state":{"status":"completed","input":{"name":"my-build-fix"}}},
            {"type":"tool","tool":"bash","state":{"status":"error","input":{"command":"dotnet build"},"error":"CS0119 Key"}},
            {"type":"tool","tool":"read","state":{"status":"completed","input":{"filePath":"a.cs"}}},
            {"type":"text","text":"이름 충돌을 고쳤습니다."}]},
          {"info":{"id":"m3","role":"user"},"parts":[{"type":"text","text":"고마워","synthetic":true}]}
        ]
        """;
        using var document = JsonDocument.Parse(json);
        var digest = MySkillLearning.Read(document.RootElement, null);
        Assert(digest.ToolCalls == 3 && digest.UsedSkills is ["my-build-fix"] && digest.LastMessageId == "m3", "the digest counts tools and skills");
        Assert(digest.Text.Contains("사용자: 빌드가 깨져", StringComparison.Ordinal) && digest.Text.Contains("[도구 bash] command=dotnet build (실패: CS0119 Key)", StringComparison.Ordinal) &&
            digest.Text.Contains("에이전트: 이름 충돌을 고쳤습니다.", StringComparison.Ordinal) && !digest.Text.Contains("고마워", StringComparison.Ordinal), "the digest reads: " + digest.Text);
        var later = MySkillLearning.Read(document.RootElement, "m2");
        Assert(later.ToolCalls == 0 && later.Text.Length == 0, "only what came after the last learned message");
        Assert(MySkillLearning.MinimumToolCalls > digest.ToolCalls, "three steps are not enough to learn from");
        string prompt = MySkillLearning.LearnPrompt(digest, [], "app");
        Assert(prompt.Contains(MySkillLearning.None, StringComparison.Ordinal) && prompt.Contains("비밀번호", StringComparison.Ordinal) && prompt.Contains(digest.Text, StringComparison.Ordinal),
            "the learning prompt has the format, the rules and the record");

        // The answers accepted.
        Assert(MySkillLearning.ParseLesson("생각해 보니\n" + MySkillLearning.None) is null, "nothing to learn");
        var lesson = MySkillLearning.ParseLesson($"""
            {MySkillLearning.Start}
            name: build-fix
            update:
            title: 빌드 오류 고치기
            description: 빌드가 깨졌을 때
            ---
            1. 오류 코드를 읽는다.
            {MySkillLearning.End}
            """);
        Assert(lesson is { Name: "build-fix", Update: null, Title: "빌드 오류 고치기", Description: "빌드가 깨졌을 때", Body: "1. 오류 코드를 읽는다." }, "a lesson is read");
        Assert(MySkillLearning.ParseLesson($"{MySkillLearning.Start}\ntitle: x\n{MySkillLearning.End}") is null, "a lesson without a body is not taken");
        // Real answers seen 2026-11-12: the fields without the markers, and the end marker missing.
        var bare = MySkillLearning.ParseLesson("name: monthly-sales-summary\nupdate:\ntitle: 월별 매출 집계\ndescription: 매출 CSV를 월별로 집계할 때\n---\n1. CSV를 읽는다.");
        Assert(bare is { Title: "월별 매출 집계", Body: "1. CSV를 읽는다." }, "an answer in the field format without the markers is taken");
        Assert(MySkillLearning.ParseLesson($"{MySkillLearning.Start}\nname: a\ntitle: 제목\n---\n본문") is { Body: "본문" }, "an answer without its end marker is taken");
        Assert(MySkillLearning.ParseLesson("이 작업은 다시 쓸 만한 절차가 없습니다.\n" + MySkillLearning.None) is null, "nothing to keep stays nothing");
        Assert(MySkillLearning.ParseLesson("모델이 설명만 길게 했습니다. title이라는 단어도 있지만 형식은 아닙니다.") is null, "prose is not taken");
        var groups = MySkillLearning.ParseReview($"{MySkillLearning.MergeStart} my-a, my-b | 같은 일\n{MySkillLearning.MergeStart} my-c, my-unknown | x",
            new HashSet<string> { "my-a", "my-b", "my-c" });
        Assert(groups.Count == 1 && groups[0].Names.SequenceEqual(["my-a", "my-b"]) && groups[0].Reason == "같은 일", "review groups name known skills only");
    }

    private static async Task Window()
    {
        MySkills.Save(null, "WPF 릴리스 확인", "WPF 앱을 릴리스할 때 버전·빌드·테스트 순서", "1. 버전을 올린다.", learned: true, project: @"C:\work\TerminalNotes");
        var hand = MySkills.Save(null, "보고서 표 머리글", "보고서를 쓸 때", "표 머리글은 한국어로.");
        MySkills.RecordUses([hand.Name]);
        var archived = MySkills.Save(null, "예전 스킬", "", "본문");
        MySkills.Archive(archived.Name);
        var skills = MySkills.List();
        MySkills.UpdateSettings(settings => settings with { Proposals = [new MySkills.MergeProposal(skills.Select(skill => skill.Name).ToArray(), "릴리스와 보고서", "", "합친 본문", "둘 다 마무리 절차")] });
        var dialog = new SkillStoreDialog(null);
        dialog.Show();
        await Task.Delay(600);
        Assert(dialog.Categories.LastOrDefault() == MySkills.Category, "나만의 스킬 is the last entry of the left list: " + string.Join(",", dialog.Categories));
        dialog.Category = MySkills.Category;
        await Idle(dialog);
        var texts = Descendants(dialog).OfType<TextBlock>().Where(text => text.IsVisible).ToList();
        Assert(texts.Any(text => AutomationProperties.GetAutomationId(text) == "SkillPartHeading" && text.Text == "나만의 스킬 · 2"), "the view is headed with its count");
        Assert(texts.Any(text => AutomationProperties.GetAutomationId(text) == "MySkillUsage" && text.Text.StartsWith($"켜짐 2/{MySkills.MaxApplied}", StringComparison.Ordinal)), "the usage of the limits is shown");
        Assert(texts.Any(text => text.Text == "자동") && texts.Any(text => text.Text.Contains("사용 1회", StringComparison.Ordinal)), "a learned skill is marked, uses are shown");
        Assert(Descendants(dialog).OfType<Border>().Any(border => AutomationProperties.GetAutomationId(border) == "MySkillProposal"), "a merge proposal is shown");
        await SaveImage(dialog, "my-skills.png");

        // Add through the editor (replaced here), toggle off, archive view.
        dialog.EditMySkill = _ => MySkills.Save(null, "새로 쓴 스킬", "직접", "본문");
        Find<Button>(dialog, "MySkillAdd").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Idle(dialog);
        Assert(Descendants(dialog).OfType<TextBlock>().Any(text => text.IsVisible && text.Text == "나만의 스킬 · 3"), "+ 새 스킬 adds one");
        Find<Button>(dialog, "MySkillArchive").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Idle(dialog);
        Assert(Descendants(dialog).OfType<TextBlock>().Any(text => text.IsVisible && text.Text == "예전 스킬"), "보관함 lists the archived skill");
        await SaveImage(dialog, "my-skills-archive.png");
        dialog.Close();

        var editor = new MySkillEditorDialog(MySkills.List()[0]);
        editor.Show();
        await Task.Delay(400);
        await SaveImage(editor, "my-skill-editor.png");
        editor.Close();
    }

    private static T Find<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Descendants(root).OfType<T>().First(element => AutomationProperties.GetAutomationId(element) == id);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var inner in Descendants(child)) yield return inner;
        }
    }

    private static async Task Idle(Window window)
    {
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await Task.Delay(80);
    }

    private static async Task SaveImage(Window window, string name)
    {
        await Idle(window);
        var content = (FrameworkElement)window.Content;
        int width = (int)Math.Ceiling(content.ActualWidth), height = (int)Math.Ceiling(content.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        string folder = Path.Combine(Path.GetTempPath(), "TerminalNotes.MySkills");
        Directory.CreateDirectory(folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, name));
        encoder.Save(stream);
        Console.WriteLine("PREVIEW " + Path.Combine(folder, name));
    }
}
