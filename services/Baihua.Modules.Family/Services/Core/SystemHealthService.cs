using Baihua.Core.Models;
using Baihua.Core.Services;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using Baihua.Core.Localization;
using Baihua.Core.Security;
using Microsoft.Extensions.Localization;
using ComponentStatus = Baihua.Contracts.Health.ComponentStatusDto;
using SystemHealthReport = Baihua.Contracts.Health.SystemHealthReportDto;

namespace Baihua.Modules.Family.Services
{
    /// <summary>
    /// 系统健康检查服务：检测所需组件和依赖
    /// </summary>
    public partial class SystemHealthService
    {
        private readonly ILogger<SystemHealthService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IAiConfigService _aiConfigService;
        private readonly VaultSettingsService _vaultSettings;
        private readonly AiMetricsService _metrics;
        private readonly IStringLocalizer<SharedResources> _loc;

        public SystemHealthService(
            IHttpClientFactory httpClientFactory,
            ILogger<SystemHealthService> logger,
            IAiConfigService aiConfigService,
            VaultSettingsService vaultSettings,
            AiMetricsService metrics,
            IStringLocalizer<SharedResources> loc)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _aiConfigService = aiConfigService;
            _vaultSettings = vaultSettings;
            _metrics = metrics;
            _loc = loc;
        }

        /// <summary>
        /// 获取系统健康状态（各子项并行，整体受 cancellationToken 约束）。
        /// </summary>
        public async Task<SystemHealthReport> GetHealthReportAsync(CancellationToken cancellationToken = default)
        {
            var report = new SystemHealthReport
            {
                Timestamp = DateTime.UtcNow,
                Components = new List<ComponentStatus>()
            };

            cancellationToken.ThrowIfCancellationRequested();

            var wallClock = Stopwatch.StartNew();
            var results = await Task.WhenAll(
                HealthCheckHelper.WithCheckDurationAsync(() => CheckGitAsync(cancellationToken)),
                HealthCheckHelper.WithCheckDurationAsync(() => CheckOllamaAsync(cancellationToken)),
                HealthCheckHelper.WithCheckDurationAsync(() => CheckPythonAsync(cancellationToken)),
                HealthCheckHelper.WithCheckDurationAsync(() => CheckNodeAsync(cancellationToken)),
                HealthCheckHelper.WithCheckDurationAsync(() => CheckPipAsync(cancellationToken)),
                HealthCheckHelper.WithCheckDurationAsync(() => CheckApiKeyAsync(cancellationToken)),
                HealthCheckHelper.WithCheckDurationAsync(() => CheckVaultPathAsync(cancellationToken)));
            wallClock.Stop();
            report.TotalWallClockMs = wallClock.ElapsedMilliseconds;

            // 容器部署下 skipped 项（宿主开发工具）不返回，健康页只展示真正适用的组件
            report.Components.AddRange(results.Where(c => c.Status != "skipped"));

            // skipped（如容器部署下不适用的宿主开发工具）不计入分母，避免无关项拉低分数/拉红状态
            var scored = report.Components.Where(c => c.Status != "skipped").ToList();
            var total = scored.Count;
            var healthy = scored.Count(c => c.Status == "healthy");
            var warning = scored.Count(c => c.Status == "warning");
            var critical = scored.Count(c => c.Status == "critical");

            report.HealthScore = total > 0 ? (healthy * 100 + warning * 50) / total : 100;
            report.Status = critical > 0 ? "critical" : (warning > 0 ? "warning" : "healthy");

            // 记录健康检查指标到 .NET Metrics（OpenTelemetry -> OpenObserve）
            foreach (var component in report.Components)
            {
                _metrics.RecordHealthCheck(
                    component.Name, component.Status,
                    component.CheckDurationMs);
            }
            _metrics.RecordHealthCheck(
                "_total", report.Status,
                report.TotalWallClockMs,
                wallClockMs: report.TotalWallClockMs,
                score: report.HealthScore);

            return report;
        }

        /// <summary>在 timeoutMs 内等待退出；若外层 cancellationToken 取消则向上抛出。</summary>
        private async Task<(bool exitedOk, int exitCode, string stdout)> WaitForProcessAsync(
            Process process,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeoutMs);
            try
            {
                await process.WaitForExitAsync(linked.Token);
                var stdout = await process.StandardOutput.ReadToEndAsync();
                _ = await process.StandardError.ReadToEndAsync();
                return (true, process.ExitCode, stdout);
            }
            catch (OperationCanceledException)
            {
                HealthCheckHelper.TryKill(process);
                cancellationToken.ThrowIfCancellationRequested();
                return (false, -1, "");
            }
        }
    }
}
