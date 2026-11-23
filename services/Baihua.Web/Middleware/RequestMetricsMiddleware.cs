using System.Diagnostics;
using Baihua.Web.Services;

namespace Baihua.Web.Middleware;

/// <summary>
/// 请求统计中间件：记录每个请求的耗时和路径
/// </summary>
public class RequestMetricsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestMetricsMiddleware> _logger;

    public RequestMetricsMiddleware(RequestDelegate next, ILogger<RequestMetricsMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RequestMetricsService metricsService)
    {
        var stopwatch = Stopwatch.StartNew();
        var method = context.Request.Method;
        var path = context.Request.Path.Value ?? "/";

        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();
            var elapsedMs = stopwatch.ElapsedMilliseconds;
            var statusCode = context.Response.StatusCode;

            // 排除不应计入性能统计的请求（静态文件、SignalR 长连接、健康检查等）
            if (!ShouldIgnore(path, statusCode))
            {
                // 记录请求指标（用于统计分析）
                metricsService.RecordRequest(method, path, elapsedMs, statusCode);
            }

            // 日志级别策略：
            // - Error: 5xx 错误
            // - Warning: 4xx 错误或慢请求(>1s)
            // - Debug: 正常请求（避免生产环境日志过多）

            if (statusCode >= 500)
            {
                // 服务器错误 - Error 级别
                _logger.LogError("请求失败 {StatusCode}: {Method} {Path} - {ElapsedMs}ms",
                    statusCode, method, path, elapsedMs);
            }
            else if (statusCode >= 400)
            {
                // 客户端错误 - Warning 级别
                _logger.LogWarning("请求被拒绝 {StatusCode}: {Method} {Path} - {ElapsedMs}ms",
                    statusCode, method, path, elapsedMs);
            }
            else if (elapsedMs > 1000 && !IsLongLivedEndpoint(path, statusCode))
            {
                // 慢请求 - Warning 级别（排除 WebSocket/SignalR 长连接）
                _logger.LogWarning("慢请求: {Method} {Path} - {ElapsedMs}ms",
                    method, path, elapsedMs);
            }
            else
            {
                // 正常请求 - Debug 级别（仅在 Debug 模式记录）
                _logger.LogDebug("请求完成: {Method} {Path} - {StatusCode}, {ElapsedMs}ms",
                    method, path, statusCode, elapsedMs);
            }
        }
    }

    /// <summary>
    /// 判断是否为长连接端点（WebSocket/SignalR），这些连接会保持打开状态
    /// </summary>
    private static bool IsLongLivedEndpoint(string path, int statusCode)
    {
        // WebSocket 协议切换成功（101 Switching Protocols）
        if (statusCode == 101)
            return true;

        // Blazor 和 SignalR Hub 端点
        if (path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase))
            return true;
        if (path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// 判断请求是否不应计入性能统计（静态文件、SignalR、健康检查等）
    /// </summary>
    private static bool ShouldIgnore(string path, int statusCode)
    {
        // 排除 WebSocket/SignalR 长连接（连接持续时间不是请求处理时间）
        if (IsLongLivedEndpoint(path, statusCode))
            return true;

        // 排除静态文件
        var staticExtensions = new[] { ".css", ".js", ".svg", ".png", ".jpg", ".jpeg", ".gif", 
            ".woff", ".woff2", ".ttf", ".eot", ".ico", ".map", ".json", ".xml" };
        if (staticExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            return true;

        // 排除健康检查（高频且不代表业务性能）
        if (path.Equals("/health", StringComparison.OrdinalIgnoreCase))
            return true;

        // 排除 Swagger 文档请求
        if (path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}

/// <summary>
/// 中间件扩展方法
/// </summary>
public static class RequestMetricsMiddlewareExtensions
{
    public static IApplicationBuilder UseRequestMetrics(this IApplicationBuilder app)
    {
        return app.UseMiddleware<RequestMetricsMiddleware>();
    }
}
