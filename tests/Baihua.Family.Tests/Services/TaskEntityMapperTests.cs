using Baihua.Core.Models;
using Baihua.Core.Services;
using System.Text.Json;
using Baihua.Core;
using Baihua.Data.Entities;
using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// TaskEntityMapper 单元测试
/// 验证 TaskEntity ↔ TaskInfo 的双向映射
/// </summary>
public class TaskEntityMapperTests
{
    [Fact]
    public void MapFromEntity_CompleteEntity_ReturnsMappedTaskInfo()
    {
        var now = DateTime.UtcNow;
        var entity = new TaskEntity
        {
            TaskId = "abc123",
            TaskType = "embedding",
            Status = "Running",
            Input = """{"key":"value"}""",
            Output = """{"result":"ok"}""",
            Error = null,
            Progress = 50,
            ProgressMessage = "处理中",
            CreatedAt = now,
            UpdatedAt = now
        };

        var task = TaskEntityMapper.MapFromEntity(entity);

        Assert.Equal("abc123", task.Id);
        Assert.Equal("embedding", task.Type);
        Assert.Equal(RunnerTaskStatus.Running, task.Status);
        Assert.NotNull(task.Parameters);
        Assert.Equal("value", task.Parameters["key"]);
        Assert.Equal(50, task.Progress.Percentage);
        Assert.Equal("处理中", task.Progress.Message);
        Assert.False(task.Result?.Success); // Status="Running", 不是 Success
        Assert.NotNull(task.Result?.Data);
    }

    [Fact]
    public void MapFromEntity_CompletedEntity_SetsResultData()
    {
        var entity = new TaskEntity
        {
            TaskId = "done",
            TaskType = "anki",
            Status = "Success",
            Output = """{"cards":3}""",
            Progress = 100,
            ProgressMessage = "完成",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var task = TaskEntityMapper.MapFromEntity(entity);

        Assert.NotNull(task.Result);
        Assert.True(task.Result.Success);
    }

    [Fact]
    public void MapFromEntity_FailedEntity_HasError()
    {
        var entity = new TaskEntity
        {
            TaskId = "fail",
            TaskType = "embedding",
            Status = "Failed",
            Error = "API 超时",
            Progress = 30,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var task = TaskEntityMapper.MapFromEntity(entity);

        Assert.Equal(RunnerTaskStatus.Failed, task.Status);
        Assert.NotNull(task.Result);
        Assert.False(task.Result.Success);
        Assert.Equal("API 超时", task.Result.Error);
    }

    [Fact]
    public void MapFromEntity_NullInput_NoParameters()
    {
        var entity = new TaskEntity
        {
            TaskId = "test", TaskType = "test", Status = "Pending",
            Input = null,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };

        var task = TaskEntityMapper.MapFromEntity(entity);
        Assert.Null(task.Parameters);
    }

    [Fact]
    public void MapFromEntity_NoOutput_NoResult()
    {
        var entity = new TaskEntity
        {
            TaskId = "test", TaskType = "test", Status = "Pending",
            Output = null, Error = null,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };

        var task = TaskEntityMapper.MapFromEntity(entity);
        Assert.Null(task.Result);
    }

    [Fact]
    public void MapFromEntity_CancelledStatus_MapsCorrectly()
    {
        var entity = new TaskEntity
        {
            TaskId = "cancelled", TaskType = "test", Status = "Cancelled",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };

        var task = TaskEntityMapper.MapFromEntity(entity);
        Assert.Equal(RunnerTaskStatus.Cancelled, task.Status);
    }

    [Fact]
    public void MapFromEntity_InvalidJsonInput_ReturnsNullParams()
    {
        var entity = new TaskEntity
        {
            TaskId = "bad-input", TaskType = "test", Status = "Pending",
            Input = "{invalid json}",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };

        var task = TaskEntityMapper.MapFromEntity(entity);
        // 反序列化失败时 Parameters 应该为 null
        Assert.Null(task.Parameters);
    }

    [Fact]
    public void MapFromEntity_InvalidJsonOutput_DoesNotThrow()
    {
        var entity = new TaskEntity
        {
            TaskId = "bad-output", TaskType = "test", Status = "Success",
            Output = "{bad json",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };

        var exception = Record.Exception(() => TaskEntityMapper.MapFromEntity(entity));
        Assert.Null(exception);
    }

    [Fact]
    public void ToEntity_ConvertsTaskInfoToEntity()
    {
        var task = new TaskInfo
        {
            Id = "task-1",
            Type = "embedding",
            Status = RunnerTaskStatus.Pending,
            Parameters = new Dictionary<string, string> { ["file"] = "doc.md" },
            Progress = new TaskProgress { Current = 0, Total = 1, Message = "创建", Percentage = 0 },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var entity = TaskEntityMapper.ToEntity(task);

        Assert.Equal("task-1", entity.TaskId);
        Assert.Equal("embedding", entity.TaskType);
        Assert.Equal("Pending", entity.Status);
        Assert.Contains("doc.md", entity.Input);
        Assert.Equal(0, entity.Progress);
        Assert.Equal("创建", entity.ProgressMessage);
    }

    [Fact]
    public void ToEntity_WithResult_SetsOutput()
    {
        var task = new TaskInfo
        {
            Id = "task-2",
            Type = "anki",
            Status = RunnerTaskStatus.Success,
            Result = new TaskResult { Success = true, Data = new { count = 5 } },
            Progress = new TaskProgress { Current = 1, Total = 1, Percentage = 100 },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var entity = TaskEntityMapper.ToEntity(task);

        Assert.Equal("Success", entity.Status);
        Assert.NotNull(entity.Output);
        Assert.Contains("count", entity.Output);
    }

    [Fact]
    public void ToEntity_NullParams_SetsNullInput()
    {
        var task = new TaskInfo
        {
            Id = "no-params", Type = "test", Status = RunnerTaskStatus.Pending,
            Progress = new TaskProgress(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };

        var entity = TaskEntityMapper.ToEntity(task);
        Assert.Null(entity.Input);
    }

    [Fact]
    public void ToEntity_ThenMapFromEntity_RoundTrip()
    {
        var original = new TaskInfo
        {
            Id = "roundtrip",
            Type = "test",
            Status = RunnerTaskStatus.Running,
            Parameters = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" },
            Progress = new TaskProgress { Current = 5, Total = 10, Message = "一半", Percentage = 50 },
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc)
        };

        var entity = TaskEntityMapper.ToEntity(original);
        var converted = TaskEntityMapper.MapFromEntity(entity);

        Assert.Equal(original.Id, converted.Id);
        Assert.Equal(original.Type, converted.Type);
        Assert.Equal(original.Status, converted.Status);
        Assert.Equal(original.Progress.Percentage, converted.Progress.Percentage);
        Assert.Equal(original.Progress.Message, converted.Progress.Message);
        Assert.NotNull(converted.Parameters);
        Assert.Equal("1", converted.Parameters["a"]);
        Assert.Equal("2", converted.Parameters["b"]);
    }
}
