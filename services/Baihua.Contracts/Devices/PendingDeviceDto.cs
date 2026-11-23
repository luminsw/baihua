using System.Text.Json.Serialization;

namespace Baihua.Contracts.Devices;

/// <summary>
/// 待授权设备信息
/// </summary>
public class PendingDeviceDto
{
    [JsonPropertyName("sessionId")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("deviceInfo")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("systemDeviceName")]
    public string? SystemDeviceName { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime RequestTime { get; set; }

    [JsonPropertyName("ipAddress")]
    public string? IpAddress { get; set; }
}
