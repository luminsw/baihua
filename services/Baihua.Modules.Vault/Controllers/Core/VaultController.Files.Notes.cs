using Baihua.Core.Modules;
using Microsoft.AspNetCore.Mvc;
using Baihua.Contracts.Vaults;

namespace Baihua.Modules.Vault.Controllers;

public partial class VaultController
{
    /// <summary>
    /// 读取笔记内容（WebUI / 移动端 / MCP 共用同一实现：VaultQueryService）
    /// </summary>
    [HttpGet("read/{*path}")]
    public async Task<ActionResult<VaultNoteResponse>> ReadNote(string path, [FromQuery] string vaultId)
    {
        try
        {
            return Ok(await _vaultQuery.ReadNoteAsync(path, vaultId, HttpContext.RequestAborted));
        }
        catch (VaultQueryException ex)
        {
            return MapVaultError(ex);
        }
    }

    /// <summary>
    /// 写入笔记内容（WebUI 编辑用）。
    /// 统一写入 notes/ 子目录；兼容传入带 notes/ 前缀的路径。
    /// </summary>
    [HttpPost("write/{*path}")]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10MB 限制，防止 DoS
    public async Task<IActionResult> WriteNote(string path, [FromQuery] string vaultId, [FromBody] WriteNoteRequest request)
    {
        try
        {
            await _vaultQuery.WriteNoteAsync(path, vaultId, request?.Content!, HttpContext.RequestAborted);
            return Ok(new { success = true });
        }
        catch (VaultQueryException ex)
        {
            return MapVaultError(ex);
        }
    }

    /// <summary>把知识库操作异常映射为原有 HTTP 语义（400/404/500 + { error }）</summary>
    private ActionResult MapVaultError(VaultQueryException ex) => ex.Code switch
    {
        VaultErrorCode.NoteNotFound => NotFound(new { error = ex.Message }),
        VaultErrorCode.Failure => StatusCode(500, new { error = ex.Message }),
        _ => BadRequest(new { error = ex.Message })
    };
}
