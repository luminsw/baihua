using Baihua.Core.Models;
using Baihua.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Baihua.Contracts.Capability;
using Baihua.Modules.Family.Services;

namespace Baihua.Modules.Family.Controllers;

/// <summary>
/// 机器能力评估 API：返回当前硬件能支持的功能范围
/// </summary>
[ApiController]
[Route("api/capability")]
public class CapabilityController : ControllerBase
{
    private readonly CapabilityService _capabilityService;

    public CapabilityController(CapabilityService capabilityService)
    {
        _capabilityService = capabilityService;
    }

    /// <summary>
    /// 获取当前机器的能力信息（含可用/被限制的功能列表）
    /// </summary>
    [HttpGet]
    public ActionResult<CapabilityInfo> GetCapability()
    {
        return Ok(_capabilityService.GetCapabilityInfo());
    }

    /// <summary>
    /// 刷新能力评估（硬件变更后调用）
    /// </summary>
    [HttpPost("refresh")]
    public ActionResult<CapabilityInfo> RefreshCapability()
    {
        _capabilityService.RefreshCapability();
        return Ok(_capabilityService.GetCapabilityInfo());
    }

}
