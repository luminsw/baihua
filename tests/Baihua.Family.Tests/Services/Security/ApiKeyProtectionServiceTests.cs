using Microsoft.Extensions.Logging;
using Baihua.Core.Security;
using Xunit;

namespace Baihua.Family.Tests.Services.Security;

public class ApiKeyProtectionServiceTests
{
    [Fact]
    public void Encrypt_Decrypt_EmptyString_ReturnsEmpty()
    {
        var service = CreateService();
        Assert.Equal("", service.Encrypt(""));
        Assert.Equal("", service.Decrypt(""));
        Assert.Equal("", service.Encrypt(null!));
        Assert.Equal("", service.Decrypt(null!));
    }

    [Fact]
    public void Encrypt_Decrypt_RoundTrip_Success()
    {
        var service = CreateService();
        var plain = "sk-test-12345";
        var encrypted = service.Encrypt(plain);
        var decrypted = service.Decrypt(encrypted);
        Assert.Equal(plain, decrypted);
    }

    [Fact]
    public void Encrypt_DifferentResultsForSameInput()
    {
        var service = CreateService();
        var plain = "sk-test-12345";
        var encrypted1 = service.Encrypt(plain);
        var encrypted2 = service.Encrypt(plain);
        // 由于使用随机 IV，相同明文应产生不同密文
        Assert.NotEqual(encrypted1, encrypted2);
    }

    [Fact]
    public void Decrypt_InvalidEncrypted_ReturnsEmpty()
    {
        var service = CreateService();
        Assert.Equal("", service.Decrypt("invalid-format"));
    }

    [Fact]
    public void Mask_EmptyOrShort_ReturnsMaskedPlaceholder()
    {
        Assert.Equal("***", ApiKeyProtectionService.Mask(""));
        Assert.Equal("***", ApiKeyProtectionService.Mask(null!));
        Assert.Equal("***", ApiKeyProtectionService.Mask("12345"));
        Assert.Equal("***", ApiKeyProtectionService.Mask("12345678"));
    }

    [Fact]
    public void Mask_LongKey_ShowsPrefixAndSuffix()
    {
        var key = "sk-proj-abcdefghij1234567890xyz";
        var masked = ApiKeyProtectionService.Mask(key);
        Assert.StartsWith("sk-pro", masked);
        Assert.Contains("...", masked);
        Assert.EndsWith("xyz", masked);
    }

    [Fact]
    public void DetectScheme_AesFormat_ReturnsAesGcm()
    {
        Assert.Equal(EncryptionScheme.AesGcm, ApiKeyProtectionService.DetectScheme("A:base64data"));
    }

    [Fact]
    public void DetectScheme_EmptyOrUnknown_ReturnsNull()
    {
        Assert.Null(ApiKeyProtectionService.DetectScheme(""));
        Assert.Null(ApiKeyProtectionService.DetectScheme(null!));
        Assert.Null(ApiKeyProtectionService.DetectScheme("X:unknown"));
    }

    [Fact]
    public void DetectScheme_Plaintext_ReturnsNull()
    {
        Assert.Null(ApiKeyProtectionService.DetectScheme("sk-plaintext-key"));
    }

    private static ApiKeyProtectionService CreateService()
    {
        var logger = new LoggerFactory().CreateLogger<ApiKeyProtectionService>();
        return new ApiKeyProtectionService(logger);
    }
}
