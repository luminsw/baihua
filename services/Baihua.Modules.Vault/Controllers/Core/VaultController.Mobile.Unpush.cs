using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Baihua.Data;

namespace Baihua.Modules.Vault.Controllers;

/// <summary>
/// 移动端知识库推送标记解除。
/// </summary>
public partial class VaultController
{
    /// <summary>
    /// 解除移动端推送标记（方案B：移动端删除本地知识库时调用，
    /// 清除服务端 PushedByDeviceId，使"已推送"标签不再残留。
    /// 仅当 PushedByDeviceId 与请求设备匹配时清除，保护其他设备的推送标记。）
    /// </summary>
    [HttpPost("/mobile-vaults/unpush")]
    public async Task<ActionResult> UnpushMobileVault([FromBody] UnpushMobileVaultRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.VaultId) || string.IsNullOrWhiteSpace(request.DeviceId))
        {
            return BadRequest(new { error = _loc["Vault_UnpushInvalidRequest"].Value });
        }

        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
            var vault = await dbContext.Vaults
                .FirstOrDefaultAsync(v => v.VaultId == request.VaultId);

            if (vault == null)
            {
                // 库不存在视为成功（幂等）
                _logger.LogInformation("[UnpushMobileVault] Vault not found (idempotent ok): {VaultId}", request.VaultId);
                return Ok(new { success = true, cleared = false });
            }

            if (vault.PushedByDeviceId != request.DeviceId)
            {
                // 推送标记属于其他设备，不清除（保护他人标记）
                _logger.LogInformation("[UnpushMobileVault] Vault pushed by another device, skip: {VaultId}", request.VaultId);
                return Ok(new { success = true, cleared = false });
            }

            vault.PushedByDeviceId = "";
            vault.PushedByDeviceName = "";
            vault.PushedAt = null;
            await dbContext.SaveChangesAsync();

            _logger.LogInformation("[UnpushMobileVault] Cleared push marker: {VaultId} by {DeviceId}", request.VaultId, request.DeviceId);
            return Ok(new { success = true, cleared = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[UnpushMobileVault] Failed for vault {VaultId}", request?.VaultId);
            return StatusCode(500, new { error = _loc["Vault_UnpushFailed", ex.Message].Value });
        }
    }
}

/// <summary>
/// 解除推送标记请求体
/// </summary>
public class UnpushMobileVaultRequest
{
    public string VaultId { get; set; } = "";
    public string DeviceId { get; set; } = "";
}
