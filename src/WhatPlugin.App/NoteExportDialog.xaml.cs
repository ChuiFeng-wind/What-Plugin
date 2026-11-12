using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>导出弹窗里的一行：文件夹或笔记。文件夹的勾选状态由里面的笔记决定，勾它就勾上里面所有笔记。</summary>
public sealed class NotePickRow : INotifyPropertyChanged
{
    private bool _selected = true;
    private bool? _folderState = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public NotePickRow(PluginChain chain, int depth, string path)
    {
        Chain = chain;
        Depth = depth;
        Name = chain.Name;
        Path = path.Length > 0 ? "  " + path : "";
        Detail = chain.Steps.Count > 0 ? $"{chain.Steps.Count} 步" : "空笔记";
        Search = string.Join("\n", new[] { chain.Name, path }
            .Concat(chain.TagList)
            .Concat(chain.Steps.SelectMany(s => s.AllPicks).Select(p => p.PluginName)));
    }

    public NotePickRow(NoteFolder folder, int depth, IReadOnlySet<long> subtree)
    {
        Folder = folder;
        Subtree = subtree;
        Depth = depth;
        Name = folder.Name;
        Path = "";
        Detail = "";
        Search = "";
    }

    public PluginChain? Chain { get; }
    public NoteFolder? Folder { get; }
    public IReadOnlySet<long> Subtree { get; } = new HashSet<long>();
    public bool IsFolder => Folder != null;
    public int Depth { get; }
    public string Name { get; }
    public string Path { get; }
    public string Search { get; }
    public string Detail { get; private set; }
    public string Glyph => IsFolder ? " " : "";
    public FontWeight Weight => IsFolder ? FontWeights.SemiBold : FontWeights.Normal;
    public Thickness Indent
    {
        get => _indent;
        set
        {
            _indent = value;
            Notify(nameof(Indent));
        }
    }

    private Thickness _indent;

    /// <summary>文件夹：全选为 true，全不选为 false，部分为 null。笔记：是否选中。</summary>
    public bool? Selected
    {
        get => IsFolder ? _folderState : _selected;
        set
        {
            if (IsFolder)
            {
                FolderToggled?.Invoke(this, value == true);
                return;
            }
            var next = value == true;
            if (_selected == next)
                return;
            _selected = next;
            Notify(nameof(Selected));
        }
    }

    public bool IsNoteSelected => !IsFolder && _selected;

    /// <summary>文件夹被点了：true 表示勾上里面所有笔记。</summary>
    public event Action<NotePickRow, bool>? FolderToggled;

    public void SetFolderState(bool? state, int selected, int total)
    {
        Detail = total > 0 ? $"{selected}/{total}" : "空";
        Notify(nameof(Detail));
        // 总是通知：空文件夹被点了以后，勾选框要退回原来的状态。
        _folderState = state;
        Notify(nameof(Selected));
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>选择要分享的混音笔记。默认全选，按文件夹层级列出。</summary>
public partial class NoteExportDialog
{
    private readonly List<NotePickRow> _rows = [];
    private readonly ListCollectionView _view;
    private readonly bool _ready;

    public NoteExportDialog(IReadOnlyList<PluginChain> chains, IReadOnlyList<NoteFolder> folders, string author)
    {
        InitializeComponent();
        WindowEffects.ApplyDialog(this);

        void Add(long? parent, int depth)
        {
            foreach (var folder in folders.Where(f => f.ParentId == parent).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var row = new NotePickRow(folder, depth, MixNotes.Subtree(folder.Id, folders));
                row.FolderToggled += Folder_Toggled;
                _rows.Add(row);
                Add(folder.Id, depth + 1);
            }
            foreach (var chain in chains.Where(c => c.FolderId == parent).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
                _rows.Add(new NotePickRow(chain, depth, string.Join(" › ", MixNotes.PathOf(parent, folders))));
        }
        Add(null, 0);
        // 文件夹找不到的笔记（数据异常）也列出来，放在最后。
        var listed = _rows.Where(r => r.Chain != null).Select(r => r.Chain!.Id).ToHashSet();
        foreach (var chain in chains.Where(c => !listed.Contains(c.Id)))
            _rows.Add(new NotePickRow(chain, 0, ""));

        _view = new ListCollectionView(_rows) { Filter = Matches };
        PickList.ItemsSource = _view;
        AuthorBox.Text = author;
        _ready = true;
        ApplyIndent();
        Refresh();
    }

    public string Author => AuthorBox.Text.Trim();

    public bool IncludeRouting => IncludeRoutes.IsChecked == true;

    public IReadOnlyList<PluginChain> SelectedChains => _rows.Where(r => r.IsNoteSelected).Select(r => r.Chain!).ToList();

    private bool Searching => SearchBox.Text.Trim().Length > 0;

    private bool Matches(object item)
    {
        var row = (NotePickRow)item;
        var search = SearchBox.Text.Trim();
        if (search.Length == 0)
            return true;
        return !row.IsFolder && row.Search.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<NotePickRow> VisibleNotes => _view.Cast<NotePickRow>().Where(r => !r.IsFolder);

    private void ApplyIndent()
    {
        // 搜索时只列笔记，不缩进，后面显示所在文件夹。
        foreach (var row in _rows)
            row.Indent = new Thickness(Searching ? 0 : row.Depth * 18, 0, 0, 0);
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready)
            return;
        ApplyIndent();
        _view.Refresh();
    }

    private void Folder_Toggled(NotePickRow folder, bool select)
    {
        foreach (var row in _rows.Where(r => r.Chain is { FolderId: { } f } && folder.Subtree.Contains(f)))
            row.Selected = select;
        Refresh();
    }

    private void PickAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in VisibleNotes)
            row.Selected = true;
        Refresh();
    }

    private void PickNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in VisibleNotes)
            row.Selected = false;
        Refresh();
    }

    private void PickInvert_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in VisibleNotes.ToList())
            row.Selected = !row.IsNoteSelected;
        Refresh();
    }

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        if (_ready)
            Refresh();
    }

    private void Refresh()
    {
        foreach (var folder in _rows.Where(r => r.IsFolder))
        {
            var notes = _rows.Where(r => r.Chain is { FolderId: { } f } && folder.Subtree.Contains(f)).ToList();
            var selected = notes.Count(r => r.IsNoteSelected);
            folder.SetFolderState(notes.Count == 0 ? false : selected == notes.Count ? true : selected == 0 ? false : null, selected, notes.Count);
        }
        var count = _rows.Count(r => r.IsNoteSelected);
        SummaryText.Text = count > 0 ? $"已选 {count} 条笔记" : "还没有选择";
        ExportButton.IsEnabled = count > 0;
        ExportButton.Content = count > 0 ? $"导出 {count} 条" : "导出";
    }

    private void Export_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
