using System.Net;
using System.Text;
using System.Text.Json;
using BaihuaSdk.Signing;

namespace BaihuaSdk.Transport;

/// <summary>
/// HTTP 传输层。
/// 封装 HttpClient、签名注入、HTTPS→HTTP 降级、错误映射、URL 构造。
/// </summary>
public class HttpTransport
{
    private readonly HttpClient _client;
    private readonly IRequestSigner _signer;
    private readonly string _baseUrl;
    private readonly string _vaultId;
    private readonly string _deviceId;

    public HttpTransport(
        HttpClient client,
        IRequestSigner signer,
        string baseUrl,
        string vaultId = "",
        string deviceId = "")
    {
        _client = client;
        _signer = signer;
        _baseUrl = NormalizeBaseUrl(baseUrl);
        _vaultId = vaultId;
        _deviceId = deviceId;
    }

    public string BaseUrl => _baseUrl;

    // ---- public HTTP methods ----

    public Task<ApiResponse<string>> GetAsync(string path,
        Dictionary<string, string>? query = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, path, query, null, ct);

    public Task<ApiResponse<byte[]>> GetBytesAsync(string path,
        Dictionary<string, string>? query = null, CancellationToken ct = default) =>
        SendBytesAsync(path, query, ct);

    public Task<ApiResponse<T>> GetJsonAsync<T>(string path,
        Dictionary<string, string>? query = null, CancellationToken ct = default) =>
        SendJsonAsync<T>(HttpMethod.Get, path, query, null, ct);

    public Task<ApiResponse<T>> PostJsonAsync<T>(string path, object? body,
        Dictionary<string, string>? query = null, CancellationToken ct = default) =>
        SendJsonAsync<T>(HttpMethod.Post, path, query, body, ct);

    public Task<ApiResponse<T>> PutJsonAsync<T>(string path, object? body,
        Dictionary<string, string>? query = null, CancellationToken ct = default) =>
        SendJsonAsync<T>(HttpMethod.Put, path, query, body, ct);

    public Task<ApiResponse<T>> DeleteJsonAsync<T>(string path,
        Dictionary<string, string>? query = null, CancellationToken ct = default) =>
        SendJsonAsync<T>(HttpMethod.Delete, path, query, null, ct);

    /// <summary>
    /// 发起 POST 流式请求（SSE），返回原始 HttpResponseMessage。
    /// 调用方负责读取流并 dispose response。
    /// </summary>
    public async Task<HttpResponseMessage> PostStreamAsync(
        string path, object? body,
        Dictionary<string, string>? query = null, CancellationToken ct = default)
    {
        var url = BuildUrl(path, query);
        var bodyStr = body != null ? JsonSerializer.Serialize(body) : null;

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (bodyStr != null)
            request.Content = new StringContent(bodyStr, Encoding.UTF8, "application/json");

        InjectSignature(request, bodyStr);

        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return response;
    }

    // ---- core send logic ----

    private async Task<ApiResponse<string>> SendAsync(
        HttpMethod method, string path,
        Dictionary<string, string>? query, object? body, CancellationToken ct)
    {
        var url = BuildUrl(path, query);
        var bodyStr = body != null ? JsonSerializer.Serialize(body) : null;

        using var request = new HttpRequestMessage(method, url);
        if (bodyStr != null)
            request.Content = new StringContent(bodyStr, Encoding.UTF8, "application/json");

        InjectSignature(request, bodyStr);

        var (response, rawBody) = await ExecuteWithFallbackAsync(request, ct);
        var serverMsg = ExtractServerError(rawBody);
        var friendly = serverMsg ?? HttpCodeMessage((int)response.StatusCode);

        if (response.IsSuccessStatusCode)
            return ApiResponse<string>.Ok(rawBody, (int)response.StatusCode);
        else
            return ApiResponse<string>.Fail((int)response.StatusCode, friendly, rawBody);
    }

    private async Task<ApiResponse<byte[]>> SendBytesAsync(
        string path, Dictionary<string, string>? query, CancellationToken ct)
    {
        var url = BuildUrl(path, query);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        InjectSignature(request, null);

        var (response, _) = await ExecuteWithFallbackAsync(request, ct);

        if (response.IsSuccessStatusCode)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return ApiResponse<byte[]>.Ok(bytes, (int)response.StatusCode);
        }

        var rawBody = await response.Content.ReadAsStringAsync(ct);
        var serverMsg = ExtractServerError(rawBody);
        var friendly = serverMsg ?? HttpCodeMessage((int)response.StatusCode);
        return ApiResponse<byte[]>.Fail((int)response.StatusCode, friendly);
    }

    private async Task<ApiResponse<T>> SendJsonAsync<T>(
        HttpMethod method, string path,
        Dictionary<string, string>? query, object? body, CancellationToken ct)
    {
        var url = BuildUrl(path, query);
        var bodyStr = body != null ? JsonSerializer.Serialize(body) : null;

        using var request = new HttpRequestMessage(method, url);
        if (bodyStr != null)
            request.Content = new StringContent(bodyStr, Encoding.UTF8, "application/json");

        InjectSignature(request, bodyStr);

        var (response, rawBody) = await ExecuteWithFallbackAsync(request, ct);

        if (response.IsSuccessStatusCode)
        {
            var obj = JsonSerializer.Deserialize<T>(rawBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return ApiResponse<T>.Ok(obj!, (int)response.StatusCode);
        }

        var serverMsg = ExtractServerError(rawBody);
        var friendly = serverMsg ?? HttpCodeMessage((int)response.StatusCode);
        return ApiResponse<T>.Fail((int)response.StatusCode, friendly);
    }

    // ---- internal utilities ----

    internal string BuildUrl(string path, Dictionary<string, string>? extraQuery = null)
    {
        var sb = new StringBuilder(_baseUrl.TrimEnd('/'));
        sb.Append(path);

        var query = new Dictionary<string, string>();
        if (_vaultId.Length > 0) query["vaultId"] = _vaultId;
        if (_deviceId.Length > 0) query["deviceId"] = _deviceId;
        if (extraQuery != null)
            foreach (var (k, v) in extraQuery) query[k] = v;

        if (query.Count > 0)
        {
            sb.Append('?');
            var first = true;
            foreach (var (k, v) in query)
            {
                if (!first) sb.Append('&');
                sb.Append(Uri.EscapeDataString(k));
                sb.Append('=');
                sb.Append(Uri.EscapeDataString(v));
                first = false;
            }
        }

        return sb.ToString();
    }

    private void InjectSignature(HttpRequestMessage request, string? body)
    {
        // 必须使用 OriginalString（保留 URL 编码），避免 Uri.ToString() 解码中文/特殊字符导致签名字符串不一致
        var signUrl = request.RequestUri!.OriginalString;
        var headers = _signer.SignRequest(
            request.Method.Method, signUrl, body, _baseUrl);
        foreach (var (k, v) in headers)
            request.Headers.TryAddWithoutValidation(k, v);
    }

    private async Task<(HttpResponseMessage Response, string RawBody)> ExecuteWithFallbackAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var response = await _client.SendAsync(request, ct);
        var rawBody = await response.Content.ReadAsStringAsync(ct);

        // HTTPS→HTTP 降级（自签名证书场景）
        if (!response.IsSuccessStatusCode && response.StatusCode == 0 &&
            request.RequestUri!.Scheme == "https")
        {
            var httpUrl = request.RequestUri.ToString().Replace("https://", "http://");
            using var fallback = new HttpRequestMessage(request.Method, httpUrl);
            foreach (var h in request.Headers)
                fallback.Headers.TryAddWithoutValidation(h.Key, h.Value);
            if (request.Content != null)
            {
                var body = await request.Content.ReadAsStringAsync(ct);
                fallback.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
            try
            {
                var fbResponse = await _client.SendAsync(fallback, ct);
                var fbBody = await fbResponse.Content.ReadAsStringAsync(ct);
                return (fbResponse, fbBody);
            }
            catch
            {
                // fallback failed, return original
            }
        }

        return (response, rawBody);
    }

    // ---- static helpers ----

    /// <summary>将地址规范化为完整的基础 URL</summary>
    public static string NormalizeBaseUrl(string address)
    {
        var trimmed = address.Trim();
        if (trimmed.Length == 0) return "";
        if (trimmed.StartsWith("http://") || trimmed.StartsWith("https://"))
            return trimmed.TrimEnd('/');
        return $"http://{trimmed}";
    }

    /// <summary>从 JSON 响应体中提取服务端错误消息</summary>
    public static string? ExtractServerError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                var msg = err.GetString();
                if (!string.IsNullOrWhiteSpace(msg)) return msg.Trim();
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>HTTP 状态码 → 中文错误消息</summary>
    public static string HttpCodeMessage(int code) => code switch
    {
        400 => "请求参数错误",
        401 => "设备未授权，请先完成配对",
        403 => "没有权限访问",
        404 => "请求的资源不存在",
        429 => "请求太频繁，请稍后再试",
        500 => "服务器内部错误",
        502 or 503 => "服务暂时不可用",
        _ => $"服务器错误（{code}）"
    };
}
