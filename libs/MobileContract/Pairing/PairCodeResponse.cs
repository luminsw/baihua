namespace MobileContract.Pairing;

/// <summary>
/// 配对码查询响应
/// </summary>
public record PairCodeResponse
{
    public string DeviceId { get; init; } = "";
    public string? PairCode { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}
