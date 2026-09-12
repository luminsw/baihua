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
    /// 获取预设的知名 AI 提供商列表。
    ///
    /// 只保留两个：`自定义`（OpenAI 兼容，手动填 BaseUrl/模型/Key）与 `DeepSeek`（官方）。
    /// 其它厂商（智谱/火山/阿里/Kimi/Ollama/LM Studio）不做预置 —— 需要时用「自定义」手动填，
    /// 避免预置里的模型名/地址随时间过期，也免去逐家维护。
    /// </summary>
    [HttpGet("presets")]
    public ActionResult<List<AiProviderPreset>> GetPresets()
    {
        var presets = new List<AiProviderPreset>
        {
            // 手动填（OpenAI 兼容）：本地 Ollama / LM Studio / 任何兼容端点都走这条
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
            // 官方 DeepSeek
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
            }
        };

        return Ok(presets);
    }
}
