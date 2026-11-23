namespace Baihua.Contracts.Health;

public class SystemHealthReportDto
{
    public DateTime Timestamp { get; set; }
    public List<ComponentStatusDto> Components { get; set; } = new();
    public int HealthScore { get; set; } // 0-100
    public string Status { get; set; } = string.Empty; // healthy, warning, critical

    /// <summary>整轮并行检测的墙钟耗时（毫秒），与各分项 <see cref="ComponentStatusDto.CheckDurationMs"/> 之和大致无关。</summary>
    public long TotalWallClockMs { get; set; }
}

/// <summary>一键修复结果</summary>
public class HealthFixResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public List<HealthFixItemDto> Fixes { get; set; } = new();
    public SystemHealthReportDto? NewReport { get; set; }
}

public class HealthFixItemDto
{
    public string Component { get; set; } = "";
    public string Status { get; set; } = ""; // fixed, manual_required, skipped, failed
    public string Message { get; set; } = "";
}

