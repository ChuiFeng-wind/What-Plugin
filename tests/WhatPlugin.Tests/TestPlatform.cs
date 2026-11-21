using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using WhatPlugin.Core;

namespace WhatPlugin.Tests;

/// <summary>只在 Windows 上有意义的测试（注册表卸载、Waves、Cakewalk、回收站、DPAPI）。其他系统上跳过。</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "只在 Windows 上运行";
    }
}

/// <summary>只在 Mac 上有意义的测试（钥匙串）。</summary>
public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "只在 Mac 上运行";
    }
}

/// <inheritdoc cref="WindowsFactAttribute"/>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "只在 Windows 上运行";
    }
}

/// <summary>
/// 测试里的样例路径按 Windows 写（D:\工程\歌.cpr）。纯逻辑的测试用 P() 换成当前系统的写法：
/// Windows 上原样不变，Mac 上变成 /D/工程/歌.cpr。解析 Windows 版宿主文件的测试不用它，那些只在 Windows 上跑。
/// </summary>
public static class TestPaths
{
    public static string P(string windowsPath)
    {
        if (OperatingSystem.IsWindows())
            return windowsPath;
        var path = windowsPath.Replace('\\', '/');
        return path.Length >= 2 && path[1] == ':' ? "/" + path[0] + path[2..] : path;
    }
}

internal static class TestPlatform
{
    /// <summary>
    /// 其他测试不能碰用户（或测试机）的默认钥匙串，所以非 Windows 上换成测试用的可逆加密。
    /// Windows 上不动，测的就是真正的 DPAPI；Mac 的钥匙串由 MacKeychainTests 用临时钥匙串单独测。
    /// </summary>
    [ModuleInitializer]
    internal static void UseTestSecretsOffWindows()
    {
        if (!OperatingSystem.IsWindows())
            SecretProtector.Backend = new TestSecretBackend();
    }
}

/// <summary>测试用：密钥固定在进程里，只用于跑测试。</summary>
internal sealed class TestSecretBackend : ISecretBackend
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    public byte[] Protect(byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return [.. nonce, .. tag, .. cipher];
    }

    public byte[] Unprotect(byte[] protectedBytes)
    {
        if (protectedBytes.Length < 28)
            throw new CryptographicException("太短");
        var plain = new byte[protectedBytes.Length - 28];
        using var aes = new AesGcm(Key, 16);
        aes.Decrypt(protectedBytes[..12], protectedBytes[28..], protectedBytes[12..28], plain);
        return plain;
    }
}
