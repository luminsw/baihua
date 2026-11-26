using System.Text.Json.Serialization;

namespace Baihua.Contracts.Devices;

/// <summary>
/// WebUI 发送知识库到移动端请求：指定一个或多个已授权设备 + 一个或多个知识库
/// </summary>
public class FetchVaultsRequest
{
    [JsonPropertyName("deviceIds")]
    public List<string> DeviceIds { get; set; } = new();

    [JsonPropertyName("vaultIds")]
    public List<string> VaultIds { get; set; } = new();
}

/// <summary>
/// 发送结果：成功送达（WS 在线）的设备与被跳过（离线）的设备
/// </summary>
public class FetchVaultsResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("deliveredDeviceIds")]
    public List<string> DeliveredDeviceIds { get; set; } = new();

    [JsonPropertyName("skippedDeviceIds")]
    public List<string> SkippedDeviceIds { get; set; } = new();
}
