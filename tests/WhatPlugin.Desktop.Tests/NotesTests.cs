using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using WhatPlugin.Core;

namespace WhatPlugin.Desktop.Tests;

/// <summary>按脚本回答的对话框：每次提问取出下一个答案，问的内容记下来给测试检查。</summary>
public sealed class ScriptedDialogs : IDialogs
{
    public Queue<object?> Answers { get; } = new();
    public List<string> Asked { get; } = [];
    public List<IReadOnlyList<string>> Options { get; } = [];

    private T Next<T>(string title)
    {
        Asked.Add(title);
        Assert.True(Answers.Count > 0, "没有准备好的回答：" + title);
        return (T)Answers.Dequeue()!;
    }

    public Task<bool> Confirm(string title, string message, string confirm, bool danger = false) => Task.FromResult(Next<bool>(title + "｜" + message));

    public Task<(bool Confirmed, bool Option)> ConfirmWithOption(string title, string message, string confirm, string option) =>
        Task.FromResult(Next<(bool, bool)>(title + "｜" + message + "｜" + option));

    public Task<string?> Prompt(string title, string message, string confirm, string text = "") => Task.FromResult(Next<string?>(title));

    public Task<int?> PickOne(string title, string message, IReadOnlyList<string> options, string confirm, int selected = 0)
    {
        Options.Add(options);
        return Task.FromResult(Next<int?>(title));
    }

    public Task<(IReadOnlyList<int>? Picked, bool Option)> PickMany(string title, string message, IReadOnlyList<(string Text, bool Checked)> options, string confirm, string? option = null, bool optionChecked = false)
    {
        Options.Add(options.Select(o => o.Text).ToList());
        return Task.FromResult(Next<(IReadOnlyList<int>?, bool)>(title));
    }

    public Task<string?> OpenFile(string title, string filterName, IReadOnlyList<string> extensions) => Task.FromResult(Next<string?>(title));

    public Task<string?> SaveFile(string title, string fileName, string filterName, string extension) => Task.FromResult(Next<string?>(title + "｜" + fileName));

    /// <summary>显示的文字记在 Shown 里；回答是“保存到的路径”，null 表示没保存。</summary>
    public List<string> Shown { get; } = [];

    public Task<string?> ShowText(string title, string text, string fileName)
    {
        Shown.Add(text);
        return Task.FromResult(Next<string?>(title));
    }

    public Task<IReadOnlyList<PurchaseDraft>?> BatchAdd(IReadOnlyList<PluginRow> installed, IReadOnlyList<WishItem> purchased) =>
        Task.FromResult(Next<IReadOnlyList<PurchaseDraft>?>("批量添加"));

    /// <summary>导出、导入窗口里列出的勾选项，按出现顺序记下来。</summary>
    public List<IReadOnlyList<PartOption>> Parts { get; } = [];

    public Task<ExportChoice?> ExportParts(IReadOnlyList<PartOption> self, IReadOnlyList<PartOption> share, bool shareMode)
    {
        Parts.Add(self);
        Parts.Add(share);
        return Task.FromResult(Next<ExportChoice?>("导出" + (shareMode ? "｜分享" : "")));
    }

    /// <summary>回答可以是勾选的 Id，也可以是“按列出的选项挑”的函数。</summary>
    public Task<IReadOnlySet<string>?> ImportParts(string title, string message, IReadOnlyList<PartOption> options, string confirm, bool danger)
    {
        Parts.Add(options);
        var answer = Next<object?>(title + "｜" + message);
        return Task.FromResult(answer is Func<IReadOnlyList<PartOption>, IReadOnlySet<string>?> pick ? pick(options) : (IReadOnlySet<string>?)answer);
    }

    /// <summary>密码输错时的提示。</summary>
    public List<string> PasswordErrors { get; } = [];

    /// <summary>依次试回答里的密码，直到解开；"" 是跳过账号密码，null 是取消。</summary>
    public Task<string?> BackupPassword(Func<string, string?> tryPassword)
    {
        while (true)
        {
            var attempt = Next<string?>("备份密码");
            if (string.IsNullOrEmpty(attempt))
                return Task.FromResult(attempt);
            if (tryPassword(attempt) is not { } problem)
                return Task.FromResult<string?>(attempt);
            PasswordErrors.Add(problem);
        }
    }

    public Task<ReviewExportResult?> ExportReviews(IReadOnlyList<PluginRow> rows, string author)
    {
        var answer = Next<object?>("导出评价｜" + author);
        return Task.FromResult(answer is Func<IReadOnlyList<PluginRow>, ReviewExportResult?> pick ? pick(rows) : (ReviewExportResult?)answer);
    }

    public Task<NoteExportResult?> ExportNotes(IReadOnlyList<PluginChain> chains, IReadOnlyList<NoteFolder> folders, string author)
    {
        var answer = Next<object?>("导出混音笔记｜" + author);
        return Task.FromResult(answer is Func<IReadOnlyList<PluginChain>, NoteExportResult?> pick ? pick(chains) : (NoteExportResult?)answer);
    }

    public List<(PluginRow Row, IReadOnlyList<AttributedReview> Others)> Compared { get; } = [];

    public Task CompareReviews(PluginRow row, IReadOnlyList<AttributedReview> others)
    {
        Compared.Add((row, others));
        return Task.CompletedTask;
    }
}

/// <summary>混音笔记：树、步骤和备选、标签、心愿单、多选、路由和从工程导入轨道。数据库和工程都在临时目录。</summary>
public sealed class NotesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-notes-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly PluginDatabase _database;
    private readonly TestEnvironment _env;
    private readonly ScriptedDialogs _dialogs = new();

    public NotesTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "t.db");
        Assert.StartsWith(Path.GetTempPath(), _dbPath, StringComparison.OrdinalIgnoreCase);
        _database = new PluginDatabase(_dbPath);
        _env = new TestEnvironment(Path.Combine(_dir, "env"));
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private void ScanPlugins()
    {
        _env.AddVst3("Pro-Q 4", "FabFilter");
        _env.AddVst3("Pro-C 2", "FabFilter", "Fx|Dynamics");
        _env.AddVst3("ValhallaRoom", "Valhalla DSP", "Fx|Reverb");
        var scan = Task.Run(() => ScanWorkflow.RunAsync(_database, _env.Scan, new ScanRequest(false, ScanFormats.Main), null, CancellationToken.None)).GetAwaiter().GetResult();
        Assert.NotNull(scan.Summary);
    }

    private string Key(string name) => _database.LoadEffects().First(p => p.Name == name).Key;

    private ChainStep Step(string name, params string[] alternates) =>
        new(name == "soothe2" ? "" : Key(name), name, "", alternates.Select(a => new ChainPick(a == "soothe2" ? "" : Key(a), a)).ToList());

    /// <summary>混音师 A 下面有人声文件夹：Lead Vox（三步，第一步有备选）、Vox Bus（编组）、Verb（效果轨）；最外层还有一条 Kick。</summary>
    private void Seed()
    {
        ScanPlugins();
        var mixer = _database.AddNoteFolder(null, "Mixer A");
        var vocals = _database.AddNoteFolder(mixer.Id, "Vocals");
        var lead = _database.AddChain("Lead Vox", vocals.Id);
        _database.SaveChain(lead.Id, "先扫共振再压", [Step("soothe2", "Pro-Q 4"), Step("Pro-C 2"), Step("ValhallaRoom")]);
        _database.SetChainTags(lead.Id, ["人声"]);
        var bus = _database.AddChain("Vox Bus", vocals.Id);
        _database.SetChainRole(bus.Id, ChannelRole.Group);
        _database.SaveChain(bus.Id, "", [Step("Pro-C 2")]);
        var verb = _database.AddChain("Verb", vocals.Id);
        _database.SetChainRole(verb.Id, ChannelRole.Effect);
        _database.SaveChain(verb.Id, "", [Step("ValhallaRoom"), Step("Pro-C 2")]);
        _database.SetRoutes(lead.Id, bus.Id, [verb.Id], [(verb.Id, "Pro-C 2")]);
        _database.AddChain("Kick", null);
    }

    private MainWindow Open(double width = 1440, double height = 900)
    {
        var window = new MainWindow(_database, _dbPath, _env.Scan) { Width = width, Height = height };
        window.Show();
        Settle(window);
        return window;
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Wait(Task task, Window window)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "操作没有结束");
        }
        task.GetAwaiter().GetResult();
        Settle(window);
    }

    private NotesView Notes(MainWindow window)
    {
        window.Navigate("Chains");
        Settle(window);
        var page = window.Notes!;
        page.Dialogs = _dialogs;
        return page;
    }

    private static NoteItem Item(NotesView page, string name) => page.Find(name) ?? throw new InvalidOperationException("树上没有 " + name);

    private static void Show(NotesView page, string name, Window window)
    {
        page.Select(Item(page, name));
        Settle(window);
    }

    // ---------- 树 ----------

    [AvaloniaFact]
    public void TreeShowsFoldersFirstWithCountsAndRememberedCollapse()
    {
        Seed();
        var window = Open();
        var page = Notes(window);

        Assert.Equal(["Mixer A", "Kick"], page.Roots.Select(r => r.Name));
        Assert.Equal("3", Item(page, "Mixer A").Count);
        Assert.Equal(["Lead Vox", "Verb", "Vox Bus"], Item(page, "Vocals").Children.Select(c => c.Name));
        Assert.Equal("3", Item(page, "Lead Vox").Count);
        Assert.Equal("4", window.NavCount("Chains"));
        Assert.Equal("在左边选一条笔记或一个文件夹", page.FindControl<TextBlock>("EmptyText")!.Text);

        Item(page, "Vocals").IsExpanded = false;
        page.Reload();
        Assert.False(Item(page, "Vocals").IsExpanded);
        Assert.True(Item(page, "Mixer A").IsExpanded);

        // 搜索插件名：只留用到它的笔记和所在的文件夹，搜索时都展开。
        page.FindControl<TextBox>("TreeSearch")!.Text = "valhalla";
        Settle(window);
        Assert.Equal(["Mixer A"], page.Roots.Select(r => r.Name));
        Assert.Equal(["Lead Vox", "Verb"], Item(page, "Vocals").Children.Select(c => c.Name));
        Assert.True(Item(page, "Vocals").IsExpanded);
        page.FindControl<TextBox>("TreeSearch")!.Text = "";
        Settle(window);
        Assert.False(Item(page, "Vocals").IsExpanded);
    }

    [AvaloniaFact]
    public void NewNotesAndFoldersGoIntoTheSelectedFolder()
    {
        ScanPlugins();
        var window = Open();
        var page = Notes(window);
        Assert.StartsWith("在左下角新建第一条笔记", page.FindControl<TextBlock>("EmptyText")!.Text);

        page.AddFolder("Mixer B");
        Assert.Equal("已在“最外层”新建文件夹“Mixer B”", window.Status);
        Assert.Equal("Mixer B", page.ShownFolder?.Name);
        page.AddChain("Drum Bus");
        Assert.Equal("已在“Mixer B”新建笔记“Drum Bus”", window.Status);
        Assert.Equal("Drum Bus", page.ShownChain?.Name);
        Assert.Equal("1", window.NavCount("Chains"));
        Assert.Equal("Mixer B  ·  还没有插件", page.MetaText);

        page.AddChain("Drum Bus");
        Assert.Contains("已经有", window.Status);
    }

    [AvaloniaFact]
    public void RenamingInTheTreeRefusesAnEmptyName()
    {
        Seed();
        var window = Open();
        var page = Notes(window);
        var kick = Item(page, "Kick");

        page.BeginRename(kick);
        kick.EditText = "  ";
        page.EndRename(kick, save: true);
        Assert.Equal("名称不能为空，没有改名", window.Status);

        page.BeginRename(kick);
        kick.EditText = "Kick In";
        page.EndRename(kick, save: true);
        Settle(window);
        Assert.Equal("已改名为“Kick In”", window.Status);
        Assert.Contains(_database.LoadChains(), c => c.Name == "Kick In");
    }

    // ---------- 笔记 ----------

    [AvaloniaFact]
    public void NoteShowsStepsAlternatesAndWhatIsMissing()
    {
        Seed();
        var window = Open();
        var page = Notes(window);
        Show(page, "Lead Vox", window);

        Assert.Equal("Lead Vox", page.ShownChain?.Name);
        Assert.Equal(["soothe2", "Pro-C 2", "ValhallaRoom"], page.Steps.Select(s => s.Name));
        Assert.Equal("未安装", page.Steps[0].Primary.StateText);
        Assert.Equal(["Pro-Q 4"], page.Steps[0].Alternates.Select(a => a.Name));
        Assert.Equal("备选 1", page.Steps[0].Alternates[0].Label);
        Assert.Equal("Mixer A › Vocals  ·  3 步，按从上到下的顺序  ·  1 个备选  ·  首选里 1 款没安装", page.MetaText);
        Assert.Equal("未安装的加入心愿单", page.WishMissingLabel);
        Assert.Contains(page.TagButtons, t => (string)t.Content! == "人声" && t.IsChecked == true);
        Assert.Equal("先扫共振再压", page.FindControl<TextBox>("ChainNote")!.Text);
    }

    [AvaloniaFact]
    public void EditingStepsSavesInOrder()
    {
        Seed();
        var window = Open();
        var page = Notes(window);
        Show(page, "Kick", window);

        page.SearchCandidates("pro");
        Settle(window);
        Assert.Equal(["Pro-C 2", "Pro-Q 4"], page.Candidates.Select(c => c.Name));
        page.AddStep(Key("Pro-Q 4"));
        page.AddStep(Key("Pro-C 2"));
        Assert.Equal("已保存 Kick", window.Status);

        page.AddAlternate(Key("Pro-C 2"));
        Assert.Equal("先在中间点选要加备选的那一步", window.Status);
        page.SelectedStep = page.Steps[0];
        Assert.Equal("点 + 加成新的一步；点 ⇄ 加为第 1 步（Pro-Q 4）的备选", page.CandidateHintText);
        page.AddAlternate(Key("Pro-C 2"));
        Assert.Equal("已把 Pro-C 2 加为第 1 步的备选", window.Status);

        page.Promote(page.Steps[0].Alternates[0]);
        Assert.Equal("第 1 步的首选改成了 Pro-C 2", window.Status);
        page.MoveStep(page.Steps[1], -1);
        page.Steps[1].Primary.Note = "收紧瞬态";
        page.FindControl<TextBox>("ChainNote")!.Text = "整体思路";
        Assert.True(page.HasPendingSave);
        page.Flush();

        var saved = _database.LoadChains().First(c => c.Name == "Kick");
        Assert.Equal(["Pro-C 2", "Pro-C 2"], saved.Steps.Select(s => s.PluginName));
        Assert.Equal(["Pro-Q 4"], saved.Steps[1].Backups.Select(b => b.PluginName));
        Assert.Equal("收紧瞬态", saved.Steps[1].Note);
        Assert.Equal("整体思路", saved.Note);

        page.RemoveAlternate(page.Steps[1].Alternates[0]);
        page.RemoveStep(page.Steps[0]);
        Assert.Equal(["Pro-C 2"], _database.LoadChains().First(c => c.Name == "Kick").Steps.Select(s => s.PluginName));
    }

    [AvaloniaFact]
    public void TagsCanBeToggledAndAdded()
    {
        Seed();
        var window = Open();
        var page = Notes(window);
        Show(page, "Kick", window);

        page.ToggleTag("鼓", true);
        page.AddCustomTag("Trap, 低频");
        Assert.Equal(["鼓", "Trap", "低频"], _database.LoadChains().First(c => c.Name == "Kick").TagList);
        Assert.Contains(page.TagButtons, t => (string)t.Content! == "低频" && t.IsChecked == true);
        Assert.Contains("Trap", Item(page, "Kick").ToolTip);
    }

    [AvaloniaFact]
    public void MissingPluginsGoToTheWishlistOnce()
    {
        Seed();
        var window = Open();
        var page = Notes(window);
        Show(page, "Lead Vox", window);

        Wait(page.AddToWishlist(page.Steps.SelectMany(s => s.Alternates.Prepend(s.Primary)).Where(p => p.CanWish).ToList()), window);

        Assert.Equal("已把 soothe2 加入心愿单", window.Status);
        var wish = Assert.Single(_database.LoadWishes());
        Assert.Equal(("soothe2", "混音笔记“Lead Vox”里用到"), (wish.Name, wish.Reason));
        Assert.Equal("在心愿单", page.Steps[0].Primary.WishText);
        Assert.False(page.Steps[0].Primary.CanWish);
        Assert.Equal("", page.WishMissingLabel);
        Assert.Equal("1", window.NavCount("Wishes"));
    }

    // ---------- 移动、删除、多选 ----------

    [AvaloniaFact]
    public void MovingAndDroppingNotesBetweenFolders()
    {
        Seed();
        var window = Open();
        var page = Notes(window);
        Show(page, "Kick", window);

        _dialogs.Answers.Enqueue((int?)2);
        Wait(page.MoveShownAsync(), window);
        Assert.Equal(["最外层", "Mixer A", "Mixer A › Vocals"], _dialogs.Options[0]);
        Assert.Equal("已移到“Mixer A › Vocals”", window.Status);

        // 拖到 Mixer A 上；文件夹不能拖进自己里面。
        Assert.True(page.Drop(Item(page, "Kick"), Item(page, "Mixer A")));
        Assert.Equal("已移到“Mixer A”", window.Status);
        Assert.False(page.Drop(Item(page, "Mixer A"), Item(page, "Vocals")));
        Assert.True(page.Drop(Item(page, "Vocals"), null));
        Assert.Equal(["Mixer A", "Vocals"], page.Roots.Select(r => r.Name));
    }

    [AvaloniaFact]
    public void DeletingAFolderKeepsItsNotesUnlessAsked()
    {
        Seed();
        var window = Open();
        var page = Notes(window);
        Show(page, "Vocals", window);
        Assert.Equal("Mixer A › Vocals  ·  3 条笔记", page.FindControl<TextBlock>("FolderMeta")!.Text);

        _dialogs.Answers.Enqueue((false, false));
        Wait(page.DeleteFolderAsync(), window);
        Assert.Contains("里面的 3 条笔记会移到“Mixer A”", _dialogs.Asked[0]);
        Assert.Contains("同时删除笔记：里面的 3 条笔记一起删除", _dialogs.Asked[0]);
        Assert.Equal(2, _database.LoadNoteFolders().Count);

        _dialogs.Answers.Enqueue((true, false));
        Wait(page.DeleteFolderAsync(), window);
        Assert.Equal("已删除文件夹“Vocals”，里面的内容移到了“Mixer A”", window.Status);
        Assert.Equal(4, _database.LoadChains().Count);

        Show(page, "Mixer A", window);
        _dialogs.Answers.Enqueue((true, true));
        Wait(page.DeleteFolderAsync(), window);
        Assert.Equal("已删除文件夹“Mixer A”和里面的 3 条笔记", window.Status);
        Assert.Equal(["Kick"], _database.LoadChains().Select(c => c.Name));
        Assert.Equal("1", window.NavCount("Chains"));
    }

    [AvaloniaFact]
    public void BatchActionsWorkOnTheWholeSelection()
    {
        Seed();
        var window = Open();
        var page = Notes(window);

        page.SelectMany([Item(page, "Vox Bus"), Item(page, "Kick")]);
        Settle(window);
        Assert.True(page.IsBatch);
        Assert.Equal("已选中 2 条笔记", page.FindControl<TextBlock>("BatchTitle")!.Text);

        _dialogs.Answers.Enqueue(((IReadOnlyList<int>?)[NoteListing.KnownTags(_database.LoadChains()).ToList().IndexOf("总线")], false));
        Wait(page.AddTagsAsync(), window);
        Assert.Equal("已给 2 条笔记加上标签：总线", window.Status);
        Assert.All(_database.LoadChains().Where(c => c.Name is "Vox Bus" or "Kick"), c => Assert.Contains("总线", c.TagList));
        Assert.True(page.IsBatch);

        // 选中文件夹和它里面的笔记：只算文件夹，移动时不能放进自己里面。
        page.SelectMany([Item(page, "Vocals"), Item(page, "Lead Vox"), Item(page, "Kick")]);
        Settle(window);
        Assert.Equal("已选中 1 条笔记、1 个文件夹", page.FindControl<TextBlock>("BatchTitle")!.Text);
        _dialogs.Answers.Enqueue((int?)0);
        Wait(page.MoveBatchAsync(), window);
        Assert.Equal(["最外层", "Mixer A"], _dialogs.Options[^1]);
        Assert.Equal("已把 1 项移到“最外层”", window.Status);

        page.MarkAll();
        Settle(window);
        Assert.Equal("已选中 4 条笔记", page.FindControl<TextBlock>("BatchTitle")!.Text);
        _dialogs.Answers.Enqueue(true);
        Wait(page.DeleteBatchAsync(), window);
        Assert.Equal("已删除 4 条笔记", window.Status);
        Assert.Empty(_database.LoadChains());
        Assert.False(page.IsBatch);
    }

    // ---------- 路由 ----------

    [AvaloniaFact]
    public void RoutePanelShowsAndChangesRouting()
    {
        Seed();
        var window = Open();
        var page = Notes(window);
        Show(page, "Lead Vox", window);

        Assert.Equal(["主输出", "Vox Bus（编组）"], page.OutputOptions);
        Assert.Equal(1, page.OutputIndex);
        Assert.Equal(["Verb（效果轨）", "Verb · Pro-C 2"], page.RouteChips);

        Show(page, "Verb", window);
        Assert.Equal("接入 2 路：Lead Vox（发送）、Lead Vox（侧链 · Pro-C 2）", page.IncomingText);

        Show(page, "Lead Vox", window);
        page.OutputIndex = 0;
        Settle(window);
        Assert.Equal("路由已保存", window.Status);
        Assert.DoesNotContain(_database.LoadRoutes(), r => r.Kind == RouteKind.Output);

        // 发送：只剩 Vox Bus 可选；侧链：Vox Bus 上只有一个插件，不用再选。
        _dialogs.Answers.Enqueue((int?)0);
        Wait(page.AddSendAsync(), window);
        Assert.Equal(["Vox Bus（编组）"], _dialogs.Options[^1]);
        _dialogs.Answers.Enqueue((int?)0);
        Wait(page.AddSidechainAsync(), window);
        Assert.Equal(["Vox Bus（编组）", "Verb（效果轨）"], _dialogs.Options[^1]);
        Assert.Equal(["Verb（效果轨）", "Vox Bus（编组）", "Verb · Pro-C 2", "Vox Bus · Pro-C 2"], page.RouteChips);

        page.SetRole(ChannelRole.Group);
        Settle(window);
        Assert.Equal("“Lead Vox”标为编组", window.Status);
        Assert.Equal(ChannelRole.Group, _database.LoadChains().First(c => c.Name == "Lead Vox").Role);
    }

    [AvaloniaFact]
    public void FolderShowsNotesOrRouteMap()
    {
        Seed();
        var window = Open();
        var page = Notes(window);
        Show(page, "Mixer A", window);

        var list = page.FindControl<ListBox>("FolderNotes")!;
        Assert.Equal(["Lead Vox", "Verb", "Vox Bus"], ((IEnumerable<NoteSummaryRow>)list.ItemsSource!).Select(r => r.Name));
        Assert.StartsWith("Mixer A › Vocals  ·  soothe2 → Pro-C 2 → ValhallaRoom", ((IEnumerable<NoteSummaryRow>)list.ItemsSource!).First().Summary);

        page.ShowsMap = true;
        Settle(window);
        var map = page.Map!;
        Assert.False(map.IsEmpty);
        Assert.Contains(_database.LoadChains().First(c => c.Name == "Lead Vox").Id, map.Cards.Keys);
        Assert.True(map.EdgeCount >= 4);
    }

    // ---------- 从工程导入 ----------

    private string WriteProject()
    {
        var path = Path.Combine(_dir, "Projects", "Song X.rpp");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        static string Fx(string name) => $"    <FXCHAIN\n      <VST \"VST3: {name}\" \"x.vst3\" 0 \"\" 1 \"\"\n      >\n    >\n";
        File.WriteAllText(path,
            "<REAPER_PROJECT 0.1 \"7.0\" 1700000000\n"
            + "  <TRACK {A}\n    NAME Drums\n    ISBUS 1 1\n" + Fx("Pro-C 2 (FabFilter)") + "  >\n"
            + "  <TRACK {B}\n    NAME Kick\n    ISBUS 2 -1\n" + Fx("Pro-Q 4 (FabFilter)") + "  >\n"
            + "  <TRACK {C}\n    NAME Vox\n" + Fx("soothe2 (oeksound)") + "  >\n"
            + "  <TRACK {D}\n    NAME Verb\n    AUXRECV 2 0 1 0 0 0 0 0 0 -1:U 0 -1 ''\n" + Fx("ValhallaRoom (Valhalla DSP)") + "  >\n"
            + ">\n");
        return path;
    }

    [AvaloniaFact]
    public void ImportingTracksBuildsNotesAndRoutesWithoutTouchingTheProject()
    {
        ScanPlugins();
        var project = WriteProject();
        var bytes = File.ReadAllBytes(project);
        var modified = File.GetLastWriteTimeUtc(project);
        var window = Open();
        var page = Notes(window);

        _dialogs.Answers.Enqueue(project);
        _dialogs.Answers.Enqueue(((IReadOnlyList<int>?)[1, 2], true));
        _dialogs.Answers.Enqueue((int?)0);
        Wait(page.ImportTracks(), window);

        Assert.Equal(["Drums［编组］　Pro-C 2", "Kick　Pro-Q 4", "Vox　soothe2（未安装）", "Verb［效果轨］　ValhallaRoom"],
            _dialogs.Options[0].Select(o => o.Split("　").Length > 2 ? string.Join("　", o.Split("　").Take(2)) : o));
        Assert.Equal(["“最外层”下的“Song X”文件夹", "最外层"], _dialogs.Options[1]);
        Assert.StartsWith("已导入 4 条笔记到“Song X”", window.Status);
        Assert.Contains("连好 2 条路由", window.Status);
        Assert.Contains("其中 1 处插件未安装", window.Status);
        Assert.Equal("Song X", page.ShownFolder?.Name);

        var chains = _database.LoadChains().ToDictionary(c => c.Name);
        Assert.Equal(ChannelRole.Group, chains["Drums"].Role);
        Assert.Equal(ChannelRole.Effect, chains["Verb"].Role);
        var routes = _database.LoadRoutes();
        Assert.Contains(routes, r => r.From == chains["Kick"].Id && r.To == chains["Drums"].Id && r.Kind == RouteKind.Output);
        Assert.Contains(routes, r => r.From == chains["Vox"].Id && r.To == chains["Verb"].Id && r.Kind == RouteKind.Send);
        Assert.Equal("4", window.NavCount("Chains"));

        Assert.Equal(bytes, File.ReadAllBytes(project));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(project));
    }

    // ---------- 截图 ----------

    [AvaloniaFact]
    public void Screenshots()
    {
        var output = Environment.GetEnvironmentVariable("WHATPLUGIN_SCREENSHOTS");
        if (output != null)
            Directory.CreateDirectory(output);
        void Save(Window window, string name)
        {
            Settle(window);
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(Path.Combine(output ?? _dir, name + ".png"), new PngBitmapEncoderOptions());
        }

        Seed();
        var window = Open();
        var page = Notes(window);
        Show(page, "Lead Vox", window);
        page.Expand(page.Steps[0], true);
        page.SelectedStep = page.Steps[0];
        page.SearchCandidates("fab");
        Save(window, "notes");

        Show(page, "Mixer A", window);
        page.ShowsMap = true;
        Save(window, "notes-map");

        page.SelectMany([Item(page, "Vocals"), Item(page, "Kick")]);
        Save(window, "notes-batch");

        var dialog = new PickDialog("选择要导入的轨道", "REAPER 工程“Song X”里有 4 条通道用了插件或接了编组、效果轨。每条勾选的通道会建一条混音笔记。",
            [("Drums［编组］　Pro-C 2　输出到主输出", false), ("Kick　Pro-Q 4　输出到 Drums", true), ("Vox　soothe2（未安装）　发送到 Verb", true), ("Verb［效果轨］　ValhallaRoom", false)],
            "下一步", true, TrackImportPlan.RoutingOption, true);
        dialog.Show();
        Save(dialog, "notes-import");
        dialog.Close();
    }
}
