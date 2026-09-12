using Baihua.Contracts.Backup;
using Baihua.Modules.Family.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// DeviceBackupService 单元测试：
/// 上传（Base64 解码 / 大小限制）/ 列表 / 下载路径解析 / 删除 / 滚动保留。
/// 使用临时 BAIHUA_HOME 隔离，不触碰真实数据目录。
/// </summary>
public class DeviceBackupServiceTests : IDisposable
{
    private readonly string _tempHome;
    private readonly DeviceBackupService _service;

    public DeviceBackupServiceTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), $"bh_devbackup_test_{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("BAIHUA_HOME", _tempHome);
        Baihua.Contracts.BaihuaPaths.Reset();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DeviceBackup:RetainCount"] = "3",
                ["DeviceBackup:MaxBytes"] = "1024"
            })
            .Build();

        _service = new DeviceBackupService(config, NullLogger<DeviceBackupService>.Instance);
    }

    [Fact]
    public async Task SaveAndList_Works()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var result = await _service.SaveAsync("dev-test", Convert.ToBase64String(payload));

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.BackupPath);
        Assert.Equal(payload.Length, result.FileSize);

        var list = _service.List("dev-test");
        Assert.Single(list);
        Assert.Equal(Path.GetFileName(result.BackupPath), list[0].Id);
        Assert.Equal(payload.Length, list[0].Size);
    }

    [Fact]
    public async Task InvalidBase64_Fails()
    {
        var result = await _service.SaveAsync("dev-test", "not-base64-!!!");
        Assert.False(result.Success);
        Assert.Contains("Base64", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OversizePayload_Fails()
    {
        var big = new byte[2048];
        var result = await _service.SaveAsync("dev-test", Convert.ToBase64String(big));
        Assert.False(result.Success);
        Assert.Contains("过大", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Retention_KeepsNewestOnly()
    {
        for (var i = 0; i < 5; i++)
        {
            var result = await _service.SaveAsync("dev-test", Convert.ToBase64String(new byte[] { (byte)i }));
            Assert.True(result.Success, $"第 {i} 次上传失败: {result.Error}");
        }

        var list = _service.List("dev-test");
        Assert.Equal(3, list.Count); // RetainCount=3
    }

    [Fact]
    public async Task DeviceIsolation_PerDevice()
    {
        await _service.SaveAsync("dev-a", Convert.ToBase64String(new byte[] { 1 }));
        await _service.SaveAsync("dev-b", Convert.ToBase64String(new byte[] { 2 }));

        var listA = _service.List("dev-a");
        var listB = _service.List("dev-b");
        Assert.Single(listA);
        Assert.Single(listB);
    }

    [Fact]
    public async Task ResolveFilePath_RejectsTraversal()
    {
        await _service.SaveAsync("dev-test", Convert.ToBase64String(new byte[] { 1 }));

        Assert.Null(_service.ResolveFilePath("dev-test", "../evil.zip"));
        Assert.Null(_service.ResolveFilePath("dev-test", "other.zip"));
        Assert.Null(_service.ResolveFilePath("dev-test", "huaji_backup_20260101_000000_000.txt"));

        var list = _service.List("dev-test");
        Assert.NotNull(_service.ResolveFilePath("dev-test", list[0].Id));
    }

    [Fact]
    public async Task Delete_RemovesFile()
    {
        await _service.SaveAsync("dev-test", Convert.ToBase64String(new byte[] { 1 }));
        var list = _service.List("dev-test");
        var id = list[0].Id;

        Assert.True(_service.Delete("dev-test", id));
        Assert.Empty(_service.List("dev-test"));
        Assert.False(_service.Delete("dev-test", id)); // 已删除
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempHome))
            {
                Directory.Delete(_tempHome, true);
            }
        }
        catch
        {
            // 清理失败不影响测试结果
        }

        Environment.SetEnvironmentVariable("BAIHUA_HOME", null);
        Baihua.Contracts.BaihuaPaths.Reset();
    }
}
