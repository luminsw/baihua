using Microsoft.Extensions.Options;

namespace Baihua.AI.Provider.OpenVino;

/// <summary>
/// OpenVINO Model Server (OVMS) 端点配置。
///
/// 百花本地 OpenVINO 推理已统一由 Intel OVMS 常驻服务提供，不再启动自研
/// Python 服务。所有 LLM 文本对话 / 视觉识别 / 嵌入请求均路由到 OVMS 的
/// OpenAI 兼容 REST 端点（/v3/chat/completions、/v3/embeddings、/v1/models）。
/// </summary>
public class OmsOptions
{
    /// <summary>功能开关（默认开启）</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>OVMS REST 基地址（默认本机 8000）</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8000";
}

