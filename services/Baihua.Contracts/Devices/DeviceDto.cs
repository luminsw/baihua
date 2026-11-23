using System.Text.Json.Serialization;

namespace Baihua.Contracts.Devices;

/// <summary>
/// 设备完整信息（含状态）
/// </summary>
public class DeviceDto : AuthorizedDeviceDto
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("firstRequestTime")]
    public DateTime FirstRequestTime { get; set; }
}
