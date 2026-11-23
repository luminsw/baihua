namespace MobileContract.Devices;

/// <summary>
/// 待审批的设备配对请求
/// </summary>
public record PendingDeviceDto
{
    public string RequestId { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public DateTimeOffset RequestTime { get; init; }
    public string? IpAddress { get; init; }
}
