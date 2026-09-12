using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;
using Baihua.Modules.Family.Services;

namespace Baihua.Modules.Family.Controllers;

/// <summary>
/// OpenObserve 日志后端配置 API
/// </summary>
[ApiController]
[Route("api/log-sink")]
public class LogSinkController : ControllerBase
{
    private readonly LogSinkConfigService _configService;
    private readonly IStringLocalizer<SharedResources> _loc;

    public LogSinkController(LogSinkConfigService configService, IStringLocalizer<SharedResources> loc)
    {
        _configService = configService;
        _loc = loc;
    }

    /// <summary>获取当前 OpenObserve 配置</summary>
    [HttpGet]
    public ActionResult<OpenObserveConfig> GetConfig()
    {
        return Ok(_configService.GetConfig());
    }

    /// <summary>更新 OpenObserve 配置</summary>
    [HttpPut]
    public ActionResult UpdateConfig([FromBody] OpenObserveConfig config)
    {
        if (config == null)
            return BadRequest(new { error = _loc["LogSink_ConfigEmpty"] });

        _configService.UpdateConfig(config);
        return Ok(new { message = _loc["LogSink_ConfigUpdated"] });
    }

    /// <summary>获取 OpenObserve Web UI 地址</summary>
    [HttpGet("web-url")]
    public ActionResult GetWebUrl()
    {
        var url = _configService.GetWebUrl();
        return Ok(new { url });
    }
}
