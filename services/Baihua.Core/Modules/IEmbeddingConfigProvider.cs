namespace Baihua.Core.Modules;

/// <summary>
/// Embedding（向量检索）配置读取接口。
///
/// 配置数据（EmbeddingConfigs 表）归 AI 模块所有，本接口由 AI 模块实现、宿主注入；
/// 知识库模块的语义检索（<c>Baihua.Core.Services.EmbeddingService</c>）只依赖本接口，
/// 不再经 HTTP 读取 AI 服务。
/// </summary>
public interface IEmbeddingConfigProvider
{
    /// <summary>
    /// 当前生效的 Embedding 配置（<b>不含密钥</b>）；未配置时返回系统默认配置（ollama + nomic-embed-text）。
    /// 需要密钥的云端嵌入模型不在本地支持范围内。
    /// </summary>
    Task<EmbeddingSettings> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>Embedding 配置（无密钥）</summary>
public sealed record EmbeddingSettings(
    string ProviderId,
    string Model,
    string BaseUrl,
    bool IsEnabled,
    int? Dimensions);
