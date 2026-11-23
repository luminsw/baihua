namespace Baihua.Web.Services;

/// <summary>
/// 解析「WebUI 自身」在服务端进程内可达的基址。
///
/// 背景：部分 SignalR 客户端（如 HeaderStatusBadge → StatusHub）连接的 hub 由 WebUI 自己提供
/// （<c>/hubs/status</c>，只存在于 WebUI：k8s 下 <c>http://127.0.0.1:8788/hubs/status</c> 是 404）。
/// 这些客户端运行在 Blazor Server 的服务端进程里，不能用 <c>NavigationManager.BaseUri</c>
/// ——它反映的是浏览器看到的外部地址（k8s 入口 http://127.0.0.1/），在 Pod 内指向 Kestrel 的
/// 5177 之外的地址（:80 无监听），结果就是 "Connection refused (127.0.0.1:80)"。
///
/// 这里以进程自身监听的端口为准：优先显式配置 <c>WebUi:SelfBaseUrl</c>，
/// 否则取 <c>ASPNETCORE_URLS</c> 里的监听端口（k8s/compose/native 都已设置该变量），
/// 兜底 WebUI 默认端口 5177。
/// </summary>
public static class WebUiSelfUrl
{
    public const int DefaultPort = 5177;

    /// <summary>返回服务端进程内可达的 WebUI 基址（形如 http://127.0.0.1:5177，无尾斜杠）。</summary>
    public static string Resolve(IConfiguration? configuration)
    {
        var explicitBase = configuration?["WebUi:SelfBaseUrl"];
        if (!string.IsNullOrWhiteSpace(explicitBase))
        {
            var trimmed = explicitBase.Trim().TrimEnd('/');
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out _)) return trimmed;
        }

        return $"http://127.0.0.1:{ResolvePort(configuration)}";
    }

    /// <summary>解析自身监听端口：ASPNETCORE_URLS（含通配地址）→ WebUi:SelfBaseUrl → 默认 5177。</summary>
    public static int ResolvePort(IConfiguration? configuration)
    {
        var urls = configuration?["ASPNETCORE_URLS"];
        if (!string.IsNullOrWhiteSpace(urls))
        {
            foreach (var raw in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Port > 0)
                    return uri.Port;
            }
        }

        var self = configuration?["WebUi:SelfBaseUrl"];
        if (!string.IsNullOrWhiteSpace(self)
            && Uri.TryCreate(self.Trim(), UriKind.Absolute, out var selfUri)
            && selfUri.Port > 0)
        {
            return selfUri.Port;
        }

        return DefaultPort;
    }
}
