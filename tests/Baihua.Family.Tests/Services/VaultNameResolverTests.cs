using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

public class VaultNameResolverTests
{
    private readonly IVaultNameResolver _resolver = new VaultNameResolver();

    [Fact]
    public void ToSafeDirectoryName_NullOrEmpty_ReturnsDefault()
    {
        Assert.Equal("未命名", _resolver.ToSafeDirectoryName(null));
        Assert.Equal("未命名", _resolver.ToSafeDirectoryName(""));
        Assert.Equal("未命名", _resolver.ToSafeDirectoryName("   "));
    }

    [Fact]
    public void ToSafeDirectoryName_ValidName_ReturnsSame()
    {
        Assert.Equal("Python编程", _resolver.ToSafeDirectoryName("Python编程"));
        Assert.Equal("中医诊断学", _resolver.ToSafeDirectoryName("中医诊断学"));
        Assert.Equal("MyVault", _resolver.ToSafeDirectoryName("MyVault"));
    }

    [Fact]
    public void ToSafeDirectoryName_RemovesIllegalCharacters()
    {
        Assert.Equal("test_name", _resolver.ToSafeDirectoryName("test:name"));
        Assert.Equal("test_name", _resolver.ToSafeDirectoryName("test/name"));
        Assert.Equal("test_name", _resolver.ToSafeDirectoryName("test\\name"));
        Assert.Equal("test_name", _resolver.ToSafeDirectoryName("test*name"));
        Assert.Equal("test_name", _resolver.ToSafeDirectoryName("test?name"));
        Assert.Equal("test_name", _resolver.ToSafeDirectoryName("test<name"));
        Assert.Equal("test_name", _resolver.ToSafeDirectoryName("test>name"));
        Assert.Equal("test_name", _resolver.ToSafeDirectoryName("test|name"));
        Assert.Equal("test_name", _resolver.ToSafeDirectoryName("test\"name"));
    }

    [Fact]
    public void ToSafeDirectoryName_TrimsWhitespaceAndDots()
    {
        Assert.Equal("test", _resolver.ToSafeDirectoryName("  test  "));
        Assert.Equal("test", _resolver.ToSafeDirectoryName(".test."));
        Assert.Equal("test", _resolver.ToSafeDirectoryName("..test.."));
    }

    [Fact]
    public void ToSafeDirectoryName_LongName_TruncatesTo100Chars()
    {
        var longName = new string('a', 150);
        var result = _resolver.ToSafeDirectoryName(longName);
        Assert.Equal(100, result.Length);
    }

    [Fact]
    public void InferNameFromPath_NullOrEmpty_ReturnsDefault()
    {
        Assert.Equal("未命名", _resolver.InferNameFromPath(null));
        Assert.Equal("未命名", _resolver.InferNameFromPath(""));
        Assert.Equal("未命名", _resolver.InferNameFromPath("   "));
    }

    [Fact]
    public void InferNameFromPath_ExtractsFileName()
    {
        Assert.Equal("Python编程", _resolver.InferNameFromPath("/home/user/vaults/Python编程"));
        Assert.Equal("Python编程", _resolver.InferNameFromPath("/home/user/vaults/Python编程/"));
        Assert.Equal("中医诊断学", _resolver.InferNameFromPath("mobile/医疗健康/中医诊断学"));
        Assert.Equal("中医诊断学", _resolver.InferNameFromPath("mobile/医疗健康/中医诊断学/"));
    }

    [Fact]
    public void InferNameFromPath_HandlesPlatformSpecificSeparators()
    {
        Assert.Equal("vault", _resolver.InferNameFromPath(Path.Combine("C:", "Users", "user", "vaults", "vault")));
        Assert.Equal("vault", _resolver.InferNameFromPath("C:/Users/user/vaults/vault"));
    }

    [Fact]
    public void GetUniqueDirectoryPath_CreatesNewPath_WhenNotExists()
    {
        using var tempDir = new TempDirectory();
        var result = _resolver.GetUniqueDirectoryPath(tempDir.Path, "test");
        Assert.Equal(Path.Combine(tempDir.Path, "test"), result);
    }

    [Fact]
    public void GetUniqueDirectoryPath_AppendsNumber_WhenExists()
    {
        using var tempDir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "test"));
        
        var result = _resolver.GetUniqueDirectoryPath(tempDir.Path, "test");
        Assert.Equal(Path.Combine(tempDir.Path, "test_2"), result);
    }

    [Fact]
    public void GetUniqueDirectoryPath_AppendsNextNumber_WhenMultipleExist()
    {
        using var tempDir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "test"));
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "test_2"));
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "test_3"));
        
        var result = _resolver.GetUniqueDirectoryPath(tempDir.Path, "test");
        Assert.Equal(Path.Combine(tempDir.Path, "test_4"), result);
    }

    private class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
