using Baihua.Contracts.Ai;
using Baihua.Core.Models;
using Baihua.Core.Security;
using Baihua.Core.Services;
using Baihua.Data.Entities;

namespace Baihua.Family.Tests.TestDoubles;

/// <summary>
/// 空实现的 IAiConfigService 测试替身（返回空 Provider 列表/空 Key），
/// 等价于旧测试里 "ServiceProvider.GetService(typeof(AiConfigService)) 返回 null" 的行为：
/// AiSettingsService 会回退到 appsettings 配置。
/// </summary>
public class StubAiConfigService : IAiConfigService
{
    public static readonly StubAiConfigService Empty = new();

    public List<AiProviderConfig> GetProviders() => new();

    public List<ApiKeySummary> GetApiKeySummaries() => new();

    public AiProviderConfig? GetProvider(string providerId) => null;

    public AiProviderConfig? GetMainProvider() => null;

    public string GetApiKey(string providerId) => "";

    public void SaveProvider(AiProviderSetting setting, string? plainApiKey = null)
    {
    }

    public bool DeleteProvider(string providerId) => false;

    public List<AiProviderBackupItem> ExportForBackup(string? password) => new();

    public void ImportFromBackup(List<AiProviderBackupItem> items, string? password, bool replaceAll = false)
    {
    }
}
