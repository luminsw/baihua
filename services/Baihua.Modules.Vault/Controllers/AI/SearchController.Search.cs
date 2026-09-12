using Baihua.Core.Modules;
using Microsoft.AspNetCore.Mvc;

namespace Baihua.Modules.Vault.Controllers;

public partial class SearchController
{
    /// <summary>知识库检索（/api/search）：检索逻辑在 VaultQueryService，控制器只做 HTTP 映射</summary>
    [HttpGet]
    public async Task<ActionResult> Search([FromQuery] string q = "", [FromQuery] string vaultId = "")
    {
        try
        {
            var outcome = await _vaultQuery.SearchAsync(q, vaultId, HttpContext.RequestAborted);
            return Ok(new { results = outcome.Results, status = outcome.Status });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "搜索失败");
            return StatusCode(500, new { error = _loc["Common_SearchFailed"], message = ex.Message });
        }
    }
}
