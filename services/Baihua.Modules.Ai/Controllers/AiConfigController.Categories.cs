using Baihua.Contracts.Ai;
using Microsoft.AspNetCore.Mvc;

namespace Baihua.Modules.Ai.Controllers;

public partial class AiConfigController
{
    /// <summary>
    /// 获取任务分类模型配置：分类定义 + 显式指派 + 各分类当前生效解析。
    /// </summary>
    [HttpGet("categories")]
    public ActionResult<AiCategoryConfigDto> GetCategories()
    {
        var providers = _aiSettings.GetAiProviders();
        var assignments = _categorySettings.GetAssignments();

        var resolved = AiTaskCategory.All
            .Select(cat =>
            {
                var (providerId, modelName, fromAssignment) = _categorySettings.Resolve(cat, providers);
                var provider = providers.FirstOrDefault(p => p.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
                return new AiCategoryResolutionDto
                {
                    Category = cat,
                    ProviderId = providerId,
                    ProviderName = provider?.Name ?? "",
                    ModelName = modelName,
                    FromAssignment = fromAssignment
                };
            })
            .ToList();

        return Ok(new AiCategoryConfigDto
        {
            Categories = Baihua.Core.Services.AiCategorySettingsService.GetDefinitions(),
            Assignments = assignments,
            Resolved = resolved
        });
    }

    /// <summary>
    /// 保存任务分类模型指派（每类一个模型配置）。
    /// </summary>
    [HttpPut("categories")]
    public IActionResult SaveCategories([FromBody] SaveAiCategoriesRequest request)
    {
        try
        {
            _categorySettings.SaveAssignments(request?.Assignments ?? new List<AiCategoryAssignmentDto>());
            _aiSettings.ClearAiProvidersCache();
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存任务分类指派失败");
            return StatusCode(500, new { error = $"保存失败: {ex.Message}" });
        }
    }
}
