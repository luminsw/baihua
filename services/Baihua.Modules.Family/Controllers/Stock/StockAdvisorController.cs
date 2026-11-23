using Baihua.Contracts.Stock;
using Baihua.Modules.Family.Services;
using Microsoft.AspNetCore.Mvc;

namespace Baihua.Modules.Family.Controllers.Stock;

/// <summary>
/// 股票 AI 建议 API（仅供学习参考，不构成投资建议）
/// </summary>
[ApiController]
[Route("api/stock")]
public class StockAdvisorController : ControllerBase
{
    private readonly StockAdvisorService _stockAdvisor;
    private readonly ILogger<StockAdvisorController> _logger;

    public StockAdvisorController(StockAdvisorService stockAdvisor, ILogger<StockAdvisorController> logger)
    {
        _stockAdvisor = stockAdvisor;
        _logger = logger;
    }

    /// <summary>
    /// AI 推荐 10 只建议购买的股票（按建议度排名，支持策略/行业/周期过滤）
    /// </summary>
    [HttpGet("recommendations")]
    [ProducesResponseType(typeof(StockRecommendationResponse), 200)]
    public async Task<IActionResult> GetRecommendations(
        [FromQuery] string? providerId = null,
        [FromQuery] string? model = null,
        [FromQuery] string? strategy = null,
        [FromQuery] string? industry = null,
        [FromQuery] string? horizon = null,
        [FromQuery] string? prompt = null,
        [FromQuery] string? direction = null,
        [FromQuery] bool refresh = false,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _stockAdvisor.GetRecommendationsAsync(providerId, model, strategy, industry, horizon, prompt, direction, refresh, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "股票推荐分析失败");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// 候选池可用行业列表（供筛选下拉）
    /// </summary>
    [HttpGet("industries")]
    public IActionResult GetIndustries()
    {
        return Ok(StockAdvisorService.GetIndustries());
    }

    /// <summary>
    /// 评估已购股票是否卖出
    /// </summary>
    [HttpPost("evaluate")]
    [ProducesResponseType(typeof(StockEvaluationResponse), 200)]
    public async Task<IActionResult> EvaluateHolding(
        [FromBody] StockEvaluationRequest request,
        [FromQuery] bool refresh = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
            return BadRequest(new { error = "code 不能为空" });

        try
        {
            var result = await _stockAdvisor.EvaluateHoldingAsync(
                request.Code.Trim(), request.ProviderId, request.Model, refresh, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "持仓评估失败: {Code}", request.Code);
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
