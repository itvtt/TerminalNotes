using System.IO;
using System.Windows;
using System.Windows.Controls;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TerminalNotes.SubTasksTests")]

namespace TerminalNotes;

// 상위·하위 태스크 (2026-11-12 사용자: "아이런처 태스크를 만들고 그안에 프로젝트들을 넣어두면 각각의 프로젝트별 태스크는 또 관리를 할 수 있는
// 구조가 아닌데" → 상위·하위 태스크): the project folders inside a task's folder are tasks of their own, shown under it (▾/▸), each with its
// own plan, terminal, schedule and skills; the parent stays a task for work across them. A sub-project is a folder with its own git
// repository or a project file; build output and package folders never are. A parent's menu turns this off for it; a single sub-task
// is hidden with 목록에서 제외 like any task.
public partial class MainWindow
{
    private static readonly string[] SubProjectMarkers =
        ["*.sln", "*.slnx", "*.csproj", "*.fsproj", "*.vbproj", "*.vcxproj", "package.json", "pyproject.toml", "setup.py", "go.mod", "Cargo.toml", "pom.xml", "build.gradle", "build.gradle.kts"];
    internal static readonly string[] NeverTaskFolders = ["node_modules", "bin", "obj", "dist", "out", "packages", "target", "venv", ".venv", "__pycache__"];

    /// <summary>Sub-task → its parent task (both canonical), as last discovered.</summary>
    private Dictionary<string, string> _subTaskParents = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Parent tasks whose sub-projects are not shown as tasks.</summary>
    private readonly HashSet<string> _subTaskParentsOff = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Folders put under a parent task in the list: sub-tasks even without a git repository or a project file.</summary>
    private readonly HashSet<string> _subTaskFolders = new(StringComparer.OrdinalIgnoreCase);

    internal string? ParentTaskOf(string project) => _subTaskParents.GetValueOrDefault(CanonicalProjectPath(project));

    /// <summary>The project folders right inside a task folder (their own git repository or a project file).</summary>
    internal static IEnumerable<string> DiscoverSubProjects(string parent)
    {
        if (!Directory.Exists(parent)) yield break;
        IEnumerable<string> children;
        try { children = Directory.EnumerateDirectories(parent).ToList(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { yield break; }
        foreach (string child in children)
        {
            string name = Path.GetFileName(child);
            if (name.StartsWith('.') || name.Equals("terminal-notes", StringComparison.OrdinalIgnoreCase) ||
                NeverTaskFolders.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            bool project;
            try
            {
                if ((File.GetAttributes(child) & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                project = Directory.Exists(Path.Combine(child, ".git")) || File.Exists(Path.Combine(child, ".git")) ||
                    SubProjectMarkers.Any(marker => Directory.EnumerateFiles(child, marker, SearchOption.TopDirectoryOnly).Any());
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            if (project) yield return child;
        }
    }

    /// <summary>The sub-projects of the given task folders, child → parent (full paths).</summary>
    private static Dictionary<string, string> DiscoverSubTasks(IEnumerable<string> parents, IReadOnlySet<string> off, IReadOnlySet<string> placed)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string parent in parents)
        {
            string key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
            if (off.Contains(key)) continue;
            foreach (string child in DiscoverSubProjects(parent)) found[Path.TrimEndingDirectorySeparator(Path.GetFullPath(child))] = key;
            foreach (string child in placed.Where(folder => Directory.Exists(folder) &&
                         string.Equals(Path.GetDirectoryName(folder), key, StringComparison.OrdinalIgnoreCase)))
                found[child] = key;
        }
        return found;
    }

    // ── moving a task into a parent task, and out again ──

    /// <summary>Why a task cannot go under another (null: it can): only a top task without sub-tasks of its own, under another top
    /// task that has no folder of that name yet.</summary>
    internal string? MoveIntoProblem(string project, string parent)
    {
        project = CanonicalProjectPath(project);
        parent = CanonicalProjectPath(parent);
        if (DirectoriesEqual(project, parent)) return "같은 태스크입니다.";
        if (ParentTaskOf(parent) is not null) return "하위 태스크 안에는 넣을 수 없습니다 · 맨 위 태스크로 옮기세요.";
        if (ParentTaskOf(project) is not null) return "하위 태스크는 먼저 상위 태스크에서 꺼낸 뒤 옮기세요.";
        if (_subTaskParents.Values.Any(owner => DirectoriesEqual(owner, project))) return "하위 태스크가 있는 태스크는 다른 태스크 안에 넣을 수 없습니다.";
        if (IsArchivedProject(project) || IsArchivedProject(parent)) return "아카이브의 태스크는 옮길 수 없습니다.";
        if (!Directory.Exists(parent)) return "옮길 태스크 폴더를 찾을 수 없습니다.";
        if (Directory.Exists(Path.Combine(parent, Path.GetFileName(project))) || File.Exists(Path.Combine(parent, Path.GetFileName(project))))
            return $"{ProjectDisplayName(parent)} 안에 같은 이름({Path.GetFileName(project)})이 이미 있습니다.";
        return null;
    }

    /// <summary>Replaces the confirmation (tests): the question, its answer.</summary>
    internal Func<string, bool>? ConfirmMoveOverride { get; set; }

    /// <summary>Puts a task under another (its folder and files go into the other's folder), after asking.</summary>
    internal async Task MoveIntoTaskAsync(string project, string parent)
    {
        project = CanonicalProjectPath(project);
        parent = CanonicalProjectPath(parent);
        if (MoveIntoProblem(project, parent) is { } problem) { SetAttentionStatus(problem); return; }
        string name = ProjectDisplayName(project), parentName = ProjectDisplayName(parent);
        string destination = Path.Combine(parent, Path.GetFileName(project));
        string question = $"'{name}'을(를) '{parentName}'의 하위 태스크로 넣을까요?\n\n프로젝트 폴더와 파일이 옮겨집니다.\n{project}\n→ {destination}\n\n열려 있는 이 태스크의 터미널은 닫힙니다.";
        if (!(ConfirmMoveOverride?.Invoke(question) ?? AppMessageDialog.Confirm(this, "하위 태스크로 넣기", question, "옮기기"))) return;
        _subTaskParentsOff.Remove(parent);
        await MoveProjectFolderAsync(project, () => destination,
            (_, _) => Task.Run(() => { Directory.Move(project, destination); return destination; }),
            (_, moved) =>
            {
                _subTaskFolders.Add(CanonicalProjectPath(moved));
                return () =>
                {
                    if (_projectTaskRows.TryGetValue(parent, out var row)) row.Node.IsExpanded = true;
                    SetStatus($"{name} · {parentName}의 하위 태스크로 옮겼습니다.");
                };
            });
    }

    /// <summary>Takes a sub-task out to the top of the workspace (its folder moves there), after asking.</summary>
    internal async Task MoveOutOfTaskAsync(string project)
    {
        project = CanonicalProjectPath(project);
        if (ParentTaskOf(project) is not { } parent) return;
        string name = ProjectDisplayName(project), parentName = ProjectDisplayName(parent);
        string destination = Path.Combine(CanonicalProjectPath(_workspace), Path.GetFileName(project));
        if (Directory.Exists(destination) || File.Exists(destination)) { SetAttentionStatus($"작업 폴더에 같은 이름({Path.GetFileName(project)})이 이미 있습니다."); return; }
        string question = $"'{name}'을(를) '{parentName}'에서 꺼내 따로 된 태스크로 만들까요?\n\n프로젝트 폴더와 파일이 옮겨집니다.\n{project}\n→ {destination}\n\n열려 있는 이 태스크의 터미널은 닫힙니다.";
        if (!(ConfirmMoveOverride?.Invoke(question) ?? AppMessageDialog.Confirm(this, "상위 태스크에서 꺼내기", question, "옮기기"))) return;
        await MoveProjectFolderAsync(project, () => destination,
            (_, _) => Task.Run(() => { Directory.Move(project, destination); return destination; }),
            (_, _) =>
            {
                _subTaskFolders.Remove(project);
                return () => SetStatus($"{name} · {parentName}에서 꺼내 따로 된 태스크로 만들었습니다.");
            });
    }

    /// <summary>A sub-task's node goes under its parent's (when both are shown); a node with sub-tasks gets the ▾/▸ look.</summary>
    private bool PlaceUnderParentTask(string project, TreeViewItem node)
    {
        if (ParentTaskOf(project) is not { } parent || !_projectTaskRows.TryGetValue(parent, out var parentRow) ||
            !IsVisibleTaskProject(project) || !IsVisibleTaskProject(parent)) return false;
        var host = parentRow.Node;
        if (!ReferenceEquals(node.Parent, host))
        {
            DetachProjectTaskNode(node);
            host.Items.Add(node);
        }
        UpdateParentTaskLook(host);
        return true;
    }

    private static void UpdateParentTaskLook(TreeViewItem node)
    {
        bool parent = node.Items.Count > 0;
        if (parent) node.SetResourceReference(StyleProperty, "TaskTreeParent");
        System.Windows.Automation.AutomationProperties.SetHelpText(node, parent ? $"하위 태스크 {node.Items.Count}개" : "");
    }

    /// <summary>A node taken out of a parent task's node (it no longer is a sub-task, or it is hidden).</summary>
    private bool DetachFromParentTask(TreeViewItem node)
    {
        if (node.Parent is not TreeViewItem owner || _taskCategoryGroups.ContainsValue(owner) || !owner.Items.Contains(node)) return false;
        owner.Items.Remove(node);
        UpdateParentTaskLook(owner);
        return true;
    }

    /// <summary>Sub-tasks in the saved order, then by name, under each parent; the rows' look: a parent with ▾/▸, its sub-tasks one step
    /// in, and (only when some task has sub-tasks) the other top tasks on the parents' left edge. Without sub-tasks the list looks as
    /// it always did.</summary>
    private void OrderSubTaskNodes()
    {
        bool anyParent = _projectTaskRows.Values.Any(row => row.Node.Items.Count > 0);
        foreach (var (_, row) in _projectTaskRows)
        {
            var node = row.Node;
            if (node.Items.Count > 0)
            {
                node.SetResourceReference(StyleProperty, "TaskTreeParent");
                var ordered = OrderTaskNodes(node.Items.OfType<TreeViewItem>()).ToArray();
                for (int i = 0; i < ordered.Length; i++) MoveTaskNodeIntoPosition(node, ordered[i], i);
            }
            else node.SetResourceReference(StyleProperty, anyParent && node.Parent is not TreeViewItem { Tag: string } ? "TaskTreeTop" : "TaskTreeLeaf");
        }
    }

    /// <summary>The parent's menu: whether its sub-projects are tasks (shown only for a folder that has some).</summary>
    private MenuItem CreateSubTaskMenuItem(string project)
    {
        var item = new MenuItem { Header = "하위 프로젝트를 태스크로 보기", IsCheckable = true,
            ToolTip = "이 폴더 안의 프로젝트(git 저장소나 프로젝트 파일이 있는 폴더)를 각각 하위 태스크로 보여 줍니다." };
        item.Click += async (_, e) =>
        {
            e.Handled = true;
            string key = CanonicalProjectPath(project);
            if (item.IsChecked) _subTaskParentsOff.Remove(key); else _subTaskParentsOff.Add(key);
            SaveSettings();
            await RefreshProjectTasksCoreAsync(true);
            SetStatus($"{ProjectDisplayName(project)} · 하위 프로젝트를 " + (item.IsChecked ? "태스크로 보여 줍니다." : "태스크로 보지 않습니다."));
        };
        return item;
    }

    private void UpdateSubTaskMenuItem(string project, MenuItem item)
    {
        bool isSub = ParentTaskOf(project) is not null;
        bool hasSubs = !isSub && DiscoverSubProjects(project).Any();
        item.Visibility = hasSubs ? Visibility.Visible : Visibility.Collapsed;
        item.IsChecked = !_subTaskParentsOff.Contains(CanonicalProjectPath(project));
    }
}
