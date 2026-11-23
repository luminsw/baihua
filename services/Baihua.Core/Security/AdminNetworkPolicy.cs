using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;

namespace Baihua.Core.Security;

/// <summary>
/// 管理 API 网络访问策略。
/// 默认仅允许 loopback 访问；容器/反向代理部署时通过环境变量显式放行网段，
/// 替代原先"信任整个 RFC1918"的宽泛放行（局域网任意设备可无认证调用管理 API 的安全隐患）。
///
/// 环境变量：
///   BAIHUA_ADMIN_ALLOWED_NETS  允许访问管理 API 的网段（逗号分隔 CIDR），默认空 = 仅 loopback
///   BAIHUA_TRUSTED_PROXY_NETS  受信任的反向代理网段（其 X-Forwarded-For 头被采信），默认空 = 仅 loopback 代理
/// </summary>
public static class AdminNetworkPolicy
{
    public const string AdminAllowedNetsEnv = "BAIHUA_ADMIN_ALLOWED_NETS";
    public const string TrustedProxyNetsEnv = "BAIHUA_TRUSTED_PROXY_NETS";

    /// <summary>解析逗号分隔的 CIDR 列表；空值返回空列表，非法项忽略。</summary>
    public static List<System.Net.IPNetwork> ParseNets(string? raw)
    {
        var nets = new List<System.Net.IPNetwork>();
        if (string.IsNullOrWhiteSpace(raw))
            return nets;

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                nets.Add(System.Net.IPNetwork.Parse(part));
            }
            catch
            {
                // 忽略非法 CIDR（配置错误不应导致启动失败，日志由调用方决定）
            }
        }
        return nets;
    }

    /// <summary>
    /// 配置 ForwardedHeaders：只信任 loopback 代理；
    /// 其他代理网段需通过 BAIHUA_TRUSTED_PROXY_NETS 显式声明，
    /// 防止局域网客户端伪造 X-Forwarded-For 绕过访问控制。
    /// </summary>
    public static void ConfigureForwardedHeaders(ForwardedHeadersOptions options, IConfiguration config)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownProxies.Add(IPAddress.Loopback);
        options.KnownProxies.Add(IPAddress.IPv6Loopback);
        foreach (var net in ParseNets(config[TrustedProxyNetsEnv]))
            options.KnownIPNetworks.Add(net);
    }

    /// <summary>是否允许访问管理 API：loopback 或显式放行网段内。</summary>
    public static bool IsAllowed(IPAddress? remoteIp, IEnumerable<System.Net.IPNetwork> allowedNets)
    {
        if (remoteIp == null)
            return false;
        if (IPAddress.IsLoopback(remoteIp) || remoteIp.ToString() == "127.0.0.1" || remoteIp.ToString() == "::1")
            return true;
        foreach (var net in allowedNets)
        {
            try
            {
                if (net.Contains(remoteIp))
                    return true;
            }
            catch
            {
                // 网段与地址族不匹配（如 IPv4 网段 vs IPv6 地址），跳过
            }
        }
        return false;
    }
}
