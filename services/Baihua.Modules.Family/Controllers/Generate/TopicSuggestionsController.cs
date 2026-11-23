using Baihua.Contracts.Generate;
using Baihua.Modules.Family.Services;
using Microsoft.AspNetCore.Mvc;

namespace Baihua.Modules.Family.Controllers.Generate;

/// <summary>
/// AI 生成知识库页的预置主题推荐（每日刷新，可个性化）
/// </summary>
[ApiController]
[Route("api/generate/topic-suggestions")]
public class TopicSuggestionsController : ControllerBase
{
    private readonly TopicSuggestionService _service;
    private readonly ILogger<TopicSuggestionsController> _logger;

    public TopicSuggestionsController(TopicSuggestionService service, ILogger<TopicSuggestionsController> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <summary>
    /// 获取今日推荐主题（按天缓存；refresh=true 强制重新生成"换一批"）
    /// </summary>
    /// <param name="context">用户知识库构成摘要（可选，用于个性化）</param>
    /// <param name="refresh">true 强制重新生成（换一批）</param>
    /// <param name="ct">取消令牌</param>
    [HttpGet]
    [ProducesResponseType(typeof(TopicSuggestionResponse), 200)]
    public async Task<IActionResult> Get(
        [FromQuery] string? context = null,
        [FromQuery] bool refresh = false,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _service.GetSuggestionsAsync(context, refresh, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "主题推荐生成失败");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
