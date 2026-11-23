namespace BaihuaSdk.Models;

/// <summary>
/// 百花服务器配置。
/// 对应 Kotlin ServerManager.kt 中的 ServerConfig data class。
/// </summary>
public record ServerConfig
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string HttpUrl { get; init; } = "";
    public string? HttpsUrl { get; init; }
    public string? ServerIp { get; init; }
    public bool IsOnline { get; init; }
    public string? DeviceId { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
}
