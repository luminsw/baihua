using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Modules.Family.Services;
using Microsoft.AspNetCore.Mvc;
using Baihua.Contracts.Ai;

namespace Baihua.Modules.Family.Controllers
{
    public partial class AIController
    {
        [HttpGet("prompt-templates")]
        public ActionResult<List<PromptTemplateDto>> GetPromptTemplates()
        {
            var templates = _scenePromptService.GetAllTemplates();
            return Ok(templates.Select(t => new PromptTemplateDto
            {
                DisplayName = t.DisplayName,
                ChatSystemPrompt = t.ChatSystemPrompt,
                SplitSystemPrompt = t.SplitSystemPrompt,
                SplitUserPrompt = t.SplitUserPrompt,
                SupplementUserPrompt = t.SupplementUserPrompt,
                DefaultCategories = t.DefaultCategories,
                ConfigurationAdvice = t.ConfigurationAdvice,
                KnowledgeBuildPlaceholder = t.KnowledgeBuildPlaceholder,
                KnowledgeBuildDescription = t.KnowledgeBuildDescription
            }).ToList());
        }

        [HttpPut("prompt-templates/{displayName}")]
        public IActionResult UpdatePromptTemplate(string displayName, [FromBody] PromptTemplateDto dto)
        {
            if (string.IsNullOrWhiteSpace(displayName))
                return BadRequest(new { error = _loc["AiChat_PromptNameEmpty"] });

            var template = new DefaultPromptProvider.PromptTemplate
            {
                DisplayName = displayName,
                ChatSystemPrompt = dto.ChatSystemPrompt ?? "",
                SplitSystemPrompt = dto.SplitSystemPrompt ?? "",
                SplitUserPrompt = dto.SplitUserPrompt ?? "",
                SupplementUserPrompt = dto.SupplementUserPrompt ?? "",
                DefaultCategories = dto.DefaultCategories ?? new List<string>(),
                ConfigurationAdvice = dto.ConfigurationAdvice ?? "",
                KnowledgeBuildPlaceholder = dto.KnowledgeBuildPlaceholder ?? "",
                KnowledgeBuildDescription = dto.KnowledgeBuildDescription ?? ""
            };

            _scenePromptService.SaveTemplate(template);
            return Ok(new { success = true });
        }

        [HttpPost("prompt-templates")]
        public IActionResult CreatePromptTemplate([FromBody] PromptTemplateDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.DisplayName))
                return BadRequest(new { error = _loc["AiChat_PromptNameEmpty"] });

            var template = new DefaultPromptProvider.PromptTemplate
            {
                DisplayName = dto.DisplayName,
                ChatSystemPrompt = dto.ChatSystemPrompt ?? "",
                SplitSystemPrompt = dto.SplitSystemPrompt ?? "",
                SplitUserPrompt = dto.SplitUserPrompt ?? "",
                SupplementUserPrompt = dto.SupplementUserPrompt ?? "",
                DefaultCategories = dto.DefaultCategories ?? new List<string>(),
                ConfigurationAdvice = dto.ConfigurationAdvice ?? "",
                KnowledgeBuildPlaceholder = dto.KnowledgeBuildPlaceholder ?? "",
                KnowledgeBuildDescription = dto.KnowledgeBuildDescription ?? ""
            };

            _scenePromptService.SaveTemplate(template);
            return Ok(new { success = true });
        }

        [HttpDelete("prompt-templates/{displayName}")]
        public IActionResult DeletePromptTemplate(string displayName)
        {
            if (string.Equals(displayName, "通用", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = _loc["AiChat_DefaultTemplateProtected"] });

            var deleted = _scenePromptService.DeleteTemplate(displayName);
            return deleted ? Ok(new { success = true }) : NotFound(new { error = _loc["AiChat_TemplateNotFound"] });
        }
    }
}