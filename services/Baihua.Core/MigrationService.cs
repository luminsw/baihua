using Microsoft.Extensions.Logging;
using Baihua.Data;

namespace Baihua.Core.Services;

/// <summary>
/// API Key 加密迁移服务：处理因加密密钥变化导致无法解密的 API Key 迁移。
/// 只在启动时由 StartupOrchestratorHostedService 调用一次。
/// </summary>
public class MigrationService
{
    private readonly ILogger<MigrationService> _logger;

    public MigrationService(ILogger<MigrationService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 检测并迁移因加密密钥变化导致无法解密的 API Key。
    /// 场景：用户未设置 BAIHUA_ENCRYPTION_KEY，容器重建后机器指纹变化，
    /// 导致之前加密的 API Key 无法解密。
    ///
    /// 迁移策略：
    /// 1. 如果 .baihua-key 文件已存在：尝试用旧版机器指纹解密失败的 Key，成功后用 .baihua-key 重新加密
    /// 2. 如果 .baihua-key 文件不存在：尝试用旧版机器指纹解密所有 Key，成功后生成 .baihua-key 并重新加密
    ///    如果没有旧数据能解密，直接生成 .baihua-key（确保后续加密使用固定密钥）
    /// </summary>
    public void MigrateApiKeysIfNeeded(AIDbContext dbContext)
    {
        try
        {
            var providers = dbContext.AiProviderSettings
                .Where(p => !string.IsNullOrEmpty(p.EncryptedApiKey))
                .ToList();

            var keyFileExists = File.Exists(Baihua.Core.Security.AesApiKeyEncryption.KeyFilePath);

            if (!keyFileExists && providers.Count == 0)
            {
                // 没有旧数据，直接生成固定密钥文件，确保后续加密稳定
                Baihua.Core.Security.AesApiKeyEncryption.GenerateKeyFile();
                _logger.LogInformation("已自动生成固定加密密钥文件：{KeyFile}", Baihua.Core.Security.AesApiKeyEncryption.KeyFilePath);
                return;
            }

            if (providers.Count == 0)
                return;

            var legacyFingerprint = Baihua.Core.Security.AesApiKeyEncryption.GetLegacyMachineFingerprint();
            var migratedCount = 0;
            var needsKeyFile = !keyFileExists;

            foreach (var provider in providers)
            {
                // 先用当前密钥尝试解密
                var currentDecrypted = Baihua.Core.Security.AesApiKeyEncryption.Decrypt(provider.EncryptedApiKey!);
                if (!string.IsNullOrEmpty(currentDecrypted))
                    continue; // 当前密钥能解密，无需迁移

                // 当前密钥无法解密，尝试用旧版机器指纹解密
                var legacyDecrypted = Baihua.Core.Security.AesApiKeyEncryption.DecryptWithFingerprint(
                    provider.EncryptedApiKey!, legacyFingerprint);

                if (string.IsNullOrEmpty(legacyDecrypted))
                {
                    // 当前/旧指纹都解不开（如密钥来自已丢失的 BAIHUA_ENCRYPTION_KEY 或已消失的旧 pod 指纹）：
                    // 仍然生成固定密钥文件，保证之后的加密/解密稳定（Family 与 AI 双进程、pod 重建都不再漂移），
                    // 解不开的旧 Key 由用户重存。
                    if (needsKeyFile)
                    {
                        Baihua.Core.Security.AesApiKeyEncryption.GenerateKeyFile();
                        needsKeyFile = false;
                        _logger.LogInformation("已生成固定加密密钥文件（存在无法迁移的旧 Key，等待在 WebUI 重新配置）");
                    }
                    _logger.LogWarning("API Key 无法解密（Provider={ProviderId}），可能使用了已丢失的 BAIHUA_ENCRYPTION_KEY。请重新在 WebUI 中设置 API Key。", provider.ProviderId);
                    continue;
                }

                // 旧密钥能解密，需要生成固定密钥文件（如果还没有）
                if (needsKeyFile)
                {
                    Baihua.Core.Security.AesApiKeyEncryption.GenerateKeyFile();
                    needsKeyFile = false;
                    _logger.LogInformation("已生成固定加密密钥文件，准备迁移 API Key");
                }

                // 用新密钥重新加密
                var reEncrypted = Baihua.Core.Security.AesApiKeyEncryption.Encrypt(legacyDecrypted);
                provider.EncryptedApiKey = reEncrypted;
                migratedCount++;
                _logger.LogInformation("API Key 已自动迁移：Provider={ProviderId}（加密密钥从机器指纹升级到固定密钥）", provider.ProviderId);
            }

            // 如果所有 Key 都能用当前密钥解密，但 .baihua-key 还不存在，说明当前用的是 BAIHUA_ENCRYPTION_KEY
            // 自动持久化到 .baihua-key，避免环境变量丢失后无法解密
            if (!keyFileExists && !needsKeyFile && migratedCount == 0)
            {
                var envKey = Environment.GetEnvironmentVariable("BAIHUA_ENCRYPTION_KEY");
                if (!string.IsNullOrWhiteSpace(envKey))
                {
                    Baihua.Core.Security.AesApiKeyEncryption.GenerateKeyFile();
                    // 重新加密所有 Key（从环境变量密钥迁移到文件密钥）
                    foreach (var provider in providers)
                    {
                        var decrypted = Baihua.Core.Security.AesApiKeyEncryption.Decrypt(provider.EncryptedApiKey!);
                        if (!string.IsNullOrEmpty(decrypted))
                        {
                            provider.EncryptedApiKey = Baihua.Core.Security.AesApiKeyEncryption.Encrypt(decrypted);
                            migratedCount++;
                        }
                    }
                    _logger.LogInformation("已将 BAIHUA_ENCRYPTION_KEY 持久化到密钥文件，并迁移 {Count} 个 API Key", migratedCount);
                }
                else
                {
                    // 当前用的是机器指纹，直接生成 .baihua-key（但机器指纹会变化，这是个问题）
                    // 为了稳定性，生成 .baihua-key 并重新加密
                    Baihua.Core.Security.AesApiKeyEncryption.GenerateKeyFile();
                    foreach (var provider in providers)
                    {
                        var decrypted = Baihua.Core.Security.AesApiKeyEncryption.Decrypt(provider.EncryptedApiKey!);
                        if (!string.IsNullOrEmpty(decrypted))
                        {
                            provider.EncryptedApiKey = Baihua.Core.Security.AesApiKeyEncryption.Encrypt(decrypted);
                            migratedCount++;
                        }
                    }
                    _logger.LogInformation("已生成固定加密密钥文件，并将 {Count} 个 API Key 从机器指纹迁移到固定密钥", migratedCount);
                }
            }

            if (migratedCount > 0)
            {
                dbContext.SaveChanges();
                _logger.LogInformation("API Key 迁移完成：共迁移 {Count} 个提供商的加密密钥", migratedCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "API Key 迁移过程出错: {Message}", ex.Message);
        }
    }
}
