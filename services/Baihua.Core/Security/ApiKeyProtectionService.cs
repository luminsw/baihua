using Microsoft.Extensions.Logging;

namespace Baihua.Core.Security;

/// <summary>
/// API Key 保护服务 - AES-256-GCM 加密方案
/// 
/// 加密方案：
/// - AES-256-GCM + 机器指纹（跨机器可恢复）
/// 
/// 安全说明：
/// - AES 密钥派生自机器指纹（机器名+用户名+应用路径）
/// - 加密后数据格式包含版本标识
/// </summary>
public class ApiKeyProtectionService
{
    private readonly ILogger<ApiKeyProtectionService> _logger;

    public ApiKeyProtectionService(
        ILogger<ApiKeyProtectionService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 加密 API Key（使用 AES-256-GCM）
    /// </summary>
    public string Encrypt(string plainApiKey)
    {
        if (string.IsNullOrEmpty(plainApiKey))
            return "";

        try
        {
            // 默认使用 AES 加密（格式: A:base64）
            return AesApiKeyEncryption.Encrypt(plainApiKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "API Key AES 加密失败");
            throw new InvalidOperationException("Unable to encrypt API Key", ex);
        }
    }

    /// <summary>
    /// 解密 API Key（使用 AES-256-GCM）
    /// </summary>
    public string Decrypt(string encryptedApiKey)
    {
        if (string.IsNullOrEmpty(encryptedApiKey))
            return "";

        try
        {
            // 尝试 AES 解密（格式: A:base64）
            if (encryptedApiKey.StartsWith("A:"))
            {
                var result = AesApiKeyEncryption.Decrypt(encryptedApiKey);
                if (!string.IsNullOrEmpty(result))
                    return result;
            }

            _logger.LogError("API Key 解密失败");
            return "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "API Key 解密失败");
            return "";
        }
    }

    /// <summary>
    /// 掩码显示 API Key（如 sk-xxx...xxx）
    /// </summary>
    public static string Mask(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey) || apiKey.Length <= 8)
            return "***";

        return $"{apiKey[..6]}...{apiKey[^4..]}";
    }

    /// <summary>
    /// 检测加密方案
    /// </summary>
    public static EncryptionScheme? DetectScheme(string encryptedApiKey)
    {
        if (string.IsNullOrEmpty(encryptedApiKey))
            return null;
        
        if (encryptedApiKey.StartsWith("A:"))
            return EncryptionScheme.AesGcm;
        
        return null;
    }
}

/// <summary>
/// API Key 配置摘要（用于显示，不含敏感信息）
/// </summary>
public class ApiKeySummary
{
    /// <summary>提供商 ID</summary>
    public string ProviderId { get; set; } = "";

    /// <summary>提供商名称</summary>
    public string ProviderName { get; set; } = "";

    /// <summary>API Key 是否已配置（SQLite 加密存储）</summary>
    public bool HasApiKey { get; set; }

    /// <summary>API Key 掩码显示</summary>
    public string? KeyMask { get; set; }

    /// <summary>加密方案</summary>
    public EncryptionScheme? Scheme { get; set; }
}
