using Baihua.Contracts.Budget;
using Baihua.Modules.Family.Services;
using Microsoft.AspNetCore.Mvc;

namespace Baihua.Modules.Family.Controllers.Budget;

/// <summary>
/// 家庭记账 API
/// </summary>
[ApiController]
[Route("api/budget")]
public class BudgetController : ControllerBase
{
    private readonly FamilyBudgetService _budget;
    private readonly ILogger<BudgetController> _logger;

    public BudgetController(FamilyBudgetService budget, ILogger<BudgetController> logger)
    {
        _budget = budget;
        _logger = logger;
    }

    /// <summary>记录列表（可按年月筛选）</summary>
    [HttpGet("transactions")]
    [ProducesResponseType(typeof(List<BudgetTransaction>), 200)]
    public async Task<IActionResult> GetTransactions([FromQuery] int? year = null, [FromQuery] int? month = null)
    {
        return Ok(await _budget.GetTransactionsAsync(year, month));
    }

    /// <summary>新增记录</summary>
    [HttpPost("transactions")]
    [ProducesResponseType(typeof(BudgetTransaction), 200)]
    public async Task<IActionResult> AddTransaction([FromBody] BudgetCreateRequest request)
    {
        try
        {
            var tx = await _budget.AddAsync(request);
            return Ok(tx);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "新增记账失败");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>删除记录</summary>
    [HttpDelete("transactions/{id:guid}")]
    public async Task<IActionResult> DeleteTransaction(Guid id)
    {
        var ok = await _budget.DeleteAsync(id);
        return ok ? Ok() : NotFound();
    }

    /// <summary>月度汇总（默认本月）</summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(BudgetSummary), 200)]
    public async Task<IActionResult> GetSummary([FromQuery] int? year = null, [FromQuery] int? month = null)
    {
        return Ok(await _budget.GetSummaryAsync(year, month));
    }
}
