using System.Text.Json;
using BaihuaSdk.Signing;
using BaihuaSdk.Transport;
using MobileContract.Pairing;
using MobileContract.Services;

namespace BaihuaSdk.Services;

/// <summary>
/// 配对服务实现。
/// 处理二维码解析、设备注册、配对码流程。
/// 与 Kotlin PairingService.kt + DeviceRegistrationService.kt 逻辑对齐。
/// </summary>
public class PairingServiceImpl : IPairingService, IDeviceRegistrationService
{
    private readonly HttpClient _httpClient;
    private readonly IRequestSigner _signer;
    private readonly string _deviceId;
    private readonly string _deviceName;
    private readonly string? _systemDeviceName;

    private string _serverUrl = "";

    public PairingServiceImpl(
        HttpClient httpClient, IRequestSigner signer,
        string deviceId, string deviceName, string? systemDeviceName = null)
    {
        _httpClient = httpClient;
        _signer = signer;
        _deviceId = deviceId;
        _deviceName = deviceName;
        _systemDeviceName = systemDeviceName;
    }

    /// <summary>设置后续配对操作使用的服务器地址</summary>
    public void Initialize(string serverUrl)
    {
        _serverUrl = HttpTransport.NormalizeBaseUrl(serverUrl);
    }

    // ---- QR Code parsing ----

    /// <summary>解析二维码 JSON 内容</summary>
    public static QrCodeContent? ParseQrCode(string content)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<QrCodeContent>(content,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed == null || string.IsNullOrEmpty(parsed.HostName))
                return null;

            var hasNewFormat = !string.IsNullOrEmpty(parsed.HttpUrl) ||
                               !string.IsNullOrEmpty(parsed.HttpsUrl) ||
                               !string.IsNullOrEmpty(parsed.BaseUrl);
            var hasOldFormat = !string.IsNullOrEmpty(parsed.ServerUrl);
            return (hasNewFormat || hasOldFormat) ? parsed : null;
        }
        catch { return null; }
    }

    /// <summary>从二维码内容提取服务器地址</summary>
    public static ServerAddresses GetServerAddresses(QrCodeContent content)
    {
        var serverId = FirstNonEmpty(content.ServerId, content.DeviceId)
                       ?? $"server-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        // 新版统一地址（baseUrl）优先
        if (!string.IsNullOrEmpty(content.BaseUrl))
        {
            return new ServerAddresses(serverId, content.BaseUrl, content.BaseUrl,
                content.HostName, content.DeviceId);
        }

        // 双地址格式
        var http = content.HttpUrl ?? content.ServerUrl ?? "";
        var https = content.HttpsUrl ?? content.HttpUrl ?? content.ServerUrl ?? "";
        return new ServerAddresses(serverId, http, https, content.HostName, content.DeviceId);
    }

    // ---- Device Registration (not part of IPairingService) ----

    /// <summary>向服务器注册本机设备（扫码/手动输入流程）</summary>
    public async Task<RegisterDeviceResult> RegisterDeviceAsync(string serverUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            var transport = new HttpTransport(_httpClient, _signer, serverUrl);
            var body = new { deviceId = _deviceId, deviceName = _deviceName, deviceType = "maui", systemDeviceName = _systemDeviceName };
            var resp = await transport.PostJsonAsync<JsonElement>(
                "/mg/register-device", body);

            if (resp.IsSuccess)
            {
                var root = resp.Data!;

                // 服务端直接返回字段（与 Kotlin/ArkTS 对齐），不是 { success, data } 包装
                if (root.ValueKind != JsonValueKind.Object)
                    return new RegisterDeviceResult { Success = false, ErrorMessage = "返回格式错误" };

                if (GetString(root, "error") is { } serverErr)
                    return new RegisterDeviceResult { Success = false, ErrorMessage = serverErr };

                return new RegisterDeviceResult
                {
                    Success = true,
                    Authorized = GetBool(root, "authorized"),
                    SharedSecret = GetString(root, "sharedSecret"),
                    RequestId = GetString(root, "requestId"),
                    ServerId = GetString(root, "serverId"),
                    DeviceName = GetString(root, "serverName") ?? GetString(root, "deviceName"),
                    AccessToken = GetString(root, "accessToken")
                };
            }

            var httpMsg = $"HTTP {(int)resp.StatusCode}";
            var bodyErr = HttpTransport.ExtractServerError(resp.RawBody);
            if (!string.IsNullOrEmpty(bodyErr))
                httpMsg += $": {bodyErr}";
            return new RegisterDeviceResult { Success = false, ErrorMessage = httpMsg };
        }
        catch (Exception ex)
        {
            return new RegisterDeviceResult { Success = false, ErrorMessage = $"异常: {ex.Message}" };
        }
    }

    // ---- IPairingService implementation (pair-code flow) ----

    /// <inheritdoc />
    public async Task<PairCodeResponse> GetPairCodeAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var transport = new HttpTransport(_httpClient, _signer, _serverUrl);
        var resp = await transport.GetJsonAsync<JsonElement>("/mg/pair/code", ct: cancellationToken);

        if (!resp.IsSuccess)
            return new PairCodeResponse();

        var root = resp.Data;
        var pairCode = root.ValueKind == JsonValueKind.Object
            ? GetString(root, "pairCode")
            : null;
        var deviceId = root.ValueKind == JsonValueKind.Object
            ? GetString(root, "deviceId") ?? ""
            : "";

        return new PairCodeResponse
        {
            PairCode = pairCode,
            DeviceId = deviceId
        };
    }

    /// <inheritdoc />
    public async Task<PairCodeResponse> RefreshPairCodeAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var transport = new HttpTransport(_httpClient, _signer, _serverUrl);
        var resp = await transport.PostJsonAsync<JsonElement>("/mg/pair/code/refresh", null, ct: cancellationToken);

        if (!resp.IsSuccess)
            return new PairCodeResponse();

        var root = resp.Data;
        var pairCode = root.ValueKind == JsonValueKind.Object
            ? GetString(root, "pairCode")
            : null;

        return new PairCodeResponse
        {
            PairCode = pairCode,
            DeviceId = _deviceId
        };
    }

    /// <inheritdoc />
    public async Task<PairResponse> PairDeviceAsync(PairRequest request, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var transport = new HttpTransport(_httpClient, _signer, _serverUrl);
        var body = new
        {
            pairCode = request.PairCode,
            deviceName = request.DeviceName ?? _deviceName,
            deviceId = request.DeviceId ?? _deviceId
        };
        var resp = await transport.PostJsonAsync<JsonElement>("/mg/pair", body, ct: cancellationToken);

        return MapPairResponse(resp);
    }

    /// <inheritdoc />
    /// <remarks>
    /// [后端未实现 /mg/pair/status] 当前服务端未向移动端暴露独立的配对状态查询端点，
    /// 因此直接抛出 <see cref="NotSupportedException"/>。待后端实现后可移除。
    /// </remarks>
    public Task<PairResponse> CheckPairStatusAsync(string requestId, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("[后端未实现 /mg/pair/status] 移动端 SDK 暂不支持查询配对状态，请通过 PairDeviceAsync 的结果或 /mg/pair 重试。");
    }

    /// <inheritdoc />
    /// <remarks>
    /// [后端未实现 /mg/verify-token] 当前服务端未实现令牌验证端点，
    /// 因此直接抛出 <see cref="NotSupportedException"/>。待后端实现后可移除。
    /// </remarks>
    public Task<bool> VerifyTokenAsync(VerifyTokenRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("[后端未实现 /mg/verify-token] 移动端 SDK 暂不支持令牌验证端点。");
    }

    /// <inheritdoc />
    public async Task<AuthConfigResponse> GetAuthConfigAsync(AuthConfigRequest request, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var transport = new HttpTransport(_httpClient, _signer, _serverUrl);
        var body = new { deviceId = request.DeviceId, deviceName = request.DeviceName ?? _deviceName };
        var resp = await transport.PostJsonAsync<JsonElement>("/mg/auth/config", body, ct: cancellationToken);

        if (resp.IsSuccess && resp.Data is JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
                return new AuthConfigResponse { Success = false, Message = "返回格式错误" };

            // HTTP 200 但业务上不成功
            if (GetString(root, "error") is { } serverErr)
                return new AuthConfigResponse { Success = false, Message = serverErr };

            return new AuthConfigResponse
            {
                Success = GetBool(root, "success"),
                SharedSecret = GetString(root, "sharedSecret"),
                Message = GetString(root, "message")
            };
        }

        // 401 Unauthorized
        if (resp.StatusCode == 401)
        {
            return new AuthConfigResponse
            {
                Success = false,
                Message = HttpTransport.ExtractServerError(resp.RawBody) ?? "设备未授权"
            };
        }

        var httpMsg = $"HTTP {(int)resp.StatusCode}";
        var bodyErr = HttpTransport.ExtractServerError(resp.RawBody);
        if (!string.IsNullOrEmpty(bodyErr))
            httpMsg += $": {bodyErr}";
        return new AuthConfigResponse { Success = false, Message = httpMsg };
    }

    // ---- helpers ----

    private void EnsureInitialized()
    {
        if (string.IsNullOrEmpty(_serverUrl))
            throw new InvalidOperationException("PairingService 尚未初始化，请先调用 Initialize(serverUrl)。");
    }

    private static PairResponse MapPairResponse(ApiResponse<JsonElement> resp)
    {
        if (!resp.IsSuccess)
        {
            return new PairResponse
            {
                Status = "failed",
                Message = resp.ErrorMessage ?? "请求失败"
            };
        }

        var root = resp.Data;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new PairResponse
            {
                Status = "failed",
                Message = "返回格式错误"
            };
        }

        return new PairResponse
        {
            RequestId = GetString(root, "requestId"),
            AccessToken = GetString(root, "accessToken"),
            ExpiresIn = GetInt(root, "expiresIn"),
            Status = GetString(root, "status") ?? "pending",
            Message = GetString(root, "message")
        };
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrEmpty(v));

    private static string? GetString(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool GetBool(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    private static int GetInt(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}

/// <summary>二维码内容（与 Kotlin QRCodeContent 对齐）</summary>
public record QrCodeContent
{
    public string? ServerId { get; init; }
    public string? HttpUrl { get; init; }
    public string? HttpsUrl { get; init; }
    public string? BaseUrl { get; init; }
    public string? ServerUrl { get; init; }
    public string HostName { get; init; } = "";
    public string? DeviceId { get; init; }
}

/// <summary>解析后的服务器地址</summary>
public record ServerAddresses(
    string ServerId,
    string HttpUrl,
    string HttpsUrl,
    string HostName,
    string? DeviceId = null);
