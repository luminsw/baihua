namespace MobileContract.Devices;

/// <summary>
/// 已授权设备信息
/// </summary>
public record AuthorizedDeviceDto
{
    public string DeviceId { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public DateTimeOffset? AuthorizedTime { get; init; }
    public DateTimeOffset? LastSyncTime { get; init; }
    public string? IpAddress { get; init; }
    public long SyncCount { get; init; }
    public DateTimeOffset? FirstSyncTime { get; init; }
}
