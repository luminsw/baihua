using Baihua.Contracts.Ai;

namespace Baihua.Core.Models
{
    /// <summary>
    /// AI 模型配置项（appsettings.json 中 Models 数组的格式）
    /// </summary>
    public class AiModelConfig
    {
        /// <summary>
        /// 模型名称
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>

        /// 是否为主模型（默认使用）
        /// </summary>
        public bool IsMain { get; set; }

        /// <summary>
        /// 任务分类（chat/reasoning/code/vision），用于按用途路由模型。
        /// 序列化进 ModelsJson，旧数据缺省为 chat。
        /// </summary>
        public string Category { get; set; } = Baihua.Contracts.Ai.AiTaskCategory.Chat;
    }

    /// <summary>
    /// appsettings.json 中 Ai 数组的单项（非密钥字段）。
    /// </summary>
    public class AiProviderConfig
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string AiBaseUrl { get; set; } = "";

        /// <summary>
        /// Anthropic 协议 Base URL（可选，用于 OpenAI 返回为空时 fallback）
        /// </summary>
        public string? AnthropicBaseUrl { get; set; }

        /// <summary>
        /// 模型列表数组，支持显式配置 IsPaid、IsMain
        /// </summary>
        public List<AiModelConfig> Models { get; set; } = new();

        /// <summary>
        /// 是否为主提供方
        /// </summary>
        public bool IsMain { get; set; }

        /// <summary>
        /// 模型层级：Tier1=固本(Embedding), Tier2=问道(本地大模型), Tier3=通玄(云端超大模型)
        /// </summary>
        public AiModelTier Tier { get; set; }

        /// <summary>
        /// 获取模型列表
        /// </summary>
        public List<AiModelConfig> GetModelOptions()
        {
            return Models ?? new List<AiModelConfig>();
        }

        /// <summary>
        /// 获取主模型名称
        /// </summary>
        public string GetMainModel()
        {
            var options = GetModelOptions();
            // 优先找标记为 IsMain 的模型
            var mainModel = options.FirstOrDefault(m => m.IsMain);
            return mainModel?.Name ?? options.FirstOrDefault()?.Name ?? "";
        }
    }
}
