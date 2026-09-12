using Baihua.Contracts.Ai;
using Baihua.Contracts.ComputePool;
using Baihua.Core.Models;

namespace Baihua.Modules.Family.Services.ComputePool;

/// <summary>
/// 算力池共享过滤：决定哪些提供方/模型可以进入算力池（广播 + 总览展示）。
/// 规则：
/// - 排除 peer- 前缀的对端登记提供方（它们只是本机指向对端的登记项，再广播/展示会造成模型重复）；
/// - 只允许本地算力（Tier1 固本 / Tier2 本地大模型），Tier3 云端模型不进局域网算力池
///   （每台机器都能直连云端，无需经算力池中转）。
/// 网关路由（/mg/pool/v1）不受此过滤影响，仍可路由本机 AI 服务持有的全部模型（含云端）。
/// </summary>
public static class ComputePoolShareFilter
{
    /// <summary>是否为算力池对端登记提供方（peer- 前缀，本机指向对端 OpenAI 兼容端点的登记项）。</summary>
    public static bool IsPeerProvider(string providerId) =>
        !string.IsNullOrWhiteSpace(providerId)
        && providerId.StartsWith("peer-", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否为本地算力层级（固本/本地大模型），云端不进池。</summary>
    public static bool IsLocalComputeTier(AiModelTier tier) =>
        tier is AiModelTier.Tier1_Embedding or AiModelTier.Tier2_Local;

    /// <summary>字符串层级的本地算力判断（ComputeProviderDto.Tier 为 "1"/"2"/"3"）。</summary>
    public static bool IsLocalComputeTier(string tier) => tier is "1" or "2";

    /// <summary>该提供方是否可进算力池（非 peer- 且为本地算力）。</summary>
    public static bool IsShareable(AiProviderConfig provider) =>
        !IsPeerProvider(provider.Id) && IsLocalComputeTier(provider.Tier);

    /// <summary>该提供方是否可进算力池（非 peer- 且为本地算力）。</summary>
    public static bool IsShareable(ComputeProviderDto provider) =>
        !IsPeerProvider(provider.Id) && IsLocalComputeTier(provider.Tier);
}
