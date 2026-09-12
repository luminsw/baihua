using Baihua.Core.Models;
using Baihua.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using System.Diagnostics;
using Baihua.Core.Localization;
using Baihua.Modules.Family.Services;
using Baihua.Contracts.Core;

namespace Baihua.Modules.Family.Controllers
{
    [ApiController]
    [Route("api/obsidian")]
    public class ObsidianController : ControllerBase
    {
        private readonly SystemHealthService _healthService;
        private readonly VaultSettingsService _vaultSettings;
        private readonly ILogger<ObsidianController> _logger;
        private readonly IStringLocalizer<SharedResources> _loc;

        public ObsidianController(
            SystemHealthService healthService, 
            VaultSettingsService vaultSettings,
            ILogger<ObsidianController> logger,
            IStringLocalizer<SharedResources> loc)
        {
            _healthService = healthService;
            _vaultSettings = vaultSettings;
            _logger = logger;
            _loc = loc;
        }

        /// <summary>
        /// 触发一次 Obsidian 预热/启动（用于确认启动逻辑可用）。
        /// </summary>
        [HttpPost("warmup")]
        public async Task<IActionResult> Warmup()
        {
            try
            {
                await _healthService.InitializeObsidianAsync();
                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Obsidian warmup failed");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 用 Obsidian 打开知识库
        /// </summary>
        [HttpPost("open-current-vault")]
        public IActionResult OpenCurrentVault()
        {
            try
            {
                var vault = _vaultSettings.GetActiveVault();
                if (vault == null)
                {
                    return BadRequest(new { success = false, error = _loc["Obsidian_NoVaultConfigured"] });
                }

                return OpenVaultInternal(vault.Path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "打开知识库失败");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 用 Obsidian 打开指定路径的仓库
        /// </summary>
        [HttpPost("open")]
        public IActionResult OpenVault([FromBody] OpenVaultRequest request)
        {
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(request.Path))
                {
                    return BadRequest(new { success = false, error = _loc["Obsidian_PathEmpty"] });
                }

                return OpenVaultInternal(request.Path.Trim());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "打开知识库失败");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        private IActionResult OpenVaultInternal(string vaultPath)
        {
            // 确保目录存在
            if (!Directory.Exists(vaultPath))
            {
                try
                {
                    Directory.CreateDirectory(vaultPath);
                }
                catch (Exception ex)
                {
                    return BadRequest(new { success = false, error = _loc["Obsidian_CreateDirFailed", ex.Message] });
                }
            }

            // 创建 notes 子目录（如果不存在）
            var notesPath = Path.Combine(vaultPath, "notes");
            if (!Directory.Exists(notesPath))
            {
                Directory.CreateDirectory(notesPath);
            }

            // 启动 Obsidian
            var obsidianExe = ObsidianExecutableResolver.Resolve();

            var startInfo = new ProcessStartInfo
            {
                FileName = obsidianExe,
                Arguments = $"--vault=\"{vaultPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.EnvironmentVariables["ELECTRON_DISABLE_AUTO_UPDATE"] = "1";
            startInfo.EnvironmentVariables["OBSIDIAN_DISABLE_AUTO_UPDATE"] = "1";

            try
            {
                Process.Start(startInfo);
                return Ok(new { success = true, path = vaultPath });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动 Obsidian 失败");
                return StatusCode(500, new { success = false, error = _loc["Obsidian_LaunchFailed", ex.Message] });
            }
        }

    }
}
