using System.Text.Json;
using Baihua.Core.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Baihua.Modules.Family.Controllers;

/// <summary>
/// AI 函数（工具）调用端点：供 AI 模块的本地模型工具复用家庭模块的工具实现（同进程直调）
/// </summary>
public partial class AIController
{
    public class AiFunctionCallRequest
    {
        public string Tool { get; set; } = "";
        public JsonElement? Arguments { get; set; }
    }

    /// <summary>
    /// 按名调用 AI 工具（search_vault / list_vaults / create_note / get_system_status / get_current_date）
    /// </summary>
    [HttpPost("functions/call")]
    public async Task<ActionResult<object>> CallAiFunction([FromBody] AiFunctionCallRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Tool))
        {
            return BadRequest(new { error = "tool is required" });
        }

        try
        {
            var args = request.Arguments ?? JsonDocument.Parse("{}").RootElement;
            string GetString(string name)
            {
                return args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var p)
                    ? p.GetString() ?? ""
                    : "";
            }

            string result = request.Tool switch
            {
                "search_vault" => await _aiFunctionService.SearchVaultAsync(GetString("query")),
                "list_vaults" => await _aiFunctionService.ListVaultsAsync(),
                "create_note" => await _aiFunctionService.CreateNoteAsync(GetString("title"), GetString("content")),
                "get_system_status" => await _aiFunctionService.GetSystemStatusAsync(),
                "get_current_date" => await _aiFunctionService.GetCurrentDateAsync(),
                _ => $"未知工具 {request.Tool}（可用：search_vault, list_vaults, create_note, get_system_status, get_current_date）"
            };

            return Ok(new { result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI 函数调用失败: {Tool}", request.Tool);
            return Ok(new { result = $"工具 {request.Tool} 执行失败: {ex.Message}" });
        }
    }
}
