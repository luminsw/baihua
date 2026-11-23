namespace Baihua.Web.Services;

/// <summary>
/// 配对请求信息
/// </summary>
public class PairingRequest
{
    public string Challenge { get; set; } = string.Empty;
    public string DeviceInfo { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

