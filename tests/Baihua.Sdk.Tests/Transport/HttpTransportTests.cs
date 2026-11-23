using System.Net;
using System.Text;
using System.Text.Json;
using BaihuaSdk.Signing;
using BaihuaSdk.Transport;
using Moq;
using Xunit;

namespace BaihuaSdk.Tests.Transport;

public class HttpTransportTests
{
    [Fact]
    public void NormalizeBaseUrl_IpOnly_AddsHttpScheme()
    {
        Assert.Equal("http://192.168.1.1", HttpTransport.NormalizeBaseUrl("192.168.1.1"));
    }

    [Fact]
    public void NormalizeBaseUrl_HttpUrl_KeepsScheme()
    {
        Assert.Equal("http://192.168.1.1:8788", HttpTransport.NormalizeBaseUrl("http://192.168.1.1:8788"));
    }

    [Fact]
    public void NormalizeBaseUrl_HttpsUrl_KeepsScheme()
    {
        Assert.Equal("https://example.com", HttpTransport.NormalizeBaseUrl("https://example.com"));
    }

    [Fact]
    public void NormalizeBaseUrl_TrailingSlash_Removed()
    {
        Assert.Equal("http://example.com:8788", HttpTransport.NormalizeBaseUrl("http://example.com:8788/"));
    }

    [Fact]
    public void NormalizeBaseUrl_Empty_ReturnsEmpty()
    {
        Assert.Equal("", HttpTransport.NormalizeBaseUrl(""));
    }

    [Fact]
    public void ExtractServerError_ValidJson_ReturnsError()
    {
        var result = HttpTransport.ExtractServerError("{\"error\":\"something went wrong\"}");
        Assert.Equal("something went wrong", result);
    }

    [Fact]
    public void ExtractServerError_NoErrorField_ReturnsNull()
    {
        Assert.Null(HttpTransport.ExtractServerError("{\"ok\":true}"));
    }

    [Fact]
    public void ExtractServerError_EmptyBody_ReturnsNull()
    {
        Assert.Null(HttpTransport.ExtractServerError(""));
        Assert.Null(HttpTransport.ExtractServerError(null));
    }

    [Fact]
    public void ExtractServerError_InvalidJson_ReturnsNull()
    {
        Assert.Null(HttpTransport.ExtractServerError("not json"));
    }

    [Fact]
    public void HttpCodeMessage_CommonCodes()
    {
        Assert.Equal("请求参数错误", HttpTransport.HttpCodeMessage(400));
        Assert.Equal("设备未授权，请先完成配对", HttpTransport.HttpCodeMessage(401));
        Assert.Equal("没有权限访问", HttpTransport.HttpCodeMessage(403));
        Assert.Equal("请求的资源不存在", HttpTransport.HttpCodeMessage(404));
        Assert.Equal("请求太频繁，请稍后再试", HttpTransport.HttpCodeMessage(429));
        Assert.Equal("服务器内部错误", HttpTransport.HttpCodeMessage(500));
        Assert.Equal("服务暂时不可用", HttpTransport.HttpCodeMessage(502));
        Assert.Equal("服务暂时不可用", HttpTransport.HttpCodeMessage(503));
    }

    [Fact]
    public void HttpCodeMessage_UnknownCode_ReturnsGeneric()
    {
        Assert.Equal("服务器错误（418）", HttpTransport.HttpCodeMessage(418));
    }

    [Fact]
    public void ApiResponse_Ok()
    {
        var resp = ApiResponse<string>.Ok("data", 200);
        Assert.True(resp.IsSuccess);
        Assert.Equal(200, resp.StatusCode);
        Assert.Equal("data", resp.Data);
        Assert.Null(resp.ErrorMessage);
    }

    [Fact]
    public void ApiResponse_Fail()
    {
        var resp = ApiResponse<string>.Fail(404, "not found", "raw");
        Assert.False(resp.IsSuccess);
        Assert.Equal(404, resp.StatusCode);
        Assert.Equal("not found", resp.ErrorMessage);
        Assert.Equal("raw", resp.RawBody);
    }

    // ---- HTTPS -> HTTP fallback tests ----

    private class HttpsToHttpFallbackHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Scheme == "https")
            {
                // 模拟 HTTPS 连接失败（StatusCode = 0）
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)0));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task GetJsonAsync_HttpsFails_FallsBackToHttp()
    {
        var handler = new HttpsToHttpFallbackHandler();
        var client = new HttpClient(handler);
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>())).Returns(new Dictionary<string, string>());

        var transport = new HttpTransport(client, signerMock.Object, "https://example.com:8788");

        var resp = await transport.GetJsonAsync<JsonElement>("/test");

        Assert.True(resp.IsSuccess);
        Assert.Equal(200, resp.StatusCode);
    }

    [Fact]
    public async Task PostJsonAsync_HttpsFails_FallsBackToHttp()
    {
        var handler = new HttpsToHttpFallbackHandler();
        var client = new HttpClient(handler);
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>())).Returns(new Dictionary<string, string>());

        var transport = new HttpTransport(client, signerMock.Object, "https://example.com:8788");

        var resp = await transport.PostJsonAsync<JsonElement>("/test", new { value = 1 });

        Assert.True(resp.IsSuccess);
        Assert.Equal(200, resp.StatusCode);
    }

    [Fact]
    public void BuildUrl_AddsVaultAndDeviceQueryParams()
    {
        var client = new HttpClient();
        var signerMock = new Mock<IRequestSigner>();
        var transport = new HttpTransport(client, signerMock.Object, "http://localhost:8788", "vault-1", "device-1");

        var url = transport.BuildUrl("/file");

        Assert.Contains("vaultId=vault-1", url);
        Assert.Contains("deviceId=device-1", url);
    }

    [Fact]
    public void BuildUrl_ChinesePath_PercentEncodes()
    {
        var client = new HttpClient();
        var signerMock = new Mock<IRequestSigner>();
        var transport = new HttpTransport(client, signerMock.Object, "http://localhost:8788", "vault-1", "device-1");

        var url = transport.BuildUrl("/file", new Dictionary<string, string>
        {
            ["path"] = "关联与区别/测试.md"
        });

        var uri = new Uri(url);
        Assert.NotEqual(uri.ToString(), uri.OriginalString);
        Assert.Contains("%E5%85%B3", url);
    }

    [Fact]
    public void InjectSignature_UsesOriginalString_ForChinesePath()
    {
        var capturedUrl = "";
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Callback<string, string, string?, string?>((method, url, body, serverUrl) => capturedUrl = url)
            .Returns(new Dictionary<string, string>());

        var handler = new HttpsToHttpFallbackHandler();
        var client = new HttpClient(handler);
        var transport = new HttpTransport(client, signerMock.Object, "http://localhost:8788", "vault-1", "device-1");

        var url = transport.BuildUrl("/file", new Dictionary<string, string>
        {
            ["path"] = "关联与区别/测试.md"
        });

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        var method = typeof(HttpTransport).GetMethod("InjectSignature", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        method.Invoke(transport, new object?[] { request, null });

        Assert.Contains("%E5%85%B3", capturedUrl);
        Assert.DoesNotContain("关联与区别", capturedUrl);
    }
}
