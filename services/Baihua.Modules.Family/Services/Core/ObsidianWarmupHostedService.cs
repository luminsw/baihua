using Microsoft.Extensions.Hosting;
namespace Baihua.Modules.Family.Services
{
    /// <summary>
    /// 后端启动时预热 Obsidian：避免用户首次搜索时才触发「启动+关闭」。
    /// </summary>
    public class ObsidianWarmupHostedService : IHostedService
    {
        private readonly ILogger<ObsidianWarmupHostedService> _logger;

        public ObsidianWarmupHostedService(ILogger<ObsidianWarmupHostedService> logger)
        {
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // 不再自动启动 Obsidian，仅在健康检查中报告状态
            // 用户可通过 WebUI 顶部状态栏查看 Obsidian 运行状态
            _logger.LogInformation("Obsidian auto-start disabled. Status will be shown in WebUI.");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

