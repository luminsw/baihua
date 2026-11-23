using BaihuaSdk.Models;

namespace BaihuaSdk.Storage;

/// <summary>
/// 服务器配置持久化接口。
/// 平台层提供实现（MAUI Preferences / SharedPreferences / DataStore）。
/// </summary>
public interface IServerConfigStore
{
    Task<IReadOnlyList<ServerConfig>> GetServersAsync();
    Task AddOrUpdateServerAsync(ServerConfig config);
    Task DeleteServerAsync(string serverId);
    Task<ServerConfig?> GetCurrentServerAsync();
    Task SetCurrentServerAsync(string? serverId);
}
