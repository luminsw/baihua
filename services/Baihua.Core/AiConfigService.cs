using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Core.Models;
using Baihua.Core.Security;
using Baihua.Contracts.Ai;

namespace Baihua.Core.Services;

/// <summary>
/// AI 配置管理服务 - 使用 SQLite + EF Core 加密存储 API Key（AI 服务进程内实现，直读/写 ai.db）。
/// 
/// 一服务一数据库：本实现仅供 AI 服务使用（唯一持有 API Key 的进程）；
/// Family 进程通过 <see cref="IAiConfigService"/> 的 HTTP 实现访问，不接触 ai.db。
/// 
/// 加密方案：
/// - AES-256-GCM + 机器指纹派生密钥（默认）
/// - 兼容 Data Protection 旧数据
/// </summary>
public class AiConfigService : IAiConfigService
{
    private readonly IDbContextFactory<AIDbContext> _dbContextFactory;
    private readonly ApiKeyProtectionService _protectionService;
    private readonly DataEncryptionService? _dataEncryption;
    private readonly ILogger<AiConfigService> _logger;

    public AiConfigService(
        IDbContextFactory<AIDbContext> dbContextFactory,
        ApiKeyProtectionService protectionService,
        DataEncryptionService? dataEncryption = null,
        ILogger<AiConfigService>? logger = null)
    {
        _dbContextFactory = dbContextFactory;
        _protectionService = protectionService;
        _dataEncryption = dataEncryption;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AiConfigService>.Instance;
    }

    /// <summary>
    /// 获取所有启用的 AI 提供商（用于前端显示，不含密钥）
    /// </summary>
    public List<AiProviderConfig> GetProviders()
    {
        try
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            var dbProviders = dbContext.AiProviderSettings
                .Where(p => p.IsEnabled)
                .OrderBy(p => p.SortOrder)
                .ThenBy(p => p.Id)
                .ToList();
            return dbProviders.Select(MapToProviderConfig).ToList();
        }
        catch (Exception ex)
        {
            // ai.db 由 AI 服务迁移；本进程（Family）启动早期表可能尚未就绪，防御性降级
            _logger.LogWarning(ex, "读取 AI 提供方配置失败（AI 数据库可能尚未就绪），返回空列表");
            return new List<AiProviderConfig>();
        }
    }

    /// <summary>
    /// 获取 API Key 配置摘要（用于设置页面显示）
    /// </summary>
    public List<ApiKeySummary> GetApiKeySummaries()
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        var dbProviders = dbContext.AiProviderSettings
            .Where(p => p.IsEnabled)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Id)
            .ToList();
        var result = new List<ApiKeySummary>();

        foreach (var provider in dbProviders)
        {
            var hasApiKey = !string.IsNullOrEmpty(provider.EncryptedApiKey);
            string? keyMask = null;
            EncryptionScheme? scheme = null;

            if (hasApiKey)
            {
                scheme = ApiKeyProtectionService.DetectScheme(provider.EncryptedApiKey!);
                try
                {
                    var decrypted = _protectionService.Decrypt(provider.EncryptedApiKey!);
                    keyMask = ApiKeyProtectionService.Mask(decrypted);
                }
                catch
                {
                    keyMask = "***error***";
                }
            }

            result.Add(new ApiKeySummary
            {
                ProviderId = provider.ProviderId,
                ProviderName = provider.ProviderName,
                HasApiKey = hasApiKey,
                KeyMask = keyMask,
                Scheme = scheme
            });
        }

        return result;
    }

    /// <summary>
    /// 获取指定 Provider 的有效 API Key（唯一来源：SQLite 加密存储）
    /// 注意：不再支持环境变量或配置文件中的 API Key
    /// </summary>
    public string GetApiKey(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return "";

        var id = providerId.Trim();

        // 仅从 SQLite 解密获取（API Key 的唯一存储位置）
        using var dbContext = _dbContextFactory.CreateDbContext();
        var dbProvider = dbContext.AiProviderSettings
            .FirstOrDefault(p => p.ProviderId == id);
        
        if (!string.IsNullOrEmpty(dbProvider?.EncryptedApiKey))
        {
            var decrypted = _protectionService.Decrypt(dbProvider.EncryptedApiKey);
            if (!string.IsNullOrEmpty(decrypted))
            {
                _logger.LogDebug("使用 SQLite 存储的 API Key");
                return decrypted;
            }
        }

        _logger.LogWarning("未找到 Provider {ProviderId} 的 API Key，请在 WebUI 的 AI配置 页面配置", id);
        return "";
    }

    /// <summary>
    /// 保存 Provider 配置（API Key 自动加密）
    /// </summary>
    public void SaveProvider(AiProviderSetting setting, string? plainApiKey = null)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        // 加密 API Key（如果提供）
        string? encryptedKey = null;
        if (!string.IsNullOrWhiteSpace(plainApiKey))
        {
            encryptedKey = _protectionService.Encrypt(plainApiKey.Trim());
        }

        // 获取现有配置以保留 API Key（如果未提供新 Key）
        var existing = dbContext.AiProviderSettings
            .FirstOrDefault(p => p.ProviderId == setting.ProviderId);
        
        // null = 不修改（保留旧 key）；"" = 清空 key
        if (plainApiKey == null && existing != null)
        {
            encryptedKey = existing.EncryptedApiKey;
        }

        setting.EncryptedApiKey = encryptedKey;

        // 如果设为主提供商，先取消其他提供商的主标记
        if (setting.IsMain)
        {
            var otherMainProviders = dbContext.AiProviderSettings
                .Where(p => p.ProviderId != setting.ProviderId && p.IsMain)
                .ToList();
            foreach (var p in otherMainProviders)
            {
                p.IsMain = false;
            }
        }

        if (existing != null)
        {
            // 更新现有配置
            existing.ProviderName = setting.ProviderName;
            existing.BaseUrl = setting.BaseUrl;
            existing.AnthropicBaseUrl = setting.AnthropicBaseUrl;
            existing.EncryptedApiKey = setting.EncryptedApiKey;
            existing.IsMain = setting.IsMain;
            existing.ModelsJson = setting.ModelsJson;
            existing.SortOrder = setting.SortOrder;
            existing.IsEnabled = setting.IsEnabled;
            existing.Tier = setting.Tier;
            dbContext.AiProviderSettings.Update(existing);
        }
        else
        {
            // 添加新配置
            dbContext.AiProviderSettings.Add(setting);
        }
        
        dbContext.SaveChanges();
        
        _logger.LogInformation("已保存 Provider 配置: {ProviderId}, API Key: {HasKey}", 
            setting.ProviderId, !string.IsNullOrEmpty(encryptedKey));
    }

    /// <summary>
    /// 删除 Provider 配置
    /// </summary>
    public bool DeleteProvider(string providerId)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        var provider = dbContext.AiProviderSettings
            .FirstOrDefault(p => p.ProviderId == providerId);
        
        if (provider != null)
        {
            dbContext.AiProviderSettings.Remove(provider);
            dbContext.SaveChanges();
            return true;
        }
        return false;
    }

    /// <summary>
    /// 获取单个 Provider 配置
    /// </summary>
    public AiProviderConfig? GetProvider(string providerId)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        var dbProvider = dbContext.AiProviderSettings
            .FirstOrDefault(p => p.ProviderId == providerId);
        return dbProvider != null ? MapToProviderConfig(dbProvider) : null;
    }

    /// <summary>
    /// 获取主 Provider
    /// </summary>
    public AiProviderConfig? GetMainProvider()
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        var provider = dbContext.AiProviderSettings
            .Where(p => p.IsMain && p.IsEnabled)
            .OrderBy(p => p.SortOrder)
            .FirstOrDefault();
        
        if (provider != null)
            return MapToProviderConfig(provider);
        
        // 如果没有主提供商，返回第一个启用的提供商
        var first = dbContext.AiProviderSettings
            .Where(p => p.IsEnabled)
            .OrderBy(p => p.SortOrder)
            .FirstOrDefault();
        
        return first != null ? MapToProviderConfig(first) : null;
    }

    /// <summary>
    /// 验证 API Key 格式
    /// </summary>
    public bool ValidateApiKeyFormat(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return false;

        // 检查是否包含控制字符（如换行符）
        if (apiKey.Any(char.IsControl))
            return false;

        // 至少 10 个字符
        var trimmed = apiKey.Trim();
        if (trimmed.Length < 10)
            return false;

        // 支持多种格式：sk-xxx, sk-xxx-xxx, 纯字符串等
        return true;
    }

    /// <summary>
    /// 序列化模型列表为 JSON
    /// </summary>
    public static string SerializeModels(List<AiModelConfig> models)
    {
        if (models == null || models.Count == 0)
            return "[]";
        return JsonSerializer.Serialize(models);
    }

    /// <summary>
    /// 映射数据库实体到配置对象
    /// </summary>
    private AiProviderConfig MapToProviderConfig(AiProviderSetting setting)
    {
        var anthropicBaseUrl = setting.AnthropicBaseUrl;
        return new AiProviderConfig
        {
            Id = setting.ProviderId,
            Name = setting.ProviderName,
            AiBaseUrl = setting.BaseUrl,
            AnthropicBaseUrl = anthropicBaseUrl,
            IsMain = setting.IsMain,
            Models = ParseModels(setting.ModelsJson),
            Tier = (Baihua.Contracts.Ai.AiModelTier)setting.Tier
        };
    }

    /// <summary>
    /// 解析模型 JSON
    /// </summary>
    private List<AiModelConfig> ParseModels(string? modelsJson)
    {
        if (string.IsNullOrWhiteSpace(modelsJson))
            return new List<AiModelConfig>();

        try
        {
            return JsonSerializer.Deserialize<List<AiModelConfig>>(modelsJson) ?? new List<AiModelConfig>();
        }
        catch
        {
            return new List<AiModelConfig>();
        }
    }

    #region 备份导出/导入（全量备份 ZIP 的 db/ai_providers.json）

    /// <summary>
    /// 导出全部 Provider（含禁用项）用于备份：API Key 解密后按备份密码/机器密钥重新加密。
    /// 一服务一数据库：加解密只发生在 AI 服务进程（唯一持有 key 的进程），Family 不参与。
    /// </summary>
    public List<AiProviderBackupItem> ExportForBackup(string? password)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        var providers = dbContext.AiProviderSettings.OrderBy(p => p.Id).ToList();

        return providers.Select(p =>
        {
            var plainApiKey = "";
            if (!string.IsNullOrEmpty(p.EncryptedApiKey))
            {
                try { plainApiKey = _protectionService.Decrypt(p.EncryptedApiKey); }
                catch (Exception ex) { _logger.LogWarning(ex, "备份时解密 Provider {ProviderId} 的 API Key 失败", p.ProviderId); }
            }

            string protectedApiKey;
            string keyProtection;
            if (!string.IsNullOrEmpty(password) && !string.IsNullOrEmpty(plainApiKey) && _dataEncryption != null)
            {
                protectedApiKey = _dataEncryption.Encrypt(plainApiKey, password);
                keyProtection = "BackupPassword";
            }
            else if (!string.IsNullOrEmpty(plainApiKey))
            {
                protectedApiKey = _protectionService.Encrypt(plainApiKey);
                keyProtection = "MachineKey";
            }
            else
            {
                protectedApiKey = "";
                keyProtection = "MachineKey";
            }

            return new AiProviderBackupItem
            {
                Id = p.Id,
                ProviderId = p.ProviderId,
                ProviderName = p.ProviderName,
                BaseUrl = p.BaseUrl,
                AnthropicBaseUrl = p.AnthropicBaseUrl,
                ProtectedApiKey = protectedApiKey,
                KeyProtection = keyProtection,
                ModelsJson = p.ModelsJson,
                IsMain = p.IsMain,
                IsEnabled = p.IsEnabled,
                SortOrder = p.SortOrder,
                CreatedAt = p.CreatedAt,
                UpdatedAt = p.UpdatedAt
            };
        }).ToList();
    }

    /// <summary>
    /// 从备份条目恢复 Provider（兼容旧备份格式：PLAINTEXT: 前缀 / MachineKey / BackupPassword）。
    /// 解密后以 AI 服务自己的密钥重新加密入库。
    /// </summary>
    public void ImportFromBackup(List<AiProviderBackupItem> items, string? password, bool replaceAll = false)
    {
        if (items == null || items.Count == 0)
            return;

        using var dbContext = _dbContextFactory.CreateDbContext();
        if (replaceAll)
        {
            dbContext.AiProviderSettings.RemoveRange(dbContext.AiProviderSettings);
            dbContext.SaveChanges();
        }

        foreach (var item in items)
        {
            try
            {
                var plainApiKey = DecryptBackupKey(item, password);
                var setting = new AiProviderSetting
                {
                    ProviderId = item.ProviderId,
                    ProviderName = item.ProviderName,
                    BaseUrl = item.BaseUrl,
                    AnthropicBaseUrl = item.AnthropicBaseUrl,
                    ModelsJson = string.IsNullOrWhiteSpace(item.ModelsJson) ? "[]" : item.ModelsJson,
                    IsMain = item.IsMain,
                    IsEnabled = item.IsEnabled,
                    SortOrder = item.SortOrder,
                    CreatedAt = item.CreatedAt,
                    UpdatedAt = item.UpdatedAt
                };
                SaveProvider(setting, plainApiKey);
                _logger.LogInformation("已从备份恢复 AI 提供方: {ProviderId}", item.ProviderId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从备份恢复 AI 提供方 {ProviderId} 失败", item.ProviderId);
            }
        }
    }

    private string DecryptBackupKey(AiProviderBackupItem item, string? password)
    {
        var protectedKey = item.ProtectedApiKey ?? "";
        if (string.IsNullOrEmpty(protectedKey))
            return "";

        if (protectedKey.StartsWith("PLAINTEXT:", StringComparison.Ordinal))
            return protectedKey["PLAINTEXT:".Length..];

        if (item.KeyProtection == "BackupPassword" && !string.IsNullOrEmpty(password) && _dataEncryption != null)
            return _dataEncryption.Decrypt(protectedKey, password);

        // MachineKey 或未知保护方式 → 尝试机器密钥解密（.baihua-key 固定密钥，跨进程一致）
        return _protectionService.Decrypt(protectedKey);
    }

    #endregion
}


