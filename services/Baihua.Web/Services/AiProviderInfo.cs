namespace Baihua.Web.Services;

/// <summary>AI 模型信息</summary>
public class AiModelInfo
{
    public string Name { get; set; } = "";

    public bool IsMain { get; set; }
}

/// <summary>后端 GET /api/assistant/providers 返回项（与 Baihua DTO 字段对齐）。</summary>
public class AiProviderInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsMain { get; set; }
    public List<AiModelInfo> Models { get; set; } = new();
}

/// <summary>编程 Agent 可选提供方（页面下拉用）。</summary>
public class CodeAgentProviderInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public bool IsMain { get; set; }
    public List<AiModelInfo> Models { get; set; } = new();
}
