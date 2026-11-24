using System.ComponentModel;
using System.Windows;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>心愿单卡片和已购表格的一行。文字来自 Core 的 <see cref="WishRow"/>；密码只显示遮罩，不进搜索文本。</summary>
public sealed class WishViewModel(WishItem item, bool installed, EffectiveLogin? login = null) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public WishRow Row { get; private set; } = login == null ? new WishRow(item, installed) : new WishRow(item, installed, login);

    public WishItem Item => Row.Item;

    /// <summary>实际使用的账号密码：跟随厂家时来自厂家账号。</summary>
    public EffectiveLogin Login => Row.Login;

    public bool HasLogin => Row.HasLogin;
    public bool IsInstalled => Row.IsInstalled;

    public string Name => Row.Name;
    public string VendorText => Row.VendorText;
    public string PriceText => Row.PriceText;
    public bool HasPrice => Row.HasPrice;
    public string ReasonLine => Row.ReasonLine;
    public Visibility InstalledVisibility => IsInstalled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TargetVisibility => Row.ReachedTarget ? Visibility.Visible : Visibility.Collapsed;
    public string LicenseText => Row.LicenseText;
    public string PriorityText => Row.PriorityText;
    public int Priority => Row.Priority;
    public decimal PriceSort => Row.PriceSort;
    public DateTime Created => Row.Created;
    public string AccountText => Row.AccountText;
    public string PasswordMask => Row.PasswordMask;
    public string PaidText => Row.PaidText;
    public decimal PaidSort => Row.PaidSort;
    public string DateText => Row.DateText;
    public string SearchText => Row.SearchText;

    public void Update(WishItem item, EffectiveLogin? login = null)
    {
        Row = Row with { Item = item, Login = login ?? Row.Login };
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
