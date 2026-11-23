using Baihua.Core.Models;
using Baihua.Core.Services;
using System.Diagnostics;
using Baihua.Modules.Family.Models;
using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

public class HealthCheckHelperTests
{
    #region IsLocalAiProvider

    [Theory]
    [InlineData("ollama")]
    [InlineData("Ollama")]
    [InlineData("OLLAMA")]
    [InlineData("Ollama Local")]
    [InlineData("My Ollama Server")]
    public void IsLocalAiProvider_Ollama_ReturnsTrue(string name)
    {
        var config = new AiProviderConfig { Name = name };
        Assert.True(HealthCheckHelper.IsLocalAiProvider(config));
    }

    [Theory]
    [InlineData("local")]
    [InlineData("Local")]
    [InlineData("LOCAL")]
    [InlineData("Local Model")]
    [InlineData("My Local AI")]
    public void IsLocalAiProvider_Local_ReturnsTrue(string name)
    {
        var config = new AiProviderConfig { Name = name };
        Assert.True(HealthCheckHelper.IsLocalAiProvider(config));
    }

    [Theory]
    [InlineData("lmstudio")]
    [InlineData("LMStudio")]
    [InlineData("LMSTUDIO")]
    [InlineData("LMStudio Local")]
    [InlineData("My LMStudio")]
    public void IsLocalAiProvider_LmStudio_ReturnsTrue(string name)
    {
        var config = new AiProviderConfig { Name = name };
        Assert.True(HealthCheckHelper.IsLocalAiProvider(config));
    }

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("Anthropic")]
    [InlineData("Azure")]
    [InlineData("DeepSeek")]
    [InlineData("Cloud AI")]
    public void IsLocalAiProvider_CloudProvider_ReturnsFalse(string name)
    {
        var config = new AiProviderConfig { Name = name };
        Assert.False(HealthCheckHelper.IsLocalAiProvider(config));
    }

    [Fact]
    public void IsLocalAiProvider_NullName_ReturnsFalse()
    {
        var config = new AiProviderConfig { Name = null! };
        Assert.False(HealthCheckHelper.IsLocalAiProvider(config));
    }

    [Fact]
    public void IsLocalAiProvider_EmptyName_ReturnsFalse()
    {
        var config = new AiProviderConfig { Name = "" };
        Assert.False(HealthCheckHelper.IsLocalAiProvider(config));
    }

    #endregion

    #region ExtractVersion

    [Theory]
    [InlineData("version 1.0", "1.0")]
    [InlineData("v2.3.4", "2.3.4")]
    [InlineData("Ollama 0.1.26", "0.1.26")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("Release: 10.20", "10.20")]
    public void ExtractVersion_ValidVersion_ReturnsVersion(string output, string expected)
    {
        var result = HealthCheckHelper.ExtractVersion(output);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("no version here")]
    [InlineData("")]
    [InlineData("just text")]
    public void ExtractVersion_NoVersion_ReturnsNull(string output)
    {
        var result = HealthCheckHelper.ExtractVersion(output);
        Assert.Null(result);
    }

    [Fact]
    public void ExtractVersion_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => HealthCheckHelper.ExtractVersion(null!));
    }

    [Theory]
    [InlineData("v1.0", "1.0")]
    [InlineData("version 2.0", "2.0")]
    public void ExtractVersion_TwoPartVersion_ReturnsVersion(string output, string expected)
    {
        var result = HealthCheckHelper.ExtractVersion(output);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ExtractVersion_MultipleVersions_ReturnsFirst()
    {
        var result = HealthCheckHelper.ExtractVersion("v1.0 and v2.0");
        Assert.Equal("1.0", result);
    }

    #endregion

    #region WithCheckDurationAsync

    [Fact]
    public async Task WithCheckDurationAsync_SetsDuration()
    {
        var status = new Baihua.Contracts.Health.ComponentStatusDto
        {
            Name = "Test",
            Status = "healthy"
        };

        var result = await HealthCheckHelper.WithCheckDurationAsync(() => Task.FromResult(status));

        Assert.True(result.CheckDurationMs >= 0);
    }

    [Fact]
    public async Task WithCheckDurationAsync_ReturnsOriginalStatus()
    {
        var status = new Baihua.Contracts.Health.ComponentStatusDto
        {
            Name = "Test",
            Status = "healthy",
            Message = "All good"
        };

        var result = await HealthCheckHelper.WithCheckDurationAsync(() => Task.FromResult(status));

        Assert.Equal("Test", result.Name);
        Assert.Equal("healthy", result.Status);
        Assert.Equal("All good", result.Message);
    }

    [Fact]
    public async Task WithCheckDurationAsync_PropagatesException()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => HealthCheckHelper.WithCheckDurationAsync(
                () => throw new InvalidOperationException("Test error")));
    }

    #endregion

    #region TryKill

    [Fact]
    public void TryKill_NullProcess_DoesNotThrow()
    {
        // Should not throw
        HealthCheckHelper.TryKill(null);
    }

    [Fact]
    public void TryKill_ExitedProcess_DoesNotThrow()
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "echo",
            Arguments = OperatingSystem.IsWindows() ? "/c echo test" : "test",
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        using var process = Process.Start(psi);
        process?.WaitForExit();

        HealthCheckHelper.TryKill(process);
    }

    [Fact]
    public void TryKill_RunningProcess_KillsProcess()
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "sleep",
            Arguments = OperatingSystem.IsWindows() ? "/c timeout /t 10 /nobreak >nul" : "10",
            UseShellExecute = false
        };
        using var process = Process.Start(psi);

        Assert.NotNull(process);
        Assert.False(process.HasExited);

        HealthCheckHelper.TryKill(process);

        // Give it a moment to exit
        Thread.Sleep(100);
        Assert.True(process.HasExited);
    }

    #endregion
}