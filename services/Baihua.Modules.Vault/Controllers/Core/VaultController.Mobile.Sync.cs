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
    /// 移动端推送 AI 生成的知识库（接收来自手机端的 DeepSeek 生成内容）
    /// </summary>
    [HttpPost("/mobile-vaults/push")]
    public async Task<ActionResult> PushMobileVault([FromBody] MobileVaultPushRequest request)
    {
        _logger.LogInformation("[PushMobileVault] Received from {RemoteIP}, VaultName={VaultName}, Industry={Industry}, NotesCount={NotesCount}",
            HttpContext.Connection.RemoteIpAddress, request.VaultName, request.Industry, request.Notes?.Count ?? 0);

        if (string.IsNullOrWhiteSpace(request.VaultName) || request.Notes == null || request.Notes.Count == 0)
        {
            return BadRequest(new { error = _loc["Vault_NameNotesRequired"].Value });
        }

        try
        {
            var vaultRoot = _vaultSettings.VaultRootPathPreference;
            var mobileDir = Path.Combine(vaultRoot, "mobile");
            var industry = string.IsNullOrWhiteSpace(request.Industry) ? _loc["Vault_MobileGenerated"].Value : request.Industry.Trim();
            var safeVaultName = _vaultNameResolver.ToSafeDirectoryName(request.VaultName.Trim());
            var industryDir = Path.Combine(mobileDir, industry);
            Directory.CreateDirectory(industryDir);

            using var dbContext = _dbContextFactory.CreateDbContext();

            // 查找是否已有同名同行业的 mobile 知识库
            var existingVault = dbContext.Vaults
                .FirstOrDefault(v => !v.IsDeleted
                    && v.Source == "mobile"
                    && v.Industry == industry
                    && v.Name == request.VaultName.Trim());

            string vaultId;
            string vaultDir;
            bool isNewVault = false;
            bool migrated = false;

            if (existingVault != null)
            {
                vaultId = existingVault.VaultId;

                // 检查现有路径是否符合新的三级结构 mobile/{行业}/{名称}/
                var expectedPath = Path.Combine(industryDir, safeVaultName);
                var isOldGuidStructure = !existingVault.Path.Equals(expectedPath, StringComparison.OrdinalIgnoreCase)
                    && !existingVault.Path.StartsWith(expectedPath + "_", StringComparison.OrdinalIgnoreCase);

                if (isOldGuidStructure && Directory.Exists(existingVault.Path))
                {
                    // 旧 GUID 结构需要迁移到三级目录结构
                    _logger.LogWarning("移动端知识库路径结构过时: {OldPath}，迁移到: {NewPath}",
                        existingVault.Path, expectedPath);

                    if (Directory.Exists(expectedPath))
                    {
                        // 目标目录已存在（不太可能，但防御）
                        vaultDir = _vaultNameResolver.GetUniqueDirectoryPath(industryDir, safeVaultName);
                    }
                    else
                    {
                        vaultDir = expectedPath;
                    }

                    Directory.Move(existingVault.Path, vaultDir);
                    existingVault.Path = vaultDir;
                    migrated = true;
                    _logger.LogInformation("知识库路径迁移完成: {VaultId} -> {NewPath}", vaultId, vaultDir);
                }
                else if (Directory.Exists(existingVault.Path))
                {
                    vaultDir = existingVault.Path;
                }
                else
                {
                    // 数据库记录存在但物理目录已丢失，报错而不是静默创建新的
                    _logger.LogError("知识库数据库记录存在但物理目录丢失: {VaultId} {Path}",
                        existingVault.VaultId, existingVault.Path);
                    return StatusCode(500, new { error = _loc["Vault_DataInconsistencyContactAdmin"].Value });
                }

                _logger.LogInformation("复用已有移动端知识库: {VaultId} {VaultName}{MigrationNote}，追加笔记",
                    vaultId, request.VaultName, migrated ? " (path migrated)" : "");
            }
            else
            {
                vaultId = Guid.NewGuid().ToString("N");
                vaultDir = _vaultNameResolver.GetUniqueDirectoryPath(industryDir, safeVaultName);
                isNewVault = true;
            }

            var notesDir = Path.Combine(vaultDir, "notes");
            Directory.CreateDirectory(notesDir);

            // 写入笔记文件
            foreach (var note in request.Notes)
            {
                var safeRelPath = string.IsNullOrWhiteSpace(note.RelPath)
                    ? $"{note.Title}.md"
                    : note.RelPath;
                // 防止路径穿越：拒绝包含 .. 的路径
                if (safeRelPath.Contains(".."))
                {
                    _logger.LogWarning("检测到路径穿越尝试，已拒绝: {RelPath}", safeRelPath);
                    return BadRequest(new { error = _loc["Vault_IllegalFilePath", safeRelPath].Value });
                }
                safeRelPath = safeRelPath.TrimStart('/', '\\');
                var notePath = Path.Combine(notesDir, safeRelPath);
                var noteDir = Path.GetDirectoryName(notePath);
                if (!string.IsNullOrEmpty(noteDir))
                {
                    Directory.CreateDirectory(noteDir);
                }
                await System.IO.File.WriteAllTextAsync(notePath, note.Content ?? "");
            }

            // 注册到数据库（仅当是新知识库时）
            var pushedByDeviceId = request.DeviceId ?? "";
            var pushedByDeviceName = request.DeviceName ?? "";
            var pushedAt = DateTime.UtcNow;

            if (isNewVault)
            {
                dbContext.Vaults.Add(new Data.Entities.Vault
                {
                    VaultId = vaultId,
                    Name = request.VaultName.Trim(),
                    Path = vaultDir,
                    IsActive = true,
                    Industry = industry,
                    Source = "mobile",
                    PushedByDeviceId = pushedByDeviceId,
                    PushedByDeviceName = pushedByDeviceName,
                    PushedAt = pushedAt,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
                await dbContext.SaveChangesAsync();
            }
            else
            {
                existingVault!.UpdatedAt = DateTime.UtcNow;
                existingVault.PushedByDeviceId = pushedByDeviceId;
                existingVault.PushedByDeviceName = pushedByDeviceName;
                existingVault.PushedAt = pushedAt;
                if (migrated)
                {
                    existingVault.Path = vaultDir;
                }
                await dbContext.SaveChangesAsync();
            }

            _logger.LogInformation("移动端知识库推送成功: {VaultId} {VaultName}，共 {NoteCount} 条笔记",
                vaultId, request.VaultName, request.Notes.Count);

            return Ok(new { success = true, vaultId, message = migrated ? _loc["Vault_PushSuccessMigrated"].Value : _loc["Vault_PushSuccess"].Value });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "移动端知识库推送失败: {VaultName}", request.VaultName);
            return StatusCode(500, new { error = _loc["Vault_PushFailed", ex.Message].Value });
        }
    }

    private class MobileCardItem
    {
        public JsonElement Front { get; set; }
        public JsonElement Back { get; set; }
        public string Deck { get; set; } = "";
        public List<string> Tags { get; set; } = new();
        public string Source { get; set; } = "";
    }
}
