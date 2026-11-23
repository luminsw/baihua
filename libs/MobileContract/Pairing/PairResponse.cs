namespace MobileContract.Pairing;

/// <summary>
/// 设备配对响应
/// </summary>
public record PairResponse
{
    public string? RequestId { get; init; }
    public string? AccessToken { get; init; }
    public int ExpiresIn { get; init; }
    public string? Status { get; init; }
    public string? Message { get; init; }
}
