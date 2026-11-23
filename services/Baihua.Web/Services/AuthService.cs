using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Baihua.Web.Services;

/// <summary>
/// 认证服务 - 管理用户认证状态
/// 使用随机令牌 + 过期时间，替代基于日期的弱令牌
/// 令牌持久化到 JSON 文件，重启后不会丢失登录状态
/// </summary>
public class AuthService
{
    // Cookie 名称
    public const string AuthCookieName = "webui_auth";
    // Cookie 有效期（天）
    public const int CookieExpiryDays = 7;
    // 令牌有效期（小时）
    private const int TokenExpiryHours = 24;

    // 令牌持久化文件（存到项目 data/ 目录，重启后不丢失）
    // 存到项目 bin 同级 data/ 目录
    private static readonly string TokensFilePath = Path.GetFullPath(
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "data", "auth-tokens.json"));

    // 活跃会话令牌（tokenId -> 过期时间）
    private ConcurrentDictionary<string, DateTime> _activeTokens = new();
    private readonly object _fileLock = new();

    // CLI 一次性令牌（token -> 过期时间）
    private readonly ConcurrentDictionary<string, DateTime> _cliTokens = new();
    private const int CliTokenExpiryMinutes = 5;

    public AuthService()
    {
        LoadTokens();
        CleanupExpiredTokens();
    }

    /// <summary>
    /// 生成认证令牌 - 随机令牌 + 过期时间
    /// </summary>
    public Task<string> GenerateAuthTokenAsync()
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes);

        var expiry = DateTime.UtcNow.AddHours(TokenExpiryHours);
        _activeTokens[token] = expiry;

        SaveTokens();
        CleanupExpiredTokens();

        return Task.FromResult(token);
    }

    /// <summary>
    /// 验证认证令牌是否有效
    /// </summary>
    public Task<bool> ValidateAuthTokenAsync(string token)
    {
        if (string.IsNullOrEmpty(token))
            return Task.FromResult(false);

        if (_activeTokens.TryGetValue(token, out var expiry))
        {
            if (DateTime.UtcNow < expiry)
                return Task.FromResult(true);

            _activeTokens.TryRemove(token, out _);
            SaveTokens();
            return Task.FromResult(false);
        }

        return Task.FromResult(false);
    }

    /// <summary>
    /// 登出 - 移除指定令牌
    /// </summary>
    public void RevokeToken(string token)
    {
        if (!string.IsNullOrEmpty(token))
        {
            _activeTokens.TryRemove(token, out _);
            SaveTokens();
        }
    }

    /// <summary>
    /// 从 JSON 文件加载持久化的令牌
    /// </summary>
    private void LoadTokens()
    {
        try
        {
            var dir = Path.GetDirectoryName(TokensFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(TokensFilePath))
            {
                var json = File.ReadAllText(TokensFilePath);
                var tokens = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json);
                if (tokens != null)
                    _activeTokens = new ConcurrentDictionary<string, DateTime>(tokens);
            }
        }
        catch (Exception)
        {
            // 文件损坏时使用空字典
            _activeTokens = new ConcurrentDictionary<string, DateTime>();
        }
    }

    /// <summary>
    /// 将令牌持久化到 JSON 文件
    /// </summary>
    private void SaveTokens()
    {
        try
        {
            lock (_fileLock)
            {
                var dir = Path.GetDirectoryName(TokensFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(_activeTokens.ToDictionary(
                    kvp => kvp.Key, kvp => kvp.Value));
                File.WriteAllText(TokensFilePath, json);
            }
        }
        catch (Exception)
        {
            // 持久化失败不影响运行，仅丢失重启后的登录状态
        }
    }

    /// <summary>
    /// 清理过期令牌
    /// </summary>
    private void CleanupExpiredTokens()
    {
        var now = DateTime.UtcNow;
        bool changed = false;
        foreach (var kvp in _activeTokens)
        {
            if (now >= kvp.Value)
            {
                _activeTokens.TryRemove(kvp.Key, out _);
                changed = true;
            }
        }
        if (changed) SaveTokens();
    }

    #region CLI Token

    public string GenerateCliToken()
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(24);
        var token = Convert.ToBase64String(tokenBytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "");

        _cliTokens[token] = DateTime.UtcNow.AddMinutes(CliTokenExpiryMinutes);
        CleanupExpiredCliTokens();
        return token;
    }

    public bool ValidateCliToken(string token)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        // 有效期内可重复使用（dashboard 打印的 URL 可能被打开多次）；
        // 过期后由 CleanupExpiredCliTokens 清理。
        if (_cliTokens.TryGetValue(token, out var expiry))
        {
            return DateTime.UtcNow < expiry;
        }
        return false;
    }

    private void CleanupExpiredCliTokens()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _cliTokens.ToArray())
        {
            if (now >= kvp.Value)
            {
                _cliTokens.TryRemove(kvp.Key, out _);
            }
        }
    }

    #endregion
}
