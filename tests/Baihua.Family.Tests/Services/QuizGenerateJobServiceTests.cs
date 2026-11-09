using Baihua.Contracts.Learn;
using Baihua.Modules.Family.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// 后台生成任务表测试（注入 fake generator，不触发真实 AI）：
/// 状态流转、进度、失败、异常、取消、列表。
/// </summary>
public class QuizGenerateJobServiceTests
{
    private sealed class FakeGenerator : IQuizBankGenerator
    {
        public Func<QuizGenerateRequestDto, IProgress<QuizGenerateProgress>?, CancellationToken, Task<QuizGeneratePreviewDto>>
            Handler { get; set; } = (_, _, _) => Task.FromResult(new QuizGeneratePreviewDto { Success = true });

        public Task<QuizGeneratePreviewDto> PreviewAsync(
            QuizGenerateRequestDto request,
            IProgress<QuizGenerateProgress>? progress,
            CancellationToken ct = default)
            => Handler(request, progress, ct);
    }

    private static QuizGenerateJobService CreateService(FakeGenerator fake)
        => new(fake, NullLogger<QuizGenerateJobService>.Instance);

    private static QuizGenerateRequestDto Request(string bank = "测试题库", string topic = "测试主题")
        => new() { BankName = bank, Topic = topic };

    [Fact]
    public void Enqueue_ReturnsQueuedJobWithSummary()
    {
        var service = CreateService(new FakeGenerator());

        var jobId = service.Enqueue(Request());
        var job = service.Get(jobId);

        Assert.NotNull(job);
        Assert.Equal("queued", job.Status);
        Assert.Equal("测试题库", job.BankName);
        Assert.Equal("测试主题", job.Topic);
        Assert.Contains(service.ListRecent(), j => j.JobId == jobId);
    }

    [Fact]
    public async Task Execute_Success_SetsResultAndCompletes()
    {
        var fake = new FakeGenerator();
        var service = CreateService(fake);
        QuizGenerateJobService? captured = service;
        string? jobId = null;

        fake.Handler = (_, progress, _) =>
        {
            progress?.Report(new QuizGenerateProgress(1, 2, "已完成第 1/2 批"));
            var running = captured!.Get(jobId!);
            Assert.Equal("running", running.Status);
            Assert.Equal(50, running.ProgressPercent);

            return Task.FromResult(new QuizGeneratePreviewDto
            {
                Success = true,
                Package = new QuizBankPackage(),
                Validation = new QuizBankValidationResultDto(),
                BatchCount = 2,
            });
        };

        jobId = service.Enqueue(Request());
        await service.ExecuteAsync(jobId, CancellationToken.None);

        var job = service.Get(jobId);
        Assert.NotNull(job);
        Assert.Equal("succeeded", job.Status);
        Assert.Equal(100, job.ProgressPercent);
        Assert.NotNull(job.Result);
        Assert.Null(job.Error);
        Assert.NotNull(job.StartedAt);
        Assert.NotNull(job.CompletedAt);
    }

    [Fact]
    public async Task Execute_GeneratorFailure_SetsFailedWithError()
    {
        var fake = new FakeGenerator
        {
            Handler = (_, _, _) => Task.FromResult(new QuizGeneratePreviewDto
            {
                Success = false,
                Error = "AI 返回内容无法解析",
            }),
        };
        var service = CreateService(fake);

        var jobId = service.Enqueue(Request());
        await service.ExecuteAsync(jobId, CancellationToken.None);

        var job = service.Get(jobId);
        Assert.NotNull(job);
        Assert.Equal("failed", job.Status);
        Assert.Equal("AI 返回内容无法解析", job.Error);
        Assert.Null(job.Result);
    }

    [Fact]
    public async Task Execute_GeneratorThrows_SetsFailed()
    {
        var fake = new FakeGenerator
        {
            Handler = (_, _, _) => throw new InvalidOperationException("boom"),
        };
        var service = CreateService(fake);

        var jobId = service.Enqueue(Request());
        await service.ExecuteAsync(jobId, CancellationToken.None);

        var job = service.Get(jobId);
        Assert.Equal("failed", job.Status);
        Assert.Equal("boom", job.Error);
    }

    [Fact]
    public async Task Cancel_RunningJob_SetsCanceled()
    {
        var fake = new FakeGenerator
        {
            Handler = async (_, _, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return new QuizGeneratePreviewDto { Success = true };
            },
        };
        var service = CreateService(fake);

        var jobId = service.Enqueue(Request());
        var run = service.ExecuteAsync(jobId, CancellationToken.None);
        await Task.Delay(100);

        Assert.True(service.Cancel(jobId));
        await run;

        Assert.Equal("canceled", service.Get(jobId)!.Status);
        Assert.False(service.Cancel(jobId)); // 已结束不再可取消
    }

    [Fact]
    public async Task ListRecent_RespectsLimit()
    {
        var fake = new FakeGenerator();
        var service = CreateService(fake);

        var ids = Enumerable.Range(0, 3).Select(i => service.Enqueue(Request($"库{i}"))).ToList();

        var recent = service.ListRecent(2);
        Assert.Equal(2, recent.Count);
        Assert.Contains(ids[2], recent.Select(j => j.JobId));
    }

    [Fact]
    public async Task Execute_UnknownJob_DoesNothing()
    {
        var service = CreateService(new FakeGenerator());

        await service.ExecuteAsync("no-such-job", CancellationToken.None);

        Assert.Null(service.Get("no-such-job"));
    }

    // ============ 批次预置（避免单批任务全程 0%） ============

    [Fact]
    public void Enqueue_PresetsTotalBatches()
    {
        var service = CreateService(new FakeGenerator());

        Assert.Equal(1, service.Get(service.Enqueue(new() { Topic = "t", QuestionCount = 10 }))!.TotalBatches);
        Assert.Equal(1, service.Get(service.Enqueue(new() { Topic = "t", QuestionCount = 3 }))!.TotalBatches);
        Assert.Equal(3, service.Get(service.Enqueue(new() { Topic = "t", QuestionCount = 25 }))!.TotalBatches);
        Assert.Equal(3, service.Get(service.Enqueue(new() { Topic = "t", QuestionCount = 30 }))!.TotalBatches);
    }

    [Fact]
    public void Enqueue_AutoMode_UsesSingleBatch()
    {
        var service = CreateService(new FakeGenerator());

        var id = service.Enqueue(new() { Topic = "t", QuestionCount = 30, QuestionCountMode = "auto" });

        Assert.Equal(1, service.Get(id)!.TotalBatches);
        Assert.Contains("排队中", service.Get(id)!.Message);
    }
}