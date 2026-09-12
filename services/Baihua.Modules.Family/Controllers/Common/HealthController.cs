using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Baihua.Contracts.Health;
using Baihua.Core.Localization;
using Baihua.Modules.Family.Services;
using Baihua.AI.Provider;

namespace Baihua.Modules.Family.Controllers;
    /// <summary>
    /// 健康检查控制器
    /// </summary>
    [ApiController]
    [Route("api/health")]
    public partial class HealthController : ControllerBase
    {
        private readonly Services.SystemHealthService _healthService;
        private readonly Baihua.Core.Services.AiSettingsService _aiSettings;
        private readonly Baihua.Core.Services.LocalAiAutoStarter _localAiAutoStarter;
        private readonly Baihua.AI.Provider.ILocalAiConfigService _localAiConfig;
        private readonly ILogger<HealthController> _logger;
        private readonly IStringLocalizer<SharedResources> _loc;

        public HealthController(
            Services.SystemHealthService healthService,
            Baihua.Core.Services.AiSettingsService aiSettings,
            Baihua.Core.Services.LocalAiAutoStarter localAiAutoStarter,
            Baihua.AI.Provider.ILocalAiConfigService localAiConfig,
            ILogger<HealthController> logger,
            IStringLocalizer<SharedResources> loc)
        {
            _healthService = healthService;
            _loc = loc;
            _aiSettings = aiSettings;
            _localAiAutoStarter = localAiAutoStarter;
            _localAiConfig = localAiConfig;
            _logger = logger;
        }

        /// <summary>
        /// 获取系统健康报告（完整自检）
        /// </summary>
        [HttpGet("full")]
        public async Task<ActionResult<SystemHealthReportDto>> GetFullHealth(CancellationToken cancellationToken)
        {
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(TimeSpan.FromSeconds(25));
                var report = await _healthService.GetHealthReportAsync(budget.Token);
                return Ok(report);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("健康检查在时限内未完成（可能机器较慢），建议使用 /api/health/simple");
                return StatusCode(StatusCodes.Status504GatewayTimeout, new
                {
                    error = _loc["Health_CheckTimeout"],
                    message = _loc["Health_CheckTimeoutMessage"]
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "健康检查失败");
                return StatusCode(500, new { error = _loc["Health_CheckFailed"], message = ex.Message });
            }
        }

        /// <summary>
        /// 简单健康检查（快速响应）
        /// </summary>
        [HttpGet("simple")]
        public ActionResult<dynamic> GetSimpleHealth()
        {
            return new
            {
                status = "healthy",
                timestamp = DateTime.UtcNow.ToString("o"),
                message = "Baihua Family Service is running"
            };
        }

        /// <summary>
        /// 检查特定组件
        /// </summary>
        [HttpGet("check/{component}")]
        public async Task<ActionResult<dynamic>> CheckComponent(string component)
        {
            try
            {
                var report = await _healthService.GetHealthReportAsync();
                var componentStatus = report.Components.FirstOrDefault(c => 
                    c.Name.ToLower() == component.ToLower());

                if (componentStatus == null)
                {
                    return NotFound(new { 
                        error = _loc["Health_ComponentNotFound", component],
                        available = string.Join(", ", report.Components.Select(c => c.Name))
                    });
                }

                return Ok(new
                {
                    component = componentStatus.Name,
                    status = componentStatus.Status,
                    version = componentStatus.Version,
                    message = componentStatus.Message
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "组件检查失败");
                return StatusCode(500, new { error = _loc["Health_ComponentCheckFailed"], message = ex.Message });
            }
        }

        /// <summary>
        /// 获取所有可用组件列表
        /// </summary>
        [HttpGet("components")]
        public async Task<ActionResult<List<string>>> GetComponents()
        {
            try
            {
                var report = await _healthService.GetHealthReportAsync();
                return Ok(report.Components.Select(c => c.Name).ToList());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取组件列表失败");
                return StatusCode(500, new { error = _loc["Health_GetComponentsFailed"], message = ex.Message });
            }
        }
}
