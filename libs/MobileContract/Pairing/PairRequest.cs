namespace MobileContract.Pairing;

/// <summary>
/// 设备配对请求
/// </summary>
public record PairRequest
{
    public string? PairCode { get; init; }
    public string? DeviceName { get; init; }
    public string? DeviceId { get; init; }
}
