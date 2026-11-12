using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace WhatPlugin.App;

/// <summary>工程页和混音笔记树的右键菜单，以及笔记树上直接改名。</summary>
public partial class MainWindow
{
    private static T? AncestorOf<T>(object source) where T : DependencyObject
    {
        for (var node = source as DependencyObject; node != null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is T found)
                return found;
        }
        return null;
    }

    private static MenuItem MenuEntry(string header, string glyph, Action action, string gesture = "")
    {
        var icon = new TextBlock { Text = glyph, FontSize = 13 };
        icon.SetResourceReference(StyleProperty, "Icon");
        var item = new MenuItem { Header = header, Icon = icon, InputGestureText = gesture };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>在 target 上弹出菜单。用键盘（菜单键、Shift+F10）打开时放在条目下方，用鼠标时放在指针处。</summary>
    private static void OpenMenu(FrameworkElement target, ContextMenuEventArgs e, params Control[] items)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = target,
            Placement = e.CursorLeft < 0 ? PlacementMode.Bottom : PlacementMode.MousePoint,
        };
        foreach (var item in items)
            menu.Items.Add(item);
        menu.IsOpen = true;
        e.Handled = true;
    }

    // ---------- 工程页 ----------

    private void ProjectFolderTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // 只弹菜单，不切换左边选中的文件夹，右边的列表保持不变。
        if (AncestorOf<TreeViewItem>(e.OriginalSource) is not { DataContext: ProjectFolderViewModel { Path: { } path } } item)
        {
            e.Handled = true;
            return;
        }
        OpenMenu(item, e, MenuEntry("打开所在文件夹", "", () => OpenFolder(path)));
    }

    private void ProjectGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (AncestorOf<DataGridRow>(e.OriginalSource) is not { Item: ProjectViewModel project } row)
        {
            e.Handled = true;
            return;
        }
        ProjectGrid.SelectedItem = project;
        OpenMenu(row, e, MenuEntry("打开所在文件夹", "", () => RevealFile(project.Entry.Path)));
    }

    /// <summary>在资源管理器里打开文件夹。只打开窗口，不改动任何文件。</summary>
    private void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            StatusText.Text = $"找不到文件夹：{path}";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception error) when (error is Win32Exception or IOException)
        {
            StatusText.Text = "无法打开文件夹：" + error.Message;
        }
    }

    /// <summary>在资源管理器里选中文件，不启动宿主，避免工程被意外打开。文件不在了就打开它原来所在的文件夹。</summary>
    private void RevealFile(string path)
    {
        if (!File.Exists(path))
        {
            if (Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder))
            {
                OpenFolder(folder);
                StatusText.Text = $"工程文件已经不在了，打开了它原来所在的文件夹：{folder}";
            }
            else
            {
                StatusText.Text = $"找不到工程文件：{path}";
            }
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception error) when (error is Win32Exception or IOException)
        {
            StatusText.Text = "无法打开文件夹：" + error.Message;
        }
    }

    // ---------- 混音笔记树 ----------

    private void NoteTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (AncestorOf<TreeViewItem>(e.OriginalSource) is not { DataContext: NoteNode node } item || node.IsEditing)
        {
            e.Handled = true;
            return;
        }
        // 先选中，右边显示的就是这一项，移动和删除都针对它。
        item.IsSelected = true;
        if (node.IsFolder)
            OpenMenu(item, e,
                MenuEntry("重命名", "", () => BeginRename(node), "F2"),
                MenuEntry("移动到…", "", () => MoveFolder_Click(item, new RoutedEventArgs())),
                new Separator(),
                MenuEntry("删除文件夹", "", () => DeleteFolder_Click(item, new RoutedEventArgs())));
        else
            OpenMenu(item, e,
                MenuEntry("重命名", "", () => BeginRename(node), "F2"),
                MenuEntry("移动到…", "", () => MoveChain_Click(item, new RoutedEventArgs())),
                new Separator(),
                MenuEntry("删除笔记", "", () => DeleteChain_Click(item, new RoutedEventArgs())));
    }

    private void NoteTree_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && SelectedNode is { IsEditing: false } node && !IsInside<TextBox>(e.OriginalSource))
        {
            BeginRename(node);
            e.Handled = true;
        }
    }

    private void NoteRename_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not NoteNode node)
            return;
        if (AncestorOf<TreeViewItem>(sender) is { } item)
            item.IsSelected = true;
        BeginRename(node);
    }

    private void BeginRename(NoteNode node)
    {
        foreach (var other in AllNoteNodes(_noteRoots).Where(n => n.IsEditing && !ReferenceEquals(n, node)))
            other.IsEditing = false;
        node.IsEditing = true;
    }

    private static IEnumerable<NoteNode> AllNoteNodes(IEnumerable<NoteNode> nodes) =>
        nodes.SelectMany(n => AllNoteNodes(n.Children).Prepend(n));

    /// <summary>输入框出现时获得焦点并全选，直接打字就是新名字。</summary>
    private void NoteRenameBox_VisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox box && box.IsVisible)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                box.Focus();
                box.SelectAll();
            });
    }

    private void NoteRenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not NoteNode node)
            return;
        if (e.Key == Key.Enter)
        {
            EndRename(node, save: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndRename(node, save: false);
            e.Handled = true;
        }
    }

    private void NoteRenameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is NoteNode node)
            EndRename(node, save: true);
    }

    /// <summary>名字为空或没变就不保存。保存会重建树，所以等焦点切换结束后再做。</summary>
    private void EndRename(NoteNode node, bool save)
    {
        if (!node.IsEditing)
            return;
        var name = node.EditText.Trim();
        node.IsEditing = false;
        if (!save || name.Length == 0 || name == node.Name)
        {
            if (save && name.Length == 0)
                StatusText.Text = "名称不能为空，没有改名";
            NoteTree.Focus();
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (node.IsFolder)
                RenameFolder(node.Id, name);
            else
                RenameNote(node.Id, name);
        });
    }
}
