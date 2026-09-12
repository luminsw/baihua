using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Baihua.Server.OpenTelemetry;

/// <summary>
/// OpenTelemetry 扩展方法，支持向 OpenObserve 推送 Metrics、Logs 和 Traces。
/// 与 hua-ge/Cloud 版本保持一致。
/// </summary>
public static class OpenTelemetryExtensions
{
    /// <summary>
    /// 添加 OpenObserve OTLP 导出（Metrics + Logs + Traces）。
    /// </summary>
    public static IOpenTelemetryBuilder AddOpenObserveTelemetry(
        this IServiceCollection services,
        string serviceName,
        string[] meterNames,
        string webUrl,
        string? user = null,
        string? password = null,
        bool enabled = true,
        string? environmentName = null,
        double traceSamplingRatio = 0.1)
    {
        // If exporter not enabled, create builder with resource and return without configuring exporters
        var env = environmentName
            ?? Environment.GetEnvironmentVariable("OTEL_DEPLOYMENT_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Production";

        var serviceVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0";

        var builder = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName, serviceVersion: serviceVersion)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["service.instance.id"] = Environment.MachineName,
                    ["deployment.environment"] = env,
                    ["host.name"] = Environment.MachineName
                }));

        if (!enabled || string.IsNullOrWhiteSpace(webUrl))
        {
            return builder;
        }

        var baseUrl = webUrl.TrimEnd('/');
        var hasAuth = !string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(password);
        var authHeader = hasAuth
            ? $"Authorization=Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"))}"
            : null;

        var metricsEndpoint = new Uri($"{baseUrl}/api/default/v1/metrics");
        var logsEndpoint = new Uri($"{baseUrl}/api/default/v1/logs");
        var tracesEndpoint = new Uri($"{baseUrl}/api/default/v1/traces");

        var isDevelopment = env.Equals("Development", StringComparison.OrdinalIgnoreCase);

        builder.WithMetrics(metrics =>
        {
            foreach (var meter in meterNames)
            {
                metrics.AddMeter(meter);
            }

            // 自定义 Histogram Bucket
            metrics.AddView("http.request.duration_ms", new ExplicitBucketHistogramConfiguration
            {
                Boundaries = new double[] { 0, 10, 25, 50, 100, 200, 500, 1000, 2000, 5000, 10000 }
            });
            metrics.AddView("ai.latency_ms", new ExplicitBucketHistogramConfiguration
            {
                Boundaries = new double[] { 0, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 30000, 60000 }
            });
            metrics.AddView("sync.operation_duration_ms", new ExplicitBucketHistogramConfiguration
            {
                Boundaries = new double[] { 0, 100, 500, 1000, 5000, 15000, 30000, 60000, 120000 }
            });
            metrics.AddView("search.latency_ms", new ExplicitBucketHistogramConfiguration
            {
                Boundaries = new double[] { 0, 10, 25, 50, 100, 250, 500, 1000, 2500 }
            });

            metrics.AddOtlpExporter(options =>
            {
                options.Endpoint = metricsEndpoint;
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
                options.TimeoutMilliseconds = 30000;
                if (!string.IsNullOrEmpty(authHeader))
                {
                    options.Headers = authHeader;
                }
            });
        });

        builder.WithLogging(logging =>
        {
            logging.AddOtlpExporter(options =>
            {
                options.Endpoint = logsEndpoint;
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
                options.TimeoutMilliseconds = 30000;
                if (!string.IsNullOrEmpty(authHeader))
                {
                    options.Headers = authHeader;
                }
            });
        });

        builder.WithTracing(tracing =>
        {
            tracing
                .AddSource(serviceName)
                .SetSampler(isDevelopment
                    ? new AlwaysOnSampler()
                    : new ParentBasedSampler(new TraceIdRatioBasedSampler(traceSamplingRatio)))
                .AddOtlpExporter(options =>
                {
                    options.Endpoint = tracesEndpoint;
                    options.Protocol = OtlpExportProtocol.HttpProtobuf;
                    options.TimeoutMilliseconds = 30000;
                    if (!string.IsNullOrEmpty(authHeader))
                    {
                        options.Headers = authHeader;
                    }
                });
        });

        return builder;
    }
}
