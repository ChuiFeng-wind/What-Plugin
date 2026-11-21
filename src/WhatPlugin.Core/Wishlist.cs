using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WhatPlugin.Core;

public enum WishStatus { Wished = 0, Purchased = 1 }

/// <summary>
/// 心愿单和已购账号是同一条记录的两个阶段：买下后状态变为 Purchased，原因和价格都留着。
/// Password、Serial 在内存里是明文，写入数据库前加密，不要输出到日志或备份。
/// </summary>
public sealed record WishItem(
    long Id,
    string Name,
    string Vendor = "",
    decimal? Price = null,
    string Currency = "¥",
    int Priority = WishPriority.CanWait,
    string Reason = "",
    string Url = "",
    string Note = "",
    WishStatus Status = WishStatus.Wished,
    DateOnly? PurchasedOn = null,
    decimal? PaidPrice = null,
    string Account = "",
    string Password = "",
    string Serial = "",
    DateTime CreatedUtc = default,
    bool SecretsLocked = false,
    LicenseKind LicenseKind = LicenseKind.Unknown,
    string ActivatedOn = "",
    int? ActivationsLeft = null,
    decimal? TargetPrice = null,
    decimal? LowestPrice = null,
    string Category = "",
    bool OwnLogin = false,
    string Invoice = "")
{
    /// <summary>这一条自己存的账号、密码或序列号。跟随厂家账号时，账号密码在 <see cref="PlatformAccount"/> 里。</summary>
    public bool HasLogin => Account.Length > 0 || Password.Length > 0 || Serial.Length > 0 || SecretsLocked;

    /// <summary>防止密码随记录的默认输出进入日志或错误信息。</summary>
    public override string ToString() => $"WishItem {{ Id = {Id}, Name = {Name}, Status = {Status} }}";
}

/// <summary>
/// 一个账号平台的登录信息，例如 Plugin Alliance 账号，同平台的插件默认共用。
/// 序列号每款不同，不放在这里。Password 在内存里是明文，写入数据库前加密。
/// </summary>
public sealed record PlatformAccount(string Platform, string Account = "", string Password = "", string Note = "", bool Locked = false)
{
    public bool IsEmpty => Account.Length == 0 && Password.Length == 0 && Note.Length == 0 && !Locked;

    public override string ToString() => $"PlatformAccount {{ Platform = {Platform} }}";
}

/// <summary>一款插件实际用的登录信息：自己的，或者所属平台的。</summary>
public sealed record EffectiveLogin(string Account, string Password, bool Locked, bool FromPlatform, string Platform)
{
    public bool HasAny => Account.Length > 0 || Password.Length > 0 || Locked;

    public override string ToString() => $"EffectiveLogin {{ Platform = {Platform}, FromPlatform = {FromPlatform} }}";
}

public static class PurchaseLogin
{
    /// <summary>跟随厂家时用平台账号；单独设置或厂家未填时用这条自己的。</summary>
    public static EffectiveLogin Resolve(WishItem item, IReadOnlyDictionary<string, PlatformAccount> platforms)
    {
        var platform = VendorCatalog.PlatformOf(item.Vendor);
        if (!item.OwnLogin && platform.Length > 0)
        {
            platforms.TryGetValue(VendorCatalog.PlatformKey(platform), out var account);
            return new EffectiveLogin(account?.Account ?? "", account?.Password ?? "", account?.Locked ?? false, true, platform);
        }
        return new EffectiveLogin(item.Account, item.Password, item.SecretsLocked, false, platform);
    }

    public static Dictionary<string, PlatformAccount> ByKey(IEnumerable<PlatformAccount> accounts) =>
        accounts.GroupBy(a => VendorCatalog.PlatformKey(a.Platform)).ToDictionary(g => g.Key, g => g.First());
}

/// <summary>批量写入的一条。账号、密码、序列号留空表示不改动已有内容。</summary>
public sealed record PurchaseDraft(string Name, string Vendor = "", string Account = "", string Password = "", string Serial = "")
{
    public override string ToString() => $"PurchaseDraft {{ Name = {Name} }}";
}

public sealed record UpsertResult(int Added, int Updated, IReadOnlyList<string>? NewPlatforms = null);

/// <summary>批量填账号的结果：新建了哪些平台账号，多少款跟随平台，多少款存成单独账号。</summary>
public sealed record LoginAssignment(IReadOnlyList<string> NewPlatforms, int Shared, int Own);

/// <summary>
/// 用系统自带的、绑定当前用户的保护加密，所以不需要另设主密码：Windows 是 DPAPI（见 <see cref="WindowsSecretBackend"/>），
/// Mac 是钥匙串里的密钥（见 <see cref="MacKeychainSecretBackend"/>）。
/// 换账号或重装系统后无法解开，迁移要靠“导出账号”。
/// </summary>
public static class SecretProtector
{
    /// <summary>当前平台的加密方式。测试可以换成别的。</summary>
    public static ISecretBackend Backend { get; set; } =
        OperatingSystem.IsWindows() ? new WindowsSecretBackend()
        : OperatingSystem.IsMacOS() ? new MacKeychainSecretBackend()
        : new UnsupportedSecretBackend();

    public static byte[] Protect(string secret) => Backend.Protect(Encoding.UTF8.GetBytes(secret));

    /// <summary>解不开时返回 null，例如数据库来自另一个 Windows 账号。</summary>
    public static string? TryUnprotect(byte[] protectedBytes)
    {
        try
        {
            return Encoding.UTF8.GetString(Backend.Unprotect(protectedBytes));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

public static partial class Money
{
    public static readonly IReadOnlyList<string> Currencies = ["¥", "$", "€", "£"];

    /// <summary>接受 199、29.99、¥1,299、$29 这些写法；空白表示没填，也算有效。</summary>
    public static bool TryParse(string text, out decimal? value)
    {
        value = null;
        var cleaned = Decoration().Replace(text, "");
        if (cleaned.Length == 0)
            return true;
        if (!decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed))
            return false;
        value = parsed;
        return true;
    }

    public static string Format(decimal? value, string currency) =>
        value == null ? "" : currency + value.Value.ToString("#,0.##", CultureInfo.InvariantCulture);

    /// <summary>不同币种不换算，分开列出：¥1,299 + $58。</summary>
    public static string Total(IEnumerable<(decimal? Value, string Currency)> amounts)
    {
        var parts = amounts
            .Where(a => a.Value != null)
            .GroupBy(a => a.Currency)
            .OrderBy(g => g.Key == "¥" ? 0 : 1)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => Format(g.Sum(a => a.Value!.Value), g.Key));
        return string.Join(" + ", parts);
    }

    [GeneratedRegex(@"[\s,，¥￥$€£]|元|刀|RMB|CNY|USD|EUR|GBP", RegexOptions.IgnoreCase)]
    private static partial Regex Decoration();
}

public sealed record PurchaseParseResult(IReadOnlyList<PurchaseDraft> Drafts, IReadOnlyList<int> BadLines);

/// <summary>
/// 批量账号的文本格式，每行一款：插件名、厂家、账号、密码、序列号，后面的可以省略。
/// 有制表符时按制表符分（从表格粘贴，或“导出账号”的文件）；
/// 否则按逗号分前四项，密码里可以有逗号。
/// </summary>
public static class PurchaseText
{
    private const string Header = "插件名\t厂家\t账号\t密码\t序列号";

    public static PurchaseParseResult Parse(string text)
    {
        var drafts = new List<PurchaseDraft>();
        var bad = new List<int>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;
            var fields = lines[i].Contains('\t') ? lines[i].Split('\t') : SplitCommas(lines[i]);
            var name = fields[0].Trim();
            if (name == "插件名")
                continue;
            if (name.Length == 0 || fields.Length > 5)
            {
                bad.Add(i + 1);
                continue;
            }
            drafts.Add(new PurchaseDraft(name, Field(fields, 1).Trim(), Field(fields, 2).Trim(), Field(fields, 3), Field(fields, 4)));
        }
        return new PurchaseParseResult(drafts, bad);
    }

    private static string Field(string[] fields, int index) => index < fields.Length ? fields[index] : "";

    private static string[] SplitCommas(string line)
    {
        var fields = new List<string>();
        var start = 0;
        for (var i = 0; i < line.Length && fields.Count < 3; i++)
        {
            if (line[i] is ',' or '，')
            {
                fields.Add(line[start..i]);
                start = i + 1;
            }
        }
        fields.Add(line[start..].Trim());
        return fields.ToArray();
    }

    /// <summary>明文导出。内容里有制表符或换行的记录写出去会读错，放进 skipped 让界面说明，不悄悄改内容。</summary>
    public static string Format(IEnumerable<WishItem> items, out IReadOnlyList<string> skipped)
    {
        var builder = new StringBuilder(Header).Append("\r\n");
        var unwritable = new List<string>();
        foreach (var item in items)
        {
            string[] fields = [item.Name, item.Vendor, item.Account, item.Password, item.Serial];
            if (item.SecretsLocked || fields.Any(f => f.IndexOfAny(['\t', '\r', '\n']) >= 0))
            {
                unwritable.Add(item.Name);
                continue;
            }
            builder.AppendJoin('\t', fields).Append("\r\n");
        }
        skipped = unwritable;
        return builder.ToString();
    }
}

/// <summary>想要程度只有两级。</summary>
public static class WishPriority
{
    public const int CanWait = 1;
    public const int MustHave = 2;

    public static int Clean(int priority) => priority >= MustHave ? MustHave : CanWait;

    public static string Text(int priority) => Clean(priority) == MustHave ? "势在必得" : "我还能忍";

    /// <summary>第 3 版以前的备份是三级：有空看看、想买、很想要。只有“很想要”算势在必得。</summary>
    public static int FromThreeLevels(int priority) => priority >= 3 ? MustHave : CanWait;
}
