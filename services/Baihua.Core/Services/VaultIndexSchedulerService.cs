using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
namespace Baihua.Core.Services
{
    /// <summary>
    /// 知识库 FTS5 索引定时更新服务
    /// 定期扫描知识库文件，以 mtime/size 快照对比为准做增量索引：
    /// 仅新增/变更的笔记重新写入、已删除的笔记删除 FTS 行；
    /// 首次启动或快照丢失（如进程重启）时才整库重建。
    /// </summary>
    public class VaultIndexSchedulerService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<VaultIndexSchedulerService> _logger;
        private readonly TimeSpan _checkInterval;
        // vaultId -> (相对路径 -> 文件指纹)；快照持久化到数据目录 JSON 文件，
        // 服务重启后加载，避免每次都整库重建（文件损坏时安全退化为全量）
        private readonly Dictionary<string, Dictionary<string, NoteFileStamp>> _snapshots;

        public VaultIndexSchedulerService(
            IServiceProvider serviceProvider,
            ILogger<VaultIndexSchedulerService> logger,
            IConfiguration configuration)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            
            // 默认每小时检查一次，可通过配置调整
            var intervalMinutes = configuration.GetValue<int?>("VaultIndex:IntervalMinutes") ?? 60;
            _checkInterval = TimeSpan.FromMinutes(Math.Max(5, intervalMinutes));

            _snapshots = VaultIndexSnapshotStore.Load(logger);
            if (_snapshots.Count > 0)
                _logger.LogInformation("已加载 {Count} 个知识库的索引快照", _snapshots.Count);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("知识库索引定时服务启动，检查间隔: {Interval} 分钟", _checkInterval.TotalMinutes);

            // 首次启动时等待 30 秒，让其他服务初始化完成
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunIndexCheckAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "知识库索引检查失败");
                }

                try
                {
                    await Task.Delay(_checkInterval, stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }

            _logger.LogInformation("知识库索引定时服务已停止");
        }

        private async Task RunIndexCheckAsync(CancellationToken stoppingToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var vaultSettings = scope.ServiceProvider.GetRequiredService<VaultSettingsService>();
            var indexer = scope.ServiceProvider.GetRequiredService<VaultNoteIndexer>();

            var vaults = vaultSettings.GetVaults();
            if (vaults.Count == 0)
            {
                _logger.LogDebug("没有配置知识库，跳过索引检查");
                return;
            }

            foreach (var vault in vaults)
            {
                if (stoppingToken.IsCancellationRequested) break;
                if (string.IsNullOrWhiteSpace(vault.Path) || !Directory.Exists(vault.Path))
                {
                    _logger.LogDebug("知识库路径无效: {VaultId}", vault.Id);
                    continue;
                }

                try
                {
                    _snapshots.TryGetValue(vault.Id, out var previous);
                    if (previous == null)
                    {
                        _logger.LogInformation("首次为知识库 {VaultName} 建立索引（无快照，整库重建）", vault.Name);
                    }

                    var result = await indexer.IndexVaultChangesAsync(vault.Id, vault.Path, previous, stoppingToken);

                    if (result.IsFullRebuild)
                    {
                        _logger.LogInformation("知识库 {VaultName} 的 FTS5 索引整库重建完成：{Added} 个文件",
                            vault.Name, result.Added);
                    }
                    else if (result.Changed)
                    {
                        _logger.LogInformation("知识库 {VaultName} 的 FTS5 索引增量更新完成：新增 {Added}、更新 {Updated}、删除 {Removed}",
                            vault.Name, result.Added, result.Updated, result.Removed);
                    }
                    else
                    {
                        _logger.LogDebug("知识库 {VaultName} 无变更，跳过索引", vault.Name);
                    }

                    // 仅在成功后更新快照；失败时不更新，下轮按同一快照重试
                    _snapshots[vault.Id] = result.Snapshot;
                    VaultIndexSnapshotStore.Save(_snapshots, _logger);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "检查知识库 {VaultId} 索引状态时失败", vault.Id);
                }
            }
        }
    }
}
