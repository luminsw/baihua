namespace Baihua.Data.Entities;

/// <summary>
/// 知识库配置（存储在 SQLite 中）
/// </summary>
public class Vault
{
    public int Id { get; set; }
    public string VaultId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsActive { get; set; }

    /// <summary>
    /// 知识库标签，逗号分隔
    /// </summary>
    public string Tags { get; set; } = "";

    /// <summary>
    /// 所属行业（如：笔记、开发等）
    /// </summary>
    public string Industry { get; set; } = "";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// 知识库来源：local=本地创建, mobile=移动端推送, synced=服务器同步
    /// </summary>
    public string Source { get; set; } = "local";

    /// <summary>
    /// 推送来源设备ID（移动端推送时记录）
    /// </summary>
    public string PushedByDeviceId { get; set; } = "";

    /// <summary>
    /// 推送来源设备名（移动端推送时记录，如"听风笔"）
    /// </summary>
    public string PushedByDeviceName { get; set; } = "";

    /// <summary>
    /// 推送时间
    /// </summary>
    public DateTime? PushedAt { get; set; }

    /// <summary>
    /// 是否已删除（移入回收站）
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// 删除时间
    /// </summary>
    public DateTime? DeletedAt { get; set; }
}
