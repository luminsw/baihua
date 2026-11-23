using Microsoft.Extensions.Caching.Distributed;

namespace Baihua.Core.Services;

/// <summary>
/// 空实现 IDistributedCache：AI 服务作为转发代理无需缓存响应
/// （缓存由调用方 Family 的 CachingChatClient 承担）。
/// 避免 AddDistributedMemoryCache 无限缓存导致内存膨胀 OOM。
/// </summary>
public class NoOpDistributedCache : IDistributedCache
{
    public byte[]? Get(string key) => null;
    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult<byte[]?>(null);
    public void Refresh(string key) { }
    public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
    public void Remove(string key) { }
    public Task RemoveAsync(string key, CancellationToken token = default) => Task.CompletedTask;
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) { }
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => Task.CompletedTask;
}
