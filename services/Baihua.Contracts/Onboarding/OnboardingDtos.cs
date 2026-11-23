namespace Baihua.Contracts.Onboarding;

/// <summary>
/// Onboarding 状态响应
/// </summary>
public class OnboardingStatusDto
{
    public bool IsOnboardingCompleted { get; set; }
    public bool HasAiConfig { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool NeedsOnboarding => !IsOnboardingCompleted;
}

/// <summary>
/// 初始化任务类型
/// </summary>
public enum InitTaskType
{
    AddFamilyMember,
    CreateComputerVault,
    CreateTcmVault
}

/// <summary>
/// 初始化任务项
/// </summary>
public class InitTaskDto
{
    public string TaskId { get; set; } = "";
    public InitTaskType TaskType { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "";
    public bool IsCompleted { get; set; }
    public bool IsSkipped { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// 初始化任务列表响应
/// </summary>
public class InitTasksResponse
{
    public List<InitTaskDto> Tasks { get; set; } = new();
    public bool AllCompleted { get; set; }
    public int CompletedCount { get; set; }
    public int TotalCount { get; set; }
}

/// <summary>
/// 创建示例知识库请求
/// </summary>
public class CreateSampleVaultRequest
{
    public string VaultName { get; set; } = "";
    public string VaultType { get; set; } = ""; // "computer" | "tcm"
}

/// <summary>
/// 创建示例知识库响应
/// </summary>
public class CreateSampleVaultResponse
{
    public bool Success { get; set; }
    public string? VaultId { get; set; }
    public string? Message { get; set; }
    public List<string> CreatedNotes { get; set; } = new();
}
