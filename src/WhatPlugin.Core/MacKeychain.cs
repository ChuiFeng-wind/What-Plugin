using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace WhatPlugin.Core;

/// <summary>
/// Mac 上的账号密码加密：随机生成一把 256 位密钥，存进当前用户的“钥匙串”，用它做 AES-GCM 加密。
/// 密钥只在钥匙串里，不写任何文件；数据库被拷到别的电脑或别的用户下解不开，和 Windows 的 DPAPI 一样。
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainSecretBackend : ISecretBackend
{
    // 存进钥匙串的那一项。改了以后以前保存的密码就解不开了。
    public const string Service = "WhatPlugin";
    public const string Account = "secret-key-v1";

    // 加密结果：版本(1) + nonce(12) + tag(16) + 密文。
    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 1 + NonceSize + TagSize;

    private readonly string? _keychainPath;
    private readonly Lock _lock = new();
    private byte[]? _key;

    /// <param name="keychainPath">指定钥匙串文件，只给测试用；null 是当前用户的默认钥匙串。</param>
    public MacKeychainSecretBackend(string? keychainPath = null) => _keychainPath = keychainPath;

    public byte[] Protect(byte[] plain)
    {
        var key = Key(create: true)!;
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(nonce, plain, cipher, tag);
        return [FormatVersion, .. nonce, .. tag, .. cipher];
    }

    public byte[] Unprotect(byte[] protectedBytes)
    {
        if (protectedBytes.Length < HeaderSize || protectedBytes[0] != FormatVersion)
            throw new CryptographicException("不是这台 Mac 保存的密码");
        // 钥匙串里还没有密钥，说明这份数据不是在这里加密的，不必新建一把。
        var key = Key(create: false) ?? throw new CryptographicException("钥匙串里没有本软件的密钥");
        var plain = new byte[protectedBytes.Length - HeaderSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(
            protectedBytes.AsSpan(1, NonceSize),
            protectedBytes.AsSpan(HeaderSize),
            protectedBytes.AsSpan(1 + NonceSize, TagSize),
            plain);
        return plain;
    }

    private byte[]? Key(bool create)
    {
        lock (_lock)
        {
            if (_key != null)
                return _key;
            var keychain = OpenKeychain();
            try
            {
                _key = Find(keychain);
                if (_key == null && create)
                {
                    var fresh = RandomNumberGenerator.GetBytes(32);
                    var status = Native.SecKeychainAddGenericPassword(keychain,
                        (uint)ServiceBytes.Length, ServiceBytes, (uint)AccountBytes.Length, AccountBytes,
                        (uint)fresh.Length, fresh, IntPtr.Zero);
                    // 另一个进程刚好同时建了一把：以钥匙串里的为准。
                    if (status == Native.ErrSecDuplicateItem)
                    {
                        _key = Find(keychain);
                    }
                    else
                    {
                        Check(status, "保存密钥");
                        _key = fresh;
                    }
                }
                return _key;
            }
            finally
            {
                if (keychain != IntPtr.Zero)
                    Native.CFRelease(keychain);
            }
        }
    }

    private static readonly byte[] ServiceBytes = Encoding.UTF8.GetBytes(Service);
    private static readonly byte[] AccountBytes = Encoding.UTF8.GetBytes(Account);

    private IntPtr OpenKeychain()
    {
        if (_keychainPath == null)
            return IntPtr.Zero; // 默认钥匙串
        var path = Encoding.UTF8.GetBytes(_keychainPath + "\0");
        Check(Native.SecKeychainOpen(path, out var keychain), "打开钥匙串");
        return keychain;
    }

    private static byte[]? Find(IntPtr keychain)
    {
        var status = Native.SecKeychainFindGenericPassword(keychain,
            (uint)ServiceBytes.Length, ServiceBytes, (uint)AccountBytes.Length, AccountBytes,
            out var length, out var data, IntPtr.Zero);
        if (status == Native.ErrSecItemNotFound)
            return null;
        Check(status, "读取密钥");
        try
        {
            if (length != 32)
                throw new CryptographicException("钥匙串里的密钥长度不对");
            var key = new byte[length];
            Marshal.Copy(data, key, 0, key.Length);
            return key;
        }
        finally
        {
            Native.SecKeychainItemFreeContent(IntPtr.Zero, data);
        }
    }

    /// <summary>用户在系统弹窗里点了“拒绝”等情况都会到这里。</summary>
    private static int Check(int status, string what) =>
        status == 0 ? 0 : throw new CryptographicException($"{what}失败：钥匙串返回 {status}");

    private static class Native
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        public const int ErrSecItemNotFound = -25300;
        public const int ErrSecDuplicateItem = -25299;

        // 这组接口苹果标为“旧接口”，但一直可用，而且能指定钥匙串文件，测试时不碰用户的钥匙串。
        [DllImport(Security)]
        public static extern int SecKeychainOpen(byte[] pathName, out IntPtr keychain);

        [DllImport(Security)]
        public static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray,
            uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName,
            out uint passwordLength, out IntPtr passwordData, IntPtr itemRef);

        [DllImport(Security)]
        public static extern int SecKeychainAddGenericPassword(IntPtr keychain,
            uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName,
            uint passwordLength, byte[] passwordData, IntPtr itemRef);

        [DllImport(Security)]
        public static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

        [DllImport(CoreFoundation)]
        public static extern void CFRelease(IntPtr cf);
    }
}
