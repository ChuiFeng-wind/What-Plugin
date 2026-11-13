using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>评价共享：导出、导入、查看别人的评价、采用和对比。别人的评价单独存放，不影响推荐。</summary>
public partial class MainWindow
{
    private const string ReviewAuthorIdSetting = "review.authorId";
    private const string ReviewAuthorNameSetting = "review.authorName";
    private const string IdentityNote = "昵称由分享者自己填写，不能证明身份。";

    private ReviewLibrary? _reviewLibrary;
    private Dictionary<string, ReviewAdoption>? _adoptions;
    private IReadOnlyList<AttributedReview> _currentShared = [];
    private long? _sharedSourceId;
    private bool _loadingShared;

    private ReviewLibrary Reviews => _reviewLibrary ??= new ReviewLibrary(_database.LoadSharedReviews());

    private Dictionary<string, ReviewAdoption> Adoptions => _adoptions ??= _database.LoadReviewAdoptions();

    private AttributedReview? SelectedShared =>
        _currentShared.FirstOrDefault(r => r.Source.Id == _sharedSourceId) ?? _currentShared.FirstOrDefault();

    // ---------- 详情面板 ----------

    private void ShowSharedReviews(PluginRow row)
    {
        UpdateAdoptionBadge(row);
        _currentShared = Reviews.For(row);
        SharedReviewCard.Visibility = _currentShared.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_currentShared.Count == 0)
            return;

        // 切换插件时尽量继续看同一个人的评价。
        var selected = SelectedShared!;
        _loadingShared = true;
        SharedAuthorPicker.ItemsSource = _currentShared.Select(r => AuthorLabel(r.Source, _currentShared.Select(c => c.Source))).ToList();
        SharedAuthorPicker.SelectedIndex = _currentShared.ToList().IndexOf(selected);
        SharedAuthorPicker.Visibility = _currentShared.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _loadingShared = false;
        ShowSharedReview(row, selected);
    }

    private void ShowSharedReview(PluginRow row, AttributedReview shared)
    {
        var review = shared.Review;
        SharedAuthor.Text = $"来自 {shared.Source.Name} 的评价";
        SharedStars.Text = review.Rating > 0 ? new string('★', review.Rating) + new string('☆', 5 - review.Rating) : "没有打分";
        SharedNote.Text = review.Note;
        SharedNoteHost.Visibility = review.Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SharedTags.Text = review.Tags.Count > 0 ? "标签：" + string.Join("、", review.Tags) : "";
        SharedTags.Visibility = review.Tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var extras = ReviewExtras(review);
        SharedExtras.Text = extras;
        SharedExtras.Visibility = extras.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var own = row.User.Normalized();
        var same = own.Rating == review.Rating && own.Note == review.Note.Trim() && own.MyTags.SequenceEqual(review.Tags);
        AdoptReviewButton.IsEnabled = !same;
        AdoptReviewButton.Content = same ? "已是这条内容" : "采用";
    }

    private void UpdateAdoptionBadge(PluginRow row)
    {
        var text = Adoptions.TryGetValue(row.Key, out var adoption) ? adoption.Describe(row.User) : "";
        AdoptionText.Text = text;
        AdoptionBadge.ToolTip = text.Length > 0 ? text + "。" + IdentityNote : null;
        AdoptionBadge.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SharedAuthor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingShared || _current == null || SharedAuthorPicker.SelectedIndex < 0 || SharedAuthorPicker.SelectedIndex >= _currentShared.Count)
            return;
        var shared = _currentShared[SharedAuthorPicker.SelectedIndex];
        _sharedSourceId = shared.Source.Id;
        ShowSharedReview(_current.Row, shared);
    }

    private void AdoptReview_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrent(force: true);
        if (_current == null || SelectedShared is not { } shared)
            return;

        var row = _current.Row;
        if (ReviewShare.HasReview(row) && !Dialogs.Confirm(this, "替换自己的评价？",
                $"“{row.Name}”已经有你写的评分、感受或标签。采用 {shared.Source.Name} 的评价会替换这三项，分类和混音属性不变。", "替换"))
            return;

        try
        {
            var (data, adoption) = _database.AdoptReview(row, shared.Review, shared.Source.Name);
            Adoptions[row.Key] = adoption;
            _current.Update(row with { User = data });
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            StatusText.Text = "采用失败：" + error.Message;
            return;
        }

        ShowDetail();
        RebuildFilterOptions();
        StatusText.Text = $"已采用 {shared.Source.Name} 对 {row.Name} 的评价，可以继续修改";
    }

    private void CompareReviews_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrent(force: true);
        if (_current != null && _currentShared.Count > 0)
            ReviewCompareDialog.Show(this, _current.Row, _currentShared);
    }

    private static string AuthorLabel(ReviewSource source, IEnumerable<ReviewSource> all) =>
        all.Count(s => s.Name == source.Name) > 1
            ? $"{source.Name}（{source.ImportedUtc.ToLocalTime():yyyy-MM-dd} 导入）"
            : source.Name;

    internal static string ReviewExtras(SharedReview review)
    {
        var parts = new List<string>();
        if (review.ProjectCount is { } projects)
            parts.Add($"用在 {projects} 个工程");
        if (review.Categories is { Count: > 0 } categories)
            parts.Add("分类：" + string.Join("、", categories));
        if (review.Mix is { } mix)
        {
            parts.AddRange(new[] { mix.LatencyText, mix.CpuText.Length > 0 ? "CPU " + mix.CpuText : "" }.Where(t => t.Length > 0));
            if (mix.LinearPhase != Tri.Unknown)
                parts.Add("线性相位：" + MixAttributes.TriText(mix.LinearPhase));
            if (mix.Sidechain != Tri.Unknown)
                parts.Add("侧链：" + MixAttributes.TriText(mix.Sidechain));
            if (mix.Oversampling != Tri.Unknown)
                parts.Add("过采样：" + MixAttributes.TriText(mix.Oversampling));
        }
        return string.Join(" · ", parts);
    }

    // ---------- 设置页 ----------

    private void RefreshReviewSources()
    {
        var rows = _plugins.Select(p => p.Row).ToList();
        var sources = _database.LoadReviewSources();
        ReviewSourceList.ItemsSource = sources.Select(s => new ReviewSourceRow(
            s,
            AuthorLabel(s, sources),
            $"{s.Count} 条评价，{Reviews.CountMatched(s, rows)} 条对应已安装的插件，{s.ImportedUtc.ToLocalTime():yyyy-MM-dd} 导入")).ToList();
        ReviewSourcesEmpty.Visibility = sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ReloadReviews()
    {
        _reviewLibrary = null;
        RefreshReviewSources();
        if (_current != null)
            ShowSharedReviews(_current.Row);
    }

    /// <summary>挑要分享的评价并生成内容，还没有保存。取消或没有评价时返回 null。</summary>
    private ReviewFile? PrepareReviews()
    {
        SaveCurrent();
        var rows = _plugins.Select(p => p.Row).Where(ReviewShare.HasReview).ToList();
        if (rows.Count == 0)
        {
            StatusText.Text = "还没有写过评价，先给插件打分或写点感受";
            return null;
        }

        var dialog = new ReviewExportDialog(rows, _database.GetSetting(ReviewAuthorNameSetting) ?? "") { Owner = this };
        if (dialog.ShowDialog() != true)
            return null;

        var author = dialog.Author;
        var authorId = _database.GetSetting(ReviewAuthorIdSetting);
        if (string.IsNullOrEmpty(authorId))
        {
            authorId = Guid.NewGuid().ToString("N");
            _database.SetSetting(ReviewAuthorIdSetting, authorId);
        }
        _database.SetSetting(ReviewAuthorNameSetting, author);
        return ReviewShare.Build(authorId, author, dialog.SelectedRows, _report.Usage, dialog.Options, DateTime.UtcNow);
    }

    private void ExportReviews()
    {
        var file = PrepareReviews();
        if (file == null)
            return;
        var save = new SaveFileDialog
        {
            Title = "导出评价",
            FileName = $"{SafeFileName(file.Author)}-插件评价{ReviewShare.Extension}",
            DefaultExt = ReviewShare.Extension,
            Filter = $"WhatPlugin 评价 (*{ReviewShare.Extension})|*{ReviewShare.Extension}",
        };
        if (!FileDialogs.Show(save, this))
            return;
        try
        {
            File.WriteAllText(save.FileName, ReviewShare.Serialize(file), new UTF8Encoding(false));
            RecordUsage(() => _database.RecordUsage(UsageKinds.ReviewExport));
            StatusText.Text = $"已导出 {file.Reviews.Count} 条评价到 {save.FileName}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "导出失败：" + error.Message;
        }
    }

    private void ImportReviews(string path)
    {
        ReviewFile file;
        try
        {
            file = ReviewShare.Read(path);
        }
        catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "导入失败：" + error.Message;
            return;
        }

        if (CheckReviewFile(file) is { } blocked)
        {
            StatusText.Text = blocked;
            return;
        }
        if (!Dialogs.Confirm(this, "导入评价", DescribeReviewFile(file), "导入"))
            return;
        StatusText.Text = ApplyReviewFile(file);
    }

    /// <summary>不能导入的原因；能导入时返回 null。</summary>
    private string? CheckReviewFile(ReviewFile file)
    {
        if (file.AuthorId == _database.GetSetting(ReviewAuthorIdSetting))
            return "这是你自己导出的评价，不需要导入";
        if (file.Reviews.Count == 0)
            return "这份文件里没有可用的评价";
        return null;
    }

    private string DescribeReviewFile(ReviewFile file)
    {
        var sources = _database.LoadReviewSources();
        var preview = new ReviewSource(0, file.AuthorId, file.Author, DateTime.UtcNow, file.Reviews.Count);
        var matched = new ReviewLibrary(file.Reviews.Select(r => new AttributedReview(preview, r))).CountMatched(preview, _plugins.Select(p => p.Row));
        var message = new StringBuilder($"来自“{file.Author}”的评价，共 {file.Reviews.Count} 条，其中 {matched} 条对应你已安装的插件。其余的会保留，以后装上就能看到。不会改动你自己的评价。{IdentityNote}");
        if (sources.FirstOrDefault(s => s.AuthorId == file.AuthorId) is { } existing)
            message.Append($"\n\n会替换之前导入的 {existing.Count} 条“{existing.Name}”的评价。");
        else if (sources.Any(s => s.Name == file.Author))
            message.Append($"\n\n已有另一位也叫“{file.Author}”的分享者，两份评价会分开保存。");
        return message.ToString();
    }

    /// <summary>保存别人的评价，返回给状态栏的说明。</summary>
    private string ApplyReviewFile(ReviewFile file)
    {
        _database.SaveReviewFile(file, DateTime.UtcNow);
        ReloadReviews();
        return $"已导入 {file.Author} 的 {file.Reviews.Count} 条评价";
    }

    private void RemoveReviewSource_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ReviewSourceRow row)
            return;
        if (!Dialogs.Confirm(this, "移除这份评价？",
                $"会移除“{row.Source.Name}”的 {row.Source.Count} 条评价。你已经采用的内容会保留。", "移除", danger: true))
            return;
        _database.RemoveReviewSource(row.Source.Id);
        ReloadReviews();
        StatusText.Text = $"已移除 {row.Source.Name} 的评价";
    }
}

/// <summary>
/// 用普通类而不是记录：列表每次刷新都会重建这些行，内容相同的记录会被当成同一项，
/// 让 WPF 继续使用旧行的辅助功能信息，读屏软件和自动化工具就找不到“移除”按钮。
/// </summary>
public sealed class ReviewSourceRow(ReviewSource source, string name, string detail)
{
    public ReviewSource Source { get; } = source;
    public string Name { get; } = name;
    public string Detail { get; } = detail;

    public override string ToString() => Name;
}

/// <summary>同一款插件，自己和每位分享者的评价并排显示，同一项对齐。</summary>
internal static class ReviewCompareDialog
{
    private const double LabelWidth = 62;
    private const double ColumnWidth = 260;

    public static void Show(Window owner, PluginRow row, IReadOnlyList<AttributedReview> others)
    {
        var window = new Window
        {
            Owner = owner,
            Title = "对比评价",
            Width = Math.Min(LabelWidth + ColumnWidth * (others.Count + 1) + 80, 1120),
            Height = 620,
            Style = (Style)Application.Current.FindResource("DialogWindow"),
        };
        WindowEffects.ApplyDialog(window);

        var grid = new Grid();
        AutomationProperties.SetAutomationId(grid, "ReviewCompareGrid");
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
        for (var i = 0; i < 4; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var (label, index) in new[] { ("评分", 1), ("感受", 2), ("标签", 3) })
            Add(grid, Cell(label, muted: true), index, 0);

        void AddColumn(string name, int rating, string note, IReadOnlyList<string> tags, string extras)
        {
            var column = grid.ColumnDefinitions.Count;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColumnWidth) });
            var header = Cell(name, muted: false);
            header.FontWeight = FontWeights.SemiBold;
            Add(grid, header, 0, column);
            var stars = Cell(rating > 0 ? new string('★', rating) + new string('☆', 5 - rating) : "没有打分", muted: rating == 0);
            if (rating > 0)
                stars.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            Add(grid, stars, 1, column);
            var body = new StackPanel();
            body.Children.Add(Cell(note.Length > 0 ? note : "没有写", muted: note.Length == 0));
            Add(grid, body, 2, column);
            var tagPanel = new StackPanel();
            tagPanel.Children.Add(Cell(tags.Count > 0 ? string.Join("、", tags) : "没有标签", muted: tags.Count == 0));
            if (extras.Length > 0)
            {
                var extra = Cell(extras, muted: true);
                extra.FontSize = 12;
                extra.Margin = new Thickness(0, 6, 16, 0);
                tagPanel.Children.Add(extra);
            }
            Add(grid, tagPanel, 3, column);
        }

        var own = row.User.Normalized();
        AddColumn("我", own.Rating, own.Note, own.MyTags, "");
        foreach (var other in others)
        {
            var label = others.Count(o => o.Source.Name == other.Source.Name) > 1
                ? $"{other.Source.Name}（{other.Source.ImportedUtc.ToLocalTime():yyyy-MM-dd}）"
                : other.Source.Name;
            AddColumn(label, other.Review.Rating, other.Review.Note, other.Review.Tags, MainWindow.ReviewExtras(other.Review));
        }

        var close = new Button { Content = "关闭", MinWidth = 76, IsCancel = true, IsDefault = true };
        AutomationProperties.SetAutomationId(close, "ReviewCompareClose");
        close.Click += (_, _) => window.Close();

        var title = new TextBlock { Text = "对比评价：" + row.Name, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        var note = new TextBlock { Text = "昵称由分享者自己填写，不能证明身份。", FontSize = 12, Margin = new Thickness(0, 4, 0, 14) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var top = new StackPanel();
        top.Children.Add(title);
        top.Children.Add(note);
        DockPanel.SetDock(top, Dock.Top);

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        bottom.Children.Add(close);
        DockPanel.SetDock(bottom, Dock.Bottom);

        var scroll = new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = false,
        };

        var layout = new DockPanel { Margin = new Thickness(24, 20, 24, 20) };
        layout.Children.Add(top);
        layout.Children.Add(bottom);
        layout.Children.Add(scroll);

        var border = new Border { BorderThickness = new Thickness(1), Child = layout };
        border.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
        window.Content = border;
        window.ShowDialog();
    }

    private static TextBlock Cell(string text, bool muted)
    {
        var cell = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 21, Margin = new Thickness(0, 0, 16, 0) };
        if (muted)
            cell.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return cell;
    }

    private static void Add(Grid grid, FrameworkElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        element.Margin = new Thickness(element.Margin.Left, element.Margin.Top, element.Margin.Right, 14);
        grid.Children.Add(element);
    }
}
