using Baihua.Core;
using Baihua.Core.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baihua.Data;
using Baihua.Core.Services;
using Baihua.Core.Services.Strategies;
using Baihua.Contracts.Vaults;

namespace Baihua.Modules.Vault.Controllers;

public partial class VaultController
{
        [HttpPost("verify-token")]
        public ActionResult<object> VerifyToken([FromBody] VerifyTokenRequest request)
        {
            if (string.IsNullOrEmpty(request.Token))
            {
                return BadRequest(new { valid = false, error = _loc["Vault_TokenRequired"].Value });
            }

            var isValid = _deviceService.ValidateAccessToken(request.Token);

            if (!isValid)
            {
                return Ok(new { valid = false, error = _loc["Vault_TokenInvalidOrExpired"].Value });
            }

            return Ok(new { valid = true, deviceId = "" });
        }

        /// <summary>
        /// 获取知识库清单（增量同步）
        /// cloud 模式：HMAC签名 + deviceId + 配额/频率检查
        /// 家庭版/本地模式：仍需 Bearer Token 验证
        /// </summary>
        [HttpGet("manifest")]
        public ActionResult<VaultManifestResponse> GetManifest([FromQuery] string vaultId, [FromQuery] string? deviceId = null)
        {
            var authResult = _syncAuthStrategy.ValidateManifest(HttpContext, vaultId, deviceId);
            if (authResult != null)
            {
                return authResult;
            }

            var targetVault = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == vaultId);
            var baseVaultPath = targetVault?.Path;
            _logger.LogDebug("GetManifest called. VaultPath={VaultPath}, VaultId={VaultId}", baseVaultPath, vaultId);

            if (string.IsNullOrEmpty(baseVaultPath))
            {
                return NotFound(new { error = _loc["Vault_NotFoundOrDeleted"].Value });
            }

            if (!System.IO.Directory.Exists(baseVaultPath))
            {
                _logger.LogError("知识库路径无效：{Path}，数据库记录存在但物理目录已丢失", baseVaultPath);
                return StatusCode(410, new { error = _loc["Vault_DataInconsistencyDirMissing"].Value, vaultId });
            }

            try
            {
                var files = new List<ManifestFile>();
                long maxMtime = 0;

                // 同步 notes/ 目录
                var notesPath = System.IO.Path.Combine(baseVaultPath, "notes");
                if (System.IO.Directory.Exists(notesPath))
                {
                    ScanDirectory(notesPath, notesPath, files, ref maxMtime, "");
                }

                // 同步 cards/ 目录
                var cardsPath = System.IO.Path.Combine(baseVaultPath, "cards");
                if (System.IO.Directory.Exists(cardsPath))
                {
                    ScanDirectory(cardsPath, cardsPath, files, ref maxMtime, "cards/");
                }

                // 回退：如果 notes/ 和 cards/ 都不存在，扫描根目录下的直接文件
                if (files.Count == 0 && !System.IO.Directory.Exists(notesPath) && !System.IO.Directory.Exists(cardsPath))
                {
                    ScanDirectory(baseVaultPath, baseVaultPath, files, ref maxMtime, "");
                }

                var cursor = maxMtime;

                var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var syncDeviceId = !string.IsNullOrWhiteSpace(deviceId) ? deviceId : "ip-" + ipAddress.GetHashCode().ToString("x");
                var syncDeviceName = !string.IsNullOrWhiteSpace(deviceId) ? deviceId : _loc["Vault_MobileDeviceName", ipAddress].Value;
                _deviceService.RecordSyncActivity(syncDeviceId, syncDeviceName, vaultId, files.Count, "manifest", ipAddress);

                _logger.LogInformation("返回全量清单：{Count} 个文件，cursor={Cursor}, vaultId={VaultId}", files.Count, cursor, vaultId);

                return Ok(new VaultManifestResponse
                {
                    VaultId = vaultId,
                    VaultName = targetVault?.Name ?? _loc["Vault_SpecifiedVault"].Value,
                    Cursor = cursor,
                    Files = files
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取清单失败");
                return StatusCode(500, new { error = _loc["Common_ReadFailed"], message = ex.Message });
            }
        }

        private void ScanDirectory(string rootPath, string currentPath, List<ManifestFile> files, ref long maxMtime, string pathPrefix = "")
        {
            foreach (var dir in System.IO.Directory.GetDirectories(currentPath))
            {
                var dirName = System.IO.Path.GetFileName(dir);
                if (ExcludedDirs.Contains(dirName))
                {
                    _logger.LogDebug("ScanDirectory 跳过排除目录: {DirName}", dirName);
                    continue;
                }
                ScanDirectory(rootPath, dir, files, ref maxMtime, pathPrefix);
            }

            foreach (var file in System.IO.Directory.GetFiles(currentPath))
            {
                var ext = System.IO.Path.GetExtension(file);
                if (!AllowedExtensions.Contains(ext))
                {
                    _logger.LogDebug("ScanDirectory 跳过不支持的文件类型: {File} ({Ext})", file, ext);
                    continue;
                }

                var relativePath = pathPrefix + file.Substring(rootPath.Length).TrimStart('/', '\\');
                relativePath = relativePath.Replace('\\', '/').TrimStart('/');
                var modified = System.IO.File.GetLastWriteTime(file);
                var modifiedUnix = new DateTimeOffset(modified).ToUnixTimeSeconds();
                
                if (modifiedUnix > maxMtime)
                {
                    maxMtime = modifiedUnix;
                }
                
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    _logger.LogWarning("ScanDirectory 计算出空的相对路径: {File}", file);
                    continue;
                }

                var fileInfo = new System.IO.FileInfo(file);
                if (fileInfo.Length == 0)
                {
                    _logger.LogWarning("ScanDirectory 跳过空文件: {File}", file);
                    continue;
                }

                files.Add(new ManifestFile
                {
                    RelPath = relativePath,
                    Op = "upsert",
                    Mtime = modifiedUnix,
                    Size = fileInfo.Length,
                    Sha256 = modifiedUnix.ToString()
                });
            }
        }

}
