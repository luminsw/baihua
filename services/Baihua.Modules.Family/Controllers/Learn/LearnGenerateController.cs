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
    private readonly QuizGenerateJobService _jobs;

    public LearnGenerateController(
        QuizBankService quizBanks, QuizBankGeneratorService generator, QuizGenerateJobService jobs)
    {
        _quizBanks = quizBanks;
        _generator = generator;
        _jobs = jobs;
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

    /// <summary>创建后台生成任务：立即返回 jobId，页面关闭后任务继续执行</summary>
    [HttpPost("generate/jobs")]
    public ActionResult<QuizGenerateJobCreatedDto> CreateJob([FromBody] QuizGenerateRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.Topic))
            return BadRequest(new { error = "生成主题不能为空" });
        if (!string.Equals(request.QuestionCountMode, "auto", StringComparison.OrdinalIgnoreCase)
            && request.QuestionCount is < 1 or > 30)
            return BadRequest(new { error = "题量须在 1-30 之间" });

        var jobId = _jobs.Enqueue(request);
        return Accepted(new QuizGenerateJobCreatedDto { JobId = jobId });
    }

    /// <summary>查询后台任务状态与结果</summary>
    [HttpGet("generate/jobs/{jobId}")]
    public ActionResult<QuizGenerateJobDto> GetJob(string jobId)
    {
        var job = _jobs.Get(jobId);
        return job is null ? NotFound(new { error = "任务不存在或已过期" }) : Ok(job);
    }

    /// <summary>最近的后台任务（页面重新进入时恢复/查看）</summary>
    [HttpGet("generate/jobs")]
    public ActionResult<List<QuizGenerateJobSummaryDto>> ListJobs([FromQuery] int limit = 10)
        => Ok(_jobs.ListRecent(limit));

    /// <summary>取消后台任务</summary>
    [HttpPost("generate/jobs/{jobId}/cancel")]
    public IActionResult CancelJob(string jobId)
        => _jobs.Cancel(jobId) ? Ok(new { ok = true }) : NotFound(new { error = "任务不存在或已结束" });

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

    /// <summary>
    /// 删除题库（连同条目 / 题目 / 答题记录）。
    /// <para>只挂 api/learn（管理面板域）——删除是管理动作，移动端同步域（mg/learn）不得暴露。</para>
    /// </summary>
    [HttpDelete("banks/{bankCode}")]
    public async Task<ActionResult<QuizBankDeleteResultDto>> DeleteBank(string bankCode, CancellationToken ct = default)
    {
        try
        {
            return Ok(await _quizBanks.DeleteBankAsync(bankCode, ct));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
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