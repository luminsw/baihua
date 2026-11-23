using System.Security.Cryptography;
using System.Text;
using Baihua.Core.Security;
using Xunit;

namespace Baihua.Family.Tests.Security;

// 定义测试集合，禁止并发运行（避免环境变量冲突）
[Collection("AesApiKeyEncryption")]
public class AesApiKeyEncryptionTests : IDisposable
{
    private readonly string? _originalEncKey;
    private readonly string? _originalHome;
    private readonly string _testKeyFilePath;
    private readonly string _testHome;

    public AesApiKeyEncryptionTests()
    {
        _originalEncKey = Environment.GetEnvironmentVariable("BAIHUA_ENCRYPTION_KEY");
        _originalHome = Environment.GetEnvironmentVariable("BAIHUA_HOME");

        // 用 BAIHUA_HOME 指向临时目录，BaihuaPaths.KeyFile = $HOME/db/.baihua-key
        _testHome = Path.Combine(Path.GetTempPath(), $"bh_test_{Guid.NewGuid():N}");
        var testDb = Path.Combine(_testHome, "db");
        Directory.CreateDirectory(testDb);
        _testKeyFilePath = Path.Combine(testDb, ".baihua-key");

        Environment.SetEnvironmentVariable("BAIHUA_HOME", _testHome);
        Environment.SetEnvironmentVariable("BAIHUA_ENCRYPTION_KEY", null);
        Baihua.Contracts.BaihuaPaths.Reset();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("BAIHUA_ENCRYPTION_KEY", _originalEncKey);
        Environment.SetEnvironmentVariable("BAIHUA_HOME", _originalHome);

        if (Directory.Exists(_testHome))
        {
            Directory.Delete(_testHome, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static void SetEncryptionKey(string key)
    {
        Environment.SetEnvironmentVariable("BAIHUA_ENCRYPTION_KEY", key);
    }

    [Fact]
    public void Encrypt_EmptyString_ReturnsEmpty()
    {
        var result = AesApiKeyEncryption.Encrypt("");
        Assert.Equal("", result);
    }

    [Fact]
    public void Encrypt_Null_ReturnsEmpty()
    {
        var result = AesApiKeyEncryption.Encrypt(null!);
        Assert.Equal("", result);
    }

    [Fact]
    public void Encrypt_ValidKey_ReturnsEncryptedString()
    {
        SetEncryptionKey("test-encryption-key-12345");
        var plainText = "sk-test-api-key-12345";

        var encrypted = AesApiKeyEncryption.Encrypt(plainText);

        Assert.NotEmpty(encrypted);
        Assert.StartsWith("A:", encrypted);
    }

    [Fact]
    public void Encrypt_SamePlaintext_ProducesDifferentCiphertext()
    {
        SetEncryptionKey("test-encryption-key-12345");
        var plainText = "sk-test-api-key-12345";

        var encrypted1 = AesApiKeyEncryption.Encrypt(plainText);
        var encrypted2 = AesApiKeyEncryption.Encrypt(plainText);

        Assert.NotEqual(encrypted1, encrypted2);
    }

    [Fact]
    public void Decrypt_ValidEncryptedText_ReturnsOriginal()
    {
        SetEncryptionKey("test-encryption-key-12345");
        var plainText = "sk-test-api-key-12345";

        var encrypted = AesApiKeyEncryption.Encrypt(plainText);
        var decrypted = AesApiKeyEncryption.Decrypt(encrypted);

        Assert.Equal(plainText, decrypted);
    }

    [Fact]
    public void Decrypt_EmptyString_ReturnsEmpty()
    {
        var result = AesApiKeyEncryption.Decrypt("");
        Assert.Equal("", result);
    }

    [Fact]
    public void Decrypt_Null_ReturnsEmpty()
    {
        var result = AesApiKeyEncryption.Decrypt(null!);
        Assert.Equal("", result);
    }

    [Fact]
    public void Decrypt_InvalidPrefix_ReturnsEmpty()
    {
        var result = AesApiKeyEncryption.Decrypt("invalid:ciphertext");
        Assert.Equal("", result);
    }

    [Fact]
    public void Decrypt_WrongKey_ReturnsEmpty()
    {
        SetEncryptionKey("key-one-12345");
        var plainText = "sk-test-api-key-12345";
        var encrypted = AesApiKeyEncryption.Encrypt(plainText);

        SetEncryptionKey("key-two-67890");
        var decrypted = AesApiKeyEncryption.Decrypt(encrypted);

        Assert.NotEqual(plainText, decrypted);
    }

    [Fact]
    public void DecryptWithFingerprint_EmptyCipherText_ReturnsEmpty()
    {
        var fingerprint = SHA256.HashData(Encoding.UTF8.GetBytes("test"));
        var result = AesApiKeyEncryption.DecryptWithFingerprint("", fingerprint);
        Assert.Equal("", result);
    }

    [Fact]
    public void DecryptWithFingerprint_InvalidPrefix_ReturnsEmpty()
    {
        var fingerprint = SHA256.HashData(Encoding.UTF8.GetBytes("test"));
        var result = AesApiKeyEncryption.DecryptWithFingerprint("invalid", fingerprint);
        Assert.Equal("", result);
    }

    [Fact]
    public void DecryptWithFingerprint_WrongFingerprint_ReturnsEmpty()
    {
        SetEncryptionKey("correct-key");
        var plainText = "sk-test-api-key-12345";
        var encrypted = AesApiKeyEncryption.Encrypt(plainText);

        var wrongFingerprint = SHA256.HashData(Encoding.UTF8.GetBytes("wrong-key"));
        var decrypted = AesApiKeyEncryption.DecryptWithFingerprint(encrypted, wrongFingerprint);

        Assert.NotEqual(plainText, decrypted);
        Assert.Equal("", decrypted);
    }

    [Fact]
    public void DecryptWithFingerprint_CorrectFingerprint_ReturnsOriginal()
    {
        SetEncryptionKey("test-encryption-key");
        var plainText = "sk-test-api-key";
        var encrypted = AesApiKeyEncryption.Encrypt(plainText);
        var fingerprint = AesApiKeyEncryption.ResolveFingerprint();

        var decrypted = AesApiKeyEncryption.DecryptWithFingerprint(encrypted, fingerprint);

        Assert.Equal(plainText, decrypted);
    }

    [Fact]
    public void EncryptDecrypt_UnicodeText_WorksCorrectly()
    {
        SetEncryptionKey("test-key-unicode");
        var plainText = "API密钥: sk-测试-ключ-🔑";

        var encrypted = AesApiKeyEncryption.Encrypt(plainText);
        var decrypted = AesApiKeyEncryption.Decrypt(encrypted);

        Assert.Equal(plainText, decrypted);
    }

    [Fact]
    public void EncryptDecrypt_LongText_WorksCorrectly()
    {
        SetEncryptionKey("test-key-long");
        var plainText = new string('A', 10000);

        var encrypted = AesApiKeyEncryption.Encrypt(plainText);
        var decrypted = AesApiKeyEncryption.Decrypt(encrypted);

        Assert.Equal(plainText, decrypted);
    }

    [Fact]
    public void GenerateKeyFile_CreatesKeyFile()
    {
        Assert.False(File.Exists(_testKeyFilePath));

        var key = AesApiKeyEncryption.GenerateKeyFile();

        Assert.True(File.Exists(_testKeyFilePath));
        Assert.NotEmpty(key);
        Assert.Equal(64, key.Length);
    }

    [Fact]
    public void ResolveFingerprint_PreferEnvVarOverFile()
    {
        var envKey = "environment-variable-key";
        Environment.SetEnvironmentVariable("BAIHUA_ENCRYPTION_KEY", envKey);

        var fingerprint = AesApiKeyEncryption.ResolveFingerprint();
        var expectedFingerprint = SHA256.HashData(Encoding.UTF8.GetBytes(envKey));

        Assert.Equal(expectedFingerprint, fingerprint);
    }

    [Fact]
    public void ResolveFingerprint_FallsBackToMachine()
    {
        Environment.SetEnvironmentVariable("BAIHUA_ENCRYPTION_KEY", null);

        var fingerprint = AesApiKeyEncryption.ResolveFingerprint();

        Assert.NotNull(fingerprint);
        Assert.Equal(32, fingerprint.Length);
    }
}
