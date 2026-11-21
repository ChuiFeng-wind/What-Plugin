using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using WhatPlugin.Core;

namespace WhatPlugin.Tests;

/// <summary>
/// 钥匙串加密只能在 Mac 上测。每个测试建一个临时钥匙串文件，测完删掉，不碰用户的默认钥匙串。
/// </summary>
public sealed class MacKeychainTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-kc-" + Guid.NewGuid().ToString("N"));
    private readonly string _keychain;
    private bool _created;

    public MacKeychainTests()
    {
        Directory.CreateDirectory(_dir);
        _keychain = Path.Combine(_dir, "test.keychain-db");
    }

    public void Dispose()
    {
        if (_created)
            Security("delete-keychain", _keychain);
        Directory.Delete(_dir, recursive: true);
    }

    private void CreateKeychain()
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Security("create-keychain", "-p", password, _keychain);
        _created = true;
        Security("unlock-keychain", "-p", password, _keychain);
    }

    private static void Security(params string[] args) => Security(true, args);

    private static bool Security(bool mustSucceed, params string[] args)
    {
        var info = new ProcessStartInfo("/usr/bin/security") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (mustSucceed)
            Assert.True(process.ExitCode == 0, $"security {args[0]} 失败：{error}");
        return process.ExitCode == 0;
    }

    [MacFact]
    [SupportedOSPlatform("macos")]
    public void SecretsRoundTripAndSurviveANewInstance()
    {
        CreateKeychain();
        var first = new MacKeychainSecretBackend(_keychain);
        var sealedBytes = first.Protect(Encoding.UTF8.GetBytes("密码 p@ss"));

        Assert.NotEqual(sealedBytes, first.Protect(Encoding.UTF8.GetBytes("密码 p@ss"))); // 每次的 nonce 不同
        Assert.Equal("密码 p@ss", Encoding.UTF8.GetString(first.Unprotect(sealedBytes)));

        // 换一个实例（相当于重开软件）：密钥从钥匙串里读回来。
        var second = new MacKeychainSecretBackend(_keychain);
        Assert.Equal("密码 p@ss", Encoding.UTF8.GetString(second.Unprotect(sealedBytes)));

        // 被改过的数据解不开。
        sealedBytes[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => second.Unprotect(sealedBytes));
        Assert.ThrowsAny<CryptographicException>(() => second.Unprotect([1, 2, 3]));
    }

    [MacFact]
    [SupportedOSPlatform("macos")]
    public void AnotherKeychainCannotDecrypt()
    {
        CreateKeychain();
        var sealedBytes = new MacKeychainSecretBackend(_keychain).Protect("x"u8.ToArray());

        // 另一个钥匙串里没有这把密钥：解不开，也不会因为解密就新建一把。
        using var other = new MacKeychainTests();
        other.CreateKeychain();
        var stranger = new MacKeychainSecretBackend(other._keychain);
        Assert.ThrowsAny<CryptographicException>(() => stranger.Unprotect(sealedBytes));
        Assert.False(Security(false, "find-generic-password", "-s", MacKeychainSecretBackend.Service, other._keychain));
    }
}
