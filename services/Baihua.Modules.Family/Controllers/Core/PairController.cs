using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Core;
using Baihua.Core.Localization;
using Baihua.Core.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baihua.Data;
using Baihua.Modules.Family.Services;
using Baihua.Modules.Family.Services.Strategies;
using Baihua.Contracts.Vaults;
using Baihua.Contracts.Pairing;

namespace Baihua.Modules.Family.Controllers
{
    [ApiController]
    public class PairController : ControllerBase
    {
        private readonly DeviceService _deviceService;
        private readonly ILogger<PairController> _logger;
        private readonly ServerAddressService _serverAddressService;
        private readonly RequestSignatureService _signatureService;
        private readonly IPairingStrategy _pairingStrategy;
        private readonly IStringLocalizer<SharedResources> _loc;

        public PairController(DeviceService deviceService, ILogger<PairController> logger, ServerAddressService serverAddressService, RequestSignatureService signatureService, IPairingStrategy pairingStrategy, IStringLocalizer<SharedResources> loc)
        {
            _deviceService = deviceService;
            _logger = logger;
            _serverAddressService = serverAddressService;
            _signatureService = signatureService;
            _pairingStrategy = pairingStrategy;
            _loc = loc;
        }

        [HttpPost("/vault/pair")]
        [HttpPost("/pair")]
        [HttpPost("/mg/pair")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("pair")]
        public ActionResult<PairResponse> Pair([FromBody] PairRequest request)
        {
            if (string.IsNullOrEmpty(request?.PairCode))
            {
                return BadRequest(new { error = _loc["Pair_CodeEmpty"] });
            }

            if (!_deviceService.ValidatePairCode(request.PairCode))
            {
                return BadRequest(new { error = _loc["Pair_CodeInvalid"] });
            }

            var deviceName = request.DeviceName ?? _loc["Pair_UnknownDevice"];
            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

            var existingDevice = _deviceService.GetAuthorizedDeviceByName(deviceName);
            if (existingDevice != null)
            {
                _logger.LogInformation("已授权设备重新配对: {DeviceName}", deviceName);
                return Ok(new PairResponse
                {
                    AccessToken = existingDevice.AccessToken,
                    ExpiresIn = 3600 * 24 * 365,
                    Status = "authorized",
                    Message = _loc["Device_AlreadyAuthorized"]
                });
            }

            return _pairingStrategy.Pair(deviceName, ipAddress, request.PairCode);
        }

        [HttpGet("/vault/pair/code")]
        [HttpGet("/pair/code")]
        [HttpGet("/mg/pair/code")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("pair")]
        public IActionResult GetPairCode()
        {
            var code = _deviceService.GetPairCode();
            return Ok(new { pairCode = code, deviceId = _serverAddressService.GetServerInstanceId() });
        }

        [HttpPost("/vault/pair/code/refresh")]
        [HttpPost("/pair/code/refresh")]
        [HttpPost("/mg/pair/code/refresh")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("pair")]
        public IActionResult RefreshPairCode()
        {
            var newCode = _deviceService.RefreshPairCode();
            return Ok(new { pairCode = newCode, message = _loc["Pair_CodeRefreshed"] });
        }

        /// <summary>
        /// 移动端扫码配对注册设备
        /// </summary>
        [HttpPost("/mg/register-device")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("pair")]
        public ActionResult RegisterDevice([FromBody] RegisterDeviceRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request?.DeviceId))
                {
                    return BadRequest(new { error = _loc["Register_DeviceIdRequired"] });
                }

                var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var deviceName = string.IsNullOrEmpty(request.DeviceName) ? _loc["Register_DefaultDeviceName"] : request.DeviceName;

                // 服务器名用 WebUI 配置的显示名（与 /health 一致），而非机器名，保证移动端显示统一
                var serverName = string.IsNullOrWhiteSpace(_serverAddressService.GetSettings().DisplayName)
                    ? Environment.MachineName
                    : _serverAddressService.GetSettings().DisplayName;

                var serverId = _serverAddressService.GetServerInstanceId();

                // 安全验证：优先通过 deviceId 查找授权设备，防止 deviceName 碰撞攻击
                var authorizedDevice = _deviceService.GetAuthorizedDeviceById(request.DeviceId);
                if (authorizedDevice != null)
                {
                    return Ok(new
                    {
                        message = _loc["Register_DeviceAuthorized"],
                        deviceId = request.DeviceId,
                        deviceName = deviceName,
                        serverName = serverName,
                        serverId = serverId,
                        ipAddress = ipAddress,
                        requestId = authorizedDevice.DeviceId,
                        authorized = true,
                        accessToken = authorizedDevice.AccessToken,
                        sharedSecret = _signatureService.GetSharedSecret()
                    });
                }

                // 已撤销设备重注册：一律走人工授权流程（不再自动恢复）
                var anyNameDevice = _deviceService.GetDeviceByNameAnyStatus(deviceName);
                if (anyNameDevice != null && anyNameDevice.Status == DeviceStatus.Revoked)
                {
                    _logger.LogInformation("Revoked device re-registering, creating pending request: {DeviceName} {DeviceId}", deviceName, request.DeviceId);
                    var pairRequest2 = _deviceService.SubmitLanDiscoveryRequest(deviceName, ipAddress, request.DeviceId, request.SystemDeviceName);
                    return Ok(new { message = _loc["Register_DeviceIdChangedReauthorize"], deviceId = request.DeviceId, deviceName, serverName, serverId = serverId, ipAddress, requestId = pairRequest2.RequestId, authorized = false });
                }

                // 自动创建局域网发现待授权请求（无需扫码）
                _logger.LogInformation("[AUTH-DIAG] Creating pending request for device: {DeviceName} ({DeviceId})",
                    deviceName, request.DeviceId);
                var pairRequest = _deviceService.SubmitLanDiscoveryRequest(deviceName, ipAddress, request.DeviceId, request.SystemDeviceName);
                _logger.LogInformation("[AUTH-DIAG] Pending request created: RequestId={RequestId}",
                    pairRequest.RequestId);

                // 自动授权模式：跳过等待，直接批准设备
                _logger.LogInformation("[AUTH-DIAG] AutoAuthorizeEnabled={Enabled}, deviceId={DeviceId}, deviceName={DeviceName}",
                    _deviceService.AutoAuthorizeEnabled, request.DeviceId, deviceName);

                if (_deviceService.AutoAuthorizeEnabled)
                {
                    _logger.LogInformation("[AUTH-DIAG] Auto-authorizing device: {DeviceName} ({DeviceId}) @ {IpAddress}",
                        deviceName, request.DeviceId, ipAddress);
                    var (success, accessToken, error) = _deviceService.AutoAuthorizeDevice(deviceName, ipAddress, request.DeviceId, request.SystemDeviceName);
                    _logger.LogInformation("[AUTH-DIAG] AutoAuthorize result: Success={Success}, AccessToken={AccessToken}, Error={Error}",
                        success, accessToken, error);
                    if (success)
                    {
                        // 清理刚创建的 pending 请求
                        _logger.LogInformation("[AUTH-DIAG] Cleaning pending request: {RequestId}", pairRequest.RequestId);
                        _deviceService.RejectRequest(pairRequest.RequestId);
                        _logger.LogInformation("[AUTH-DIAG] Returning authorized: DeviceId={DeviceId}, AccessToken={AccessToken}",
                            request.DeviceId, accessToken);
                        return Ok(new
                        {
                            message = _loc["Register_DeviceAuthorized"],
                            deviceId = request.DeviceId,
                            deviceName = deviceName,
                            serverName = serverName,
                            serverId = serverId,
                            ipAddress = ipAddress,
                            requestId = request.DeviceId,
                            authorized = true,
                            accessToken = accessToken,
                            sharedSecret = _signatureService.GetSharedSecret()
                        });
                    }
                    _logger.LogWarning("Auto-authorize failed for {DeviceName}: {Error}", deviceName, error);
                }

                return Ok(new
                {
                    message = _loc["Register_DeviceRegistered"],
                    deviceId = request.DeviceId,
                    deviceName = deviceName,
                    serverName = serverName,
                    serverId = serverId,
                    ipAddress = ipAddress,
                    requestId = pairRequest.RequestId,
                    authorized = false,
                    accessToken = (string?)null
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error registering device");
                return StatusCode(500, new { error = "Failed to register device", message = ex.Message });
            }
        }
    }
}
