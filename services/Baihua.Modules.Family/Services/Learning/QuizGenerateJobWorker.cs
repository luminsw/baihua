namespace Baihua.Modules.Family.Services;

/// <summary>
/// 题库生成后台任务消费者：单读队列 + 串行执行，避免同时打爆 AI provider。
/// </summary>
public sealed class QuizGenerateJobWorker : BackgroundService
{
    private readonly QuizGenerateJobService _jobs;
    private readonly ILogger<QuizGenerateJobWorker> _logger;

    public QuizGenerateJobWorker(QuizGenerateJobService jobs, ILogger<QuizGenerateJobWorker> logger)
    {
        _jobs = jobs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var jobId = await _jobs.DequeueAsync(stoppingToken);
                await _jobs.ExecuteAsync(jobId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "题库生成后台任务循环异常");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }
}