namespace Baihua.Contracts.Tasks;

public class TasksResponse
{
    public List<TaskInfo> Tasks { get; set; } = new();
}

public class TaskInfo
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public int Status { get; set; }

    public string StatusText => Status switch
    {
        0 => "Pending",
        1 => "Running",
        2 => "Success",
        3 => "Failed",
        4 => "Timeout",
        5 => "Cancelled",
        _ => "Unknown"
    };

    public TaskProgress Progress { get; set; } = new();
    public TaskResult? Result { get; set; }
    public Dictionary<string, string>? Parameters { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class TaskProgress
{
    public int Current { get; set; }
    public int Total { get; set; }
    public string Message { get; set; } = "";
    public double Percentage { get; set; }
}

public class TaskResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public object? Data { get; set; }
}

public class AiTaskResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? TaskId { get; set; }
}

public class VaultGenerationRequest
{
    public string Industry { get; set; } = string.Empty;
    public string Keyword { get; set; } = string.Empty;
    public string? Model { get; set; }
    public int NoteCount { get; set; } = 30;

    /// <summary>详细度档位：concise 简洁突出重点 / balanced 适中（默认） / comprehensive 详细全面</summary>
    public string? DetailLevel { get; set; }

    public bool GenerateCards { get; set; }
}

/// <summary>知识库生成详细度档位（同时控制篇数范围与每篇篇幅，由 AI 在范围内按主题自主决定）</summary>
public static class VaultGenDetail
{
    public const string Concise = "concise";
    public const string Balanced = "balanced";
    public const string Comprehensive = "comprehensive";

    public static string Normalize(string? level) =>
        level?.ToLowerInvariant() switch
        {
            Concise => Concise,
            Comprehensive => Comprehensive,
            _ => Balanced
        };

    public static string Label(string? level) =>
        Normalize(level) switch
        {
            Concise => "简洁突出重点",
            Comprehensive => "详细全面",
            _ => "适中"
        };

    /// <summary>(篇数范围提示, 每篇篇幅提示, 进度估算上限)</summary>
    public static (string CountHint, string LengthHint, int MaxNotes) Describe(string? level) =>
        Normalize(level) switch
        {
            Concise => ("6-10 篇", "每篇 200-400 字，只保留最核心的知识点，删掉可有可无的展开", 10),
            Comprehensive => ("28-45 篇", "每篇 800-1500 字，覆盖知识点及其细节、示例、常见误区", 45),
            _ => ("12-22 篇", "每篇 400-800 字，覆盖核心知识点并适当展开", 22)
        };
}

public class VaultGenerationResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? TaskId { get; set; }
}

public class AiTaskRequest
{
    public string Query { get; set; } = string.Empty;
    public bool SaveToVault { get; set; } = true;
    public string? VaultId { get; set; }
    public string? VaultPath { get; set; }
    public string? Model { get; set; }
    public bool AutoSplit { get; set; }
    public string? SystemPrompt { get; set; }
    public string? Industry { get; set; }
}

public class RetryAiTaskRequest
{
    public int TimeoutMinutes { get; set; }
    public string? Model { get; set; }
}

public class TaskHistoryItem
{
    public string TaskId { get; set; } = string.Empty;
    public string TaskType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int Progress { get; set; }
    public string ProgressMessage { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class TaskHistoryResponse
{
    public bool Success { get; set; }
    public List<TaskHistoryItem> Tasks { get; set; } = new();
    public int Total { get; set; }
}

public class CleanupRequest
{
    public int OlderThanDays { get; set; }
}

public class CleanupResponse
{
    public bool Success { get; set; }
    public int DeletedCount { get; set; }
}

public class TaskStatsResponse
{
    public bool Success { get; set; }
    public int Total { get; set; }
    public int Pending { get; set; }
    public int Running { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
}
