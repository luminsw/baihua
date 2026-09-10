using Baihua.Core;
using Baihua.Core.Localization;
using Baihua.Family.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Baihua.Contracts.Tasks;

namespace Baihua.Family.Controllers
{
    public partial class TasksController : ControllerBase
    {
        [HttpGet]
        public ActionResult GetTasks([FromQuery] string? status = null, [FromQuery] int limit = 50)
        {
            var tasks = _taskManager.GetAllTasks();
            
            if (!string.IsNullOrEmpty(status))
            {
                tasks = tasks.Where(t => t.Status.ToString().Equals(status, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            return Ok(new { tasks = tasks.Take(limit), total = tasks.Count });
        }

        [HttpDelete("{taskId}")]
        public ActionResult DeleteTask(string taskId)
        {
            if (_taskManager.DeleteTask(taskId))
            {
                return Ok(new { success = true });
            }
            return NotFound();
        }

        [HttpPost("{taskId}/cancel")]
        public async Task<IActionResult> CancelTask(string taskId)
        {
            var task = _taskManager.GetTask(taskId);
            if (task == null)
            {
                return NotFound(new { error = _loc["Task_NotFound"] });
            }

            if (task.Status != RunnerTaskStatus.Running && task.Status != RunnerTaskStatus.Pending)
            {
                return BadRequest(new { error = _loc["Task_CancelOnlyRunning"] });
            }

            var cancelled = await _taskManager.CancelTaskAsync(taskId);
            if (!cancelled)
            {
                return BadRequest(new { error = _loc["Task_CancelFailed"] });
            }

            // OpenClaw 任务需要额外杀掉进程
            if (task.Type == "openclaw" && task.Parameters?.TryGetValue("openclawId", out var openClawIdStr) == true
                && int.TryParse(openClawIdStr, out var openClawId))
            {
                _ = Task.Run(async () => await _openClawTaskService.CancelTaskAsync(openClawId));
            }

            var providerId = task.Parameters?.GetValueOrDefault("providerId");
            var model = task.Parameters?.GetValueOrDefault("model");

            return Ok(new { success = true, message = _loc["Task_CancelSuccess"] });
        }

        /// <summary>
        /// 获取任务历史列表
        /// </summary>
        [HttpGet("history")]
        public ActionResult<TaskHistoryResponse> GetTaskHistory([FromQuery] int limit = 100, [FromQuery] int offset = 0)
        {
            try
            {
                var tasks = _taskManager.GetAllTasks(limit, offset);
                var total = _taskManager.GetTaskCount();
                
                return Ok(new TaskHistoryResponse
                {
                    Success = true,
                    Tasks = tasks.Select(t => new TaskHistoryItem
                    {
                        TaskId = t.Id,
                        TaskType = t.Type,
                        Status = t.Status.ToString(),
                        Progress = (int)t.Progress.Percentage,
                        ProgressMessage = t.Progress.Message,
                        CreatedAt = t.CreatedAt,
                        UpdatedAt = t.UpdatedAt
                    }).ToList(),
                    Total = total
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取任务历史失败");
                return StatusCode(500, new { error = _loc["Task_HistoryError"] });
            }
        }

        /// <summary>
        /// 清理指定时间之前的任务
        /// </summary>
        [HttpPost("cleanup")]
        public ActionResult<CleanupResponse> CleanupTasks([FromBody] CleanupRequest request)
        {
            try
            {
                int count;
                if (request.OlderThanDays > 0)
                {
                    var retention = TimeSpan.FromDays(request.OlderThanDays);
                    count = _taskManager.CleanupOldTasks(retention);
                }
                else
                {
                    // 默认清理所有已完成的任务
                    count = _taskManager.CleanupAllCompletedTasks();
                }
                
                return Ok(new CleanupResponse { Success = true, DeletedCount = count });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清理任务失败");
                return StatusCode(500, new { error = _loc["Task_CleanupError"] });
            }
        }

        /// <summary>
        /// 清空所有任务历史（包括进行中的任务）
        /// </summary>
        [HttpDelete("all")]
        public ActionResult DeleteAllTasks()
        {
            try
            {
                _taskManager.DeleteAllTasks();
                return Ok(new { success = true, message = _loc["Task_AllDeleted"] });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清空所有任务失败");
                return StatusCode(500, new { error = _loc["Task_DeleteAllError"] });
            }
        }

        /// <summary>
        /// 获取任务统计信息
        /// </summary>
        [HttpGet("stats")]
        public ActionResult<TaskStatsResponse> GetTaskStats()
        {
            try
            {
                var total = _taskManager.GetTaskCount();
                var pending = _taskManager.GetTasksByStatus("Pending", 1000).Count;
                var running = _taskManager.GetTasksByStatus("Running", 1000).Count;
                var completed = _taskManager.GetTasksByStatus("Success", 1000).Count;
                var failed = _taskManager.GetTasksByStatus("Failed", 1000).Count;
                
                return Ok(new TaskStatsResponse
                {
                    Success = true,
                    Total = total,
                    Pending = pending,
                    Running = running,
                    Completed = completed,
                    Failed = failed
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取任务统计失败");
                return StatusCode(500, new { error = _loc["Task_StatsError"] });
            }
        }
    }
}
