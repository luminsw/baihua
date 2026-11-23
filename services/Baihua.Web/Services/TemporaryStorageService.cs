using System.Collections.Concurrent;

namespace Baihua.Web.Services;

/// <summary>
/// 临时存储服务（用于拆分页面等大内容传递）
/// </summary>
public class TemporaryStorageService
{
    private readonly ConcurrentDictionary<string, string> _storage = new();
    private readonly ConcurrentDictionary<string, DateTime> _expiry = new();

    /// <summary>
    /// 存储数据（10 分钟过期）
    /// </summary>
    public string Set(string value)
    {
        var key = Guid.NewGuid().ToString("N")[..8];
        _storage[key] = value;
        _expiry[key] = DateTime.UtcNow.AddMinutes(10);
        return key;
    }

    /// <summary>
    /// 获取数据
    /// </summary>
    public bool TryGet(string key, out string? value)
    {
        if (_storage.TryGetValue(key, out value))
        {
            // 检查是否过期
            if (_expiry.TryGetValue(key, out var expiry) && expiry > DateTime.UtcNow)
            {
                return true;
            }
            else
            {
                // 过期删除
                _storage.TryRemove(key, out _);
                _expiry.TryRemove(key, out _);
            }
        }
        return false;
    }

    /// <summary>
    /// 删除数据
    /// </summary>
    public void Remove(string key)
    {
        _storage.TryRemove(key, out _);
        _expiry.TryRemove(key, out _);
    }
}
