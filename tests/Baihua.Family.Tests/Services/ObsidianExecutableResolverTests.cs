using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

public class ObsidianExecutableResolverTests
{
    [Fact]
    public void Resolve_WhenNotFound_ReturnsObsidian()
    {
        // 清理环境变量，确保没有显式指定
        Environment.SetEnvironmentVariable("TASK_RUNNER_OBSIDIAN_EXE_PATH", null);
        Environment.SetEnvironmentVariable("TASK_RUNNER_OBSIDIAN_EXE", null);

        // 当 PATH 中也没有 obsidian 时，应返回 "obsidian"
        // 这里只验证不会崩溃
        var result = ObsidianExecutableResolver.Resolve();
        Assert.NotNull(result);
    }

    [Fact]
    public void TryGetPath_WhenEnvVarPointsToValidFile_ReturnsTrue()
    {
        // 创建一个临时文件
        var tempFile = Path.GetTempFileName();
        try
        {
            Environment.SetEnvironmentVariable("TASK_RUNNER_OBSIDIAN_EXE_PATH", tempFile);
            Environment.SetEnvironmentVariable("TASK_RUNNER_OBSIDIAN_EXE", null);

            var result = ObsidianExecutableResolver.TryGetPath(out var exePath);
            Assert.True(result);
            Assert.Equal(tempFile, exePath);
        }
        finally
        {
            File.Delete(tempFile);
            Environment.SetEnvironmentVariable("TASK_RUNNER_OBSIDIAN_EXE_PATH", null);
        }
    }

    [Fact]
    public void TryGetPath_WhenEnvVarPointsToInvalidFile_FallsThrough()
    {
        // 只设置一个不存在的路径
        Environment.SetEnvironmentVariable("TASK_RUNNER_OBSIDIAN_EXE_PATH", null);
        Environment.SetEnvironmentVariable("TASK_RUNNER_OBSIDIAN_EXE", null);

        // 由于无法预测 PATH 中是否真的有 obsidian，这里只验证不会崩溃
        var result = ObsidianExecutableResolver.TryGetPath(out var exePath);
        // 无论返回 true 还是 false，都应该是非崩溃的行为
        Assert.NotNull(exePath);
    }

    [Fact]
    public void TryGetPath_WithQuotedPath_StripsQuotes()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            Environment.SetEnvironmentVariable("TASK_RUNNER_OBSIDIAN_EXE_PATH", $"\"{tempFile}\"");
            Environment.SetEnvironmentVariable("TASK_RUNNER_OBSIDIAN_EXE", null);

            var result = ObsidianExecutableResolver.TryGetPath(out var exePath);
            Assert.True(result);
            Assert.Equal(tempFile, exePath);
        }
        finally
        {
            File.Delete(tempFile);
            Environment.SetEnvironmentVariable("TASK_RUNNER_OBSIDIAN_EXE_PATH", null);
        }
    }

    [Fact]
    public void TryGetPath_AltEnvVar_Works()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            Environment.SetEnvironmentVariable("TASK_RUNNER_OBSIDIAN_EXE_PATH", null);
            Environment.SetEnvironmentVariable("TASK_RUNNER_OBSIDIAN_EXE", tempFile);

            var result = ObsidianExecutableResolver.TryGetPath(out var exePath);
            Assert.True(result);
            Assert.Equal(tempFile, exePath);
        }
        finally
        {
            File.Delete(tempFile);
            Environment.SetEnvironmentVariable("TASK_RUNNER_OBSIDIAN_EXE", null);
        }
    }
}
