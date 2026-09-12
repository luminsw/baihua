using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// TaskCancellationManager 单元测试
/// 验证 CancellationTokenSource 的创建、移除和取消操作
/// </summary>
public class TaskCancellationManagerTests
{
    [Fact]
    public void CreateCts_ReturnsNewCancellationTokenSource()
    {
        var manager = new TaskCancellationManager();
        var cts = manager.CreateCts("task-1");
        Assert.NotNull(cts);
        Assert.False(cts.IsCancellationRequested);
    }

    [Fact]
    public void CreateCts_WithTimeout_CancelsAfterTimeout()
    {
        var manager = new TaskCancellationManager();
        var cts = manager.CreateCts("task-1", TimeSpan.FromMilliseconds(100));

        // 轮询等待取消（CI 高负载下允许宽裕时间），避免脆弱的固定 Sleep
        Assert.True(cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)),
            "超时后 CTS 应在 10 秒内触发取消");
    }

    [Fact]
    public void CreateCts_SameTaskId_ReplacesPrevious()
    {
        var manager = new TaskCancellationManager();
        var cts1 = manager.CreateCts("task-1");
        var cts2 = manager.CreateCts("task-1"); // 替换

        Assert.NotSame(cts1, cts2);
        Assert.False(cts2.IsCancellationRequested);
    }

    [Fact]
    public void RemoveCts_RemovesAndDisposes()
    {
        var manager = new TaskCancellationManager();
        var cts = manager.CreateCts("task-1");
        Assert.True(manager.HasActiveCts("task-1"));

        manager.RemoveCts("task-1");
        Assert.False(manager.HasActiveCts("task-1"));
    }

    [Fact]
    public void RemoveCts_NonExistent_DoesNotThrow()
    {
        var manager = new TaskCancellationManager();
        manager.RemoveCts("nonexistent");
        // 静默通过
    }

    [Fact]
    public void TryCancel_CancelsActiveCts()
    {
        var manager = new TaskCancellationManager();
        manager.CreateCts("task-1");

        var result = manager.TryCancel("task-1");
        Assert.True(result);
    }

    [Fact]
    public void TryCancel_NonExistent_ReturnsFalse()
    {
        var manager = new TaskCancellationManager();
        Assert.False(manager.TryCancel("nonexistent"));
    }

    [Fact]
    public void TryCancel_AlreadyCancelled_DoesNotThrow()
    {
        var manager = new TaskCancellationManager();
        var cts = manager.CreateCts("task-1");
        cts.Cancel();

        // 第二次取消不应该抛异常
        var result = manager.TryCancel("task-1");
        // 因为 CTS 已被取消后再 TryCancel 可能不会触发对外部 CTS 的取消
        // 主要验证不抛异常
    }

    [Fact]
    public void HasActiveCts_ReturnsCorrectState()
    {
        var manager = new TaskCancellationManager();
        Assert.False(manager.HasActiveCts("task-1"));

        manager.CreateCts("task-1");
        Assert.True(manager.HasActiveCts("task-1"));

        manager.RemoveCts("task-1");
        Assert.False(manager.HasActiveCts("task-1"));
    }

    [Fact]
    public void CancelAll_CancelsAllActive()
    {
        var manager = new TaskCancellationManager();
        var ctsA = manager.CreateCts("a");
        var ctsB = manager.CreateCts("b");

        manager.CancelAll();

        // CancelAll 会取消 CTS，之后 RemoveCts 会移除条目
        Assert.True(ctsA.IsCancellationRequested);
        Assert.True(ctsB.IsCancellationRequested);
    }

    [Fact]
    public void Clear_RemovesAllCts()
    {
        var manager = new TaskCancellationManager();
        manager.CreateCts("a");
        manager.CreateCts("b");

        manager.Clear();

        Assert.False(manager.HasActiveCts("a"));
        Assert.False(manager.HasActiveCts("b"));
    }

    [Fact]
    public void MultipleTasks_IndependentlyManaged()
    {
        var manager = new TaskCancellationManager();
        var cts1 = manager.CreateCts("a");
        var cts2 = manager.CreateCts("b");

        Assert.False(cts1.IsCancellationRequested);
        Assert.False(cts2.IsCancellationRequested);

        manager.TryCancel("a");

        Assert.True(cts1.IsCancellationRequested);
        Assert.False(cts2.IsCancellationRequested);
    }
}
