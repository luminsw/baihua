using Baihua.Contracts.Search;
using Baihua.Contracts.Vaults;

namespace Baihua.Core.Modules;

/// <summary>
/// 知识库查询能力（跨模块只读/写笔记接口）。
///
/// 由知识库模块实现、宿主注入；其他模块（如家庭模块的 MCP 工具）只依赖本接口，
/// 不依赖知识库模块的任何实现类型——合并为单进程后这是进程内直调，不再有 HTTP 转发。
/// </summary>
public interface IVaultQueryService
{
    /// <summary>把知识库 id 解析为磁盘绝对路径；id 为空或知识库不存在时返回 null</summary>
    string? ResolveVaultPath(string? vaultId);

    /// <summary>检索笔记（语义 → FTS5 → obsidian-cli → 文件扫描，逐级回退）</summary>
    Task<VaultSearchOutcome> SearchAsync(string query, string vaultId, CancellationToken cancellationToken = default);

    /// <summary>读取一篇笔记（含 frontmatter 解析出的标签/AI 元信息）</summary>
    Task<VaultNoteResponse> ReadNoteAsync(string path, string vaultId, CancellationToken cancellationToken = default);

    /// <summary>新建或覆盖一篇笔记（自动创建目录，含路径穿越防护）</summary>
    Task WriteNoteAsync(string path, string vaultId, string content, CancellationToken cancellationToken = default);
}

/// <summary>检索结果 + 检索方式状态（原 /api/search 响应体）</summary>
public sealed record VaultSearchOutcome(IReadOnlyList<SearchResult> Results, SearchStatusInfo Status);

/// <summary>知识库操作失败原因（宿主/控制器据此映射 HTTP 状态码）</summary>
public enum VaultErrorCode
{
    /// <summary>缺少笔记路径</summary>
    PathRequired,

    /// <summary>缺少/无效知识库</summary>
    VaultRequired,

    /// <summary>知识库目录不存在</summary>
    VaultPathNotExists,

    /// <summary>笔记不存在</summary>
    NoteNotFound,

    /// <summary>笔记内容为空</summary>
    ContentRequired,

    /// <summary>非法路径（目录穿越）</summary>
    IllegalPath,

    /// <summary>其它失败（IO 等）</summary>
    Failure
}

/// <summary>知识库操作异常：<see cref="Message"/> 已是面向用户的中文文案</summary>
public sealed class VaultQueryException(VaultErrorCode code, string message) : Exception(message)
{
    /// <summary>失败原因</summary>
    public VaultErrorCode Code { get; } = code;
}
