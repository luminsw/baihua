using System.Security.Cryptography;
using Baihua.Core.Security;
using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;
using Moq;
using Xunit;

namespace Baihua.Family.Tests.Services.Security;

/// <summary>
/// DataEncryptionService 单元测试 — 覆盖 GCM 加密/解密、CBC 向后兼容、篡改检测
/// </summary>
public class DataEncryptionServiceTests
{
    private readonly DataEncryptionService _service;
    private const string TestPassword = "TestP@ssw0rd!2026";

    public DataEncryptionServiceTests()
    {
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<DataEncryptionService>();
        _service = new DataEncryptionService(logger, TestLocalizer.Instance);
    }

    // ==================== GCM 加密/解密 ====================

    [Fact]
    public void Encrypt_WithGcm_ReturnsGcmPrefixedString()
    {
        var encrypted = _service.Encrypt("Hello World", TestPassword);
        Assert.StartsWith("gcm:", encrypted);
    }

    [Fact]
    public void EncryptDecrypt_GcmRoundTrip_ReturnsOriginalText()
    {
        const string original = "这是一段测试文本，包含中文和特殊字符：!@#$%^&*()";
        var encrypted = _service.Encrypt(original, TestPassword);
        var decrypted = _service.Decrypt(encrypted, TestPassword);
        Assert.Equal(original, decrypted);
    }

    [Fact]
    public void Encrypt_DifferentInput_ProducesDifferentOutput()
    {
        var enc1 = _service.Encrypt("Hello", TestPassword);
        var enc2 = _service.Encrypt("World", TestPassword);
        Assert.NotEqual(enc1, enc2);
    }

    [Fact]
    public void Encrypt_SameInput_ProducesDifferentCiphertext()
    {
        // 每次加密使用随机 salt+nonce，应产生不同密文
        var enc1 = _service.Encrypt("Same text", TestPassword);
        var enc2 = _service.Encrypt("Same text", TestPassword);
        Assert.NotEqual(enc1, enc2);
    }

    [Fact]
    public void Encrypt_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", _service.Encrypt("", TestPassword));
        Assert.Equal("", _service.Encrypt("", "any"));
    }

    [Fact]
    public void Decrypt_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", _service.Decrypt("", TestPassword));
        Assert.Equal("", _service.Decrypt("", "any"));
    }

    [Fact]
    public void Encrypt_NullOrEmptyPassword_Throws()
    {
        Assert.Throws<ArgumentException>(() => _service.Encrypt("data", ""));
    }

    [Fact]
    public void Decrypt_WrongPassword_Throws()
    {
        var encrypted = _service.Encrypt("secret data", TestPassword);
        Assert.Throws<InvalidOperationException>(() => _service.Decrypt(encrypted, "WrongPassword"));
    }

    [Fact]
    public void Decrypt_TamperedGcmData_Throws()
    {
        var encrypted = _service.Encrypt("sensitive data", TestPassword);
        // 篡改：修改 base64 中的一个字符
        var tampered = encrypted[..^2] + (encrypted[^2] == 'A' ? 'B' : 'A') + encrypted[^1];
        Assert.Throws<InvalidOperationException>(() => _service.Decrypt(tampered, TestPassword));
    }

    [Fact]
    public void GcmEncryptDecrypt_LongText_Works()
    {
        var longText = new string('中', 10000);
        var encrypted = _service.Encrypt(longText, TestPassword);
        var decrypted = _service.Decrypt(encrypted, TestPassword);
        Assert.Equal(longText, decrypted);
    }

    [Fact]
    public void GcmEncryptDecrypt_MultilineText_Works()
    {
        const string multiline = "Line 1\nLine 2\nLine 3\n\nLine 5";
        var encrypted = _service.Encrypt(multiline, TestPassword);
        var decrypted = _service.Decrypt(encrypted, TestPassword);
        Assert.Equal(multiline, decrypted);
    }

    // ==================== CBC 向后兼容 ====================

    /// <summary>
    /// 验证旧 CBC 格式密文仍可正常解密
    /// </summary>
    [Fact]
    public void Decrypt_LegacyCbcFormat_Works()
    {
        // 手动生成一段 CBC 密文（模拟旧版本加密的数据）
        var legacyEncrypted = EncryptLegacyCbc("legacy data from old version", TestPassword);
        // 确认不是 GCM 格式
        Assert.DoesNotContain("gcm:", legacyEncrypted);
        // 解密应成功
        var decrypted = _service.Decrypt(legacyEncrypted, TestPassword);
        Assert.Equal("legacy data from old version", decrypted);
    }

    [Fact]
    public void Decrypt_LegacyCbc_ChineseText_Works()
    {
        var legacyEncrypted = EncryptLegacyCbc("旧版本加密的中文数据", TestPassword);
        var decrypted = _service.Decrypt(legacyEncrypted, TestPassword);
        Assert.Equal("旧版本加密的中文数据", decrypted);
    }

    // ==================== 格式自动检测 ====================

    [Fact]
    public void Decrypt_AutoDetectsGcmVsCbc()
    {
        // GCM
        var gcmEnc = _service.Encrypt("GCM data", TestPassword);
        Assert.Equal("GCM data", _service.Decrypt(gcmEnc, TestPassword));

        // CBC (legacy)
        var cbcEnc = EncryptLegacyCbc("CBC data", TestPassword);
        Assert.Equal("CBC data", _service.Decrypt(cbcEnc, TestPassword));
    }

    // ==================== Helper: 模拟旧版 CBC 加密 ====================

    private static string EncryptLegacyCbc(string plainText, string password)
    {
        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        var salt = new byte[16];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(salt);

        using var pbkdf2 = new System.Security.Cryptography.Rfc2898DeriveBytes(
            password, salt, 100000, HashAlgorithmName.SHA256);
        aes.Key = pbkdf2.GetBytes(32);
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        using var ms = new MemoryStream();
        using var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write);
        using var sw = new StreamWriter(cs);
        sw.Write(plainText);
        sw.Close();

        var encrypted = ms.ToArray();
        var result = new byte[16 + 16 + encrypted.Length];
        Buffer.BlockCopy(salt, 0, result, 0, 16);
        Buffer.BlockCopy(aes.IV, 0, result, 16, 16);
        Buffer.BlockCopy(encrypted, 0, result, 32, encrypted.Length);

        return Convert.ToBase64String(result);
    }
}
