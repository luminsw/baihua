using System.Net;
using System.Text;
using System.Text.Json;

namespace BaihuaSdk.Tests;

/// <summary>
/// Mock HttpMessageHandler for testing HttpClient requests.
/// Captures request bodies and returns configured responses.
/// </summary>
internal class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode status, object content, Action<string>? onBody)> _responses = new();

    public List<string> RequestLog { get; } = new();

    public void SetupResponse(string path, HttpStatusCode status, object content, Action<string>? onBody = null)
    {
        _responses[path] = (status, content, onBody);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        RequestLog.Add(path);

        if (request.Content != null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            if (_responses.TryGetValue(path, out var resp) && resp.onBody != null)
                resp.onBody(body);
        }

        if (_responses.TryGetValue(path, out var response))
        {
            HttpContent httpContent;
            if (response.content is string str)
                httpContent = new StringContent(str, Encoding.UTF8, "application/json");
            else if (response.content is byte[] bytes)
                httpContent = new ByteArrayContent(bytes);
            else
                httpContent = new StringContent(JsonSerializer.Serialize(response.content), Encoding.UTF8, "application/json");

            return new HttpResponseMessage(response.status) { Content = httpContent };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}