using System.Text.Json.Serialization;

namespace Baihua.Contracts.Devices;

/// <summary>
/// 已授权设备信息
/// </summary>
public class AuthorizedDeviceDto
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>
    /// 系统设备名（与花记名互补，如“HUAWEI P60”）
    /// </summary>
    [JsonPropertyName("systemDeviceName")]
    public string? SystemDeviceName { get; set; }

    [JsonPropertyName("authorizedTime")]
    public DateTime? AuthorizedTime { get; set; }

    [JsonPropertyName("lastSyncTime")]
    public DateTime? LastSyncTime { get; set; }

    [JsonPropertyName("ipAddress")]
    public string? IpAddress { get; set; }

    [JsonPropertyName("syncCount")]
    public int SyncCount { get; set; }

    [JsonPropertyName("firstSyncTime")]
    public DateTime? FirstSyncTime { get; set; }

    [JsonPropertyName("syncedVaultIds")]
    public List<string> SyncedVaultIds { get; set; } = new();

    /// <summary>
    /// 已同步知识库的 ID→名称 映射，用于前端直接显示中文名称
    /// </summary>
    [JsonPropertyName("syncedVaultNames")]
    public Dictionary<string, string> SyncedVaultNames { get; set; } = new();
}
