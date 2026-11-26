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
        private readonly Baihua.Core.WebSocket.DeviceWebSocketHub _wsHub;
        private readonly ILogger<DevicesController> _logger;
        private readonly IStringLocalizer<SharedResources> _loc;

        public DevicesController(DeviceService deviceService, IHubContext<DeviceHub> hubContext, Baihua.Core.Services.VaultSettingsService vaultSettings, Baihua.Core.WebSocket.DeviceWebSocketHub wsHub, ILogger<DevicesController> logger, IStringLocalizer<SharedResources> loc)
        {
            _deviceService = deviceService;
            _hubContext = hubContext;
            _vaultSettings = vaultSettings;
            _wsHub = wsHub;
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
            var onlineIds = _wsHub.GetOnlineDeviceIds();
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
                SyncedVaultNames = d.SyncedVaultNames,
                IsOnline = onlineIds.Contains(d.DeviceId)
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

        /// <summary>
        /// WebUI 管理端撤销设备授权：保留 Revoked 记录，设备重注册需人工批准。
        /// </summary>
        [HttpPost("/api/devices/revoke")]
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

        /// <summary>
        /// 移动端删除服务器时的自助解绑（HMAC 签名）：彻底移除配对记录，
        /// 重新扫码注册等同新设备（自动授权模式下扫码即授权）。
        /// 修复：此前与 WebUI 管理撤销共用 RevokeDevice 留下 Revoked 记录，
        /// 被"已撤销设备重注册一律人工审批"拦截 → 删除服务器后重新扫码一直"等待授权"。
        /// </summary>
        [HttpPost("/mg/devices/revoke")]
        public IActionResult UnpairDevice([FromBody] RevokeDeviceRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.DeviceId))
                return BadRequest(new { error = _loc["Devices_DeviceIdRequired"] });

            var success = _deviceService.UnpairDevice(request.DeviceId);
            if (!success)
                return BadRequest(new { error = _loc["Devices_DeviceNotFound"] });

            _logger.LogInformation("设备已解绑（客户端删除服务器），设备ID: {DeviceId}", request.DeviceId);
            return Ok(new { success = true, message = _loc["Devices_DeviceRevoked"] });
        }

        [HttpGet("stats")]
        public ActionResult<MobileStats> GetMobileStats()
        {
            var stats = _deviceService.GetMobileStats();
            return Ok(stats);
        }

        /// <summary>
        /// WebUI 把选中的知识库「发送」给移动端：定向推送 fetch_vaults 指令，
        /// 客户端收到后调用既有的增量同步流程获取这些库。仅送达在线设备。
        /// </summary>
        [HttpPost("/api/devices/fetch-vaults")]
        public async Task<ActionResult<FetchVaultsResponse>> SendVaultsToDevices([FromBody] FetchVaultsRequest request)
        {
            if (request == null || request.DeviceIds.Count == 0 || request.VaultIds.Count == 0)
                return BadRequest(new { error = _loc["Devices_FetchVaultsInvalidRequest"] });

            // 校验目标设备均已授权（未授权设备不该收到任何指令）
            var authorized = _deviceService.GetAuthorizedDevices()
                .Select(d => d.DeviceId)
                .ToHashSet();
            var unknownDevices = request.DeviceIds.Where(id => !authorized.Contains(id)).ToList();
            if (unknownDevices.Count > 0)
                return BadRequest(new { error = _loc["Devices_FetchVaultsDeviceNotAuthorized", string.Join(", ", unknownDevices)] });

            // 校验知识库存在，并带上名称（客户端展示用）
            var vaults = _vaultSettings.GetVaults();
            var vaultNames = new Dictionary<string, string>();
            var missingVaults = new List<string>();
            foreach (var vaultId in request.VaultIds)
            {
                var vault = vaults.FirstOrDefault(v => v.Id == vaultId);
                if (vault == null)
                {
                    missingVaults.Add(vaultId);
                }
                else
                {
                    vaultNames[vaultId] = vault.Name;
                }
            }
            if (missingVaults.Count > 0)
                return BadRequest(new { error = _loc["Devices_FetchVaultsVaultNotFound", string.Join(", ", missingVaults)] });

            var delivered = await _wsHub.SendVaultFetchAsync(request.DeviceIds, request.VaultIds, vaultNames);

            _logger.LogInformation("发送知识库到设备：vaults={VaultIds}, devices={DeviceIds}, delivered={Delivered}",
                string.Join(",", request.VaultIds), string.Join(",", request.DeviceIds), string.Join(",", delivered));

            return Ok(new FetchVaultsResponse
            {
                Success = delivered.Count > 0,
                DeliveredDeviceIds = delivered.ToList(),
                SkippedDeviceIds = request.DeviceIds.Where(id => !delivered.Contains(id)).ToList()
            });
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
