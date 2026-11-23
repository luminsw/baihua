using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;
using Baihua.Core.Security;
using System.Security.Cryptography;
using System.Text;

namespace Baihua.Core.Security;

/// <summary>
/// 数据加密服务 - 用于加密和解密笔记内容
///
/// 加密方案：
/// - 新数据：AES-256-GCM 认证加密（格式: "gcm:{base64}"）
/// - 旧数据：AES-256-CBC + PKCS7（向后兼容解密，新加密不再使用）
/// - 密钥派生自用户密码或系统密钥（PBKDF2-SHA256, 100,000 迭代）
/// - 支持密码更改和密钥轮换
/// </summary>
public class DataEncryptionService
{
    private const string GcmPrefix = "gcm:";
    private const int SaltSize = 16;
    private const int IvSize = 12;  // GCM nonce 推荐 12 字节
    private const int TagSize = 16; // GCM 认证标签 16 字节
    private const int LegacyIvSize = 16; // CBC IV

    private readonly ILogger<DataEncryptionService> _logger;
    private readonly IStringLocalizer<SharedResources> _loc;

    public DataEncryptionService(ILogger<DataEncryptionService> logger, IStringLocalizer<SharedResources> loc)
    {
        _logger = logger;
        _loc = loc;
    }

    /// <summary>
    /// 加密数据（使用 AES-256-GCM 认证加密）
    /// </summary>
    public string Encrypt(string plainText, string password)
    {
        if (string.IsNullOrEmpty(plainText))
            return "";

        if (string.IsNullOrEmpty(password))
            throw new ArgumentException(_loc["Encryption_PasswordEmpty"], nameof(password));

        try
        {
            var salt = new byte[SaltSize];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(salt);

            var key = DeriveKey(password, salt);
            var nonce = new byte[IvSize];
            rng.GetBytes(nonce);

            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var cipherText = new byte[plainBytes.Length];
            var tag = new byte[TagSize];

            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Encrypt(nonce, plainBytes, cipherText, tag);

            // 组合：salt(16) + nonce(12) + tag(16) + cipherText
            var result = new byte[SaltSize + IvSize + TagSize + cipherText.Length];
            Buffer.BlockCopy(salt, 0, result, 0, SaltSize);
            Buffer.BlockCopy(nonce, 0, result, SaltSize, IvSize);
            Buffer.BlockCopy(tag, 0, result, SaltSize + IvSize, TagSize);
            Buffer.BlockCopy(cipherText, 0, result, SaltSize + IvSize + TagSize, cipherText.Length);

            return GcmPrefix + Convert.ToBase64String(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "数据加密失败");
            throw new InvalidOperationException(_loc["Encryption_EncryptFailed"], ex);
        }
    }

    /// <summary>
    /// 解密数据（自动检测 GCM 新格式和 CBC 旧格式）
    /// </summary>
    public string Decrypt(string encryptedText, string password)
    {
        if (string.IsNullOrEmpty(encryptedText))
            return "";

        if (string.IsNullOrEmpty(password))
            throw new ArgumentException(_loc["Encryption_PasswordEmpty"], nameof(password));

        try
        {
            // 检测新格式 "gcm:" 前缀
            if (encryptedText.StartsWith(GcmPrefix))
            {
                return DecryptGcm(encryptedText[GcmPrefix.Length..], password);
            }

            // 向后兼容：尝试解密旧 CBC 格式
            return DecryptLegacyCbc(encryptedText, password);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "数据解密失败");
            throw new InvalidOperationException(_loc["Encryption_DecryptFailed"], ex);
        }
    }

    /// <summary>
    /// GCM 格式解密
    /// </summary>
    private string DecryptGcm(string base64Data, string password)
    {
        var data = Convert.FromBase64String(base64Data);

        if (data.Length < SaltSize + IvSize + TagSize)
            throw new ArgumentException(_loc["Encryption_GcmDataInvalid"]);

        var salt = new byte[SaltSize];
        var nonce = new byte[IvSize];
        var tag = new byte[TagSize];
        var cipherText = new byte[data.Length - SaltSize - IvSize - TagSize];

        Buffer.BlockCopy(data, 0, salt, 0, SaltSize);
        Buffer.BlockCopy(data, SaltSize, nonce, 0, IvSize);
        Buffer.BlockCopy(data, SaltSize + IvSize, tag, 0, TagSize);
        Buffer.BlockCopy(data, SaltSize + IvSize + TagSize, cipherText, 0, cipherText.Length);

        var key = DeriveKey(password, salt);
        var plainBytes = new byte[cipherText.Length];

        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Decrypt(nonce, cipherText, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }

    /// <summary>
    /// 旧 CBC 格式解密（向后兼容，仅解密旧数据）
    /// </summary>
    private string DecryptLegacyCbc(string base64Data, string password)
    {
        var encryptedData = Convert.FromBase64String(base64Data);

        if (encryptedData.Length < SaltSize + LegacyIvSize)
            throw new ArgumentException(_loc["Encryption_CbcDataInvalid"]);

        var salt = new byte[SaltSize];
        var iv = new byte[LegacyIvSize];
        var cipherText = new byte[encryptedData.Length - SaltSize - LegacyIvSize];

        Buffer.BlockCopy(encryptedData, 0, salt, 0, SaltSize);
        Buffer.BlockCopy(encryptedData, SaltSize, iv, 0, LegacyIvSize);
        Buffer.BlockCopy(encryptedData, SaltSize + LegacyIvSize, cipherText, 0, cipherText.Length);

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = DeriveKey(password, salt);
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
        using var msDecrypt = new MemoryStream(cipherText);
        using var csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read);
        using var srDecrypt = new StreamReader(csDecrypt);

        return srDecrypt.ReadToEnd();
    }

    /// <summary>
    /// 派生密钥（PBKDF2-SHA256, 100,000 迭代 → 256-bit）
    /// </summary>
    private byte[] DeriveKey(string password, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, 100000, HashAlgorithmName.SHA256, 32);
    }

}
