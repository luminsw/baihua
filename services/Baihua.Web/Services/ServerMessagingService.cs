using Baihua.Contracts.ServerMessaging;

namespace Baihua.Web.Services;

/// <summary>
/// 百花服务器互联消息服务（对端管理 + 双向消息）。
/// </summary>
public class ServerMessagingService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ServerMessagingService> _logger;

    public ServerMessagingService(IHttpClientFactory httpClientFactory, ILogger<ServerMessagingService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<List<ServerPeerDto>> GetPeersAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient("FamilyApi");
            var response = await client.GetAsync("api/server-peers");
            if (!response.IsSuccessStatusCode) return new List<ServerPeerDto>();
            return await response.Content.ReadFromJsonAsync<List<ServerPeerDto>>() ?? new List<ServerPeerDto>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ServerMessaging] 获取对端列表失败");
            return new List<ServerPeerDto>();
        }
    }

    public async Task<(bool ok, string? error, ServerPeerDto? peer)> AddPeerAsync(string name, string baseUrl, string? token)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("FamilyApi");
            var response = await client.PostAsJsonAsync("api/server-peers", new ServerPeerSaveRequest
            {
                Name = name,
                BaseUrl = baseUrl,
                Token = string.IsNullOrWhiteSpace(token) ? null : token
            });
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                return (false, $"添加失败（HTTP {(int)response.StatusCode}）: {body}", null);
            }
            var peer = await response.Content.ReadFromJsonAsync<ServerPeerDto>();
            return (true, null, peer);
        }
        catch (Exception ex)
        {
            return (false, $"添加失败: {ex.Message}", null);
        }
    }

    public async Task<bool> DeletePeerAsync(Guid peerId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("FamilyApi");
            var response = await client.DeleteAsync($"api/server-peers/{peerId}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ServerMessaging] 删除对端失败");
            return false;
        }
    }

    public async Task<(bool ok, string? error)> SendMessageAsync(Guid peerId, string content)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("FamilyApi");
            var response = await client.PostAsJsonAsync("api/server-msg/send", new ServerMessageSendRequest
            {
                PeerId = peerId,
                Content = content
            });
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                return (false, $"发送失败（HTTP {(int)response.StatusCode}）: {body}");
            }
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"发送失败: {ex.Message}");
        }
    }

    public async Task<List<ServerMessageDto>> GetMessagesAsync(Guid peerId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("FamilyApi");
            var response = await client.GetAsync($"api/server-msg/list?peerId={peerId}");
            if (!response.IsSuccessStatusCode) return new List<ServerMessageDto>();
            return await response.Content.ReadFromJsonAsync<List<ServerMessageDto>>() ?? new List<ServerMessageDto>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ServerMessaging] 获取消息列表失败");
            return new List<ServerMessageDto>();
        }
    }
}
