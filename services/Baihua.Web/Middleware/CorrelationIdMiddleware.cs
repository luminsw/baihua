using System.Diagnostics;

namespace Baihua.Web.Middleware;

/// <summary>
/// 请求关联ID中间件 - 用于追踪请求全链路
/// </summary>
public class CorrelationIdMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    // 请求头名称（与常见规范一致）
    public const string CorrelationIdHeader = "X-Correlation-Id";
    public const string RequestIdHeader = "X-Request-Id";

    // AsyncLocal 用于在同一会话中传递
    private static readonly AsyncLocal<string?> _currentCorrelationId = new();

    public static string? CurrentCorrelationId => _currentCorrelationId.Value;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 1. 尝试从请求头获取已有的 CorrelationId
        var correlationId = GetCorrelationIdFromHeaders(context);

        // 2. 如果没有，生成新的
        if (string.IsNullOrEmpty(correlationId))
        {
            correlationId = GenerateCorrelationId();
        }

        // 3. 设置到 AsyncLocal，供后续代码使用
        _currentCorrelationId.Value = correlationId;

        // 4. 添加到响应头（方便客户端追踪）
        context.Response.Headers[CorrelationIdHeader] = correlationId;

        // 5. 创建带 CorrelationId 的 Scope
        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["RequestPath"] = context.Request.Path,
            ["RequestMethod"] = context.Request.Method
        }))
        {
            // 6. 记录请求开始（仅在 Debug 级别）
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Request started [{CorrelationId}] {Method} {Path}",
                    correlationId, context.Request.Method, context.Request.Path);
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                await _next(context);
            }
            finally
            {
                stopwatch.Stop();

                // 7. 根据响应状态码和耗时选择日志级别
                var statusCode = context.Response.StatusCode;
                var elapsedMs = stopwatch.ElapsedMilliseconds;

                LogRequestCompletion(correlationId, context.Request.Method, context.Request.Path,
                    statusCode, elapsedMs);
            }
        }
    }

    private static string? GetCorrelationIdFromHeaders(HttpContext context)
    {
        // 优先从 CorrelationIdHeader 获取，其次是 RequestIdHeader
        if (context.Request.Headers.TryGetValue(CorrelationIdHeader, out var correlationId) &&
            !string.IsNullOrEmpty(correlationId))
        {
            return correlationId.ToString();
        }

        if (context.Request.Headers.TryGetValue(RequestIdHeader, out var requestId) &&
            !string.IsNullOrEmpty(requestId))
        {
            return requestId.ToString();
        }

        return null;
    }

    private static string GenerateCorrelationId()
    {
        // 生成短GUID（移除连字符，更紧凑）
        return Guid.NewGuid().ToString("N")[..16];
    }

    private void LogRequestCompletion(string correlationId, string method, string path,
        int statusCode, long elapsedMs)
    {
        // 根据状态码和耗时选择日志级别

        // 错误请求 - Error 级别
        if (statusCode >= 500)
        {
            _logger.LogError(
                "Request failed [{CorrelationId}] {Method} {Path} - Status: {StatusCode}, Elapsed: {ElapsedMs}ms",
                correlationId, method, path, statusCode, elapsedMs);
            return;
        }

        // 客户端错误 - Warning 级别
        if (statusCode >= 400)
        {
            _logger.LogWarning(
                "Request rejected [{CorrelationId}] {Method} {Path} - Status: {StatusCode}, Elapsed: {ElapsedMs}ms",
                correlationId, method, path, statusCode, elapsedMs);
            return;
        }

        // 慢请求 - Warning 级别（>1秒），但排除 WebSocket 连接（101 Switching Protocols）
        // WebSocket 是长连接，耗时长是预期行为
        if (elapsedMs > 1000 && statusCode != 101)
        {
            _logger.LogWarning(
                "Slow request [{CorrelationId}] {Method} {Path} - Status: {StatusCode}, Elapsed: {ElapsedMs}ms",
                correlationId, method, path, statusCode, elapsedMs);
            return;
        }

        // WebSocket 连接完成 - Information 级别（仅用于追踪连接生命周期）
        // 包括 /hubs/status (SignalR Hub) 和 /_blazor (Blazor Server)
        if (statusCode == 101 && _logger.IsEnabled(LogLevel.Information))
        {
            var wsType = path.StartsWith("/hubs", StringComparison.OrdinalIgnoreCase) ? "SignalR" :
                         path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase) ? "Blazor" : "WebSocket";
            _logger.LogInformation(
                "{WsType} closed [{CorrelationId}] {Method} {Path} - Duration: {ElapsedMs}ms",
                wsType, correlationId, method, path, elapsedMs);
            return;
        }

        // 正常请求 - Debug 级别（避免生产环境日志过多）
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Request completed [{CorrelationId}] {Method} {Path} - Status: {StatusCode}, Elapsed: {ElapsedMs}ms",
                correlationId, method, path, statusCode, elapsedMs);
        }
    }
}

/// <summary>
/// 扩展方法
/// </summary>
public static class CorrelationIdMiddlewareExtensions
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        return app.UseMiddleware<CorrelationIdMiddleware>();
    }
}
