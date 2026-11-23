namespace Baihua.Data.Entities;

/// <summary>
/// 设备同步活动日志
/// </summary>
public class DeviceSyncLog
{
    public int Id { get; set; }

    /// <summary>
    /// 设备ID
    /// </summary>
    public string DeviceId { get; set; } = "";

    /// <summary>
    /// 设备名称
    /// </summary>
    public string DeviceName { get; set; } = "";

    /// <summary>
    /// 设备IP地址
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>
    /// 同步的知识库ID
    /// </summary>
    public string? VaultId { get; set; }

    /// <summary>
    /// 同步的文件数量
    /// </summary>
    public int FileCount { get; set; }

    /// <summary>
    /// 同步类型：manifest / file / full
    /// </summary>
    public string SyncType { get; set; } = "manifest";

    /// <summary>
    /// 同步时间
    /// </summary>
    public DateTime SyncTime { get; set; }
}
