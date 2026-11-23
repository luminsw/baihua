using System.Text.Json.Serialization;

namespace Baihua.Contracts.Devices;

/// <summary>
/// 移动端统计信息
/// </summary>
public class MobileStats
{
    [JsonPropertyName("totalDevices")]
    public int TotalDevices { get; set; }

    [JsonPropertyName("totalSyncs")]
    public int TotalSyncs { get; set; }

    [JsonPropertyName("totalSyncFiles")]
    public int TotalSyncFiles { get; set; }

    [JsonPropertyName("activeDevices7Days")]
    public int ActiveDevices7Days { get; set; }

    [JsonPropertyName("activeDevices30Days")]
    public int ActiveDevices30Days { get; set; }

    [JsonPropertyName("devices")]
    public List<DeviceStat> Devices { get; set; } = new();
}

/// <summary>
/// 单个设备统计
/// </summary>
public class DeviceStat
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = "";

    [JsonPropertyName("systemDeviceName")]
    public string? SystemDeviceName { get; set; }

    [JsonPropertyName("ipAddress")]
    public string? IpAddress { get; set; }

    [JsonPropertyName("syncCount")]
    public int SyncCount { get; set; }

    [JsonPropertyName("firstSyncTime")]
    public DateTime? FirstSyncTime { get; set; }

    [JsonPropertyName("lastSyncTime")]
    public DateTime? LastSyncTime { get; set; }

    [JsonPropertyName("authorizedTime")]
    public DateTime AuthorizedTime { get; set; }
}
