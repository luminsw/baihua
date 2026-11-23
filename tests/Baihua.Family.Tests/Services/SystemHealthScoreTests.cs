using Xunit;
using Baihua.Contracts.Health;

namespace Baihua.Family.Tests.Services;

public class SystemHealthScoreTests
{
    [Fact]
    public void CalculateHealthScore_AllHealthy_Returns100()
    {
        var components = new List<ComponentStatusDto>
        {
            new() { Name = "git", Status = "healthy" },
            new() { Name = "ollama", Status = "healthy" },
            new() { Name = "python", Status = "healthy" }
        };

        var score = CalculateScore(components);
        Assert.Equal(100, score);
    }

    [Fact]
    public void CalculateHealthScore_AllWarning_Returns50()
    {
        var components = new List<ComponentStatusDto>
        {
            new() { Name = "git", Status = "warning" },
            new() { Name = "ollama", Status = "warning" }
        };

        var score = CalculateScore(components);
        Assert.Equal(50, score);
    }

    [Fact]
    public void CalculateHealthScore_Mixed_ReturnsCorrectWeightedScore()
    {
        var components = new List<ComponentStatusDto>
        {
            new() { Name = "git", Status = "healthy" },
            new() { Name = "ollama", Status = "warning" },
            new() { Name = "python", Status = "healthy" }
        };

        var score = CalculateScore(components);
        Assert.Equal((2 * 100 + 1 * 50) / 3, score);
    }

    [Fact]
    public void CalculateHealthScore_EmptyComponents_Returns0()
    {
        var components = new List<ComponentStatusDto>();

        var score = CalculateScore(components);
        Assert.Equal(0, score);
    }

    [Fact]
    public void CalculateOverallStatus_AllHealthy_ReturnsHealthy()
    {
        var components = new List<ComponentStatusDto>
        {
            new() { Name = "git", Status = "healthy" },
            new() { Name = "ollama", Status = "healthy" }
        };

        var status = CalculateOverallStatus(components);
        Assert.Equal("healthy", status);
    }

    [Fact]
    public void CalculateOverallStatus_WithCritical_ReturnsCritical()
    {
        var components = new List<ComponentStatusDto>
        {
            new() { Name = "git", Status = "healthy" },
            new() { Name = "ollama", Status = "critical" },
            new() { Name = "python", Status = "healthy" }
        };

        var status = CalculateOverallStatus(components);
        Assert.Equal("critical", status);
    }

    [Fact]
    public void CalculateOverallStatus_WithWarningNoCritical_ReturnsWarning()
    {
        var components = new List<ComponentStatusDto>
        {
            new() { Name = "git", Status = "healthy" },
            new() { Name = "ollama", Status = "warning" },
            new() { Name = "python", Status = "healthy" }
        };

        var status = CalculateOverallStatus(components);
        Assert.Equal("warning", status);
    }

    [Fact]
    public void CalculateOverallStatus_EmptyComponents_ReturnsHealthy()
    {
        var components = new List<ComponentStatusDto>();

        var status = CalculateOverallStatus(components);
        Assert.Equal("healthy", status);
    }

    private static int CalculateScore(List<ComponentStatusDto> components)
    {
        var total = components.Count;
        var healthy = components.Count(c => c.Status == "healthy");
        var warning = components.Count(c => c.Status == "warning");
        return total > 0 ? (healthy * 100 + warning * 50) / total : 0;
    }

    private static string CalculateOverallStatus(List<ComponentStatusDto> components)
    {
        var critical = components.Count(c => c.Status == "critical");
        var warning = components.Count(c => c.Status == "warning");
        return critical > 0 ? "critical" : (warning > 0 ? "warning" : "healthy");
    }
}