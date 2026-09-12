using Baihua.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Baihua.Contracts.Onboarding;
using Baihua.Core.Localization;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Modules.Family.Services;

namespace Baihua.Modules.Family.Controllers;

public partial class OnboardingController
{
    /// <summary>
    /// 获取初始化任务列表
    /// </summary>
    [HttpGet("tasks")]
    public async Task<ActionResult<InitTasksResponse>> GetTasks()
    {
        await EnsureInitTasksCreatedAsync();

        var progresses = await _dbContext.InitTaskProgresses.ToListAsync();
        var hasAuthorizedDevices = _deviceService.GetAuthorizedDevices().Any();
        var vaults = _vaultSettings.GetVaults();
        var hasComputerVault = vaults.Any(v => v.Name.Contains("计算机") || v.Name.Contains("电脑") || v.Name.Contains("技术"));
        var hasTcmVault = vaults.Any(v => v.Name.Contains("中医") || v.Name.Contains("脾胃") || v.Name.Contains("中药"));

        var tasks = new List<InitTaskDto>
        {
            new()
            {
                TaskId = "add-family-member",
                TaskType = InitTaskType.AddFamilyMember,
                Title = _loc["Onboarding_TitleAddFamilyMember"],
                Description = _loc["Onboarding_DescAddFamilyMember"],
                Icon = "👨‍👩‍👧‍👦",
                IsCompleted = (progresses.FirstOrDefault(p => p.TaskId == "add-family-member")?.IsCompleted ?? false)
                             || hasAuthorizedDevices
            },
            new()
            {
                TaskId = "create-computer-vault",
                TaskType = InitTaskType.CreateComputerVault,
                Title = _loc["Onboarding_TitleCreateComputerVault"],
                Description = _loc["Onboarding_DescCreateComputerVault"],
                Icon = "💻",
                IsCompleted = (progresses.FirstOrDefault(p => p.TaskId == "create-computer-vault")?.IsCompleted ?? false)
                             || hasComputerVault
            },
            new()
            {
                TaskId = "create-tcm-vault",
                TaskType = InitTaskType.CreateTcmVault,
                Title = _loc["Onboarding_TitleCreateTcmVault"],
                Description = _loc["Onboarding_DescCreateTcmVault"],
                Icon = "🌿",
                IsCompleted = (progresses.FirstOrDefault(p => p.TaskId == "create-tcm-vault")?.IsCompleted ?? false)
                             || hasTcmVault
            }
        };

        var completedCount = tasks.Count(t => t.IsCompleted);
        return Ok(new InitTasksResponse
        {
            Tasks = tasks,
            AllCompleted = completedCount == tasks.Count,
            CompletedCount = completedCount,
            TotalCount = tasks.Count
        });
    }

    /// <summary>
    /// 标记初始化任务完成
    /// </summary>
    [HttpPost("tasks/{taskId}/complete")]
    public async Task<IActionResult> CompleteTask(string taskId)
    {
        await EnsureInitTasksCreatedAsync();

        var progress = await _dbContext.InitTaskProgresses
            .FirstOrDefaultAsync(p => p.TaskId == taskId);

        if (progress == null)
            return NotFound(new { error = _loc["Onboarding_TaskNotFound"] });

        progress.IsCompleted = true;
        progress.IsSkipped = false;
        progress.CompletedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        return Ok(new { success = true });
    }

    /// <summary>
    /// 跳过初始化任务
    /// </summary>
    [HttpPost("tasks/{taskId}/skip")]
    public async Task<IActionResult> SkipTask(string taskId)
    {
        await EnsureInitTasksCreatedAsync();

        var progress = await _dbContext.InitTaskProgresses
            .FirstOrDefaultAsync(p => p.TaskId == taskId);

        if (progress == null)
            return NotFound(new { error = _loc["Onboarding_TaskNotFound"] });

        progress.IsSkipped = true;
        progress.CompletedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        return Ok(new { success = true });
    }


    private async Task EnsureInitTasksCreatedAsync()
    {
        var existing = await _dbContext.InitTaskProgresses.ToListAsync();
        var requiredTasks = new[]
        {
            ("add-family-member", "AddFamilyMember"),
            ("create-computer-vault", "CreateComputerVault"),
            ("create-tcm-vault", "CreateTcmVault")
        };

        foreach (var (taskId, taskType) in requiredTasks)
        {
            if (!existing.Any(e => e.TaskId == taskId))
            {
                _dbContext.InitTaskProgresses.Add(new InitTaskProgress
                {
                    TaskId = taskId,
                    TaskType = taskType,
                    IsCompleted = false,
                    IsSkipped = false
                });
            }
        }

        if (_dbContext.ChangeTracker.HasChanges())
        {
            await _dbContext.SaveChangesAsync();
        }
    }
}
