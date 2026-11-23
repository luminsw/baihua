namespace Baihua.Contracts.ComputePool;

/// <summary>
/// 百花局域网算力池 —— 对端服务器能力广播（/mg/capabilities，X-Server-Token 鉴权）。
/// 每台百花机器汇报自己的 AI 提供方/模型/算力，供其他机器发现并选用（打通物理壁垒）。
/// </summary>
public class ComputeNodeCapabilitiesDto
{
    public string ServerId { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>本机对外入口（如 http://192.168.3.13）</summary>
    public string HostUrl { get; set; } = "";

    /// <summary>
    /// 本机对外暴露的 OpenAI 兼容推理端点（对端注册提供方时填 AiBaseUrl）。
    /// 为空表示本机暂不对外提供推理（未配置 BAIHUA_PUBLIC_OPENAI_BASE_URL）。
    /// </summary>
    public string OpenAiBaseUrl { get; set; } = "";

    public List<ComputeProviderDto> Providers { get; set; } = new();

    public string? GpuName { get; set; }
    public double? GpuVramGb { get; set; }
    public int? CpuCores { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>该节点模型商店（可拉取的已下载模型）</summary>
    public List<ModelStoreEntryDto> ModelStore { get; set; } = new();

    /// <summary>该节点的本地绘图能力（文生图/文生视频，ComfyUI）。空 = 不对外提供绘图。</summary>
    public DrawCapabilityDto? Draw { get; set; }
}

public class DrawCapabilityDto
{
    /// <summary>本机 ComfyUI 是否在线（绘图前置条件）。</summary>
    public bool ComfyOnline { get; set; }

    /// <summary>是否支持文生图。</summary>
    public bool Image { get; set; }

    /// <summary>是否支持文生视频。</summary>
    public bool Video { get; set; }

    /// <summary>出图 checkpoint 名（可为空）。</summary>
    public string? ImageCheckpoint { get; set; }

    /// <summary>出视频 checkpoint 名（可为空）。</summary>
    public string? VideoCheckpoint { get; set; }

    /// <summary>出图 checkpoint 全集（CheckpointLoaderSimple）。</summary>
    public List<string> ImageCheckpoints { get; set; } = new();

    /// <summary>出视频 checkpoint 全集（含 LTX/Wan/Hunyuan 等视频类 checkpoint）。</summary>
    public List<string> VideoCheckpoints { get; set; } = new();

    /// <summary>Z-Image-Turbo 可用 UNet 模型（UNETLoader）。</summary>
    public List<string> UnetModels { get; set; } = new();

    /// <summary>Z-Image-Turbo 可用 CLIP 文本编码器（CLIPLoader）。</summary>
    public List<string> ClipModels { get; set; } = new();

    /// <summary>Z-Image-Turbo 可用 VAE（VAELoader）。</summary>
    public List<string> VaeModels { get; set; } = new();
}

public class ComputeProviderDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Tier { get; set; } = "Tier2";
    public List<ComputeModelDto> Models { get; set; } = new();
}

public class ComputeModelDto
{
    public string Name { get; set; } = "";
    public bool IsMain { get; set; }
    public int? ContextWindow { get; set; }

    /// <summary>实测 token/s（来自本机 ModelBenchmark 最近结果；无则 null）</summary>
    public double? TokensPerSecond { get; set; }
}

/// <summary>算力池深度任务请求（POST /api/compute-pool/chat）</summary>
public class PoolChatRequest
{
    public string ModelName { get; set; } = "";
    public string Prompt { get; set; } = "";
}

/// <summary>算力池总览节点（WebUI /compute 用）</summary>
public class ComputePoolNodeDto
{
    public string ServerId { get; set; } = "";

    /// <summary>对端登记 ID（非本机节点；算力池页用于删除过期的对端登记）。</summary>
    public Guid? PeerId { get; set; }

    public string Name { get; set; } = "";
    public string HostUrl { get; set; } = "";
    public string OpenAiBaseUrl { get; set; } = "";
    public bool IsLocal { get; set; }
    public bool Online { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public string? GpuName { get; set; }
    public double? GpuVramGb { get; set; }
    public int? CpuCores { get; set; }
    public List<ComputeProviderDto> Providers { get; set; } = new();

    /// <summary>该节点的本地绘图能力（文生图/文生视频）。空 = 不对外提供绘图。</summary>
    public DrawCapabilityDto? Draw { get; set; }

    /// <summary>本机是否已自动注册该节点的提供方（可选用）</summary>
    public bool ProviderRegistered { get; set; }

    /// <summary>该节点模型商店（可拉取的已下载模型）</summary>
    public List<ModelStoreEntryDto> ModelStore { get; set; } = new();
}

public class ComputePoolViewDto
{
    public List<ComputePoolNodeDto> Nodes { get; set; } = new();
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class SelectComputeModelRequest
{
    public string ServerId { get; set; } = "";
    public string ModelName { get; set; } = "";
}

/// <summary>跨机测速请求（POST /mg/benchmark/run，X-Server-Token 鉴权）</summary>
public class PeerBenchmarkRequest
{
    public string ModelName { get; set; } = "";
    /// <summary>提示词分类（"tcm"/"coding"，默认 tcm），取该分类第一个提示快速测速</summary>
    public string? Category { get; set; }
}

/// <summary>单模型快速测速结果</summary>
public class BenchmarkRunResultDto
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string ModelName { get; set; } = "";
    public double TokensPerSecond { get; set; }
    public double LatencyMs { get; set; }
    public DateTime TestedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>局域网模型商店条目（对端可拉取的已下载模型）</summary>
public class ModelStoreEntryDto
{
    public string Name { get; set; } = "";
    public long SizeBytes { get; set; }
    public int FileCount { get; set; }
}

/// <summary>从对端拉取模型（POST /api/compute-pool/pull-model）</summary>
public class PullModelRequest
{
    public string ServerId { get; set; } = "";
    public string ModelName { get; set; } = "";
}

/// <summary>跨机模型布署请求（POST /api/compute-pool/deploy）</summary>
public class DeployModelRequest
{
    public string ServerId { get; set; } = "";
    public string ModelName { get; set; } = "";

    /// <summary>目标设备（GPU/CPU/AUTO，默认 GPU；GPU 失败自动回退 CPU）</summary>
    public string? Device { get; set; }
}

/// <summary>跨机模型布署结果（对端拉取 + 启动运行时后返回）</summary>
public class DeployModelResultDto
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string ModelName { get; set; } = "";
    /// <summary>实际使用设备（GPU/CPU…）</summary>
    public string Device { get; set; } = "";
    public int Port { get; set; }
    public string Endpoint { get; set; } = "";
    public DateTime DeployedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>对端布署请求（POST /mg/model-store/deploy，X-Server-Token 鉴权）</summary>
public class PeerDeployRequest
{
    /// <summary>来源服务器 ID（对端据此查该机的互联口令来拉取模型）</summary>
    public string SourceServerId { get; set; } = "";

    /// <summary>模型 tar 下载地址（必须指向来源服务器的 /mg/model-store/download/{name}）</summary>
    public string SourceUrl { get; set; } = "";

    public string ModelName { get; set; } = "";

    /// <summary>目标设备（GPU/CPU/AUTO，默认 GPU；GPU 失败自动回退 CPU）</summary>
    public string? Device { get; set; }
}
