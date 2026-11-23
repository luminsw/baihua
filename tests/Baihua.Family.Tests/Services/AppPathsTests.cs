using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

public class AppPathsTests
{
    private static readonly string[] _knownEnvVars = new[] { "BAIHUA_DATA_DIR", "BAIHUA_HOME" };

    private static void ClearKnown()
    {
        foreach (var v in _knownEnvVars) Environment.SetEnvironmentVariable(v, null);
    }

    [Fact]
    public void GetConfigDirectory_WithBaihuaDataDir_ReturnsEnvDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        ClearKnown();
        Environment.SetEnvironmentVariable("BAIHUA_DATA_DIR", tempDir);
        try
        {
            var result = AppPaths.GetConfigDirectory();
            Assert.Equal(tempDir, result);
            Assert.True(Directory.Exists(result));
        }
        finally
        {
            ClearKnown();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir);
        }
    }

    [Fact]
    public void GetConfigDirectory_WithoutEnvVar_ReturnsBaseDirectory()
    {
        ClearKnown();
        var result = AppPaths.GetConfigDirectory();
        Assert.Equal(AppDomain.CurrentDomain.BaseDirectory, result);
    }

    [Fact]
    public void GetConfigDirectory_WithEmptyEnvVar_FallsBackToBase()
    {
        ClearKnown();
        Environment.SetEnvironmentVariable("BAIHUA_DATA_DIR", "  ");
        var result = AppPaths.GetConfigDirectory();
        Assert.Equal(AppDomain.CurrentDomain.BaseDirectory, result);
        ClearKnown();
    }
}
