using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace Baihua.Modules.Vault.Controllers;

public partial class SearchController
{

        #region FTS5 Index Management

        /// <summary>
        /// 重建指定知识库的向量索引（纯向量检索前提，生成全部笔记 embedding）
        /// </summary>
        [HttpPost("embedding-reindex")]
        public async Task<ActionResult> EmbeddingReindex([FromBody] ReindexRequest request)
        {
            try
            {
                var vaultPath = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == request.VaultId)?.Path;
                if (string.IsNullOrEmpty(vaultPath) || !Directory.Exists(vaultPath))
                {
                    return BadRequest(new { error = _loc["Vault_PathInvalid"].Value });
                }

                _logger.LogInformation("开始重建知识库 {VaultId} 的向量索引", request.VaultId);
                var (indexed, failed) = await _embeddingService.IndexVaultAsync(request.VaultId, vaultPath, HttpContext.RequestAborted);

                return Ok(new { success = true, indexed, failed });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重建向量索引失败");
                return StatusCode(500, new { error = "重建向量索引失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 获取知识库向量索引状态
        /// </summary>
        [HttpGet("embedding-index-status")]
        public async Task<ActionResult> EmbeddingIndexStatus([FromQuery] string vaultId)
        {
            try
            {
                var count = await _embeddingService.GetIndexedCountAsync(vaultId);
                return Ok(new { vaultId, indexedCount = count, hasIndex = count > 0 });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取向量索引状态失败");
                return StatusCode(500, new { error = "获取向量索引状态失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 重建指定知识库的 FTS5 全文索引
        /// </summary>
        [HttpPost("reindex")]
        public async Task<ActionResult> Reindex([FromBody] ReindexRequest request)
        {
            try
            {
                var vaultPath = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == request.VaultId)?.Path;
                if (string.IsNullOrEmpty(vaultPath) || !Directory.Exists(vaultPath))
                {
                    return BadRequest(new { error = _loc["Vault_PathInvalid"].Value });
                }

                _logger.LogInformation("开始重建知识库 {VaultId} 的 FTS5 索引", request.VaultId);
                await _vaultNoteIndexer.IndexVaultAsync(request.VaultId, vaultPath, HttpContext.RequestAborted);

                return Ok(new { success = true, message = _loc["Vault_Fts5Reindexed", request.VaultId].Value });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重建 FTS5 索引失败");
                return StatusCode(500, new { error = _loc["Search_ReindexFailed"], message = ex.Message });
            }
        }

        /// <summary>
        /// 获取知识库 FTS5 索引状态
        /// </summary>
        [HttpGet("index-status")]
        public async Task<ActionResult> IndexStatus([FromQuery] string vaultId)
        {
            try
            {
                var (count, _) = await _vaultNoteIndexer.GetIndexStatsAsync(vaultId, HttpContext.RequestAborted);
                return Ok(new { vaultId, indexedCount = count, hasIndex = count > 0 });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取 FTS5 索引状态失败");
                return StatusCode(500, new { error = _loc["Search_StatusFailed"], message = ex.Message });
            }
        }

        #endregion
}
