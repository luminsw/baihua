using Microsoft.AspNetCore.Mvc;
using Baihua.Modules.Family.Services;

namespace Baihua.Modules.Family.Controllers;

/// <summary>
/// 多行业选择题题库学习 API（/learn 页后端）。
///
/// 判定零 AI：作答判定＝比对选项 key（QuizBankService 纯确定性路径）。
/// 「AI 分析」由 Web 端另调 api/assistant/chat（可选、可能产生费用），不经本控制器。
/// 行业无关：本控制器不含任何行业语义。
/// </summary>
[ApiController]
[Route("api/learn")]
public class LearnBankController : ControllerBase
{
    private readonly QuizBankService _quizBankService;
    private readonly ILogger<LearnBankController> _logger;

    public LearnBankController(QuizBankService quizBankService, ILogger<LearnBankController> logger)
    {
        _quizBankService = quizBankService;
        _logger = logger;
    }

    /// <summary>题库概览列表（行业→题库两级）</summary>
    [HttpGet("banks")]
    public async Task<ActionResult<List<Contracts.Learn.QuizBankOverviewDto>>> GetBanks(CancellationToken ct = default)
    {
        return Ok(await _quizBankService.GetOverviewsAsync(ct));
    }

    /// <summary>随机抽题（作答前不泄露正确项）</summary>
    [HttpGet("banks/{bankCode}/questions")]
    public async Task<ActionResult<List<Contracts.Learn.QuizQuestionViewDto>>> DrawQuestions(
        string bankCode, [FromQuery] int count = 10, [FromQuery] int excludeAnswered = 0, CancellationToken ct = default)
    {
        if (count is < 1 or > 50)
            return BadRequest(new { error = "抽题数量须在 1-50 之间" });
        try
        {
            return Ok(await _quizBankService.DrawQuestionsAsync(bankCode, count, null, ct));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>作答提交（判定零 AI：比对所选 key；仅家庭成员记 QuizAttempts）</summary>
    [HttpPost("banks/{bankCode}/answer")]
    public async Task<ActionResult<Contracts.Learn.QuizAnswerResultDto>> SubmitAnswer(
        string bankCode, [FromBody] Contracts.Learn.QuizAnswerSubmitDto submit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(submit.QuestionCode) || string.IsNullOrWhiteSpace(submit.SelectedKey))
            return BadRequest(new { error = "缺少题目代码或所选选项" });
        try
        {
            return Ok(await _quizBankService.SubmitAnswerAsync(bankCode, submit, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// 导出题库包文件（百花→花阁/花记的交换格式，QuizBankPackage 契约）。
    /// 落文件下载：application/json 附件（camelCase——仓库 JSON 规范）。
    /// </summary>
    [HttpGet("banks/{bankCode}/export")]
    public async Task<IActionResult> ExportPackage(string bankCode, CancellationToken ct = default)
    {
        try
        {
            var package = await _quizBankService.ExportPackageAsync(
                await ResolveIndustryCodeAsync(bankCode, ct), bankCode, ct);
            var fileName = $"quizbank-{bankCode}-{package.Bank.Version}.json";
            var jsonOptions = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            };
            return File(
                System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(package, jsonOptions)),
                "application/json",
                fileName);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>由 bankCode 反查其行业 code（导出端点需要两级定位）</summary>
    private async Task<string> ResolveIndustryCodeAsync(string bankCode, CancellationToken ct)
    {
        var overviews = await _quizBankService.GetOverviewsAsync(ct);
        var hit = overviews.FirstOrDefault(b => b.BankCode == bankCode)
            ?? throw new InvalidOperationException($"题库不存在：{bankCode}");
        return hit.IndustryCode;
    }
}