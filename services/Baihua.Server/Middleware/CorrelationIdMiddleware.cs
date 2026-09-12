using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Baihua.Server.Middleware;

/// <summary>
/// 请求关联 ID 中间件 — 为每个 HTTP 请求分配唯一 correlationId，
/// 贯穿整个请求生命周期，便于在日志和 OpenObserve 中追踪调用链。
/// </summary>
public class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 优先使用客户端传入的 correlationId，否则生成新的
        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault()
            ?? Guid.NewGuid().ToString("N")[..12];

        context.Items["CorrelationId"] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        // 将 correlationId 添加到日志作用域
        var loggerFactory = (ILoggerFactory?)context.RequestServices.GetService(typeof(ILoggerFactory));
        var logger = loggerFactory?.CreateLogger("CorrelationId");
        using var scope = logger?.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        await _next(context);
        scope?.Dispose();
    }
}

public static class CorrelationIdMiddlewareExtensions
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<CorrelationIdMiddleware>();
    }
}
