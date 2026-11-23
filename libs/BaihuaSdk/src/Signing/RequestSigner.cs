using System.Security.Cryptography;
using System.Text;

namespace BaihuaSdk.Signing;

/// <summary>
/// 移动端请求签名器。
/// 与百花后端 RequestSignatureService 配对，与 Kotlin RequestSigner.kt 算法一致。
///
/// 签名格式: X-Mobile-Signature: {timestamp}:{base64_hmac}
/// 签名字符串: {timestamp}\n{method}\n{path}\n{sha256(body)}
/// </summary>
public class RequestSigner : IRequestSigner
{
    private readonly ServerSecretStore _secrets = new();
    private readonly string _deviceId;
    private readonly string _deviceName;
    private readonly string? _websiteBaseUrl;
    private readonly string? _mobileClientSecret;

    public RequestSigner(
        string deviceId,
        string deviceName,
        string? websiteBaseUrl = null,
        string? mobileClientSecret = null)
    {
        _deviceId = deviceId;
        _deviceName = deviceName;
        _websiteBaseUrl = websiteBaseUrl?.TrimEnd('/').ToLowerInvariant();
        _mobileClientSecret = mobileClientSecret;
    }

    public string DeviceId => _deviceId;
    public string DeviceName => _deviceName;

    // ---- IRequestSigner ----

    public void SetSharedSecret(string secret) => _secrets.SetGlobal(secret);

    public void SetServerSecret(string serverUrl, string secret) => _secrets.Set(serverUrl, secret);

    public string? GetServerSecret(string serverUrl) => _secrets.Get(serverUrl);

    public bool HasServerSecret(string serverUrl) => _secrets.Has(serverUrl);

    public void ClearSecrets() => _secrets.Clear();

    public IReadOnlyDictionary<string, string> SignRequest(
        string method, string url, string? body = null, string? serverUrl = null)
    {
        var secret = serverUrl != null ? _secrets.Get(serverUrl) : _secrets.GetGlobal();

        // 官网 fallback：使用构建时注入的固定客户端密钥
        if (string.IsNullOrEmpty(secret) && serverUrl != null && IsWebsiteUrl(serverUrl))
            secret = _mobileClientSecret;

        if (string.IsNullOrEmpty(secret))
            return new Dictionary<string, string>();

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var path = ExtractPathFromUrl(url);
        var bodyHash = body != null ? Sha256Hex(body) : string.Empty;
        var signString = $"{timestamp}\n{method.ToUpperInvariant()}\n{path}\n{bodyHash}";
        var signature = HmacSha256Base64(signString, secret!);

        return new Dictionary<string, string>
        {
            ["X-Mobile-Signature"] = $"{timestamp}:{signature}",
            ["X-Device-Id"] = _deviceId,
            ["X-Device-Name"] = _deviceName,
        };
    }

    // ---- Internal: crypto ----

    /// <summary>计算 SHA-256 哈希，返回十六进制小写字符串</summary>
    public static string Sha256Hex(string data)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>计算 HMAC-SHA256，返回 Base64 字符串</summary>
    public static string HmacSha256Base64(string message, string key)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var hash = HMACSHA256.HashData(keyBytes, messageBytes);
        return Convert.ToBase64String(hash);
    }

    // ---- Internal: utilities ----

    /// <summary>从 URL 中提取 path（含 query string）</summary>
    public static string ExtractPathFromUrl(string url)
    {
        var protocolIdx = url.IndexOf("://", StringComparison.Ordinal);
        if (protocolIdx >= 0)
        {
            var afterProtocol = url[(protocolIdx + 3)..];
            var pathIdx = afterProtocol.IndexOf('/');
            return pathIdx >= 0 ? afterProtocol[pathIdx..] : "/";
        }
        return url;
    }

    private bool IsWebsiteUrl(string url) =>
        _websiteBaseUrl != null &&
        (url ?? string.Empty).TrimEnd('/').Equals(_websiteBaseUrl, StringComparison.OrdinalIgnoreCase);
}
