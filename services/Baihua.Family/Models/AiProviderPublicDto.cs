namespace Baihua.Family.Models
{
    /// <summary>
    /// AI 模型信息（返回给前端）
    /// </summary>
    public class AiModelPublicDto
    {
        public string Name { get; set; } = "";

        public bool IsMain { get; set; }
    }

    /// <summary>
    /// 返回给前端的 AI 配置（不含密钥与 BaseUrl）。
    /// </summary>
    public class AiProviderPublicDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsMain { get; set; }
        public List<AiModelPublicDto> Models { get; set; } = new();
    }
}
