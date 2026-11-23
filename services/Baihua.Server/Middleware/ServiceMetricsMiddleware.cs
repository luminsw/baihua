using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Diagnostics;
using Baihua.Contracts.Metrics;

namespace Baihua.Server.Middleware;

public class ServiceMetricsMiddleware
{
    private readonly RequestDelegate _next;

    public ServiceMetricsMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ServiceMetrics metrics)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await _next(context);
        }
        finally
        {
            sw.Stop();
            var isError = context.Response.StatusCode >= 400;
            metrics.RecordHttpRequest(sw.Elapsed.TotalMilliseconds, isError);
        }
    }
}

public static class ServiceMetricsMiddlewareExtensions
{
    public static IApplicationBuilder UseServiceMetrics(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<ServiceMetricsMiddleware>();
    }
}
