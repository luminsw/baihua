using Baihua.Core.Models;
using Baihua.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Baihua.Contracts.LocalModels;
using Baihua.Contracts.OpenClaw;
using Baihua.Core.Localization;
using Baihua.Family.Services;
using Microsoft.Extensions.Localization;
using Baihua.AI.Provider;

namespace Baihua.Family.Controllers;
    /// <summary>
    /// 本地模型部署 API：硬件检测、模型推荐、部署管理
    /// </summary>
    [ApiController]
    [Route("api/local-models")]
    public partial class LocalModelDeploymentController : ControllerBase
    {
        private readonly HardwareInfoService _hardwareInfoService;
        private readonly LocalModelDeploymentService _deploymentService;
        private readonly LocalModelSettingsService _localModelSettings;
        private readonly ILogger<LocalModelDeploymentController> _logger;
        private readonly IStringLocalizer<SharedResources> _loc;

        public LocalModelDeploymentController(
            HardwareInfoService hardwareInfoService,
            LocalModelDeploymentService deploymentService,
            LocalModelSettingsService localModelSettings,
            ILogger<LocalModelDeploymentController> logger,
            IStringLocalizer<SharedResources> loc)
        {
            _hardwareInfoService = hardwareInfoService;
            _deploymentService = deploymentService;
            _localModelSettings = localModelSettings;
            _logger = logger;
            _loc = loc;
        }

        /// <summary>
        /// 获取当前硬件信息
        /// </summary>
        [HttpGet("hardware")]
        public ActionResult<HardwareInfoDto> GetHardware([FromQuery] bool forceRefresh = false)
        {
            try
            {
                var info = forceRefresh
                    ? _hardwareInfoService.RefreshHardwareInfo()
                    : _hardwareInfoService.GetHardwareInfo();
                // k8s：本机 Family 容器扫不到 GPU（GPU 在 bh-openvino pod）——
                // pod 报告非 CPU 设备时，把它合并进硬件信息，避免页面误报"未检测到显卡/将使用 CPU"
                if (info.Gpus is null or { Count: 0 })
                {
                    var pod = ProbeOpenVinoPodInfo();
                    _logger.LogInformation("[HW-ENRICH] Gpus={GpuCount} podDevice={PodDevice}", info.Gpus?.Count ?? 0, pod.Device);
                    if (!string.IsNullOrWhiteSpace(pod.Device) && !pod.Device.Equals("CPU", StringComparison.OrdinalIgnoreCase))
                    {
                        info.Gpus = new List<GpuInfoDto>
                        {
                            new GpuInfoDto
                            {
                                Name = pod.DeviceName ?? $"Intel GPU（OpenVINO 服务 · {pod.Device}）",
                                IsIntegrated = true,
                                VramBytes = pod.VramBytes
                            }
                        };
                    }
                }
                return Ok(info);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取硬件信息失败");
                return StatusCode(500, new { error = _loc["LocalModel_GetHardwareFailed"], message = ex.Message });
            }
        }
        private PodHealthDto ProbeOpenVinoPodInfo()
        {
            var podUrl = Environment.GetEnvironmentVariable("OPENVINO_LLM_URL")
                ?? Environment.GetEnvironmentVariable("OPENVINO_HOST_URL");
            if (string.IsNullOrWhiteSpace(podUrl)) return new PodHealthDto();
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var health = System.Text.Json.JsonSerializer.Deserialize<PodHealthDto>(
                    client.GetStringAsync(podUrl.TrimEnd('/') + "/health").GetAwaiter().GetResult(),
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return health ?? new PodHealthDto();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[HW-ENRICH] 探测 OpenVINO pod 失败 ({Url})", podUrl);
                return new PodHealthDto();
            }
        }

private sealed class PodHealthDto
        {
            public string? Device { get; set; }
            public string? DeviceName { get; set; }
            public long? VramBytes { get; set; }
        }
    }


