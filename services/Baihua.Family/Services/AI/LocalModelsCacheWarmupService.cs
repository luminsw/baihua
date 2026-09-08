using Baihua.Core.Models;
using Baihua.Core.Services;

namespace Baihua.Family.Services
{
    /// <summary>
    /// 应用启动时预热本地模型相关缓存（硬件信息 + 工具/运行中/已下载/可下载模型）
    /// </summary>
    public class LocalModelsCacheWarmupService : BackgroundService
    {
        private readonly ILogger<LocalModelsCacheWarmupService> _logger;
        private readonly IServiceProvider _serviceProvider;

        public LocalModelsCacheWarmupService(
            ILogger<LocalModelsCacheWarmupService> logger,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 延迟几秒，等 Kestrel 启动完成后再预热，避免日志混在启动输出中
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var hardwareService = scope.ServiceProvider.GetRequiredService<HardwareInfoService>();

                _logger.LogInformation("正在预热本地模型缓存...");

                // 预热硬件信息
                hardwareService.WarmupCache();

                // 后台预热工具状态/运行中模型/已下载模型（避免页面首访冷加载 5s+）
                try
                {
                    var deployment = scope.ServiceProvider.GetRequiredService<LocalModelDeploymentService>();
                    _ = Task.WhenAll(
                        deployment.GetLocalToolsAsync(ct: stoppingToken),
                        deployment.GetRunningModelsAsync(ct: stoppingToken),
                        deployment.GetDownloadedModelsAsync(stoppingToken),
                        deployment.GetAvailableModelsAsync("ollama", stoppingToken),
                        deployment.GetAvailableModelsAsync("lmstudio", stoppingToken),
                        deployment.GetAvailableModelsAsync("llamacpp", stoppingToken),
                        deployment.GetAvailableModelsAsync("openvino", stoppingToken));
                    _logger.LogInformation("tool/running/downloaded/available cache warmup started");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "tool/model cache warmup failed (non-critical)");
                }

                _logger.LogInformation("本地模型缓存预热完成");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "本地模型缓存预热失败（非关键，首次访问时会自动填充）");
            }
        }
    }
}
