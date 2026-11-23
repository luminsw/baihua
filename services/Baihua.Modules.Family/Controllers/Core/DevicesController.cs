using Baihua.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;
using Baihua.Modules.Family.Services;
using Baihua.Core.Hubs;
using Microsoft.AspNetCore.SignalR;
using Baihua.Contracts.Devices;

namespace Baihua.Modules.Family.Controllers
{
    [ApiController]
    [Route("api/devices")]
    [Route("mg/devices")]
    public partial class DevicesController : ControllerBase
    {
        private readonly DeviceService _deviceService;
        private readonly IHubContext<DeviceHub> _hubContext;
        private readonly Baihua.Core.Services.VaultSettingsService _vaultSettings;
        private readonly ILogger<DevicesController> _logger;
        private readonly IStringLocalizer<SharedResources> _loc;

        public DevicesController(DeviceService deviceService, IHubContext<DeviceHub> hubContext, Baihua.Core.Services.VaultSettingsService vaultSettings, ILogger<DevicesController> logger, IStringLocalizer<SharedResources> loc)
        {
            _deviceService = deviceService;
            _hubContext = hubContext;
            _vaultSettings = vaultSettings;
            _logger = logger;
            _loc = loc;
        }

        [HttpGet("pending")]
        public ActionResult<List<PendingDeviceDto>> GetPendingDevices()
        {
            var pendingRequests = _deviceService.GetPendingRequests();
            var dtos = pendingRequests.Select(r => new PendingDeviceDto
            {
                RequestId = r.RequestId,
                DeviceName = r.DeviceName,
                SystemDeviceName = r.SystemDeviceName,
                RequestTime = r.RequestTime,
                IpAddress = r.IpAddress
            }).ToList();
            return Ok(dtos);
        }

        [HttpGet("authorized")]
        public ActionResult<List<AuthorizedDeviceDto>> GetAuthorizedDevices()
        {
            var devices = _deviceService.GetAuthorizedDevices();
            var dtos = devices.Select(d => new AuthorizedDeviceDto
            {
                DeviceId = d.DeviceId,
                DeviceName = d.DeviceName,
                SystemDeviceName = d.SystemDeviceName,
                AuthorizedTime = d.AuthorizedTime,
                LastSyncTime = d.LastSyncTime,
                IpAddress = d.IpAddress,
                SyncCount = d.SyncCount,
                FirstSyncTime = d.FirstSyncTime,
                SyncedVaultIds = d.SyncedVaultIds,
                SyncedVaultNames = d.SyncedVaultNames
            }).ToList();
            return Ok(dtos);
        }

        [HttpGet]
        public ActionResult<List<DeviceDto>> GetAllDevices()
        {
            var devices = _deviceService.GetAllDevices();
            var dtos = devices.Select(d => new DeviceDto
            {
                DeviceId = d.DeviceId,
                DeviceName = d.DeviceName,
                Status = d.Status.ToString(),
                FirstRequestTime = d.FirstRequestTime,
                AuthorizedTime = d.AuthorizedTime,
                LastSyncTime = d.LastSyncTime,
                IpAddress = d.IpAddress,
                SyncCount = d.SyncCount,
                FirstSyncTime = d.FirstSyncTime
            }).ToList();
            return Ok(dtos);
        }

        [HttpPost("authorize")]
        public IActionResult AuthorizeDevice([FromBody] AuthorizeDeviceRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.RequestId))
                return BadRequest(new { error = _loc["Devices_RequestIdRequired"] });

            var (success, accessToken, error) = _deviceService.AuthorizeDevice(request.RequestId);
            if (!success)
                return BadRequest(new { error });

            _logger.LogInformation("设备已授权，请求ID: {RequestId}", request.RequestId);
            return Ok(new { success = true, message = _loc["Devices_DeviceAuthorized"], accessToken });
        }

        [HttpPost("reject")]
        public IActionResult RejectDevice([FromBody] RejectDeviceRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.RequestId))
                return BadRequest(new { error = _loc["Devices_RequestIdRequired"] });

            var success = _deviceService.RejectRequest(request.RequestId);
            if (!success)
                return BadRequest(new { error = _loc["Devices_RequestNotFound"] });

            _logger.LogInformation("设备配对已拒绝，请求ID: {RequestId}", request.RequestId);
            return Ok(new { success = true, message = _loc["Devices_RequestRejected"] });
        }

        [HttpPost("revoke")]
        public IActionResult RevokeDevice([FromBody] RevokeDeviceRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.DeviceId))
                return BadRequest(new { error = _loc["Devices_DeviceIdRequired"] });

            var success = _deviceService.RevokeDevice(request.DeviceId);
            if (!success)
                return BadRequest(new { error = _loc["Devices_DeviceNotFound"] });

            _logger.LogInformation("设备授权已撤销，设备ID: {DeviceId}", request.DeviceId);
            return Ok(new { success = true, message = _loc["Devices_DeviceRevoked"] });
        }

        [HttpGet("stats")]
        public ActionResult<MobileStats> GetMobileStats()
        {
            var stats = _deviceService.GetMobileStats();
            return Ok(stats);
        }

        /// <summary>
        /// 获取自动授权开关状态。
        /// </summary>
        [HttpGet("auto-auth")]
        public ActionResult GetAutoAuthStatus()
        {
            return Ok(new { enabled = _deviceService.AutoAuthorizeEnabled });
        }

        /// <summary>
        /// 设置自动授权开关。
        /// </summary>
        [HttpPost("auto-auth")]
        public IActionResult SetAutoAuthStatus([FromBody] AutoAuthRequest request)
        {
            _deviceService.AutoAuthorizeEnabled = request?.Enabled == true;
            _logger.LogInformation("Auto-authorize set to: {Enabled}", _deviceService.AutoAuthorizeEnabled);
            return Ok(new { success = true, enabled = _deviceService.AutoAuthorizeEnabled });
        }
    }
}

/// <summary>
/// 自动授权开关请求。
/// </summary>
public class AutoAuthRequest
{
    public bool Enabled { get; set; }
}
