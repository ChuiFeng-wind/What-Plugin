using System.Text;
using System.Text.RegularExpressions;

namespace WhatPlugin.Core;

/// <summary>把名称化成可比较的形式，用于判断不同格式是否是同一款插件。</summary>
public static partial class Normalizer
{
    // 同一厂家在不同格式里的不同写法。键是 AlphanumericOnly 之后的文本。
    private static readonly Dictionary<string, string> VendorAliases = new()
    {
        ["wavesaudio"] = "Waves",
        ["ikmultimediaproduction"] = "IK Multimedia",
        ["celemonysoftware"] = "Celemony",
        ["pspaudiowarecom"] = "PSPaudioware",
        ["universalaudiouadx"] = "Universal Audio",
        ["overloudmorevox"] = "Overloud",
        ["johnshieldodeusaudio"] = "O Deus Audio",
    };

    public static string Name(string name)
    {
        // WaveShell 的各个格式和版本是同一个外壳，合成一条。
        if (name.StartsWith("WaveShell", StringComparison.OrdinalIgnoreCase))
            return "waveshell";

        var stripped = StripRepeatedly(name, TrailingFormatTag());
        stripped = StripRepeatedly(WithoutChannelVariant(stripped), TrailingFormatTag());
        var result = AlphanumericOnly(stripped);
        return result.Length > 0 ? result : AlphanumericOnly(name);
    }

    /// <summary>去掉声道版本后缀："C4 Mono/Stereo" → "C4"。同一款插件的声道版本合成一条。</summary>
    public static string WithoutChannelVariant(string name)
    {
        var stripped = BracketedVariant().Replace(ChannelVariant().Replace(name, ""), "");
        return stripped.Length > 0 ? stripped : name;
    }

    /// <summary>统一后的厂家显示名，例如 "iZotope, Inc." → "iZotope"。</summary>
    public static string CanonicalVendor(string vendor)
    {
        var stripped = StripRepeatedly(vendor.Trim(), CompanySuffix()).Trim();
        if (stripped.Length == 0)
            stripped = vendor.Trim();
        return VendorAliases.GetValueOrDefault(AlphanumericOnly(stripped), stripped);
    }

    public static string Vendor(string vendor) => AlphanumericOnly(CanonicalVendor(vendor));

    private static string StripRepeatedly(string text, Regex pattern)
    {
        while (true)
        {
            var next = pattern.Replace(text, "");
            if (next == text)
                return text;
            text = next;
        }
    }

    private static string AlphanumericOnly(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    // 不用 \b：下划线属于单词字符，"ProQ3_x64" 的 x64 前面没有 \b。
    // 单独的 32 / 64 只在括号里或紧跟连字符时才算架构标记，否则可能是产品名的一部分。
    [GeneratedRegex(@"(?:[\s_\-\.]*[\(\[]?(?<![a-z0-9])(x86_64|x64|x86|win64|win32|64\s?bit|32\s?bit|vst3|vst2|vst|clap|aax)[\)\]]?|\s*\((?:32|64)\)|[\-_\.](?:32|64))\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingFormatTag();

    // 数字只认环绕声声道布局，不能把 "Foo 2.5" 这样的版本号当成声道。
    [GeneratedRegex(@"\s+(?:Mono|Stereo|Quad|5\.0|5\.1|7\.1)(?:/(?:Mono|Stereo|Quad|5\.0|5\.1|7\.1))?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChannelVariant();

    // "Pro-C 2 (Mono)"、"Volcano 2 (Mono SC)"：只认括号里的写法，SC 单独出现可能是产品名。
    [GeneratedRegex(@"\s*\((?:Mono|Stereo|SC|Sidechain)(?:[\s/]+(?:Mono|Stereo|SC|Sidechain))*\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BracketedVariant();

    [GeneratedRegex(@"[,\.\s]+(inc|llc|ltd|limited|gmbh|corp|corporation|co|ab|s\.?a\.?|sas|bv|srl|pty)\b\.?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompanySuffix();
}
