using Baihua.Contracts.Tasks;
using Baihua.Modules.Family.Services;
using Microsoft.AspNetCore.Mvc;

namespace Baihua.Modules.Family.Controllers;

/// <summary>
/// 全局 AI 生成详细度设置（简洁/适中/详细；编程任务除外）
/// </summary>
[ApiController]
[Route("api/ai/detail-level")]
public class AiDetailController : ControllerBase
{
    private readonly AiDetailSettingsService _settings;

    public AiDetailController(AiDetailSettingsService settings)
    {
        _settings = settings;
    }

    [HttpGet]
    public ActionResult<object> Get()
    {
        var level = _settings.GetDetailLevel();
        return Ok(new
        {
            detailLevel = level,
            label = VaultGenDetail.Label(level)
        });
    }

    [HttpPost]
    public IActionResult Set([FromBody] SetDetailLevelRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DetailLevel))
            return BadRequest(new { error = "detailLevel 不能为空（concise/balanced/comprehensive）" });
        _settings.SetDetailLevel(request.DetailLevel);
        return Ok(new { detailLevel = _settings.GetDetailLevel() });
    }

    public class SetDetailLevelRequest
    {
        public string DetailLevel { get; set; } = "";
    }
}
