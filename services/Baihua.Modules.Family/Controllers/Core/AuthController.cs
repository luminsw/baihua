using Baihua.Core;
using Baihua.Core.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;
using MobileContract.Pairing;

namespace Baihua.Modules.Family.Controllers;

/// <summary>
/// 移动端认证配置端点。
/// 为已授权设备提供轻量级密钥查询，无副作用（不创建设备记录、不修改数据库）。
/// </summary>
[ApiController]
[Route("mg/auth")]
public class AuthController : ControllerBase
{
    private readonly DeviceService _deviceService;
    private readonly RequestSignatureService _signatureService;
    private readonly IStringLocalizer<SharedResources> _loc;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        DeviceService deviceService,
        RequestSignatureService signatureService,
        IStringLocalizer<SharedResources> loc,
        ILogger<AuthController> logger)
    {
        _deviceService = deviceService;
        _signatureService = signatureService;
        _loc = loc;
        _logger = logger;
    }

    /// <summary>
    /// 获取认证配置（共享密钥）。
    /// 仅已授权设备可获取，未授权设备返回 401。
    /// </summary>
    [HttpPost("config")]
    public ActionResult<AuthConfigResponse> GetAuthConfig([FromBody] AuthConfigRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.DeviceId))
        {
            return BadRequest(new AuthConfigResponse
            {
                Success = false,
                Message = _loc["AuthConfig_DeviceIdRequired"]
            });
        }

        _logger.LogInformation("[AUTH-DIAG] AuthConfig request: DeviceId={DeviceId}", request.DeviceId);

        var authorizedDevice = _deviceService.GetAuthorizedDeviceById(request.DeviceId);
        if (authorizedDevice == null)
        {
            _logger.LogWarning("[AUTH-DIAG] AuthConfig REJECTED: device {DeviceId} not authorized", request.DeviceId);
            return Unauthorized(new AuthConfigResponse
            {
                Success = false,
                Message = _loc["AuthConfig_DeviceNotAuthorized"]
            });
        }

        _logger.LogInformation("[AUTH-DIAG] AuthConfig OK: device={DeviceId}", request.DeviceId);

        return Ok(new AuthConfigResponse
        {
            Success = true,
            SharedSecret = _signatureService.GetSharedSecret(),
            Message = _loc["AuthConfig_Success"]
        });
    }
}
