namespace BaihuaSdk.Signing;

/// <summary>
/// 请求签名器接口。
/// 实现 HMAC-SHA256 请求签名，与百花后端 RequestSignatureService 配对。
///
/// 签名格式: X-Mobile-Signature: {timestamp}:{base64_hmac}
/// 签名字符串: {timestamp}\n{method}\n{path}\n{sha256(body)}
/// </summary>
public interface IRequestSigner
{
    /// <summary>设置全局共享密钥（fallback）</summary>
    void SetSharedSecret(string secret);

    /// <summary>设置指定服务器的共享密钥</summary>
    void SetServerSecret(string serverUrl, string secret);

    /// <summary>获取指定服务器的共享密钥（优先按服务器，fallback 到全局）</summary>
    string? GetServerSecret(string serverUrl);

    /// <summary>检查指定服务器是否有独立密钥</summary>
    bool HasServerSecret(string serverUrl);

    /// <summary>清空所有密钥（仅测试用）</summary>
    void ClearSecrets();

    /// <summary>
    /// 为请求生成签名头 Map。
    /// 返回 X-Mobile-Signature、X-Device-Id、X-Device-Name。
    /// </summary>
    IReadOnlyDictionary<string, string> SignRequest(
        string method, string url, string? body = null, string? serverUrl = null);
}
