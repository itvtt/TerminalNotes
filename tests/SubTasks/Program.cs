using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TerminalNotes;
using TerminalNotes.Services;
using Application = System.Windows.Application;

// 상위·하위 태스크: the project folders inside a task folder are its sub-tasks (▾/▸ under it); node_modules and plain folders never
// are; a parent's switch turns them off; a task dropped on another goes under it after asking, its folder and files moved, and comes
// back out the same way.
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
        Console.WriteLine(_failures == 0 ? "PASS sub-tasks: found, shown under their parent, switched off, moved in and out with their folders" : $"FAIL sub-tasks ({_failures})");
        return _failures;
    }

    private static void Assert(bool condition, string name)
    {
        Console.WriteLine((condition ? "ok   " : "FAIL ") + name);
        if (!condition) _failures++;
    }

    private static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "TerminalNotes sub tasks " + Guid.NewGuid().ToString("N")[..8]);
        string workspace = Path.Combine(root, "work");
        string parent = Path.Combine(workspace, "i-luncher");
        Directory.CreateDirectory(Path.Combine(parent, ".git"));
        Directory.CreateDirectory(Path.Combine(parent, "Launcher"));
        File.WriteAllText(Path.Combine(parent, "Launcher", "Launcher.csproj"), "<Project/>");
        Directory.CreateDirectory(Path.Combine(parent, "AppKit", ".git"));
        Directory.CreateDirectory(Path.Combine(parent, "docs"));
        Directory.CreateDirectory(Path.Combine(parent, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(parent, "node_modules", "pkg", "package.json"), "{}");
        Directory.CreateDirectory(Path.Combine(workspace, "node_modules", "left-pad"));
        File.WriteAllText(Path.Combine(workspace, "node_modules", "left-pad", "package.json"), "{}");
        string standalone = Path.Combine(workspace, "Notes");
        Directory.CreateDirectory(standalone);
        File.WriteAllText(Path.Combine(standalone, "memo.txt"), "keep me");
        Directory.CreateDirectory(Path.Combine(workspace, "Other"));
        string settings = Path.Combine(root, "settings.json");
        File.WriteAllText(settings, JsonSerializer.Serialize(new { Workspace = workspace, CodexWorkspace = workspace, SetupCompleted = true, WorkspaceDefaultsVersion = 1 }));
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(root, "data"));
        var window = new MainWindow(settings, autoStartCodex: false) { Width = 1200, Height = 800 };
        try
        {
            window.Show();
            await Refresh(window);
            var rows = Rows(window);
            string Key(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            Console.WriteLine("tasks: " + string.Join(", ", rows.Keys.Select(path => Path.GetRelativePath(workspace, path))));
            Assert(rows.ContainsKey(Key(parent)) && rows.ContainsKey(Key(Path.Combine(parent, "Launcher"))) && rows.ContainsKey(Key(Path.Combine(parent, "AppKit"))),
                "the project folders inside a task folder are tasks (a project file, a git repository)");
            Assert(!rows.ContainsKey(Key(Path.Combine(parent, "docs"))) && !rows.ContainsKey(Key(Path.Combine(parent, "node_modules"))) &&
                !rows.ContainsKey(Key(Path.Combine(workspace, "node_modules"))), "plain folders and node_modules are not");
            var parentNode = Node(rows[Key(parent)]);
            var launcherNode = Node(rows[Key(Path.Combine(parent, "Launcher"))]);
            Assert(ReferenceEquals(launcherNode.Parent, parentNode) && parentNode.Items.Count == 2, "they are shown under their parent");
            Assert(ReferenceEquals(parentNode.Style, window.FindResource("TaskTreeParent")) && parentNode.IsExpanded, "the parent shows ▾ and is open");
            Assert(ReferenceEquals(Node(rows[Key(standalone)]).Style, window.FindResource("TaskTreeTop")) && ReferenceEquals(launcherNode.Style, window.FindResource("TaskTreeLeaf")),
                "the other top tasks line up with the parent; sub-tasks are one step in");
            Assert(window.ParentTaskOf(Path.Combine(parent, "Launcher")) is { } owner && string.Equals(owner, Key(parent), StringComparison.OrdinalIgnoreCase), "a sub-task knows its parent");
            await SaveImage(window, "sub-tasks.png");
            parentNode.IsExpanded = false;
            await Idle(window);
            Assert(!launcherNode.IsVisible, "▸ folds the sub-tasks away");
            parentNode.IsExpanded = true;

            // The parent's switch.
            Field<HashSet<string>>(window, "_subTaskParentsOff").Add(Key(parent));
            await Refresh(window);
            Assert(!Rows(window).ContainsKey(Key(Path.Combine(parent, "Launcher"))) && ReferenceEquals(Node(Rows(window)[Key(parent)]).Style, window.FindResource("TaskTreeLeaf")) &&
                ReferenceEquals(Node(Rows(window)[Key(standalone)]).Style, window.FindResource("TaskTreeLeaf")),
                "turned off, the sub-projects are no tasks and every row looks as before");
            Field<HashSet<string>>(window, "_subTaskParentsOff").Clear();
            await Refresh(window);

            // What cannot go where.
            Assert(window.MoveIntoProblem(Path.Combine(workspace, "Other"), Path.Combine(parent, "Launcher"))?.Contains("하위 태스크 안에는", StringComparison.Ordinal) == true, "nothing goes into a sub-task");
            Assert(window.MoveIntoProblem(parent, Path.Combine(workspace, "Other"))?.Contains("하위 태스크가 있는", StringComparison.Ordinal) == true, "a task with sub-tasks stays on top");
            Assert(window.MoveIntoProblem(standalone, parent) is null, "a plain top task may go under another");

            // Into: asked, the folder and its files (and its plan) moved, a sub-task even without a project file.
            string plan = TerminalTabDocumentStore.GetProjectPlanPath(Key(standalone));
            Directory.CreateDirectory(Path.GetDirectoryName(plan)!);
            File.WriteAllText(plan, "# Notes plan");
            string? asked = null;
            window.ConfirmMoveOverride = question => { asked = question; return true; };
            await window.MoveIntoTaskAsync(standalone, parent);
            await Refresh(window);
            string moved = Path.Combine(parent, "Notes");
            Console.WriteLine("asked: " + asked?.Replace('\n', ' '));
            Assert(asked?.Contains("'Notes'을(를) 'i-luncher'의 하위 태스크로 넣을까요?", StringComparison.Ordinal) == true && asked.Contains("폴더와 파일이 옮겨집니다", StringComparison.Ordinal), "it asks first, saying the folder moves");
            Assert(!Directory.Exists(standalone) && File.ReadAllText(Path.Combine(moved, "memo.txt")) == "keep me", "the folder and its files moved into the parent's folder");
            Assert(File.Exists(TerminalTabDocumentStore.GetProjectPlanPath(Key(moved))), "its plan moved with it");
            Assert(Rows(window).TryGetValue(Key(moved), out var movedRow) && ReferenceEquals(Node(movedRow).Parent, Node(Rows(window)[Key(parent)])),
                "it is a sub-task of the parent now, even without a project file");
            Assert(window.MoveIntoProblem(Path.Combine(workspace, "Other"), parent) is null &&
                Directory.CreateDirectory(Path.Combine(workspace, "Notes")) is not null && window.MoveIntoProblem(Path.Combine(workspace, "Notes"), parent)?.Contains("같은 이름", StringComparison.Ordinal) == true,
                "a folder of the same name already there is refused");
            Directory.Delete(Path.Combine(workspace, "Notes"));
            await Refresh(window);

            // A "no" leaves everything as it is.
            window.ConfirmMoveOverride = _ => false;
            await window.MoveIntoTaskAsync(Path.Combine(workspace, "Other"), parent);
            Assert(Directory.Exists(Path.Combine(workspace, "Other")), "answered no, nothing moves");

            // Out again.
            window.ConfirmMoveOverride = _ => true;
            await window.MoveOutOfTaskAsync(moved);
            await Refresh(window);
            Assert(Directory.Exists(standalone) && !Directory.Exists(moved) && File.ReadAllText(Path.Combine(standalone, "memo.txt")) == "keep me", "꺼내기 moves it back to the top");
            Assert(Rows(window).TryGetValue(Key(standalone), out var backRow) && window.ParentTaskOf(standalone) is null && !(Node(backRow).Parent is TreeViewItem holder && holder.Tag is string),
                "a top task again");
        }
        finally
        {
            window.Close();
            await Task.Delay(300);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static async Task Refresh(MainWindow window)
    {
        var task = (Task<bool>)typeof(MainWindow).GetMethod("RefreshProjectTasksCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [true])!;
        await task;
        typeof(MainWindow).GetMethod("UpdateProjectTaskTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        await Idle(window);
    }

    private static Dictionary<string, object> Rows(MainWindow window)
    {
        var rows = typeof(MainWindow).GetField("_projectTaskRows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (System.Collections.IEnumerable)rows)
        {
            var key = (string)entry.GetType().GetProperty("Key")!.GetValue(entry)!;
            result[key] = entry.GetType().GetProperty("Value")!.GetValue(entry)!;
        }
        return result;
    }

    private static TreeViewItem Node(object row) => (TreeViewItem)row.GetType().GetProperty("Node")!.GetValue(row)!;

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static async Task Idle(Window window)
    {
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await Task.Delay(80);
    }

    private static async Task SaveImage(MainWindow window, string name)
    {
        await Idle(window);
        var tree = Field<TreeView>(window, "_taskTree");
        int width = (int)Math.Ceiling(tree.ActualWidth), height = (int)Math.Ceiling(Math.Min(tree.ActualHeight, 400));
        if (width == 0 || height == 0) { Console.WriteLine("not rendered"); return; }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)), null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(tree) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
        }
        bitmap.Render(visual);
        string folder = Path.Combine(Path.GetTempPath(), "TerminalNotes.SubTasks");
        Directory.CreateDirectory(folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, name));
        encoder.Save(stream);
        Console.WriteLine("PREVIEW " + Path.Combine(folder, name));
    }
}
