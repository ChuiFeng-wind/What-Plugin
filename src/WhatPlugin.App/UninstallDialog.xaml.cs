using System.ComponentModel;
using System.Text;
using System.Windows;
using WhatPlugin.Core;

namespace WhatPlugin.App;

public sealed class UninstallPlanRow : INotifyPropertyChanged
{
    private readonly IReadOnlyList<UninstallOption> _methods;
    private readonly Action _changed;
    private int _methodIndex;

    public UninstallPlanRow(UninstallPlan plan, Action changed)
    {
        Plan = plan;
        _changed = changed;
        Reason = plan.Plugin.User.BlacklistReason;
        _methods = UninstallOptions.For(plan);
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

    public string Detail => UninstallOptions.Detail(Plan, _methods[_methodIndex]);
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
        var (text, canStart) = UninstallOptions.Summary(Choices);
        SummaryText.Text = text;
        StartButton.IsEnabled = canStart;
    }

    private void Start_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
