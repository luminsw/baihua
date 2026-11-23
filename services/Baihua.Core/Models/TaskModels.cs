namespace Baihua.Core;

public enum RunnerTaskStatus { Pending, Running, Success, Failed, Timeout, Cancelled }

public class TaskInfo
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public RunnerTaskStatus Status { get; set; }
    public TaskProgress Progress { get; set; } = new();
    public TaskResult? Result { get; set; }
    public Dictionary<string, string>? Parameters { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class TaskProgress
{
    public int Current { get; set; }
    public int Total { get; set; }
    public double Percentage { get; set; }
    public string Message { get; set; } = "";
}

public class TaskResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public object? Data { get; set; }
}
