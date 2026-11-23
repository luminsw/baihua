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
    [HttpGet("file")]
    public IActionResult GetFile([FromQuery] string path, [FromQuery] string vaultId, [FromQuery] string? deviceId = null)
    {
        var authResult = _syncAuthStrategy.ValidateFile(HttpContext, vaultId, deviceId);
        if (authResult != null)
        {
            return authResult;
        }

        _logger.LogInformation("GetFile请求: path={Path}, vaultId={VaultId}", path, vaultId);
        
        if (string.IsNullOrEmpty(path))
        {
            return BadRequest(new { error = _loc["Vault_PathEmpty"] });
        }

        var baseVaultPath = ResolveVaultPath(vaultId);
        if (string.IsNullOrEmpty(baseVaultPath))
        {
            return BadRequest(new { error = _loc["Vault_Required"] });
        }

        try
        {
            // 路径安全检查：阻止目录遍历
            path = path.Replace("\\", "/").TrimStart('/');
            if (path.Contains(".."))
            {
                _logger.LogWarning("检测到目录遍历尝试: {Path}", path);
                return BadRequest(new { error = _loc["Vault_IllegalPath"] });
            }

            var ext = System.IO.Path.GetExtension(path);
            if (!AllowedExtensions.Contains(ext))
            {
                return BadRequest(new { error = _loc["Vault_UnsupportedFileType", ext] });
            }

            string filePath;
            if (path.StartsWith("cards/"))
            {
                filePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseVaultPath, path));
            }
            else if (path.StartsWith("notes/"))
            {
                filePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseVaultPath, path));
            }
            else
            {
                var notesPath = System.IO.Path.Combine(baseVaultPath, "notes");
                filePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(notesPath, path));
            }

            // 确保文件路径在知识库目录内（防止路径遍历）
            var baseFullPath = System.IO.Path.GetFullPath(baseVaultPath);
            if (!filePath.StartsWith(baseFullPath, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("路径遍历被阻止: {FilePath} 不在 {BasePath} 内", filePath, baseFullPath);
                return BadRequest(new { error = _loc["Vault_IllegalPath"] });
            }
            
            if (!System.IO.File.Exists(filePath))
            {
                _logger.LogWarning("文件不存在：{Path}", path);
                return NotFound();
            }

            if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                var content = System.IO.File.ReadAllText(filePath);
                return Ok(content);
            }
            
            if (!ext.Equals(".md", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = System.IO.File.ReadAllBytes(filePath);
                var mimeType = GetMimeType(ext);
                return File(bytes, mimeType);
            }

            var mdContent = System.IO.File.ReadAllText(filePath);
            return Ok(mdContent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取文件失败：{Path}", path);
            return StatusCode(500, new { error = _loc["Vault_ReadFailed"], message = ex.Message });
        }
    }

    private string GetMimeType(string ext)
    {
        return ext.ToLower() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".bmp" => "image/bmp",
            ".ico" => "image/x-icon",
            ".json" => "application/json",
            _ => "application/octet-stream"
        };
    }
}
