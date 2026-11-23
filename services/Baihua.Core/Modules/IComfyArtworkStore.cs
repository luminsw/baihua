using Baihua.Contracts.Ai;

namespace Baihua.Core.Modules;

/// <summary>
/// 绘图/视频生成产物流水（ComfyArtworks 表）的存取接口。
///
/// 数据归 AI 模块独占（该表在 AI 模块的表结构里），家庭模块的绘图控制器只依赖本接口，
/// 不再直连 AI 的 DbContext、也不再经 HTTP 调 AI 服务。
/// </summary>
public interface IComfyArtworkStore
{
    /// <summary>历史生成记录（最近 N 条，可按类型过滤）</summary>
    Task<List<AiComfyArtworkDto>> ListAsync(int limit = 50, string? kind = null, CancellationToken cancellationToken = default);

    /// <summary>保存一条生成记录（成功/失败都记录）</summary>
    Task<AiComfyArtworkDto> CreateAsync(SaveAiComfyArtworkRequest request, CancellationToken cancellationToken = default);

    /// <summary>删除一条历史记录</summary>
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);
}
