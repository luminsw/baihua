using System.Text.Json;
using BaihuaSdk.Signing;
using BaihuaSdk.Transport;
using MobileContract.Devices;
using MobileContract.Services;

namespace BaihuaSdk.Push;

/// <summary>
/// 移动端推送轮询实现。
/// 通过 HTTP 长轮询获取服务器待下发的同步推送请求，与 WebSocket 互为降级。
/// </summary>
public class PushPollingServiceImpl : IPushPollingService
{
    private readonly HttpClient _httpClient;
    private readonly IRequestSigner _signer;

    private string _serverUrl = "";

    public PushPollingServiceImpl(HttpClient httpClient, IRequestSigner signer)
    {
        _httpClient = httpClient;
        _signer = signer;
    }

    /// <summary>设置后续轮询使用的服务器地址</summary>
    public void Initialize(string serverUrl)
    {
        _serverUrl = HttpTransport.NormalizeBaseUrl(serverUrl);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PushSyncRequest>> PollPendingAsync(
        string deviceName, bool wait = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_serverUrl))
            throw new InvalidOperationException("PushPollingService 尚未初始化，请先调用 Initialize(serverUrl)。");

        var transport = new HttpTransport(_httpClient, _signer, _serverUrl);
        var query = $"deviceName={Uri.EscapeDataString(deviceName)}&wait={wait.ToString().ToLowerInvariant()}";
        var response = await transport.GetJsonAsync<List<PushSyncRequest>>(
            $"/mg/devices/push-pending?{query}", ct: cancellationToken);

        return response.IsSuccess && response.Data != null
            ? response.Data
            : new List<PushSyncRequest>();
    }
}
