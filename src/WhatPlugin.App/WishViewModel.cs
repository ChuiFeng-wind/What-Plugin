using System.ComponentModel;
using System.Windows;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>心愿单卡片和已购表格的一行。密码只显示遮罩，不进搜索文本。</summary>
public sealed class WishViewModel(WishItem item, bool installed, EffectiveLogin? login = null) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public WishItem Item { get; private set; } = item;

    /// <summary>实际使用的账号密码：跟随厂家时来自厂家账号。</summary>
    public EffectiveLogin Login { get; private set; } = login ?? new EffectiveLogin(item.Account, item.Password, item.SecretsLocked, false, "");

    /// <summary>账号、密码或序列号至少有一项可用。</summary>
    public bool HasLogin => Login.HasAny || Item.Serial.Length > 0;
    public bool IsInstalled { get; } = installed;

    public string Name => Item.Name;
    public string VendorText => Item.Vendor.Length > 0 ? Item.Vendor : "厂家未填";
    public string PriceText => Item.Price == null ? "未定价" : Money.Format(Item.Price, Item.Currency);
    public bool HasPrice => Item.Price != null;
    public string ReasonLine => Item.Reason.Length > 0 ? Item.Reason.ReplaceLineEndings(" ") : "还没写为什么想买";
    public Visibility InstalledVisibility => IsInstalled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TargetVisibility => PurchaseAdvisor.ReachedTarget(Item) ? Visibility.Visible : Visibility.Collapsed;
    public string LicenseText => LicenseMigration.KindText(Item.LicenseKind);

    public string PriorityText => WishPriority.Text(Item.Priority);

    public int Priority => WishPriority.Clean(Item.Priority);
    public decimal PriceSort => Item.Price ?? decimal.MaxValue;
    public DateTime Created => Item.CreatedUtc;

    public string AccountText => Login.Account.Length > 0 ? Login.Account + (Login.FromPlatform ? "（厂家账号）" : "") : "未填";
    public string PasswordMask => Login.Locked ? "无法解开" : Login.Password.Length > 0 ? "••••••••" : "未填";
    public string PaidText => Money.Format(Item.PaidPrice, Item.Currency);
    public decimal PaidSort => Item.PaidPrice ?? -1;
    public string DateText => Item.PurchasedOn?.ToString("yyyy-MM-dd") ?? "";

    public string SearchText => string.Join(' ', Item.Name, Item.Vendor, Item.Reason, Item.Note, Login.Account, Login.Platform, Item.ActivatedOn,
        Item.LicenseKind == LicenseKind.Unknown ? "" : LicenseText).ToLowerInvariant();

    public void Update(WishItem item, EffectiveLogin? login = null)
    {
        Item = item;
        Login = login ?? Login;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
