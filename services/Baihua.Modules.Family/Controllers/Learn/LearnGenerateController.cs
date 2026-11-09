using Microsoft.AspNetCore.Mvc;
using Baihua.Contracts.Learn;
using Baihua.Modules.Family.Services;

namespace Baihua.Modules.Family.Controllers;

/// <summary>
/// 题库生成 / 校验 / 导入 API（WebUI 生成页专用）。
///
/// 仅挂 <c>api/learn</c>（管理面板访问域），**不挂 mg/learn** —— 生成与写入不属于移动端同步能力。
/// 预览不落库；导入统一走 append 模式（除非显式 overwrite），防止 AI 生成题号撞车覆盖人工题库。
/// </summary>
[ApiController]
[Route("api/learn")]
public class LearnGenerateController : ControllerBase
{
    private readonly QuizBankService _quizBanks;
    private readonly QuizBankGeneratorService _generator;

    public LearnGenerateController(QuizBankService quizBanks, QuizBankGeneratorService generator)
    {
        _quizBanks = quizBanks;
        _generator = generator;
    }

    /// <summary>AI 生成预览（不落库）：返回题库包 + 校验结果 + 提示词 + 原始响应</summary>
    [HttpPost("generate/preview")]
    public async Task<ActionResult<QuizGeneratePreviewDto>> Preview(
        [FromBody] QuizGenerateRequestDto request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Topic))
            return BadRequest(new { error = "生成主题不能为空" });
        if (!string.Equals(request.QuestionCountMode, "auto", StringComparison.OrdinalIgnoreCase)
            && request.QuestionCount is < 1 or > 30)
            return BadRequest(new { error = "题量须在 1-30 之间" });

        try
        {
            return Ok(await _generator.PreviewAsync(request, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
    }

    /// <summary>行业画像列表（生成页行业/题型下拉）：内置画像 + 数据库中已有但未登记的行业</summary>
    [HttpGet("industries")]
    public async Task<ActionResult<List<QuizIndustryProfileDto>>> Industries(CancellationToken ct = default)
    {
        var overviews = await _quizBanks.GetOverviewsAsync(ct);
        var existing = overviews
            .Select(b => (Code: b.IndustryCode, Name: b.IndustryName))
            .Distinct();
        return Ok(QuizIndustryProfileCatalog.MergeWithExisting(existing));
    }

    /// <summary>校验题库包（粘贴模式 / 删除题目后复检），不落库</summary>
    [HttpPost("validate")]
    public ActionResult<QuizBankValidationResultDto> Validate([FromBody] QuizBankPackage package)
    {
        var profile = package is null
            ? null
            : QuizIndustryProfileCatalog.Resolve(package.Industry?.Code, package.Industry?.Name);
        return Ok(QuizBankPackageValidator.Validate(package, profile));
    }

    /// <summary>导入题库包（默认 append 模式；Errors 非空时拒绝）</summary>
    [HttpPost("import")]
    public async Task<ActionResult<QuizBankImportResultDto>> Import(
        [FromBody] QuizBankImportRequestDto request, CancellationToken ct = default)
    {
        var profile = QuizIndustryProfileCatalog.Resolve(
            request.Package.Industry?.Code, request.Package.Industry?.Name);
        var validation = QuizBankPackageValidator.Validate(request.Package, profile);
        if (!validation.IsValid)
        {
            return BadRequest(new
            {
                error = "题库包校验未通过，未写入。请先修正 errors 中的问题。",
                validation,
            });
        }

        try
        {
            var result = await _quizBanks.ImportPackageAsync(
                request.Package,
                new QuizBankImportOptions { OverwriteExisting = request.OverwriteExisting },
                ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}