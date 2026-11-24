using System.Collections.ObjectModel;
using System.ComponentModel;
using Material.Icons;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void NotifyAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>左边树上的一项：文件夹或笔记。</summary>
public sealed class NoteItem : Observable
{
    private bool _isExpanded;
    private bool _isEditing;
    private bool _showMarkBox;
    private string _editText = "";

    public NoteItem(NoteTreeEntry entry, bool expanded)
    {
        Entry = entry;
        _isExpanded = expanded;
        Children = entry.Children.Select(c => new NoteItem(c, expanded)).ToList();
    }

    public NoteTreeEntry Entry { get; private set; }
    public bool IsFolder => Entry.IsFolder;
    public long Id => Entry.Id;
    public string Name => Entry.Name;
    public string Count => Entry.Count;
    public string ToolTip => Entry.ToolTip;
    public ChannelRole Role => Entry.Role;
    public List<NoteItem> Children { get; }

    public MaterialIconKind Icon => IsFolder
        ? (IsExpanded ? MaterialIconKind.FolderOpenOutline : MaterialIconKind.FolderOutline)
        : Role == ChannelRole.Track ? MaterialIconKind.FileDocumentOutline : RouteMapView.RoleIcon(Role);

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            Notify(nameof(IsExpanded));
            Notify(nameof(Icon));
        }
    }

    /// <summary>正在树上直接改名。</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value)
                return;
            _isEditing = value;
            if (value)
                EditText = Name;
            Notify(nameof(IsEditing));
            Notify(nameof(IsShowingName));
        }
    }

    public bool IsShowingName => !_isEditing;

    public string EditText
    {
        get => _editText;
        set
        {
            _editText = value;
            Notify(nameof(EditText));
        }
    }

    /// <summary>打开“多选”时每一行前面显示勾选框。</summary>
    public bool ShowMarkBox
    {
        get => _showMarkBox;
        set
        {
            if (_showMarkBox == value)
                return;
            _showMarkBox = value;
            Notify(nameof(ShowMarkBox));
        }
    }

    /// <summary>笔记改了标签、步骤或类型时更新这一行，不重建树。</summary>
    public void Update(PluginChain chain)
    {
        Entry = NoteListing.Note(chain);
        NotifyAll();
    }

    public override string ToString() => Name;
}

/// <summary>一款插件：某一步的首选或备选。改说明时通知保存，不重建列表，否则输入框会失去焦点。</summary>
public sealed class PickItem(ChainPick pick, ChainStepState state, PluginRow? plugin, PluginUsage? usage, Action changed) : Observable
{
    private string _note = pick.Note;
    private string _label = "";
    private WishStatus? _wish;

    public ChainStepState State => state;
    public PluginRow? Plugin => plugin;
    public string? PluginKey => plugin?.Key;
    public string Name => plugin?.Name ?? pick.PluginName;
    public string Detail => NoteListing.PickDetail(pick, state, plugin, usage);
    public string StateText => NoteListing.StateText(state);
    public bool HasState => StateText.Length > 0;
    public bool IsWarning => state is ChainStepState.Missing or ChainStepState.Blacklisted;

    /// <summary>没安装、而且以前移进垃圾桶并卸载过：提示当时的评价。</summary>
    public PluginRow? Remembered { get; init; }
    public bool HasRemembered => Remembered != null;
    public string RememberedText => Remembered == null ? "" : "以前移进垃圾桶并卸载了。" + TrashMemory.Describe(Remembered);

    /// <summary>心愿单里同名记录的状态，没有就是 null。</summary>
    public WishStatus? Wish
    {
        get => _wish;
        set
        {
            if (_wish == value)
                return;
            _wish = value;
            NotifyAll();
        }
    }

    /// <summary>没安装、心愿单里也还没有，可以一键加入。</summary>
    public bool CanWish => state == ChainStepState.Missing && Wish == null;
    public string WishText => Wish == WishStatus.Purchased ? "已购买" : "在心愿单";
    public bool ShowWishText => state == ChainStepState.Missing && Wish != null;

    /// <summary>“备选 1”这样的序号，首选没有。</summary>
    public string Label
    {
        get => _label;
        set
        {
            _label = value;
            Notify(nameof(Label));
        }
    }

    public string Note
    {
        get => _note;
        set
        {
            if (_note == value)
                return;
            _note = value;
            changed();
        }
    }

    public void Refresh() => NotifyAll();

    public ChainPick ToPick() => pick with { Note = _note };

    public override string ToString() => Name;
}

/// <summary>链上的一步：首选插件和按顺序排的备选。</summary>
public sealed class StepItem : Observable
{
    private int _number;
    private bool _isExpanded;

    public StepItem(PickItem primary, IEnumerable<PickItem> alternates)
    {
        Primary = primary;
        Alternates = new ObservableCollection<PickItem>(alternates);
        Alternates.CollectionChanged += (_, _) => Relabel();
        Relabel();
    }

    public PickItem Primary { get; private set; }
    public ObservableCollection<PickItem> Alternates { get; }
    public string Name => Primary.Name;

    public int Number
    {
        get => _number;
        set
        {
            _number = value;
            Notify(nameof(Number));
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            Notify(nameof(IsExpanded));
            Notify(nameof(AlternatesText));
        }
    }

    /// <summary>有备选时带个箭头，提示可以展开、收起。</summary>
    public string AlternatesText => Alternates.Count == 0 ? "+ 备选" : $"备选 {Alternates.Count} {(IsExpanded ? "▴" : "▾")}";

    public string AlternatesHint => Alternates.Count == 0
        ? "还没有备选。在右边找到插件，点 ⇄ 就会加到这一步的备选里"
        : "没有首选插件时按顺序换用下面的备选";

    public bool MoveAlternate(PickItem pick, int offset)
    {
        var at = Alternates.IndexOf(pick);
        var target = at + offset;
        if (at < 0 || target < 0 || target >= Alternates.Count)
            return false;
        Alternates.Move(at, target);
        return true;
    }

    /// <summary>备选升为首选，原来的首选放到它原来的位置。</summary>
    public void Promote(PickItem pick)
    {
        var at = Alternates.IndexOf(pick);
        if (at < 0)
            return;
        Alternates[at] = Primary;
        Primary = pick;
        pick.Label = "";
        Relabel();
        Notify(nameof(Primary));
        Notify(nameof(Name));
    }

    private void Relabel()
    {
        for (var i = 0; i < Alternates.Count; i++)
            Alternates[i].Label = $"备选 {i + 1}";
        Notify(nameof(AlternatesText));
        Notify(nameof(AlternatesHint));
    }

    public ChainStep ToStep() => ChainStep.From(Primary.ToPick(), Alternates.Select(a => a.ToPick()));

    public override string ToString() => Name;
}

/// <summary>右边可以加进笔记的插件。</summary>
public sealed record CandidateItem(PluginRow Plugin, PluginUsage? Usage)
{
    public string Key => Plugin.Key;
    public string Name => Plugin.Name;
    public string Detail => $"{(Plugin.Vendor.Length > 0 ? Plugin.Vendor : "未知")}  ·  {PluginLabels.UsageSummary(Usage)}";

    public override string ToString() => Name;
}

/// <summary>批量面板上列出的一项。</summary>
public sealed record BatchRow(bool IsFolder, string Name, string Path)
{
    public MaterialIconKind Icon => IsFolder ? MaterialIconKind.FolderOutline : MaterialIconKind.FileDocumentOutline;
}
