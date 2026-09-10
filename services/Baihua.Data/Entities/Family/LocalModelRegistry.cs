namespace Baihua.Data.Entities;

/// <summary>
/// 本地大模型注册表（Agent 经百花 MCP 写入，WebUI 只读展示）。
/// 唯一事实来源：百花不再探测本机模型，模型信息由 DSH/OpenClaw 等 Agent
/// 诊断本机环境、安装并启动推理服务后，调用 baihua_local_model_register 登记。
/// </summary>
public class LocalModelRegistry
{
    public int Id { get; set; }
    public string Tool { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string? ParameterSize { get; set; }
    public string? Quantization { get; set; }
    public string? Usage { get; set; }
    public long? SizeBytes { get; set; }
    public string? Capabilities { get; set; }
    public string? Notes { get; set; }
    public string RegisteredBy { get; set; } = "";
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}