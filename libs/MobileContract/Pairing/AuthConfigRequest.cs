namespace MobileContract.Pairing;

/// <summary>
/// 获取认证配置请求
/// </summary>
public record AuthConfigRequest
{
    public string DeviceId { get; init; } = "";
    public string? DeviceName { get; init; }
}
