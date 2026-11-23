namespace Baihua.Contracts.Metrics;

public class RequestMetric
{
    public DateTime Timestamp { get; set; }
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public long ElapsedMilliseconds { get; set; }
    public int StatusCode { get; set; }
}

public class PathFrequency
{
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string FullPath { get; set; } = "";
    public int Count { get; set; }
    public long AvgElapsedMs { get; set; }
    public long MaxElapsedMs { get; set; }
    public long MinElapsedMs { get; set; }
}

public class MetricsSummary
{
    public int TotalRequests { get; set; }
    public long AvgElapsedMs { get; set; }
    public long MaxElapsedMs { get; set; }
    public long MinElapsedMs { get; set; }
    public int ErrorCount { get; set; }
    public int UniquePaths { get; set; }
}
