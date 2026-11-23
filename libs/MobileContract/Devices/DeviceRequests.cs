namespace MobileContract.Devices;

public record AuthorizeDeviceRequest(string? RequestId);
public record RejectDeviceRequest(string? RequestId);
public record RevokeDeviceRequest(string? DeviceId);

/// <summary>
/// 向指定设备推送同步请求
/// </summary>
public record PushToVaultRequest
{
    public string? DeviceId { get; init; }
    public string? VaultId { get; init; }
    public string? Action { get; init; }
}

/// <summary>
/// 服务器发起的同步推送请求
/// </summary>
public record PushSyncRequest
{
    public string RequestId { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string? VaultId { get; init; }
    public string? VaultName { get; init; }
    public string Action { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
}
