using Baihua.Core.Models;
using Baihua.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Baihua.Contracts.Benchmark;
using Baihua.Core.Localization;
using Baihua.Family.Services;

namespace Baihua.Family.Controllers;

/// <summary>
/// 模型基准测试 API
/// </summary>
[ApiController]
[Route("api/benchmark")]
public class ModelBenchmarkController : ControllerBase
{
    private readonly ModelBenchmarkService _benchmarkService;
    private readonly BenchmarkRepository _benchmarkRepo;
    private readonly ILogger<ModelBenchmarkController> _logger;
    private readonly IStringLocalizer<SharedResources> _loc;

    public ModelBenchmarkController(
        ModelBenchmarkService benchmarkService,
        BenchmarkRepository benchmarkRepo,
        ILogger<ModelBenchmarkController> logger,
        IStringLocalizer<SharedResources> loc)
    {
        _benchmarkService = benchmarkService;
        _benchmarkRepo = benchmarkRepo;
        _logger = logger;
        _loc = loc;
    }

    /// <summary>
    /// 获取内置推荐模型列表
    /// </summary>
    [HttpGet("models")]
    public ActionResult<List<RecommendedBenchmarkModel>> GetRecommendedModels([FromQuery] string? category)
    {
        return Ok(BenchmarkPrompts.GetModelsByCategory(category ?? ""));
    }

    /// <summary>
    /// 获取测试提示词列表
    /// </summary>
    [HttpGet("prompts")]
    public ActionResult<List<BenchmarkPrompt>> GetPrompts([FromQuery] string? category)
    {
        return Ok(BenchmarkPrompts.GetPromptsByCategory(category ?? ""));
    }

    /// <summary>
    /// 开始运行基准测试（异步，立即返回）
    /// </summary>
    [HttpPost("run")]
    public IActionResult RunBenchmark([FromBody] RunBenchmarkRequest request)
    {
        // 在后台执行测试
        _ = Task.Run(async () =>
        {
            try
            {
                await _benchmarkService.RunBenchmarkAsync(request.Model, request.PromptIds, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "后台基准测试任务失败");
            }
        });

        return Accepted(new { message = _loc["Benchmark_Started"] });
    }

    /// <summary>
    /// 停止当前运行的基准测试
    /// </summary>
    [HttpPost("stop")]
    public IActionResult StopBenchmark()
    {
        _benchmarkService.StopBenchmark();
        return Ok(new { message = _loc["Benchmark_Stopped"] });
    }

    /// <summary>
    /// 获取当前测试状态
    /// </summary>
    [HttpGet("status")]
    public ActionResult<BenchmarkStatusDto> GetStatus()
    {
        return Ok(_benchmarkService.GetStatus());
    }

    /// <summary>
    /// 获取测试历史
    /// </summary>
    [HttpGet("history")]
    public ActionResult<List<BenchmarkSession>> GetHistory([FromQuery] string? category)
    {
        return Ok(_benchmarkRepo.GetHistory(category));
    }

    /// <summary>
    /// 获取排行榜
    /// </summary>
    [HttpGet("leaderboard")]
    public ActionResult<List<BenchmarkLeaderboardEntry>> GetLeaderboard([FromQuery] string? category)
    {
        return Ok(_benchmarkRepo.GetLeaderboard(category));
    }

    /// <summary>
    /// 删除某条历史记录
    /// </summary>
    [HttpDelete("history/{sessionId}")]
    public async Task<IActionResult> DeleteSession(string sessionId)
    {
        var ok = await _benchmarkRepo.DeleteSessionAsync(sessionId);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>
    /// 清空所有历史
    /// </summary>
    [HttpDelete("history")]
    public async Task<IActionResult> ClearHistory()
    {
        await _benchmarkRepo.ClearHistoryAsync();
        return NoContent();
    }
}
