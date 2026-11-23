using System.Text.Json.Serialization;

namespace Baihua.Contracts.Devices;

/// <summary>
/// 授权设备请求
/// </summary>
public class AuthorizeDeviceRequest
{
    public string? RequestId { get; set; }
}

/// <summary>
/// 拒绝设备配对请求
/// </summary>
public class RejectDeviceRequest
{
    public string? RequestId { get; set; }
}

/// <summary>
/// 撤销设备授权请求
/// </summary>
public class RevokeDeviceRequest
{
    public string? DeviceId { get; set; }
}

/// <summary>
/// 推送知识库同步通知请求
/// </summary>
public class PushToVaultRequest
{
    public string? DeviceId { get; set; }
    public string? VaultId { get; set; }
    public string? Action { get; set; }
}
