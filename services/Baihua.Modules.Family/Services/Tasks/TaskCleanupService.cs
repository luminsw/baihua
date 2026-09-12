using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Core;
namespace Baihua.Modules.Family.Services
{
    /// <summary>
    /// 后台任务清理服务
    /// 定期清理过期的任务记录
    /// </summary>
    public class TaskCleanupService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<TaskCleanupService> _logger;
        private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1);

        public TaskCleanupService(
            IServiceProvider serviceProvider,
            ILogger<TaskCleanupService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 防御性日志：记录托管环境的简要状态以便排查启动时问题
            try
            {
                var procId = Environment.ProcessId;
                _logger.LogDebug("CleanupService running in PID {Pid}", procId);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "操作失败"); }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var taskManager = scope.ServiceProvider.GetRequiredService<TaskManager>();
                    taskManager.CleanupOldTasks(TimeSpan.FromHours(24));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during task cleanup");
                }

                try
                {
                    await Task.Delay(_cleanupInterval, stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken);
        }
    }
}
