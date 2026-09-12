using Baihua.Contracts.Ai;
using Baihua.Core.Models;
using Baihua.Core.Security;
using Baihua.Data.Entities;

namespace Baihua.Core.Services;

/// <summary>
/// AI 提供方配置数据源（AI 模块对外暴露的配置能力接口）。
///
/// 合并为单进程后只有进程内实现 <see cref="AiConfigService"/>：
/// 其他模块（家庭模块的备份/算力池等）只依赖本接口，不直接读写 AI 模块的 DbContext，
/// API Key 的加解密始终只发生在 AI 模块内部。
/// </summary>
public interface IAiConfigService
{
    /// <summary>获取所有启用的 AI 提供商（不含密钥）</summary>
    List<AiProviderConfig> GetProviders();

    /// <summary>获取 API Key 配置摘要（掩码，用于设置页面显示）</summary>
    List<ApiKeySummary> GetApiKeySummaries();

    /// <summary>获取单个 Provider 配置（不含密钥）</summary>
    AiProviderConfig? GetProvider(string providerId);

    /// <summary>获取主 Provider（无主时回退第一个启用的）</summary>
    AiProviderConfig? GetMainProvider();

    /// <summary>
    /// 获取指定 Provider 的有效 API Key。
    /// 仅 AI 模块内部（推理/密钥二维码）使用，其他模块不应调用。
    /// </summary>
    string GetApiKey(string providerId);

    /// <summary>保存 Provider 配置（plainApiKey：null=保留旧 key，""=清空，非空=更新并加密）</summary>
    void SaveProvider(AiProviderSetting setting, string? plainApiKey = null);

    /// <summary>删除 Provider 配置</summary>
    bool DeleteProvider(string providerId);

    /// <summary>导出全部 Provider（含禁用项）用于全量备份；password 非空时密钥再加密</summary>
    List<AiProviderBackupItem> ExportForBackup(string? password);

    /// <summary>从备份导入 Provider（replaceAll=true 时先清空现有配置）</summary>
    void ImportFromBackup(List<AiProviderBackupItem> items, string? password, bool replaceAll = false);
}
