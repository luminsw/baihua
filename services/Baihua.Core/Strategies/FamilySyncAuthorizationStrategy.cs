using Baihua.Core;
using Baihua.Core.Localization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using System.Net;

namespace Baihua.Core.Services.Strategies;

/// <summary>
/// 家庭版同步授权策略：Bearer Token + 设备授权验证
/// </summary>
public class FamilySyncAuthorizationStrategy : ISyncAuthorizationStrategy
{
    private readonly DeviceService _deviceService;
    private readonly IStringLocalizer<SharedResources> _loc;

    public FamilySyncAuthorizationStrategy(DeviceService deviceService, IStringLocalizer<SharedResources> loc)
    {
        _deviceService = deviceService;
        _loc = loc;
    }

    public ActionResult? ValidateManifest(HttpContext httpContext, string vaultId, string? deviceId)
    {
        if (!ValidateDeviceAuthorization(httpContext))
        {
            return new UnauthorizedObjectResult(new { error = _loc["Device_NotAuthorized"] });
        }

        return null;
    }

    public ActionResult? ValidateFile(HttpContext httpContext, string vaultId, string? deviceId)
    {
        if (!ValidateDeviceAuthorization(httpContext))
        {
            return new UnauthorizedObjectResult(new { error = _loc["Device_NotAuthorized"] });
        }

        return null;
    }

    private bool ValidateDeviceAuthorization(HttpContext httpContext)
    {
        var authHeader = httpContext.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer "))
        {
            var token = authHeader.Substring("Bearer ".Length).Trim();
            if (_deviceService.ValidateAccessToken(token))
            {
                return true;
            }
        }

        // 仅允许本机回环请求（单进程合并后由宿主中间件按 X-Device-Id 完成设备授权并注入 Bearer）。
        // 不做来源 IP 与已授权设备匹配：IP 是动态的，不同设备在不同时间可能分配相同 IP，
        // 会导致未授权设备借已授权设备的 IP 绕过授权验证。
        var remoteIp = httpContext.Connection.RemoteIpAddress;
        if (remoteIp != null &&
            (IPAddress.IsLoopback(remoteIp) || remoteIp.ToString() == "127.0.0.1" || remoteIp.ToString() == "::1"))
        {
            return true;
        }

        return false;
    }
}
