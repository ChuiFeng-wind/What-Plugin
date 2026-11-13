using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>
/// 混音笔记树的多选：点“多选”后逐行勾选，或者按住 Ctrl 点击加选或取消，Shift 点击连选，Ctrl+A 全选，Esc 取消。
/// 多选时中间显示批量操作（移动、加标签、去标签、删除），也可以把选中的一起拖到文件夹上。
/// 选中状态只存在于当前这棵树上，树重建（改动之后）就清空。
/// </summary>
public partial class MainWindow
{
    private NoteNode? _markAnchor;
    private bool _clearMarksOnUp;

    private List<NoteNode> MarkedNodes => AllNoteNodes(_noteRoots).Where(n => n.IsMarked).ToList();

    /// <summary>当前能看到的节点，从上到下（折叠起来的文件夹里面的不算）。</summary>
    private static IEnumerable<NoteNode> VisibleNoteNodes(IEnumerable<NoteNode> nodes) =>
        nodes.SelectMany(n => n.IsExpanded ? VisibleNoteNodes(n.Children).Prepend(n) : [n]);

    private bool MultiMode => NoteMultiToggle.IsChecked == true;

    /// <summary>
    /// 处理多选的点击：Ctrl、Shift 点击，或者打开了“多选”以后的普通点击。
    /// 返回 true 表示这次点击已经用来多选，不再改变当前显示的那一项。
    /// </summary>
    private bool HandleMarkClick(MouseButtonEventArgs e)
    {
        // 展开箭头、勾选框、改名输入框各管各的。
        if (IsInside<ToggleButton>(e.OriginalSource) || IsInside<System.Windows.Controls.TextBox>(e.OriginalSource))
            return false;
        var node = NodeAt(e.OriginalSource);
        var modifiers = Keyboard.Modifiers;
        var keys = modifiers is ModifierKeys.Control or ModifierKeys.Shift;
        if (node == null || !keys && !MultiMode)
        {
            // 普通点击：点在已选中的一项上可能是要拖动，松开时再清空；点别处直接清空。
            if (!keys && MarkedNodes.Count > 0)
            {
                if (node is { IsMarked: true })
                    _clearMarksOnUp = true;
                else
                    ClearMarks();
            }
            return false;
        }

        // 第一次用 Ctrl、Shift 加选时，把正在显示的那一项也算进去。
        if (keys && MarkedNodes.Count == 0 && SelectedNode is { } primary && !ReferenceEquals(primary, node))
        {
            primary.IsMarked = true;
            _markAnchor = primary;
        }

        if (modifiers == ModifierKeys.Shift)
        {
            var visible = VisibleNoteNodes(_noteRoots).ToList();
            var from = visible.IndexOf(_markAnchor ?? SelectedNode ?? node);
            var to = visible.IndexOf(node);
            if (from < 0)
                from = to;
            foreach (var between in visible.Skip(Math.Min(from, to)).Take(Math.Abs(to - from) + 1))
                between.IsMarked = true;
        }
        else if (node.IsMarked)
        {
            // 已经选中的：可能是要把选中的一起拖走，松开时没拖动才取消它。
            _toggleOnUp = node;
            _dragNode = node;
            return true;
        }
        else
        {
            node.IsMarked = true;
            _markAnchor = node;
        }
        ShowBatchPanel();
        return true;
    }

    private NoteNode? _toggleOnUp;

    private void NoteTree_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_toggleOnUp is { } node)
        {
            _toggleOnUp = null;
            node.IsMarked = false;
            _markAnchor = node;
            ShowBatchPanel();
        }
        if (!_clearMarksOnUp)
            return;
        _clearMarksOnUp = false;
        ClearMarks();
    }

    /// <summary>
    /// 勾选框的状态变了（鼠标点、键盘或辅助功能切换都会到这里），刷新中间的面板。
    /// 用状态事件而不是 Click：辅助功能切换时不会触发 Click。
    /// </summary>
    private void NoteMark_Changed(object sender, RoutedEventArgs e)
    {
        if (!_buildingNoteTree)
            ShowBatchPanel();
    }

    /// <summary>“多选”开关：打开后每一行前面出现勾选框，点一行就是选中或取消；关掉时清空选择。</summary>
    private void NoteMulti_Changed(object sender, RoutedEventArgs e)
    {
        ApplyMultiMode();
        if (!MultiMode)
            ClearMarks();
    }

    private void ApplyMultiMode()
    {
        var show = MultiMode;
        foreach (var node in AllNoteNodes(_noteRoots))
            node.ShowMarkBox = show;
    }

    private void ClearMarks()
    {
        foreach (var node in MarkedNodes)
            node.IsMarked = false;
        _markAnchor = null;
        _clearMarksOnUp = false;
        _toggleOnUp = null;
        ShowBatchPanel();
    }

    private void ClearMarks_Click(object sender, RoutedEventArgs e) => ClearMarks();

    private void MarkAllNotes()
    {
        foreach (var node in VisibleNoteNodes(_noteRoots).Where(n => !n.IsFolder))
            node.IsMarked = true;
        ShowBatchPanel();
    }

    /// <summary>多选的内容。选中了文件夹又选中它里面的东西时，只算最外面的那个文件夹。</summary>
    private NoteBatch CurrentBatch()
    {
        var notes = new List<PluginChain>();
        var folders = new List<NoteFolder>();
        void Walk(IEnumerable<NoteNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (!node.IsMarked)
                {
                    Walk(node.Children);
                    continue;
                }
                if (node.IsFolder)
                {
                    if (_noteFolders.FirstOrDefault(f => f.Id == node.Id) is { } folder)
                        folders.Add(folder);
                }
                else if (_chains.FirstOrDefault(c => c.Id == node.Id) is { } chain)
                    notes.Add(chain);
            }
        }
        Walk(_noteRoots);
        var inside = folders.SelectMany(f => MixNotes.Subtree(f.Id, _noteFolders)).ToHashSet();
        var contained = _chains.Where(c => c.FolderId is { } f && inside.Contains(f)).ToList();
        return new NoteBatch(notes, folders, contained, inside);
    }

    /// <summary>有多选时，中间换成批量操作面板。</summary>
    private void ShowBatchPanel()
    {
        var marked = MarkedNodes;
        if (marked.Count == 0)
        {
            if (BatchDetail.Visibility == Visibility.Visible)
            {
                BatchDetail.Visibility = Visibility.Collapsed;
                ChainDetail.Visibility = _shownChain != null ? Visibility.Visible : Visibility.Collapsed;
                FolderDetail.Visibility = _shownFolder != null ? Visibility.Visible : Visibility.Collapsed;
                ChainEmpty.Visibility = _shownChain == null && _shownFolder == null ? Visibility.Visible : Visibility.Collapsed;
            }
            return;
        }

        var batch = CurrentBatch();
        BatchDetail.Visibility = Visibility.Visible;
        ChainDetail.Visibility = Visibility.Collapsed;
        FolderDetail.Visibility = Visibility.Collapsed;
        ChainEmpty.Visibility = Visibility.Collapsed;
        NoteBatchTitle.Text = "已选中 " + batch.Describe();
        BatchMeta.Text = batch.Folders.Count > 0
            ? $"选中的文件夹里一共有 {batch.Contained.Count} 条笔记。加标签、去标签对这些笔记也生效。"
            : "点左边的勾选框，或按住 Ctrl 点击加选、取消，Shift 点击连选，Ctrl+A 全选，Esc 取消选择。选中的可以一起拖到文件夹上。";
        BatchList.ItemsSource = batch.Folders
            .Select(f => new BatchRow("", f.Name, FolderPathText(f.ParentId)))
            .Concat(batch.Notes.Select(c => new BatchRow("", c.Name, FolderPathText(c.FolderId))))
            .ToList();
        BatchTagRemoveButton.IsEnabled = batch.TagTargets.Any(c => c.TagList.Count > 0);
        BatchTagAddButton.IsEnabled = batch.TagTargets.Count > 0;
    }

    // ---------- 批量操作 ----------

    private void BatchMove_Click(object sender, RoutedEventArgs e)
    {
        var batch = CurrentBatch();
        if (batch.IsEmpty || !PickFolder("移动 " + batch.Describe(), null, batch.Inside, out var target))
            return;
        MoveBatch(batch, target);
    }

    private void MoveBatch(NoteBatch batch, long? target)
    {
        FlushChain();
        RememberExpansion();
        var moved = 0;
        var failed = new List<string>();
        foreach (var folder in batch.Folders.Where(f => f.ParentId != target))
        {
            try
            {
                _database.MoveNoteFolder(folder.Id, target);
                moved++;
            }
            catch (ArgumentException)
            {
                failed.Add(folder.Name);
            }
        }
        foreach (var chain in batch.Notes.Where(c => c.FolderId != target))
        {
            try
            {
                _database.MoveChain(chain.Id, target);
                moved++;
            }
            catch (ArgumentException)
            {
                failed.Add(chain.Name);
            }
        }
        if (target is { } id)
            _collapsedNoteFolders.Remove(id);
        LoadChains(selectFolder: target);
        StatusText.Text = $"已把 {moved} 项移到“{FolderPathText(target)}”"
            + (failed.Count > 0 ? $"；{failed.Count} 项没有移动，那里已经有同名的：{string.Join("、", failed.Take(5))}" + (failed.Count > 5 ? " 等" : "") : "");
    }

    private void NoteBatchDelete_Click(object sender, RoutedEventArgs e) => DeleteBatch();

    private void DeleteBatch()
    {
        var batch = CurrentBatch();
        if (batch.IsEmpty)
            return;
        var withNotes = false;
        bool confirmed;
        if (batch.Folders.Count == 0)
        {
            confirmed = Dialogs.Confirm(this, "删除笔记",
                $"删除选中的 {batch.Notes.Count} 条笔记？里面的步骤、备选和思路会一起删除，不能恢复。插件不受影响。", "删除", danger: true);
        }
        else
        {
            var message = $"删除 {batch.Describe()}？"
                + (batch.Notes.Count > 0 ? $"选中的 {batch.Notes.Count} 条笔记会被删除，不能恢复。" : "")
                + (batch.Contained.Count > 0 ? $"文件夹里的 {batch.Contained.Count} 条笔记默认移到上一层，不会删除。" : "");
            confirmed = batch.Contained.Count > 0
                ? Dialogs.Confirm(this, "批量删除", message, "删除", $"同时删除文件夹里的 {batch.Contained.Count} 条笔记（不能恢复）", ref withNotes)
                : Dialogs.Confirm(this, "批量删除", message, "删除", danger: true);
        }
        if (!confirmed)
            return;

        // 取消等待中的保存，否则会往已删除的笔记写。
        if (_shownChain is { } shown && (batch.Notes.Any(c => c.Id == shown.Id) || withNotes && batch.Contained.Any(c => c.Id == shown.Id)))
        {
            _chainSaveTimer.Stop();
            _shownChain = null;
        }
        else
            FlushChain();
        RememberExpansion();
        var deleted = _database.DeleteChains(batch.Notes.Select(c => c.Id));
        foreach (var folder in batch.Folders)
        {
            if (withNotes)
                deleted += _database.DeleteNoteFolderWithNotes(folder.Id);
            else
                _database.DeleteNoteFolder(folder.Id);
            _collapsedNoteFolders.Remove(folder.Id);
        }
        _shownFolder = null;
        LoadChains();
        StatusText.Text = $"已删除 {deleted} 条笔记" + (batch.Folders.Count > 0 ? $"、{batch.Folders.Count} 个文件夹" : "")
            + (batch.Folders.Count > 0 && !withNotes && batch.Contained.Count > 0 ? "，文件夹里的笔记移到了上一层" : "");
    }

    private void BatchAddTags_Click(object sender, RoutedEventArgs e)
    {
        var batch = CurrentBatch();
        var targets = batch.TagTargets;
        if (targets.Count == 0)
            return;
        var known = MixNotes.PresetTags
            .Concat(_chains.SelectMany(c => c.TagList))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var picked = Dialogs.PickMany(this, "加标签", $"给 {targets.Count} 条笔记加上勾选的标签。已经有的不会重复。",
            known.Select(t => (t, false)).ToList(), "加上");
        if (picked == null)
            return;
        var add = picked.Select(i => known[i]).ToList();
        ApplyTags(targets, tags => tags.Concat(add), $"已给 {targets.Count} 条笔记加上标签：{string.Join("、", add)}");
    }

    private void BatchRemoveTags_Click(object sender, RoutedEventArgs e)
    {
        var batch = CurrentBatch();
        var targets = batch.TagTargets;
        var present = targets.SelectMany(c => c.TagList).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (present.Count == 0)
            return;
        var picked = Dialogs.PickMany(this, "去掉标签", $"从 {targets.Count} 条笔记上去掉勾选的标签。",
            present.Select(t => (t, false)).ToList(), "去掉");
        if (picked == null)
            return;
        var remove = picked.Select(i => present[i]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ApplyTags(targets, tags => tags.Where(t => !remove.Contains(t)), $"已从 {targets.Count} 条笔记上去掉标签：{string.Join("、", remove)}");
    }

    private void ApplyTags(IReadOnlyList<PluginChain> targets, Func<IEnumerable<string>, IEnumerable<string>> change, string done)
    {
        FlushChain();
        RememberExpansion();
        try
        {
            foreach (var chain in targets)
                _database.SetChainTags(chain.Id, change(chain.TagList).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusText.Text = "保存失败：" + error.Message;
        }
        LoadChains();
        StatusText.Text = done;
    }
}

/// <summary>多选的内容：直接选中的笔记和文件夹，以及文件夹（含子文件夹）里面的笔记。</summary>
public sealed record NoteBatch(List<PluginChain> Notes, List<NoteFolder> Folders, List<PluginChain> Contained, HashSet<long> Inside)
{
    public bool IsEmpty => Notes.Count + Folders.Count == 0;

    /// <summary>加标签、去标签的对象：选中的笔记，加上选中的文件夹里面的笔记。</summary>
    public IReadOnlyList<PluginChain> TagTargets => Notes.Concat(Contained).DistinctBy(c => c.Id).ToList();

    public string Describe() =>
        string.Join("、", new[] { Notes.Count > 0 ? $"{Notes.Count} 条笔记" : "", Folders.Count > 0 ? $"{Folders.Count} 个文件夹" : "" }.Where(s => s.Length > 0));
}

public sealed record BatchRow(string Icon, string Name, string Path);
