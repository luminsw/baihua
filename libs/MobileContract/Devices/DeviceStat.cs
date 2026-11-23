namespace MobileContract.Devices;

/// <summary>
/// 单个设备的统计数据
/// </summary>
public record DeviceStat
{
    public string DeviceId { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public string? IpAddress { get; init; }
    public long SyncCount { get; init; }
    public DateTimeOffset? FirstSyncTime { get; init; }
    public DateTimeOffset? LastSyncTime { get; init; }
    public DateTimeOffset? AuthorizedTime { get; init; }
}
