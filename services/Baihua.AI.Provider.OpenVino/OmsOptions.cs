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

/// <summary>
/// OVMS 模型 id 映射助手：把百花内部使用的 OpenVINO 模型标识
/// （视觉 "3b"/"7b"、显示名、或 LLM 模型目录名）映射为 OVMS config.json 里注册的 model id。
/// </summary>
public static class OmsModelMap
{
    /// <summary>OVMS config.json 中已知的全部模型 id（供 DisplayName 回查等）</summary>
    public static readonly string[] KnownOmsIds =
    {
        "qwen3.5-4b", "qwen3-embedding-0.6b",
        "qwen2.5-vl-7b", "qwen2.5-vl-3b", "qwen2.5", "qwen2.5-14b",
        "qwen2.5-coder-7b", "qwen3.5-9b", "biancang", "bge-small-zh",
    };

    /// <summary>
    /// 百花视觉内部 Id → OVMS 模型 id。
    /// 视觉模型已由 Qwen2.5-VL-7B 切换为 Qwen3.5-9B（int8，含视觉/文本嵌入，
    /// 2026-08-29 实测视觉可用）；VL-7B/VL-3B 目录已清理以释放磁盘。
    /// </summary>
    public static string VisionModelId(string internalId)
    {
        return "qwen3.5-4b";
    }

    /// <summary>LLM 对话模型 id（百花文本推理后端）——统一指向 OVMS 的对话模型</summary>
    public static string ChatModelId => "qwen3.5-4b";

    /// <summary>嵌入模型 id（RAG）</summary>
    public static string EmbeddingModelId => "qwen3-embedding-0.6b";

    /// <summary>本地模型目录名 → OVMS 注册模型 id（用于把 OVMS 注册状态合并到目录页）</summary>
    public static string? OmsIdForDirName(string dirName) => dirName switch
    {
        "Qwen3.5-4B-int4-ov" => "qwen3.5-4b",
        "Qwen3-Embedding-0.6B-int8-ov" => "qwen3-embedding-0.6b",
        "Qwen2.5-VL-7B-Instruct-int4-ov" => "qwen2.5-vl-7b",
        "Qwen2.5-VL-3B-Instruct-int4-ov" => "qwen2.5-vl-3b",
        "Qwen2.5-7B-Instruct-int4-ov" => "qwen2.5",
        "Qwen2.5-14B-Instruct-INT4-OV" => "qwen2.5-14b",
        "Qwen2.5-Coder-7B-Instruct-int4-ov" => "qwen2.5-coder-7b",
        "Qwen3.5-9B-int8-ov" => "qwen3.5-9b",
        "BianCang-Qwen2.5-7B-Instruct" => "biancang",
        "bge-small-zh-v1.5" => "bge-small-zh",
        _ => null
    };

    /// <summary>OVMS 注册模型 id → 本地模型目录名（用于按目录估算模型大小/安装状态）</summary>
    public static string? DirNameForOmsId(string omsId) => omsId switch
    {
        "qwen3.5-4b" => "Qwen3.5-4B-int4-ov",
        "qwen3-embedding-0.6b" => "Qwen3-Embedding-0.6B-int8-ov",
        "qwen2.5-vl-7b" => "Qwen2.5-VL-7B-Instruct-int4-ov",
        "qwen2.5-vl-3b" => "Qwen2.5-VL-3B-Instruct-int4-ov",
        "qwen2.5" => "Qwen2.5-7B-Instruct-int4-ov",
        "qwen2.5-14b" => "Qwen2.5-14B-Instruct-INT4-OV",
        "qwen2.5-coder-7b" => "Qwen2.5-Coder-7B-Instruct-int4-ov",
        "qwen3.5-9b" => "Qwen3.5-9B-int8-ov",
        "biancang" => "BianCang-Qwen2.5-7B-Instruct",
        "bge-small-zh" => "bge-small-zh-v1.5",
        _ => null
    };

    /// <summary>
    /// 状态探测：查询 OVMS 是否已注册/可用某模型（懒加载，首次推理时才真正编译加载）。
    /// 返回该模型是否在 /v1/models 或 /v1/models/{id} 可见。
    /// </summary>
    public static bool IsAvailableJsonAvailable(System.Text.Json.JsonElement root)
    {
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return true;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.Array)
            return false;
        return data.GetArrayLength() > 0;
    }
}
