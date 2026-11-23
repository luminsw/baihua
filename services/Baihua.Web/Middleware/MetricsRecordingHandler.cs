using System.Diagnostics;
using System.Net.Http;
using Baihua.Web.Services;

namespace Baihua.Web.Middleware;

/// <summary>
/// 自动记录所有出站 HTTP 调用到 ApiCallMetricsService 和 E2EPerformanceService。
/// 替代手动在每个 ApiService 方法中调用 RecordApiCall —— 只要通过 IHttpClientFactory
/// 创建的 HttpClient 发送请求，都会被此 Handler 自动计时并记录。
/// </summary>
public class MetricsRecordingHandler : DelegatingHandler
{
    private readonly ApiCallMetricsService _metricsService;
    private readonly EndToEndPerformanceService _e2eService;
    private readonly ILogger<MetricsRecordingHandler> _logger;

    public MetricsRecordingHandler(
        ApiCallMetricsService metricsService,
        EndToEndPerformanceService e2eService,
        ILogger<MetricsRecordingHandler> logger)
    {
        _metricsService = metricsService;
        _e2eService = e2eService;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var endpoint = request.RequestUri?.AbsolutePath ?? request.RequestUri?.ToString() ?? "/unknown";
        var method = request.Method.Method;

        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            stopwatch.Stop();

            // 记录到 API 调用指标
            _metricsService.RecordCall(
                endpoint, method,
                stopwatch.ElapsedMilliseconds,
                response.IsSuccessStatusCode,
                (int)response.StatusCode);

            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();

            _metricsService.RecordCall(
                endpoint, method,
                stopwatch.ElapsedMilliseconds,
                false,
                null,
                ex.Message);

            throw;
        }
        // OperationCanceledException 不记录，正常传播
    }
}
