namespace WhatPlugin.Core;

public enum ILokFillAction
{
    /// <summary>厂家账号原来是空的，填入了 iLok 的账号和密码。</summary>
    Filled,
    /// <summary>厂家账号就是 iLok 账号，密码同步成 iLok 的新密码。</summary>
    Synced,
    /// <summary>厂家账号是别的账号，不覆盖，只在备注里写上 iLok 账号名。</summary>
    NoteAdded,
    /// <summary>已经是最新的，不用改。</summary>
    Unchanged,
    /// <summary>密码解不开（另一个 Windows 账号加密的），不碰。</summary>
    Skipped,
}

public sealed record ILokFillResult(ILokFillAction Action, PlatformAccount Updated);

/// <summary>
/// 把“iLok”这条厂家账号填到用 iLok 激活的厂家上。只在用户点按钮时运行。
/// 已经填了别的账号的厂家不覆盖；备注里只写 iLok 账号名，不写密码，因为备注不加密。
/// </summary>
public static class ILokFill
{
    public const string Platform = "iLok";
    private const string NotePrefix = "iLok 账号：";

    public static bool IsILok(string platform) => VendorCatalog.PlatformKey(platform) == VendorCatalog.PlatformKey(Platform);

    public static ILokFillResult Apply(PlatformAccount ilok, PlatformAccount target)
    {
        if (ilok.Locked || ilok.Account.Length == 0)
            throw new ArgumentException("iLok 账号未填或解不开", nameof(ilok));
        if (target.Locked)
            return new ILokFillResult(ILokFillAction.Skipped, target);

        if (target.Account.Length == 0 && target.Password.Length == 0)
            return new ILokFillResult(ILokFillAction.Filled, target with { Account = ilok.Account, Password = ilok.Password });

        if (string.Equals(target.Account, ilok.Account, StringComparison.OrdinalIgnoreCase))
        {
            return target.Password == ilok.Password && target.Account == ilok.Account
                ? new ILokFillResult(ILokFillAction.Unchanged, target)
                : new ILokFillResult(ILokFillAction.Synced, target with { Account = ilok.Account, Password = ilok.Password });
        }

        var line = NotePrefix + ilok.Account;
        List<string> lines = target.Note.Length == 0 ? [] : target.Note.ReplaceLineEndings("\n").Split('\n').ToList();
        var index = lines.FindIndex(l => l.StartsWith(NotePrefix, StringComparison.Ordinal));
        if (index >= 0 && lines[index] == line)
            return new ILokFillResult(ILokFillAction.Unchanged, target);
        if (index >= 0)
            lines[index] = line;
        else
            lines.Add(line);
        return new ILokFillResult(ILokFillAction.NoteAdded, target with { Note = string.Join(Environment.NewLine, lines) });
    }

    public static string Describe(ILokFillAction action) => action switch
    {
        ILokFillAction.Filled => "空着，会填入 iLok 账号和密码",
        ILokFillAction.Synced => "就是 iLok 账号，会同步成新密码",
        ILokFillAction.NoteAdded => "已填别的账号，不覆盖，只在备注里记下 iLok 账号名",
        ILokFillAction.Skipped => "密码解不开，跳过",
        _ => "已是最新",
    };
}
