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
    /// <summary>
    /// 浏览知识库目录结构（WebUI 使用）
    /// </summary>
    [HttpGet("vaults/{vaultId}/browse")]
    public ActionResult<VaultBrowseResponse> BrowseVault(string vaultId, [FromQuery] string? path = "")
    {
        var baseVaultPath = ResolveVaultPath(vaultId);
        if (string.IsNullOrEmpty(baseVaultPath))
        {
            return NotFound(new { error = _loc["Vault_NotFound"] });
        }

        // 使用 notes/ 子目录作为知识库内容根目录
        var notesPath = System.IO.Path.Combine(baseVaultPath, "notes");
        var effectiveRoot = System.IO.Directory.Exists(notesPath) ? notesPath : baseVaultPath;

        var targetPath = string.IsNullOrEmpty(path)
            ? effectiveRoot
            : System.IO.Path.Combine(effectiveRoot, path.Trim('/').Replace('/', System.IO.Path.DirectorySeparatorChar));

        var fullRootPath = System.IO.Path.GetFullPath(effectiveRoot);
        var fullTargetPath = System.IO.Path.GetFullPath(targetPath);
        if (!fullTargetPath.StartsWith(fullRootPath, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = _loc["Vault_IllegalPath"] });
        }

        if (!System.IO.Directory.Exists(targetPath))
        {
            return NotFound(new { error = _loc["Vault_DirNotFound"] });
        }

        var items = new List<VaultBrowseItem>();

        foreach (var dir in System.IO.Directory.GetDirectories(targetPath))
        {
            var dirName = System.IO.Path.GetFileName(dir);
            if (ExcludedDirs.Contains(dirName)) continue;
            var relativePath = dir.Substring(fullRootPath.Length).TrimStart('/', '\\').Replace('\\', '/');
            items.Add(new VaultBrowseItem
            {
                Name = dirName,
                Path = relativePath,
                IsDirectory = true,
                Modified = System.IO.Directory.GetLastWriteTime(dir)
            });
        }

        foreach (var file in System.IO.Directory.GetFiles(targetPath, "*.md"))
        {
            var fileName = System.IO.Path.GetFileNameWithoutExtension(file);
            var relativePath = file.Substring(fullRootPath.Length).TrimStart('/', '\\').Replace('\\', '/');
            var fileInfo = new System.IO.FileInfo(file);
            items.Add(new VaultBrowseItem
            {
                Name = fileName,
                Path = relativePath[..^3],
                IsDirectory = false,
                Size = fileInfo.Length,
                Modified = fileInfo.LastWriteTime
            });
        }

        items = items.OrderBy(i => !i.IsDirectory).ThenBy(i => i.Name).ToList();

        return Ok(new VaultBrowseResponse
        {
            VaultId = vaultId,
            VaultName = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == vaultId)?.Name ?? "",
            CurrentPath = path ?? "",
            Items = items
        });
    }
}
