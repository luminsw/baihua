using System.Collections.Concurrent;

namespace BaihuaSdk.Signing;

/// <summary>
/// 多服务器共享密钥存储。
/// 线程安全的内存字典，键为标准化后的服务器 URL。
/// </summary>
public class ServerSecretStore
{
    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.OrdinalIgnoreCase);
    private string _globalSecret = string.Empty;

    /// <summary>设置全局共享密钥（fallback）</summary>
    public void SetGlobal(string secret) => _globalSecret = secret ?? string.Empty;

    /// <summary>获取全局密钥</summary>
    public string GetGlobal() => _globalSecret;

    /// <summary>设置指定服务器的密钥</summary>
    public void Set(string serverUrl, string secret)
    {
        var key = Normalize(serverUrl);
        if (string.IsNullOrEmpty(secret))
            _secrets.TryRemove(key, out _);
        else
            _secrets[key] = secret;
    }

    /// <summary>获取指定服务器的密钥（优先服务器，fallback 到全局）</summary>
    public string? Get(string serverUrl)
    {
        var key = Normalize(serverUrl);
        if (_secrets.TryGetValue(key, out var secret) && secret.Length > 0)
            return secret;
        return _globalSecret.Length > 0 ? _globalSecret : null;
    }

    /// <summary>检查指定服务器是否有独立密钥</summary>
    public bool Has(string serverUrl)
    {
        var key = Normalize(serverUrl);
        return _secrets.TryGetValue(key, out var s) && s.Length > 0;
    }

    /// <summary>清空所有密钥</summary>
    public void Clear()
    {
        _globalSecret = string.Empty;
        _secrets.Clear();
    }

    /// <summary>获取所有服务器密钥的快照（用于持久化）</summary>
    public IReadOnlyDictionary<string, string> GetAll() => _secrets;

    private static string Normalize(string url) =>
        (url ?? string.Empty).TrimEnd('/').ToLowerInvariant();
}
