namespace Baihua.Contracts.OpenClaw;

public class CreateOpenClawTaskRequest
{
    public string Prompt { get; set; } = "";
}

public class OpenClawTaskDto
{
    public int Id { get; set; }
    public string TaskId { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Status { get; set; } = "";
    public string? ReportPath { get; set; }
    public string? Result { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>OpenClaw 本地 AI 单个 Provider 配置</summary>
public class OpenClawLocalProviderConfigDto
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "";
    public string ApiType { get; set; } = "openai-completions";
    public List<OpenClawLocalModelDto> Models { get; set; } = new();
}

/// <summary>llama.cpp 配置</summary>
public class OpenClawLlamaCppConfigDto
{
    public bool Enabled { get; set; }
    public string BinaryPath { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public string BaseUrl { get; set; } = "http://localhost:8080";
    public int Port { get; set; } = 8080;
    public int NGpuLayers { get; set; } = 999;
    public int ContextSize { get; set; } = 4096;
    public string ApiType { get; set; } = "openai-completions";

    // 预定义启动参数（降低使用门槛，勾选即生效）
    /// <summary>Flash Attention（推荐 Intel Arc / 现代 GPU 启用）</summary>
    public bool UseFlashAttn { get; set; }
    /// <summary>内存锁定 --mlock（避免模型被换出到 swap）</summary>
    public bool UseMlock { get; set; }
    /// <summary>禁用内存映射 --no-mmap（某些系统更稳定）</summary>
    public bool UseNoMmap { get; set; }

    /// <summary>CPU 推理线程数（-t），0=自动使用全部核心</summary>
    public int Threads { get; set; }
    /// <summary>推理批处理大小（-b），默认 2048，越大吞吐量越高</summary>
    public int BatchSize { get; set; } = 2048;
    /// <summary>KV Cache K 量化类型（--cache-type-k），显存紧张时选 q8_0 或 q4_0</summary>
    public string CacheTypeK { get; set; } = "";
    /// <summary>KV Cache V 量化类型（--cache-type-v），显存紧张时选 q8_0 或 q4_0</summary>
    public string CacheTypeV { get; set; } = "";
    /// <summary>连续批处理（--cont-batching），提升并发请求吞吐量</summary>
    public bool UseContBatching { get; set; } = true;

    /// <summary>额外启动参数（高级自定义，仍保留文本框）</summary>
    public string ExtraArgs { get; set; } = "";
}

/// <summary>OpenVINO 配置</summary>
public class OpenClawOpenVinoConfigDto
{
    public bool Enabled { get; set; }
    /// <summary>OpenVINO genai server 脚本或可执行文件路径</summary>
    public string BinaryPath { get; set; } = "";
    /// <summary>OpenVINO 优化后的模型目录路径</summary>
    public string ModelPath { get; set; } = "";
    public string BaseUrl { get; set; } = "http://localhost:8000";
    public int Port { get; set; } = 8000;
    /// <summary>推理设备：CPU / GPU / NPU</summary>
    public string Device { get; set; } = "CPU";
    public int ContextSize { get; set; } = 4096;
    public string ApiType { get; set; } = "openai-completions";
    /// <summary>额外启动参数</summary>
    public string ExtraArgs { get; set; } = "";
}

/// <summary>OpenClaw 本地 AI 配置汇总</summary>
public class OpenClawLocalAiConfigDto
{
    public OpenClawLocalProviderConfigDto? Ollama { get; set; }
    public OpenClawLocalProviderConfigDto? LmStudio { get; set; }
    public OpenClawLlamaCppConfigDto? LlamaCpp { get; set; }
    public OpenClawOpenVinoConfigDto? OpenVino { get; set; }
}

/// <summary>保存 OpenClaw 本地 AI 配置请求</summary>
public class SaveOpenClawLocalAiConfigRequest
{
    public OpenClawLocalProviderConfigDto? Ollama { get; set; }
    public OpenClawLocalProviderConfigDto? LmStudio { get; set; }
    public OpenClawLlamaCppConfigDto? LlamaCpp { get; set; }
    public OpenClawOpenVinoConfigDto? OpenVino { get; set; }
}

/// <summary>检测本地 AI 服务请求</summary>
public class DetectLocalAiRequest
{
    public string Provider { get; set; } = "";
}

/// <summary>本地 AI 服务检测/启动状态</summary>
public class LocalAiServiceStatusDto
{
    public string Provider { get; set; } = "";
    public bool IsRunning { get; set; }
    public bool AttemptedStart { get; set; }
    public bool StartSuccess { get; set; }
    public string? Message { get; set; }
    /// <summary>本次检测到的可用推理设备（如 CPU/GPU/NPU），前端回填下拉框</summary>
    public List<string> Devices { get; set; } = new();
    /// <summary>实际执行的启动命令（排查问题时可直接复制到命令行手动运行）</summary>
    public string? CommandLine { get; set; }
}

/// <summary>OpenClaw 默认模型信息</summary>
public class OpenClawDefaultModelDto
{
    public string? CurrentModel { get; set; }
    public List<string> AvailableModels { get; set; } = new();
}

/// <summary>设置 OpenClaw 默认模型请求</summary>
public class SetOpenClawDefaultModelRequest
{
    public string Model { get; set; } = "";
}

/// <summary>同步本地模型到 OpenClaw 请求</summary>
public class SyncLocalModelsRequest
{
    public string Provider { get; set; } = "";
}

/// <summary>OpenClaw 本地模型信息</summary>
public class OpenClawLocalModelDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ApiType { get; set; } = "openai-completions";
    public List<string> Input { get; set; } = new() { "text" };
    public int ContextWindow { get; set; } = 128000;
    public int MaxTokens { get; set; } = 4096;
}

/// <summary>模型配置文件（预设）</summary>
public class ModelProfileDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Model { get; set; } = "";
    public string? Provider { get; set; }
    public string? SizeInfo { get; set; }
    public string? SpeedLabel { get; set; }
}

/// <summary>模型配置文件列表响应</summary>
public class ModelProfileListDto
{
    public List<ModelProfileDto> Profiles { get; set; } = new();
    public string? CurrentProfile { get; set; }
}

/// <summary>设置模型配置文件请求</summary>
public class SetModelProfileRequest
{
    public string Profile { get; set; } = "";
}
