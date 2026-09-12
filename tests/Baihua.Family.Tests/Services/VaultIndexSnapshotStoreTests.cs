using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Modules.Family.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// Vault 索引快照持久化测试：往返一致、缺文件/损坏文件安全降级、目录自动创建。
/// </summary>
public class VaultIndexSnapshotStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly ILogger _logger = NullLogger.Instance;

    public VaultIndexSnapshotStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bh-snapshot-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "snapshots.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static Dictionary<string, Dictionary<string, NoteFileStamp>> SampleSnapshots()
    {
        return new Dictionary<string, Dictionary<string, NoteFileStamp>>(StringComparer.OrdinalIgnoreCase)
        {
            ["vault-1"] = new Dictionary<string, NoteFileStamp>(StringComparer.OrdinalIgnoreCase)
            {
                ["笔记/甲.md"] = new NoteFileStamp(new DateTime(2026, 8, 13, 10, 0, 0, DateTimeKind.Utc), 1234),
                ["笔记/乙.md"] = new NoteFileStamp(new DateTime(2026, 8, 13, 11, 0, 0, DateTimeKind.Utc), 5678),
            }
        };
    }

    [Fact]
    public void Roundtrip_save_then_load_returns_equal_content()
    {
        VaultIndexSnapshotStore.Save(SampleSnapshots(), _logger, _path);

        var loaded = VaultIndexSnapshotStore.Load(_logger, _path);

        Assert.True(loaded.ContainsKey("vault-1"));
        var stamps = loaded["vault-1"];
        Assert.Equal(2, stamps.Count);
        Assert.Equal(1234, stamps["笔记/甲.md"].Length);
        Assert.Equal(new DateTime(2026, 8, 13, 11, 0, 0, DateTimeKind.Utc), stamps["笔记/乙.md"].LastWriteTimeUtc);
    }

    [Fact]
    public void Load_missing_file_returns_empty()
    {
        var loaded = VaultIndexSnapshotStore.Load(_logger, Path.Combine(_dir, "not-exists.json"));
        Assert.Empty(loaded);
    }

    [Fact]
    public void Load_corrupt_file_returns_empty_without_throw()
    {
        File.WriteAllText(_path, "{ this is not valid json !!!");

        var loaded = VaultIndexSnapshotStore.Load(_logger, _path);

        Assert.Empty(loaded);
    }

    [Fact]
    public void Save_to_nonexistent_directory_creates_it()
    {
        var deep = Path.Combine(_dir, "a", "b", "snapshots.json");

        VaultIndexSnapshotStore.Save(SampleSnapshots(), _logger, deep);

        Assert.True(File.Exists(deep));
    }
}
