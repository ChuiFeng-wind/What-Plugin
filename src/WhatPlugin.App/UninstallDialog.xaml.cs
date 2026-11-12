using System.ComponentModel;
using System.Text;
using System.Windows;
using WhatPlugin.Core;

namespace WhatPlugin.App;

public enum UninstallMethod { Official, RecycleFiles, Skip }

/// <param name="Reason">卸载原因，记进卸载历史；空表示用移入垃圾桶的原因。</param>
public sealed record UninstallChoice(UninstallPlan Plan, UninstallMethod Method, InstalledProgram? Program, string Reason = "");

public sealed class UninstallPlanRow : INotifyPropertyChanged
{
    private readonly List<(string Label, UninstallMethod Method, InstalledProgram? Program)> _methods = [];
    private readonly Action _changed;
    private int _methodIndex;

    public UninstallPlanRow(UninstallPlan plan, Action changed)
    {
        Plan = plan;
        _changed = changed;
        Reason = plan.Plugin.User.BlacklistReason;
        if (plan.Exact != null)
            _methods.Add(($"运行官方卸载程序：{plan.Exact.Name}", UninstallMethod.Official, plan.Exact));
        if (plan.Files.Count > 0)
            _methods.Add(($"把 {plan.Files.Count} 个文件移到回收站", UninstallMethod.RecycleFiles, null));
        foreach (var possible in plan.Possible)
            _methods.Add(($"可能相关，请确认后再选：{possible.Name}", UninstallMethod.Official, possible));
        _methods.Add(("这款先不卸载", UninstallMethod.Skip, null));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public UninstallPlan Plan { get; }
    public string Name => Plan.Plugin.Name;
    public string MethodName => "怎么卸载 " + Name;
    public string ReasonName => "卸载 " + Name + " 的原因";

    /// <summary>卸载原因，默认填移入垃圾桶时写的原因，可以改。</summary>
    public string Reason { get; set; }
    public IReadOnlyList<string> Methods => _methods.Select(m => m.Label).ToList();
    public UninstallMethod Method => _methods[_methodIndex].Method;
    public InstalledProgram? Program => _methods[_methodIndex].Program;

    public int MethodIndex
    {
        get => _methodIndex;
        set
        {
            if (value < 0 || value == _methodIndex)
                return;
            _methodIndex = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
            _changed();
        }
    }

    public string Detail
    {
        get
        {
            var text = new StringBuilder();
            if (Method == UninstallMethod.Official && Program != null)
            {
                text.AppendLine($"发布者：{(Program.Publisher.Length > 0 ? Program.Publisher : "未知")}，版本：{(Program.Version.Length > 0 ? Program.Version : "未知")}");
                text.AppendLine($"命令：{Program.UninstallCommand}");
                text.AppendLine(Program == Plan.Exact && Plan.AlsoInProgram.Count > 0
                    ? $"同一安装包里还有：{string.Join("、", Plan.AlsoInProgram.Take(12))}{(Plan.AlsoInProgram.Count > 12 ? $" 等 {Plan.AlsoInProgram.Count} 款" : "")}，它们可能一起被卸载。"
                    : "官方卸载程序可能同时卸载同一安装包里的其他插件。");
            }
            else if (Method == UninstallMethod.RecycleFiles)
            {
                foreach (var file in Plan.Files)
                    text.AppendLine($"{Labels.Format(file.Format)}，{MainWindow.FormatSize(file.Size)}：{file.Path}");
                if (Plan.Exact == null)
                    text.AppendLine("没有找到能确定的官方卸载程序。只移动这些插件文件，安装包的其他内容和卸载记录会留下。");
            }
            else if (Method == UninstallMethod.Skip && Plan.Files.Count == 0 && Plan.Exact == null)
            {
                text.AppendLine("没有可以移除的文件，也没有找到官方卸载程序。");
            }

            if (Method != UninstallMethod.Skip)
            {
                foreach (var shared in Plan.SharedFiles)
                    text.AppendLine($"和其他插件共用，不会移动：{shared}");
                if (Plan.Projects > 0)
                    text.AppendLine($"有 {Plan.Projects} 个工程用过这款插件，卸载后这些工程打开时可能缺插件。");
            }
            return text.ToString().TrimEnd();
        }
    }
}

/// <summary>每款插件选一种卸载方式。这里不执行任何操作，确认后才返回。</summary>
public partial class UninstallDialog
{
    private readonly List<UninstallPlanRow> _rows;

    public UninstallDialog(IReadOnlyList<UninstallPlan> plans)
    {
        InitializeComponent();
        WindowEffects.ApplyDialog(this);
        _rows = plans.Select(p => new UninstallPlanRow(p, UpdateSummary)).ToList();
        PlanList.ItemsSource = _rows;
        HeadingText.Text = $"卸载 {plans.Count} 款插件";
        UpdateSummary();
    }

    public IReadOnlyList<UninstallChoice> Choices =>
        _rows.Select(r => new UninstallChoice(r.Plan, r.Method, r.Program, r.Reason.Trim())).ToList();

    private void UpdateSummary()
    {
        var programs = _rows.Where(r => r.Method == UninstallMethod.Official).Select(r => r.Program!.Key).Distinct().Count();
        var files = _rows.Where(r => r.Method == UninstallMethod.RecycleFiles).Sum(r => r.Plan.Files.Count);
        var parts = new List<string>();
        if (programs > 0)
            parts.Add($"运行 {programs} 个卸载程序");
        if (files > 0)
            parts.Add($"把 {files} 个文件移到回收站");
        SummaryText.Text = parts.Count > 0 ? "将" + string.Join("，", parts) : "没有要执行的操作";
        StartButton.IsEnabled = parts.Count > 0;
    }

    private void Start_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
