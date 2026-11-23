namespace BaihuaSdk.Models;

/// <summary>
/// 设备授权等待结果。
/// </summary>
public record AuthorizationResult
{
    /// <summary>是否已授权成功。</summary>
    public bool IsAuthorized { get; init; }

    /// <summary>授权成功后返回的共享密钥。</summary>
    public string? SharedSecret { get; init; }

    /// <summary>未授权时的请求 ID，供 WebUI 授权使用。</summary>
    public string? RequestId { get; init; }

    /// <summary>错误信息。</summary>
    public string? ErrorMessage { get; init; }

    public static AuthorizationResult Authorized(string sharedSecret) =>
        new() { IsAuthorized = true, SharedSecret = sharedSecret };

    public static AuthorizationResult NotAuthorized(string? requestId = null, string? errorMessage = null) =>
        new() { IsAuthorized = false, RequestId = requestId, ErrorMessage = errorMessage };

    public static AuthorizationResult Failed(string errorMessage) =>
        new() { IsAuthorized = false, ErrorMessage = errorMessage };
}
