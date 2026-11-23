namespace Baihua.Data.Entities;

/// <summary>
/// 任务记录（存储在 SQLite 中）
/// </summary>
public class TaskEntity
{
    public int Id { get; set; }
    public string TaskId { get; set; } = "";
    public string TaskType { get; set; } = ""; // Embedding, AnkiCard, etc.
    public string Status { get; set; } = "Pending"; // Pending, Running, Completed, Failed, Cancelled
    public string? Input { get; set; } // JSON 格式的输入参数
    public string? Output { get; set; } // JSON 格式的输出结果
    public string? Error { get; set; } // 错误信息
    public int Progress { get; set; } // 0-100
    public string? ProgressMessage { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
