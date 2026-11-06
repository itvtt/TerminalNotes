using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TerminalNotes;

// 사용자 지시: 태스크 목록 안에서도 끌어다 놓아 순서를 바꾼다. The drag that places a task on a terminal slot also works on
// the list: dropped on another task row it lands above or below that row (a gold line shows where). The order is saved;
// tasks never moved keep coming after it by name, and with no saved order the list stays alphabetical.
public partial class MainWindow
{
    private readonly List<string> _taskOrder = [];
    private TaskDropLine? _taskDropLine;

    private void LoadTaskOrder(string[]? saved)
    {
        _taskOrder.Clear();
        foreach (string project in saved ?? [])
        {
            string path = CanonicalProjectPath(project);
            if (!_taskOrder.Any(known => DirectoriesEqual(known, path))) _taskOrder.Add(path);
        }
    }

    // The saved order first, then tasks never moved, by name.
    private IEnumerable<TreeViewItem> OrderTaskNodes(IEnumerable<TreeViewItem> nodes) =>
        nodes.OrderBy(node => TaskOrderRank((string)node.Tag!))
            .ThenBy(node => ProjectDisplayName((string)node.Tag!), StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => (string)node.Tag!, StringComparer.OrdinalIgnoreCase);

    // 사용자: "터미널에서 클릭하는거랑 태스크리스트랑 순서가 다르다?" — the menus that offer tasks (an empty terminal cell, plan mode's
    // task menu) list them in the task list's order.
    private IEnumerable<string> OrderLikeTaskList(IEnumerable<string> projects) =>
        projects.OrderBy(TaskOrderRank)
            .ThenBy(ProjectDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(project => project, StringComparer.OrdinalIgnoreCase);

    private int TaskOrderRank(string project)
    {
        int index = _taskOrder.FindIndex(path => DirectoriesEqual(path, project));
        return index < 0 ? int.MaxValue : index;
    }

    private void EnableTaskReorderDrop(Border surface, string project)
    {
        surface.AllowDrop = true;
        // The list group already marks task drags handled (it accepts them for itself), so listen to handled events too.
        surface.AddHandler(DragDrop.DragOverEvent, new DragEventHandler((_, e) =>
        {
            if (TaskReorderSource(e.Data, project) is null) { HideTaskDropLine(); return; }
            ShowTaskDropLine(surface, e.GetPosition(surface).Y > surface.ActualHeight / 2);
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }), handledEventsToo: true);
        surface.AddHandler(DragDrop.DragLeaveEvent, new DragEventHandler((_, _) => HideTaskDropLine()), handledEventsToo: true);
        surface.AddHandler(DragDrop.DropEvent, new DragEventHandler((_, e) =>
        {
            HideTaskDropLine();
            if (TaskReorderSource(e.Data, project) is not { } moved) return;
            MoveTaskInOrder(moved, project, e.GetPosition(surface).Y > surface.ActualHeight / 2);
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }), handledEventsToo: true);
    }

    private string? TaskReorderSource(IDataObject data, string target) =>
        !_planWorkMode && !_closing && !_closePending && _titleEditor is null &&
        data.GetDataPresent(TaskSlotDragFormat) && data.GetData(TaskSlotDragFormat) is string moved &&
        !DirectoriesEqual(moved, target) && _projectTaskRows.ContainsKey(CanonicalProjectPath(moved)) ? CanonicalProjectPath(moved) : null;

    /// <summary>Puts <paramref name="moved"/> just above (or below) <paramref name="target"/> and saves the order.</summary>
    internal void MoveTaskInOrder(string moved, string target, bool below)
    {
        if (_taskOrderGroup is null) return;
        moved = CanonicalProjectPath(moved);
        // The order as shown (it includes tasks never moved), with one task moved next to the target.
        var shown = _taskOrderGroup.Items.OfType<TreeViewItem>().Select(node => (string)node.Tag!)
            .Where(path => !DirectoriesEqual(path, moved)).ToList();
        int index = shown.FindIndex(path => DirectoriesEqual(path, target));
        if (index < 0) return;
        shown.Insert(below ? index + 1 : index, moved);
        // Tasks this tab does not show (archive, excluded) keep their saved places after the shown ones.
        var hidden = _taskOrder.Where(path => !shown.Any(shownPath => DirectoriesEqual(shownPath, path))).ToList();
        _taskOrder.Clear();
        _taskOrder.AddRange(shown);
        _taskOrder.AddRange(hidden);
        UpdateProjectTaskTree();
        SaveSettings();
        SetStatus($"{ProjectDisplayName(moved)} · 태스크 순서를 바꿨습니다.");
    }

    private void MoveTaskOrderEntry(string source, string destination)
    {
        int index = _taskOrder.FindIndex(path => DirectoriesEqual(path, source));
        if (index >= 0) _taskOrder[index] = CanonicalProjectPath(destination);
    }

    private void ForgetTaskOrderEntry(string project) => _taskOrder.RemoveAll(path => DirectoriesEqual(path, project));

    private void ShowTaskDropLine(Border surface, bool below)
    {
        if (_taskDropLine is { } shown && ReferenceEquals(shown.AdornedElement, surface)) { shown.SetBelow(below); return; }
        HideTaskDropLine();
        if (AdornerLayer.GetAdornerLayer(surface) is not { } layer) return;
        _taskDropLine = new TaskDropLine(surface, below);
        layer.Add(_taskDropLine);
    }

    private void HideTaskDropLine()
    {
        if (_taskDropLine is not { } line) return;
        _taskDropLine = null;
        AdornerLayer.GetAdornerLayer(line.AdornedElement)?.Remove(line);
    }

    // A gold line in the gap above or below the row where the dragged task will land.
    private sealed class TaskDropLine : Adorner
    {
        private static readonly Pen Line = CreatePen();
        private bool _below;

        internal TaskDropLine(UIElement adorned, bool below) : base(adorned)
        {
            _below = below;
            IsHitTestVisible = false;
        }

        internal bool Below => _below;

        internal void SetBelow(bool below)
        {
            if (_below == below) return;
            _below = below;
            InvalidateVisual();
        }

        private static Pen CreatePen()
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xE5, 0xC0, 0x7B)), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            return pen;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            double y = _below ? AdornedElement.RenderSize.Height + 3 : -3;
            drawingContext.DrawLine(Line, new Point(2, y), new Point(Math.Max(2, AdornedElement.RenderSize.Width - 2), y));
        }
    }
}
