using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Localization;
using Baihua.Core;
using Baihua.Core.Localization;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Baihua.Core.Hubs;
using Microsoft.EntityFrameworkCore;
using Baihua.Data;

namespace Baihua.Core.Services
{
    /// <summary>
    /// 任务管理器——统筹任务的生命周期管理
    /// 职责：内存状态管理、协调 ITaskRepository（持久化）、ITaskNotifier（推送）、ITaskCancellationManager（取消）
    /// 经重构从 480+ 行的上帝类拆分为 4 个关注点分离的组件
    /// </summary>
    public class TaskManager
    {
        private sealed class SimpleLocalizer : IStringLocalizer<SharedResources>
        {
            public LocalizedString this[string name] => new LocalizedString(name, Translate(name), resourceNotFound: true);
            public LocalizedString this[string name, params object[] arguments] => new LocalizedString(name, string.Format(Translate(name), arguments), resourceNotFound: true);
            public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Array.Empty<LocalizedString>();
            public IStringLocalizer WithCulture(System.Globalization.CultureInfo culture) => this;

            private static string Translate(string key)
            {
                // Provide a small set of Chinese translations used by tests
                return key switch
                {
                    "Task_Created" => "任务已创建",
                    "Task_Progress_Failed" => "失败",
                    "Task_Progress_Done" => "任务完成",
                    "Task_Progress_Percentage" => "{0}%",
                    "Task_Progress_FailedNote" => "失败",
                    _ => key,
                };
            }
        }

        private readonly ConcurrentDictionary<string, TaskInfo> _tasks = new();
        private readonly ITaskRepository _repository;
        private readonly ITaskNotifier _notifier;
        private readonly ITaskCancellationManager _cancellationManager;
        private readonly IStringLocalizer<SharedResources> _loc;
        private readonly ILogger<TaskManager>? _logger;

        /// <summary>
        /// 全依赖构造函数（推荐使用）
        /// </summary>
        public TaskManager(
            ITaskRepository repository,
            ITaskNotifier notifier,
            ITaskCancellationManager cancellationManager,
            ILogger<TaskManager>? logger = null,
            IStringLocalizer<SharedResources>? loc = null)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
            _cancellationManager = cancellationManager ?? throw new ArgumentNullException(nameof(cancellationManager));
            _logger = logger;
            _loc = loc ?? new SimpleLocalizer();
        }

        /// <summary>
        /// 原始构造函数（向后兼容）
        /// 自动创建默认的 ITaskRepository、ITaskNotifier、ITaskCancellationManager 实现
        /// </summary>
        public TaskManager(
            IDbContextFactory<FamilyDbContext> dbContextFactory,
            IHubContext<TaskProgressHub>? hubContext = null,
            ILogger<TaskManager>? logger = null)
        {
            if (dbContextFactory == null)
                throw new ArgumentNullException(nameof(dbContextFactory));

            _repository = new TaskRepository(dbContextFactory, logger as ILogger<TaskRepository>);
            _cancellationManager = new TaskCancellationManager();
            _loc = new SimpleLocalizer();
            _notifier = new TaskNotifier(hubContext, logger as ILogger<TaskNotifier>,
                new Lazy<TaskManager>(() => this));
            _logger = logger;
        }

        // ============ 公开 API ============

        /// <summary>
        /// 推送补充事件（比如 Anki 卡片生成进度、AI 对话之外的活动）
        /// </summary>
        public async Task NotifySupplementEventAsync(string taskId, string eventName, object? data = null)
        {
            await _notifier.NotifySupplementEventAsync(taskId, eventName, data);
        }

        /// <summary>
        /// 创建新任务并持久化
        /// </summary>
        public string CreateTask(string type, Dictionary<string, string>? parameters = null)
        {
            var taskId = Guid.NewGuid().ToString("N");
            var now = DateTime.UtcNow;

            var taskInfo = new TaskInfo
            {
                Id = taskId,
                Type = type,
                Status = RunnerTaskStatus.Pending,
                Parameters = parameters,
                Progress = new TaskProgress { Current = 0, Total = 1, Message = _loc["Task_Created"] },
                CreatedAt = now,
                UpdatedAt = now
            };

            _tasks[taskId] = taskInfo;

            var entity = TaskEntityMapper.ToEntity(taskInfo);
            _repository.CreateTask(entity);

            _ = NotifyTaskUpdateAsync(taskId);
            return taskId;
        }

        /// <summary>
        /// 按 TaskId 获取任务（优先从内存，回退到数据库）
        /// </summary>
        public TaskInfo? GetTask(string taskId)
        {
            // 优先从内存获取
            if (_tasks.TryGetValue(taskId, out var task))
                return task;

            // 内存中没有，从数据库加载
            var dbTask = _repository.GetTaskById(taskId);
            if (dbTask == null) return null;

            task = TaskEntityMapper.MapFromEntity(dbTask);
            _tasks[taskId] = task;
            return task;
        }

        /// <summary>
        /// 获取所有任务列表（从数据库查询）
        /// </summary>
        public List<TaskInfo> GetAllTasks(int limit = 100, int offset = 0)
        {
            return _repository.GetAllTasks(limit, offset)
                .Select(TaskEntityMapper.MapFromEntity)
                .ToList();
        }

        /// <summary>
        /// 按状态查询任务
        /// </summary>
        public List<TaskInfo> GetTasksByStatus(string status, int limit = 100)
        {
            return _repository.GetTasksByStatus(status, limit)
                .Select(TaskEntityMapper.MapFromEntity)
                .ToList();
        }

        /// <summary>
        /// 删除任务（内存 + 数据库）
        /// </summary>
        public bool DeleteTask(string taskId)
        {
            _tasks.TryRemove(taskId, out _);
            return _repository.DeleteTask(taskId);
        }

        /// <summary>
        /// 清理超过保留期限的旧任务
        /// </summary>
        public int CleanupOldTasks(TimeSpan retentionPeriod)
        {
            var cutoffDate = DateTime.UtcNow - retentionPeriod;

            // 清理内存中的旧任务
            var oldTasks = _tasks.Values.Where(t => t.UpdatedAt < cutoffDate).ToList();
            foreach (var task in oldTasks)
            {
                _tasks.TryRemove(task.Id, out _);
            }

            // 清理数据库中的旧任务
            var dbDeleted = _repository.DeleteOldTasks(cutoffDate);

            return oldTasks.Count;
        }

        /// <summary>
        /// 清理所有已完成的任务
        /// </summary>
        public int CleanupAllCompletedTasks()
        {
            var completedStatuses = new[]
                { RunnerTaskStatus.Success, RunnerTaskStatus.Failed, RunnerTaskStatus.Timeout, RunnerTaskStatus.Cancelled };

            // 清理内存中已完成的任务
            var completedTasks = _tasks.Values.Where(t => completedStatuses.Contains(t.Status)).ToList();
            foreach (var task in completedTasks)
            {
                _tasks.TryRemove(task.Id, out _);
            }

            // 清理数据库
            _repository.DeleteCompletedTasks();

            return completedTasks.Count;
        }

        /// <summary>
        /// 删除所有任务
        /// </summary>
        public int DeleteAllTasks()
        {
            _tasks.Clear();
            return _repository.DeleteAllTasks();
        }

        /// <summary>
        /// 获取任务总数
        /// </summary>
        public int GetTaskCount()
        {
            return _repository.GetTaskCount();
        }

        /// <summary>
        /// 为任务创建 CancellationTokenSource
        /// </summary>
        public CancellationTokenSource CreateTaskCts(string taskId, TimeSpan? timeout = null)
        {
            return _cancellationManager.CreateCts(taskId, timeout);
        }

        /// <summary>
        /// 移除任务的 CancellationTokenSource
        /// </summary>
        public void RemoveTaskCts(string taskId)
        {
            _cancellationManager.RemoveCts(taskId);
        }

        /// <summary>
        /// 取消任务
        /// </summary>
        public async Task<bool> CancelTaskAsync(string taskId)
        {
            var task = GetTask(taskId);
            if (task == null) return false;

            if (task.Status != RunnerTaskStatus.Running && task.Status != RunnerTaskStatus.Pending)
                return false;

            _cancellationManager.TryCancel(taskId);
            await UpdateStatus(taskId, RunnerTaskStatus.Cancelled, _loc["Task_CancelledByUser"]);
            return true;
        }

        /// <summary>
        /// 更新任务状态（内存 + 数据库 + 推送）
        /// </summary>
        public async Task UpdateStatus(string taskId, RunnerTaskStatus status, string? error = null, object? data = null)
        {
            if (!_tasks.TryGetValue(taskId, out var task)) return;

            task.Status = status;
            task.UpdatedAt = DateTime.UtcNow;

            var resultData = data != null ? JsonSerializer.Serialize(data) : null;
            var errorMsg = error;

            if (!string.IsNullOrEmpty(error) || data != null)
            {
                task.Result = new TaskResult
                {
                    Success = status == RunnerTaskStatus.Success,
                    Error = error,
                    Data = data
                };
            }

            if (status == RunnerTaskStatus.Success && task.Progress.Total > 0)
            {
                task.Progress.Current = task.Progress.Total;
                task.Progress.Percentage = 100.0;
                task.Progress.Message = _loc["Task_Progress_Done"];
            }

            if (status == RunnerTaskStatus.Failed && !string.IsNullOrEmpty(task.Progress.Message))
            {
                if (!task.Progress.Message.Contains("(失败)") && !task.Progress.Message.Contains("失败") && !task.Progress.Message.StartsWith("❌"))
                {
                    task.Progress.Message += _loc["Task_Progress_FailedNote"];
                }
            }

            // 持久化到数据库
            var startedAt = status == RunnerTaskStatus.Running ? DateTime.UtcNow : (DateTime?)null;
            var completedAt = status is RunnerTaskStatus.Success or RunnerTaskStatus.Failed or RunnerTaskStatus.Timeout
                ? DateTime.UtcNow
                : (DateTime?)null;

            _repository.UpdateStatus(
                taskId,
                status.ToString(),
                errorMsg,
                resultData,
                (int)task.Progress.Percentage,
                task.Progress.Message,
                startedAt,
                completedAt);

            await NotifyTaskUpdateAsync(taskId);
        }

        /// <summary>
        /// 更新任务进度（首次更新自动将 Pending→Running）
        /// </summary>
        public async Task UpdateProgress(string taskId, int current, int total, string message)
        {
            if (!_tasks.TryGetValue(taskId, out var task)) return;

            var prevStatus = task.Status;
            var oldProgress = task.Progress;

            // 首次更新进度时自动将 Pending 转为 Running
            if (task.Status == RunnerTaskStatus.Pending)
            {
                task.Status = RunnerTaskStatus.Running;
            }

            var newProgress = new TaskProgress
            {
                Current = current,
                Total = total,
                Message = message,
                Percentage = total > 0 ? (double)current / total * 100 : 0
            };

            task.Progress = newProgress;
            task.UpdatedAt = DateTime.UtcNow;

            // 持久化到数据库
            var startedAt = prevStatus == RunnerTaskStatus.Pending ? DateTime.UtcNow : (DateTime?)null;

            try
            {
                _repository.UpdateProgress(
                    taskId,
                    (int)newProgress.Percentage,
                    message,
                    startedAt);
            }
            catch
            {
                // DB 写入失败时回滚内存状态
                task.Status = prevStatus;
                task.Progress = oldProgress;
                throw;
            }

            await NotifyTaskUpdateAsync(taskId);
        }

        // ============ 内部方法 ============

        private async Task NotifyTaskUpdateAsync(string taskId)
        {
            await _notifier.NotifyTaskUpdateAsync(taskId);
        }
    }
}
