using Baihua.Web.Services;

namespace Baihua.Web.Middleware;

/// <summary>
/// WebUI 认证中间件
/// 检查请求是否包含有效的认证 Cookie 或 CLI Token，如果没有则重定向到登录页面
/// </summary>
public class AuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AuthenticationMiddleware> _logger;

    // 不需要认证的路径（精确匹配或前缀匹配）
    private static readonly string[] ExemptPaths = new[]
    {
        "/login",
        "/_blazor",
        "/_framework",
        "/css",
        "/js",
        "/favicon.ico",
        "/health",
        "/api/health",
        "/api/internal",
        "/api/auth/cli-token",
        "/hubs/status"
    };

    // 静态文件扩展名
    private static readonly string[] StaticExtensions = new[]
    {
        ".css", ".js", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".svg", ".woff", ".woff2", ".ttf", ".eot"
    };

    public AuthenticationMiddleware(RequestDelegate next, ILogger<AuthenticationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, AuthService authService)
    {
        var path = context.Request.Path.Value ?? "/";

        // 检查是否是豁免路径或静态文件
        if (IsExemptPath(path))
        {
            await _next(context);
            return;
        }

        // 检查 CLI 一次性令牌（query string，优先于 Cookie）
        var cliToken = context.Request.Query["cli-token"].FirstOrDefault();
        if (!string.IsNullOrEmpty(cliToken) && authService.ValidateCliToken(cliToken))
        {
            // 生成正式认证令牌并写入 Cookie
            var authToken = await authService.GenerateAuthTokenAsync();
            context.Response.Cookies.Append(AuthService.AuthCookieName, authToken, new CookieOptions
            {
                Path = "/",
                MaxAge = TimeSpan.FromDays(AuthService.CookieExpiryDays),
                SameSite = SameSiteMode.Strict,
                HttpOnly = true
            });

            // 重定向到当前路径（仅去掉 cli-token，保留其他 query 参数）
            var remainingParams = context.Request.Query
                .Where(kvp => !string.Equals(kvp.Key, "cli-token", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var cleanUrl = context.Request.PathBase + context.Request.Path;
            if (remainingParams.Count > 0)
            {
                var qs = string.Join("&", remainingParams.Select(kvp =>
                    $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value.ToString())}"));
                cleanUrl += "?" + qs;
            }
            context.Response.Redirect(cleanUrl);
            return;
        }

        // 检查认证 Cookie
        var authCookie = context.Request.Cookies[AuthService.AuthCookieName];
        if (!string.IsNullOrEmpty(authCookie) && await authService.ValidateAuthTokenAsync(authCookie))
        {
            await _next(context);
            return;
        }

        // 未认证，记录日志
        _logger.LogDebug("Unauthorized request to {Path}, redirecting to login", path);

        // API 请求返回 401（不加 WWW-Authenticate：该头会让浏览器弹出"需要授权访问"对话框）
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized", message = "请先登录" });
            return;
        }

        // 页面请求重定向到登录页（考虑 BasePath）
        var returnUrl = Uri.EscapeDataString(context.Request.PathBase + context.Request.Path + context.Request.QueryString);
        var loginPath = context.Request.PathBase + "/login";
        context.Response.Redirect($"{loginPath}?returnUrl={returnUrl}");
    }

    private static bool IsExemptPath(string path)
    {
        // 检查前缀匹配
        foreach (var exemptPath in ExemptPaths)
        {
            if (path.Equals(exemptPath, StringComparison.OrdinalIgnoreCase))
                return true;
            if (path.StartsWith(exemptPath + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // 检查静态文件扩展名
        foreach (var ext in StaticExtensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

/// <summary>
/// 中间件扩展方法
/// </summary>
public static class AuthenticationMiddlewareExtensions
{
    public static IApplicationBuilder UseWebUIAuthentication(this IApplicationBuilder app)
    {
        return app.UseMiddleware<AuthenticationMiddleware>();
    }
}
