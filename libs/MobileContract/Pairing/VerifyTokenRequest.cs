namespace MobileContract.Pairing;

/// <summary>
/// 验证访问令牌请求
/// </summary>
public record VerifyTokenRequest
{
    public string? Token { get; init; }
}
