using Baihua.Core;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Baihua.Contracts.OpenClaw;
using Baihua.Core.Localization;
using Baihua.Data;
using Baihua.Data.Entities;

namespace Baihua.Modules.Family.Services;

public interface IOpenClawTaskService
{
    Task<OpenClawTaskDto> CreateTaskAsync(string prompt);
    Task<OpenClawTaskDto?> GetTaskAsync(int id);
    Task<List<OpenClawTaskDto>> GetTasksAsync(int limit = 100);
    Task<bool> DeleteTaskAsync(int id);
    Task<bool> CancelTaskAsync(int id);
    Task<string?> GetReportContentAsync(int id);
}

public class OpenClawTaskService : IOpenClawTaskService
{
    private readonly IDbContextFactory<FamilyDbContext> _dbFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Baihua.Core.Services.TaskManager? _taskManager;
    private readonly ILogger<OpenClawTaskService> _logger;
    private readonly IStringLocalizer<SharedResources> _loc;
    private readonly string _reportsDir;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Process> _runningProcesses = new();
    // OpenClaw TaskId -> TaskManager TaskId 映射
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _openClawToTaskManagerMap = new();

    public OpenClawTaskService(IDbContextFactory<FamilyDbContext> dbFactory, IHttpClientFactory httpClientFactory, Baihua.Core.Services.TaskManager? taskManager, ILogger<OpenClawTaskService> logger, IStringLocalizer<SharedResources> loc)
    {
        _dbFactory = dbFactory;
        _httpClientFactory = httpClientFactory;
        _taskManager = taskManager;
        _logger = logger;
        _loc = loc;
        var dataDir = Baihua.Contracts.BaihuaPaths.Db;
        _reportsDir = Path.Combine(dataDir, "openclaw-reports");
        Directory.CreateDirectory(_reportsDir);
    }

    public async Task<OpenClawTaskDto> CreateTaskAsync(string prompt)
    {
        var task = new OpenClawTask
        {
            TaskId = Guid.NewGuid().ToString("N")[..8],
            Prompt = prompt,
            Status = "running"
        };
        using var db = await _dbFactory.CreateDbContextAsync();
        db.OpenClawTasks.Add(task);
        await db.SaveChangesAsync();

        // 同时注册到 TaskManager，使任务页可见
        string? tmTaskId = null;
        if (_taskManager != null)
        {
            tmTaskId = _taskManager.CreateTask("openclaw", new Dictionary<string, string>
            {
                ["prompt"] = prompt.Length > 100 ? prompt[..100] + "..." : prompt,
                ["openclawId"] = task.Id.ToString()
            });
            _openClawToTaskManagerMap[task.Id] = tmTaskId;
            _ = _taskManager.UpdateStatus(tmTaskId, Baihua.Core.RunnerTaskStatus.Running);
        }

        _ = RunOpenClawAsync(task.Id, prompt, tmTaskId);

        return ToDto(task);
    }

    private static readonly TimeSpan OpenClawTaskTimeout = TimeSpan.FromMinutes(30);

    private async Task RunOpenClawAsync(int taskId, string prompt, string? tmTaskId)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var task = await db.OpenClawTasks.FindAsync(taskId);
        if (task == null) return;

        try
        {
            var sessionId = $"webui-{task.TaskId}";
            var startInfo = new ProcessStartInfo
            {
                FileName = "openclaw",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            startInfo.ArgumentList.Add("agent");
            startInfo.ArgumentList.Add("--session-id");
            startInfo.ArgumentList.Add(sessionId);
            startInfo.ArgumentList.Add("--message");
            startInfo.ArgumentList.Add(prompt);
            startInfo.ArgumentList.Add("--json");
            startInfo.ArgumentList.Add("--timeout");
            startInfo.ArgumentList.Add(((int)OpenClawTaskTimeout.TotalSeconds).ToString());

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException(_loc["OpenClawTask_CannotStartProcess"]);
            }

            _runningProcesses.TryAdd(taskId, process);

            // 必须同时开始读取 stdout 和 stderr，否则管道缓冲区满会导致死锁
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            // 整体超时：如果进程在 30 分钟内未退出则强制终止
            using var timeoutCts = new CancellationTokenSource(OpenClawTaskTimeout);
            var processExitTask = process.WaitForExitAsync(timeoutCts.Token);
            await processExitTask;

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                _logger.LogWarning("OpenClaw stderr: {Stderr}", stderr);
            }

            if (process.ExitCode != 0)
            {
                task.Status = "failed";
                task.ErrorMessage = $"Exit code {process.ExitCode}: {stderr}";
                task.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                return;
            }

            // Parse JSON output
            var reportContent = ParseOpenClawOutput(stdout);
            var reportFileName = $"{task.TaskId}_{DateTime.UtcNow:yyyyMMddHHmmss}.md";
            var reportPath = Path.Combine(_reportsDir, reportFileName);
            await File.WriteAllTextAsync(reportPath, reportContent);

            task.Status = "completed";
            task.ReportPath = reportPath;
            task.Result = reportContent[..Math.Min(reportContent.Length, 2000)];
            task.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("OpenClaw task {TaskId} timed out after {Timeout}m", task.TaskId, OpenClawTaskTimeout.TotalMinutes);
            task.Status = "failed";
            task.ErrorMessage = string.Format(_loc["OpenClawTask_Timeout"], OpenClawTaskTimeout.TotalMinutes);
            task.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenClaw task {TaskId} failed", task.TaskId);
            task.Status = "failed";
            task.ErrorMessage = ex.Message;
            task.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        finally
        {
            _runningProcesses.TryRemove(taskId, out _);
        }
    }

    public async Task<bool> CancelTaskAsync(int id)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var task = await db.OpenClawTasks.FindAsync(id);
        if (task == null || task.Status != "running") return false;

        // 尝试终止进程
        if (_runningProcesses.TryRemove(id, out var process))
        {
            try
            {
                process.Kill(true);
                _logger.LogInformation("OpenClaw task {TaskId} process killed", task.TaskId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OpenClaw task {TaskId} process kill failed", task.TaskId);
            }
        }

        task.Status = "failed";
        task.ErrorMessage = _loc["OpenClawTask_UserCancelled"];
        task.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // 同步更新 TaskManager 任务状态
        if (_openClawToTaskManagerMap.TryRemove(id, out var tmTaskId) && _taskManager != null)
        {
            await _taskManager.UpdateStatus(tmTaskId, Baihua.Core.RunnerTaskStatus.Cancelled, _loc["OpenClawTask_UserCancelled"]);
        }

        return true;
    }

    private string ParseOpenClawOutput(string jsonOutput)
    {
        try
        {
            var doc = JsonDocument.Parse(jsonOutput);
            var root = doc.RootElement;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(_loc["OpenClawTask_ReportHeader"]);
            sb.AppendLine();

            // OpenClaw may wrap payloads/meta under "result" or at root level
            var result = root;
            if (root.TryGetProperty("result", out var resultProp))
            {
                result = resultProp;
            }

            // Meta info
            if (result.TryGetProperty("meta", out var meta))
            {
                if (meta.TryGetProperty("durationMs", out var durationMs))
                    sb.AppendLine(string.Format(_loc["OpenClawTask_Duration"], durationMs.GetDouble()));
                if (meta.TryGetProperty("agentMeta", out var agentMeta) &&
                    agentMeta.TryGetProperty("model", out var modelProp))
                    sb.AppendLine(string.Format(_loc["OpenClawTask_Model"], modelProp.GetString()));
                sb.AppendLine();
            }

            // Payloads
            if (result.TryGetProperty("payloads", out var payloads))
            {
                foreach (var payload in payloads.EnumerateArray())
                {
                    if (payload.TryGetProperty("type", out var typeProp))
                    {
                        var type = typeProp.GetString();
                        if (type == "text" && payload.TryGetProperty("text", out var textProp))
                        {
                            sb.AppendLine(textProp.GetString());
                            sb.AppendLine();
                        }
                        else if (type == "json" && payload.TryGetProperty("json", out var jsonProp))
                        {
                            sb.AppendLine("```json");
                            sb.AppendLine(jsonProp.GetRawText());
                            sb.AppendLine("```");
                            sb.AppendLine();
                        }
                        else if (type == "artifact" && payload.TryGetProperty("title", out var titleProp))
                        {
                            sb.AppendLine($"### {titleProp.GetString()}");
                            if (payload.TryGetProperty("content", out var contentProp))
                            {
                                sb.AppendLine("```");
                                sb.AppendLine(contentProp.GetString());
                                sb.AppendLine("```");
                            }
                            sb.AppendLine();
                        }
                    }
                    else if (payload.TryGetProperty("text", out var textProp))
                    {
                        // Some payloads don't have "type" field
                        sb.AppendLine(textProp.GetString());
                        sb.AppendLine();
                    }
                }
            }

            // Fallback: if no structured payloads, include raw JSON
            if (sb.Length < 50)
            {
                sb.AppendLine("```json");
                sb.AppendLine(root.GetRawText());
                sb.AppendLine("```");
            }

            return sb.ToString();
        }
        catch (JsonException)
        {
            // Not valid JSON, return raw output wrapped in markdown
            return string.Format(_loc["OpenClawTask_FallbackOutput"], jsonOutput);
        }
    }

    public async Task<OpenClawTaskDto?> GetTaskAsync(int id)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var task = await db.OpenClawTasks.FindAsync(id);
        return task == null ? null : ToDto(task);
    }

    public async Task<List<OpenClawTaskDto>> GetTasksAsync(int limit = 100)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var tasks = await db.OpenClawTasks
            .OrderByDescending(t => t.CreatedAt)
            .Take(limit)
            .ToListAsync();
        return tasks.Select(ToDto).ToList();
    }

    public async Task<bool> DeleteTaskAsync(int id)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var task = await db.OpenClawTasks.FindAsync(id);
        if (task == null) return false;

        if (!string.IsNullOrEmpty(task.ReportPath) && File.Exists(task.ReportPath))
        {
            File.Delete(task.ReportPath);
        }

        db.OpenClawTasks.Remove(task);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<string?> GetReportContentAsync(int id)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var task = await db.OpenClawTasks.FindAsync(id);
        if (task?.ReportPath == null || !File.Exists(task.ReportPath))
            return null;

        return await File.ReadAllTextAsync(task.ReportPath);
    }

    private static OpenClawTaskDto ToDto(OpenClawTask task) => new()
    {
        Id = task.Id,
        TaskId = task.TaskId,
        Prompt = task.Prompt,
        Status = task.Status,
        ReportPath = task.ReportPath,
        Result = task.Result,
        ErrorMessage = task.ErrorMessage,
        CreatedAt = task.CreatedAt,
        CompletedAt = task.CompletedAt
    };



}
