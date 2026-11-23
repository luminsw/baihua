namespace Baihua.Data.Entities;

public class OpenClawTask
{
    public int Id { get; set; }
    public string TaskId { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Prompt { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string? ReportPath { get; set; }
    public string? Result { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
