using System.Text.Json;
using BaihuaSdk.Models;
using BaihuaSdk.Storage;

namespace MobileApp.Maui.Services;

/// <summary>
/// IServerConfigStore 的 MAUI 实现。
/// 使用 Preferences 存储 JSON 序列化的服务器列表。
/// </summary>
public class MauiServerConfigStore : IServerConfigStore
{
    private const string ServersKey = "baihua_servers";
    private const string CurrentServerKey = "baihua_current_server";

    public Task<IReadOnlyList<ServerConfig>> GetServersAsync()
    {
        var json = Preferences.Default.Get(ServersKey, "[]");
        var list = JsonSerializer.Deserialize<List<ServerConfig>>(json)
                   ?? new List<ServerConfig>();
        return Task.FromResult<IReadOnlyList<ServerConfig>>(list);
    }

    public async Task AddOrUpdateServerAsync(ServerConfig config)
    {
        var servers = (await GetServersAsync()).ToList();
        var idx = servers.FindIndex(s => s.Id == config.Id);
        if (idx >= 0)
            servers[idx] = config;
        else
            servers.Add(config);

        Preferences.Default.Set(ServersKey, JsonSerializer.Serialize(servers));
    }

    public async Task DeleteServerAsync(string serverId)
    {
        var servers = (await GetServersAsync())
            .Where(s => s.Id != serverId).ToList();
        Preferences.Default.Set(ServersKey, JsonSerializer.Serialize(servers));
    }

    public Task<ServerConfig?> GetCurrentServerAsync()
    {
        var id = Preferences.Default.Get(CurrentServerKey, "");
        if (string.IsNullOrEmpty(id)) return Task.FromResult<ServerConfig?>(null);

        var json = Preferences.Default.Get(ServersKey, "[]");
        var servers = JsonSerializer.Deserialize<List<ServerConfig>>(json) ?? new();
        return Task.FromResult(servers.FirstOrDefault(s => s.Id == id));
    }

    public Task SetCurrentServerAsync(string? serverId)
    {
        Preferences.Default.Set(CurrentServerKey, serverId ?? "");
        return Task.CompletedTask;
    }
}
