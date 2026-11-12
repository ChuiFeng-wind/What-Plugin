using System.Security.Cryptography;
using System.Text;

namespace WhatPlugin.Core;

/// <summary>备份里加密的那一段。全部是 Base64，方便放进 JSON。</summary>
public sealed record EncryptedSection(string Kdf, int Iterations, string Salt, string Nonce, string Tag, string Data);

/// <summary>备份密码不对，或者加密的那一段被改过。</summary>
public sealed class BackupPasswordException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 用备份密码加密账号、密码和序列号，让备份能带到别的电脑上（Windows 账号加密的换机就解不开）。
/// AES-256-GCM，密钥由备份密码经 PBKDF2-SHA256 派生。备份密码本身不保存在任何地方。
/// </summary>
public static class BackupCrypto
{
    public const string Kdf = "PBKDF2-SHA256";
    public const int DefaultIterations = 600_000;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    // 绑定用途，防止这一段被拿去别处冒充。
    private static readonly byte[] Associated = Encoding.UTF8.GetBytes("WhatPlugin-backup-secrets-v1");

    public static EncryptedSection Encrypt(string plaintext, string password, int iterations = DefaultIterations)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("备份密码不能为空", nameof(password));
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = DeriveKey(password, salt, iterations);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, data, cipher, tag, Associated);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(data);
        }
        return new EncryptedSection(Kdf, iterations,
            Convert.ToBase64String(salt), Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(cipher));
    }

    /// <exception cref="BackupPasswordException">密码不对或内容被改过。</exception>
    /// <exception cref="FormatException">这一段的格式不认识。</exception>
    public static string Decrypt(EncryptedSection section, string password)
    {
        if (section.Kdf != Kdf || section.Iterations < 10_000 || section.Iterations > 10_000_000)
            throw new FormatException("备份里加密部分的格式不认识。");
        byte[] salt, nonce, tag, cipher;
        try
        {
            salt = Convert.FromBase64String(section.Salt);
            nonce = Convert.FromBase64String(section.Nonce);
            tag = Convert.FromBase64String(section.Tag);
            cipher = Convert.FromBase64String(section.Data);
        }
        catch (FormatException e)
        {
            throw new FormatException("备份里加密的部分已损坏。", e);
        }
        if (salt.Length != SaltSize || nonce.Length != NonceSize || tag.Length != TagSize)
            throw new FormatException("备份里加密的部分已损坏。");

        var key = DeriveKey(password ?? "", salt, section.Iterations);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, Associated);
            return Encoding.UTF8.GetString(plain);
        }
        catch (AuthenticationTagMismatchException e)
        {
            throw new BackupPasswordException("备份密码不对，或者备份文件被改动过。", e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeySize);
}
