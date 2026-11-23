using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;
using Baihua.Core.Notifications;
using Microsoft.AspNetCore.Mvc;
using Baihua.Core.Services;
using Baihua.Contracts.Vaults;

namespace Baihua.Modules.Vault.Controllers
{
    [ApiController]
    [Route("api/settings")]
    public partial class VaultSettingsController : ControllerBase
    {
        private readonly VaultSettingsService _vaultSettings;
        private readonly WebUINotificationService _webUINotification;
        private readonly ILogger<VaultSettingsController> _logger;
        private readonly IStringLocalizer<SharedResources> _loc;

        public VaultSettingsController(VaultSettingsService vaultSettings, WebUINotificationService webUINotification, ILogger<VaultSettingsController> logger, IStringLocalizer<SharedResources> loc)
        {
            _vaultSettings = vaultSettings;
            _webUINotification = webUINotification;
            _logger = logger;
            _loc = loc;
        }

        [HttpGet("vault-root")]
        public ActionResult<VaultRootResponse> GetVaultRoot()
        {
            return Ok(new VaultRootResponse { VaultPath = _vaultSettings.VaultPath });
        }

        [HttpPost("vault-root")]
        public IActionResult SetVaultRoot([FromBody] VaultRootRequest request)
        {
            if (request == null)
                return BadRequest(new { error = _loc["Vault_RequestCannotBeEmpty"].Value });

            var next = string.IsNullOrWhiteSpace(request.VaultPath) ? "" : request.VaultPath.Trim();
            if (string.IsNullOrWhiteSpace(next))
                return BadRequest(new { error = _loc["Vault_PathCannotBeEmpty"].Value });

            _vaultSettings.SetVaultPath(next);
            _ = _webUINotification.NotifyVaultStatusChangedAsync();
            _logger.LogInformation("Runtime VaultPath updated: {Path}", next);

            return Ok(new { success = true });
        }

        #region 知识库根路径偏好

        /// <summary>
        /// 获取知识库根路径偏好
        /// </summary>
        [HttpGet("vault-root-path-preference")]
        public ActionResult<VaultRootPathPreferenceResponse> GetVaultRootPathPreference()
        {
            try
            {
                return Ok(new VaultRootPathPreferenceResponse
                {
                    VaultRootPath = _vaultSettings.VaultRootPathPreference
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取 VaultRootPathPreference 失败");
                return Ok(new VaultRootPathPreferenceResponse { VaultRootPath = string.Empty });
            }
        }

        /// <summary>
        /// 修复知识库路径
        /// </summary>
        [HttpPost("vaults/fix-paths")]
        public IActionResult FixVaultPaths()
        {
            var vaults = _vaultSettings.GetVaults();
            var fixedPaths = new List<string>();
            var migratedPaths = new List<string>();

            foreach (var vault in vaults)
            {
                if (string.IsNullOrWhiteSpace(vault.Path)) continue;
                if (Directory.Exists(vault.Path)) continue;

                var originalPath = vault.Path;
                var isWindowsPath = System.Text.RegularExpressions.Regex.IsMatch(originalPath, @"^[A-Za-z]:\\");
                if (isWindowsPath && !OperatingSystem.IsWindows())
                {
                    var drive = originalPath[0].ToString().ToLowerInvariant();
                    var wslPath = "/mnt/" + drive + "/" + originalPath[3..].Replace('\\', '/');
                    if (Directory.Exists(wslPath))
                    {
                        if (_vaultSettings.UpdateVaultPath(vault.Id, wslPath))
                        {
                            migratedPaths.Add($"{vault.Name}: {originalPath} -> {wslPath}");
                            _logger.LogInformation("知识库路径跨平台迁移: {Name} {Original} -> {Wsl}", vault.Name, originalPath, wslPath);
                            continue;
                        }
                    }
                }

                try
                {
                    Directory.CreateDirectory(vault.Path);
                    fixedPaths.Add(vault.Name);
                    _logger.LogInformation("修复知识库路径: {Name} -> {Path}", vault.Name, vault.Path);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "无法创建知识库目录: {Name} -> {Path}", vault.Name, vault.Path);
                }
            }

            var totalFixed = fixedPaths.Count + migratedPaths.Count;
            if (totalFixed > 0)
            {
                _ = _webUINotification.NotifyVaultStatusChangedAsync();
            }

            return Ok(new
            {
                success = true,
                fixedCount = fixedPaths.Count,
                migratedCount = migratedPaths.Count,
                fixedVaults = fixedPaths,
                migratedVaults = migratedPaths
            });
        }

        #endregion

        /// <summary>
        /// 手动同步知识库
        /// </summary>
        [HttpPost("vaults/sync")]
        public IActionResult SyncVaults()
        {
            var rootPath = _vaultSettings.VaultRootPathPreference;
            if (string.IsNullOrWhiteSpace(rootPath))
                return BadRequest(new { error = _loc["Vault_RootPathNotSet"].Value });

            if (!Directory.Exists(rootPath))
                return BadRequest(new { error = _loc["Vault_RootPathNotExists", rootPath].Value });

            var (added, removed) = _vaultSettings.SyncVaultsWithFilesystem(rootPath);
            if (added > 0 || removed > 0)
            {
                _ = _webUINotification.NotifyVaultStatusChangedAsync();
            }

            return Ok(new { success = true, added, removed });
        }
    }
}
