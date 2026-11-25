namespace Baihua.Data.Entities;

public sealed class CodeAgentSession
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Prompt { get; set; } = "";
    public string? Language { get; set; }
    public string? ProviderId { get; set; }
    public string? Model { get; set; }
    public string ToolMode { get; set; } = "All";
    public bool IsPipeline { get; set; }
    public bool PlanPro { get; set; }
    public string? Output { get; set; }
    public string? Research { get; set; }
    public string? Code { get; set; }
    public string? Review { get; set; }
    public string? FileName { get; set; }
    public string? SessionStateJson { get; set; }
}
