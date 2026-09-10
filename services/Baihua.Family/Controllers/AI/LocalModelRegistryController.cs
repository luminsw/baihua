using Baihua.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Baihua.Family.Controllers.AI;

/// <summary>
/// 本地大模型注册表只读 API（WebUI 展示用）。
/// 注册/注销由 Agent 经 MCP 工具操作，此 Controller 仅提供列表查询。
/// </summary>
[ApiController]
[Route("api/local-models")]
public class LocalModelRegistryController : ControllerBase
{
    private readonly LocalModelRegistryService _registry;

    public LocalModelRegistryController(LocalModelRegistryService registry)
    {
        _registry = registry;
    }

    [HttpGet("registry")]
    public async Task<IActionResult> GetRegistry()
    {
        var models = await _registry.ListAsync();
        return Ok(models);
    }
}