using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Baihua.Core.Services;
using System.Security.Cryptography;
using System.Text;

namespace Baihua.Core.Security;

public class RequestSignatureService
{
    private readonly string _sharedSecret;
    private readonly ILogger<RequestSignatureService> _logger;

    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromMinutes(5);

    public RequestSignatureService(ServerAddressService serverAddressService, IConfiguration configuration, ILogger<RequestSignatureService> logger)
    {
        _logger = logger;
        _sharedSecret = ResolveSharedSecret(serverAddressService, configuration);
        if (!string.IsNullOrEmpty(_sharedSecret))
        {
            _logger.LogInformation("[Signature] SharedSecret loaded (length={Len}, source={Source})",
                _sharedSecret.Length, string.IsNullOrEmpty(configuration.GetValue<string>("MobileAuth:SharedSecret")) ? "auto-generated" : "config");
        }
        else
        {
            _logger.LogWarning("[Signature] SharedSecret is empty — mobile auth will not work");
        }
    }

    private static string ResolveSharedSecret(ServerAddressService serverAddressService, IConfiguration configuration)
    {
        var configSecret = configuration.GetValue<string>("MobileAuth:SharedSecret");
        if (!string.IsNullOrWhiteSpace(configSecret))
            return configSecret;

        try
        {
            var dbSecret = serverAddressService.GetSharedSecret();
            if (!string.IsNullOrWhiteSpace(dbSecret))
                return dbSecret;
        }
        catch
        {
        }

        // Family 版：自动生成共享密钥（避免 IP 白名单回退的安全漏洞）
        // DB 中的密钥会在 GetSettings() 首次调用时自动生成并持久化，
        // 此回退仅在 DB 尚未初始化时生效（临时内存密钥）
        return GenerateRandomSecret();
    }

    /// <summary>生成随机 32 字符共享密钥</summary>
    private static string GenerateRandomSecret()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";
        var bytes = RandomNumberGenerator.GetBytes(32);
        return new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
    }

    /// <summary>
    /// 是否已配置共享密钥
    /// </summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_sharedSecret);

    /// <summary>
    /// 获取共享密钥（仅用于测试/调试，生产环境不要暴露）
    /// </summary>
    public string GetSharedSecret() => _sharedSecret;

    /// <summary>
    /// 验证请求签名
    /// </summary>
    /// <param name="method">HTTP 方法 (GET/POST/...)</param>
    /// <param name="path">请求路径（含查询字符串）</param>
    /// <param name="body">请求体文本</param>
    /// <param name="signatureHeader">X-Mobile-Signature 头值，格式: timestamp:base64_signature</param>
    /// <returns>验证是否通过</returns>
    public bool VerifySignature(string method, string path, string? body, string? signatureHeader)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("[Signature] MobileAuth secret not configured, rejecting request. Set BAIHUA_MOBILE_AUTH_SECRET env var.");
            return false;
        }

        if (string.IsNullOrEmpty(signatureHeader))
        {
            _logger.LogWarning("[Signature] Missing X-Mobile-Signature header for {Method} {Path}", method, path);
            return false;
        }

        // 解析签名头: "timestamp:base64_signature"
        var parts = signatureHeader.Split(':', 2);
        if (parts.Length != 2 || !long.TryParse(parts[0], out var timestamp))
        {
            _logger.LogWarning("[Signature] Invalid signature format for {Method} {Path}: {Header}", method, path, signatureHeader);
            return false;
        }

        // 验证时间戳（防重放攻击）
        var requestTime = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        var now = DateTimeOffset.UtcNow;
        if (Math.Abs((now - requestTime).TotalMinutes) > TimestampTolerance.TotalMinutes)
        {
            _logger.LogWarning("[Signature] Timestamp out of range: request={RequestTime} ({Timestamp}), now={Now}, diff={Diff}min, path={Path}",
                requestTime, timestamp, now, Math.Abs((now - requestTime).TotalMinutes), path);
            return false;
        }

        // 计算期望的签名
        var expectedSignature = ComputeSignature(method, path, body, timestamp);

        // 诊断日志（仅在签名不匹配时输出，帮助调试）
        var bodyHash = string.IsNullOrEmpty(body)
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        var signString = $"{timestamp}\n{method.ToUpperInvariant()}\n{path}\n{bodyHash}";

        // 使用固定时间比较防止时序攻击
        var providedBytes = Convert.FromBase64String(parts[1]);
        var expectedBytes = Convert.FromBase64String(expectedSignature);

        if (providedBytes.Length != expectedBytes.Length)
        {
            _logger.LogWarning("[Signature] Signature length mismatch for {Method} {Path}: provided={ProvidedLen} expected={ExpectedLen}", method, path, providedBytes.Length, expectedBytes.Length);
            return false;
        }

        var isValid = CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
        if (!isValid)
        {
            _logger.LogWarning("[Signature] Invalid signature for {Method} {Path}. signString={SignString} provided={Provided} expected={Expected}", method, path, signString, parts[1], expectedSignature);
        }
        else
        {
        }

        return isValid;
    }

    /// <summary>
    /// 计算 HMAC-SHA256 签名
    /// 签名字符串格式: {timestamp}\n{method}\n{path}\n{sha256(body)}
    /// </summary>
    public string ComputeSignature(string method, string path, string? body, long timestamp)
    {
        var bodyHash = string.IsNullOrEmpty(body)
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

        var signString = $"{timestamp}\n{method.ToUpperInvariant()}\n{path}\n{bodyHash}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_sharedSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signString));
        return Convert.ToBase64String(hash);
    }
}
