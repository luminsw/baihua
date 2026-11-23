namespace MobileContract.Devices;

/// <summary>
/// 完整设备信息（包含状态）
/// </summary>
public record DeviceDto : AuthorizedDeviceDto
{
    public string Status { get; init; } = "";
    public DateTimeOffset? FirstRequestTime { get; init; }
}
