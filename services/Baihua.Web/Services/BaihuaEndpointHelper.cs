namespace Baihua.Web.Services;

/// <summary>
/// 出站连接 Baihua 后端时的基址处理。Windows 上 <c>localhost</c> 常优先解析为 <c>::1</c>，
/// 而 Kestrel 绑定 <c>localhost</c> 时常见仅为 IPv4 监听，会导致连接失败与重试，延迟可达数秒。
/// 因此把本机回环统一成显式 IPv4 <c>127.0.0.1</c>，避免走 <c>::1</c>。
/// </summary>
public static class BaihuaEndpointHelper
{
    public static string NormalizeOutboundBaseUrl(string? url, string fallback = "http://127.0.0.1:8788")
    {
        var raw = string.IsNullOrWhiteSpace(url) ? fallback : url.Trim().TrimEnd('/');
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return fallback;

        if (ShouldRewriteLoopbackToIpv4(uri.Host))
            return RewriteHost(uri, "127.0.0.1");

        return raw;
    }

    private static bool ShouldRewriteLoopbackToIpv4(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        // http://[::1]:port 解析后 Host 为 "::1"
        if (string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static string RewriteHost(Uri uri, string newHost)
    {
        var builder = new UriBuilder(uri) { Host = newHost };
        return builder.Uri.ToString().TrimEnd('/');
    }
}
