using Baihua.Core.Notifications;
using Microsoft.AspNetCore.Mvc;
using Baihua.Data.Entities;
using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Core.Security;
using Baihua.Contracts.Ai;

namespace Baihua.Modules.Ai.Controllers;

public partial class AiConfigController
{
    /// <summary>
    /// 获取预设的知名 AI 提供商列表
    /// </summary>
    [HttpGet("presets")]
    public ActionResult<List<AiProviderPreset>> GetPresets()
    {
        var presets = new List<AiProviderPreset>
        {
            new()
            {
                Id = "custom",
                Name = _loc["AiConfig_PresetCustom"],
                BaseUrl = "",
                Models = new()
                {
                    new() { Name = "", IsMain = true }
                }
            },
            new()
            {
                Id = "zhipu",
                Name = _loc["AiConfig_PresetZhipu"],
                BaseUrl = "https://open.bigmodel.cn/api/paas/v4",
                Models = new()
                {
                    new() { Name = "glm-4-plus", IsMain = true },
                    new() { Name = "glm-4-flash", IsMain = false },
                    new() { Name = "glm-4-air", IsMain = false },
                    new() { Name = "glm-4-long", IsMain = false }
                }
            },
            new()
            {
                Id = "volcano",
                Name = _loc["AiConfig_PresetVolcano"],
                BaseUrl = "https://ark.cn-beijing.volces.com/api/v3",
                Models = new()
                {
                    new() { Name = "doubao-seed-1-6-251015", IsMain = true },
                    new() { Name = "doubao-1-5-pro-256k-250815", IsMain = false },
                    new() { Name = "deepseek-r1-250528", IsMain = false },
                    new() { Name = "deepseek-v3-250528", IsMain = false }
                }
            },
            new()
            {
                Id = "aliyun",
                Name = _loc["AiConfig_PresetAliyun"],
                BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1",
                Models = new()
                {
                    new() { Name = "qwen3.7-plus", IsMain = true },
                    new() { Name = "qwen3.7-max", IsMain = false },
                    new() { Name = "qwen3.7-flash", IsMain = false },
                    new() { Name = "deepseek-v3", IsMain = false },
                    new() { Name = "deepseek-r1", IsMain = false }
                }
            },
            new()
            {
                Id = "deepseek",
                Name = _loc["AiConfig_PresetDeepSeek"],
                BaseUrl = "https://api.deepseek.com",
                AnthropicBaseUrl = "https://api.deepseek.com/anthropic",
                Models = new()
                {
                    new() { Name = "deepseek-v4-pro", IsMain = true },
                    new() { Name = "deepseek-v4-flash", IsMain = false }
                }
            },
            new()
            {
                Id = "kimi",
                Name = _loc["AiConfig_PresetKimi"],
                BaseUrl = "https://api.moonshot.cn/v1",
                Models = new()
                {
                    new() { Name = "kimi-k3", IsMain = true },
                    new() { Name = "kimi-k2.7-code", IsMain = false },
                    new() { Name = "kimi-k2.6", IsMain = false }
                }
            },
            new()
            {
                Id = "ollama",
                Name = _loc["AiConfig_PresetLocalOllama"],
                BaseUrl = "http://localhost:11434/v1",
                Tier = AiModelTier.Tier2_Local,
                Models = new()
                {
                    new() { Name = "qwen3:14b", IsMain = true },
                    new() { Name = "deepseek-r1:14b", IsMain = false },
                    new() { Name = "llama3.2:latest", IsMain = false }
                }
            },
            new()
            {
                Id = "lmstudio",
                Name = _loc["AiConfig_PresetLocalLmStudio"],
                BaseUrl = "http://localhost:1234/v1",
                Tier = AiModelTier.Tier2_Local,
                Models = new()
                {
                    new() { Name = "loaded-model", IsMain = true }
                }
            }
        };

        // 根据机器能力过滤本地 Provider 预设
        if (!_capabilityService.CanUse(Baihua.Core.Services.LocalComputeFeature.AiConfigLocalProviderPresets))
        {
            presets = presets.Where(p =>
                !p.Id.Equals("ollama", StringComparison.OrdinalIgnoreCase) &&
                !p.Id.Equals("lmstudio", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return Ok(presets);
    }
}
