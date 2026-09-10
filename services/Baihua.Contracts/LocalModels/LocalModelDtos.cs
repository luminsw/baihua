namespace Baihua.Contracts.LocalModels;

/// <summary>
/// 硬件信息 DTO
/// </summary>
public class HardwareInfoDto
{
    public CpuInfoDto Cpu { get; set; } = new();
    public MemoryInfoDto Memory { get; set; } = new();
    public List<GpuInfoDto> Gpus { get; set; } = new();
    public string OsPlatform { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public bool IsWsl { get; set; }
    public long TotalDiskSpaceBytes { get; set; }
    public long AvailableDiskSpaceBytes { get; set; }

    /// <summary>
    /// 纯 CPU 推理时的估算速度（tokens/秒）。仅在无独立 GPU 时有意义。
    /// </summary>
    public double? EstimatedCpuTokensPerSecond { get; set; }
}

public class CpuInfoDto
{
    public string Name { get; set; } = "";
    public int CoreCount { get; set; }
    public int LogicalProcessorCount { get; set; }
    public string Architecture { get; set; } = "";
    public string? MaxFrequencyMHz { get; set; }
}

public class MemoryInfoDto
{
    public long TotalBytes { get; set; }
    public long AvailableBytes { get; set; }
    public double TotalGiB => Math.Round(TotalBytes / (1024.0 * 1024 * 1024), 1);
    public double AvailableGiB => Math.Round(AvailableBytes / (1024.0 * 1024 * 1024), 1);
}

public class GpuInfoDto
{
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public long? VramBytes { get; set; }
    public double? VramGiB => VramBytes.HasValue ? Math.Round(VramBytes.Value / (1024.0 * 1024 * 1024), 1) : null;
    public bool IsIntegrated { get; set; }
    public bool IsAppleSilicon { get; set; }
    public string? DriverVersion { get; set; }

    // ---- 扩展：AI 算力与性能估算 ----

    /// <summary>计算单元数量（CUDA 核心数 / Xe 核心数 / RDNA CU 数 / Apple GPU 核心数）</summary>
    public int? ComputeUnits { get; set; }

    /// <summary>显存带宽（GB/s），从规格数据库匹配或推算</summary>
    public double? MemoryBandwidthGBps { get; set; }

    /// <summary>最大时钟频率（MHz）</summary>
    public double? MaxClockMHz { get; set; }

    /// <summary>估算 AI 算力（FP16 TFLOPS）</summary>
    public double? EstimatedTflopsFp16 { get; set; }

    /// <summary>估算 AI 算力（INT8 TFLOPS），约等于 FP16 × 2</summary>
    public double? EstimatedTflopsInt8 { get; set; }

    /// <summary>估算 AI 算力（INT4 TFLOPS），约等于 FP16 × 4</summary>
    public double? EstimatedTflopsInt4 { get; set; }

    /// <summary>估算 Llama-3-8B Q4_K_M 推理速度（tokens/秒）</summary>
    public double? EstimatedTokensPerSecond { get; set; }
}

/// <summary>
/// 推荐模型 DTO
/// </summary>
public class RecommendedModelDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string OllamaModelName { get; set; } = "";
    public string? LmStudioSearchName { get; set; }
    public string Description { get; set; } = "";
    public string ParameterSize { get; set; } = "";
    public string Quantization { get; set; } = "";
    public double SizeGiB { get; set; }
    public double? MinVramGiB { get; set; }
    public double MinRamGiB { get; set; }
    public List<string> Tags { get; set; } = new();
    public string Suitability { get; set; } = "";
    public int MatchScore { get; set; }
    public List<ModelSourceDto> Sources { get; set; } = new();
    public bool IsDownloadedOllama { get; set; }
    public bool IsDownloadedLmStudio { get; set; }
    public bool IsDownloadedLlamaCpp { get; set; }
    public string Company { get; set; } = "";

    /// <summary>模态：text / vision / multimodal</summary>
    public string Modality { get; set; } = "text";

    /// <summary>
    /// 在当前硬件上的估算输出速度（tokens/秒）。null 表示无法估算。
    /// </summary>
    public double? EstimatedTokensPerSecond { get; set; }
}

public class ModelSourceDto
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool IsMirror { get; set; }
}

/// <summary>
/// 部署请求
/// </summary>
public class DeployLocalModelRequest
{
    public string ModelId { get; set; } = "";
    public string TargetTool { get; set; } = "ollama"; // ollama | lmstudio
    public string? PreferredSource { get; set; }
}

/// <summary>
/// 部署结果
/// </summary>
public class DeployLocalModelResult
{
    public bool Success { get; set; }
    public string TaskId { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>
/// 部署任务状态
/// </summary>
public class DeployTaskStatusDto
{
    public string TaskId { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string Status { get; set; } = "pending"; // pending / running / completed / failed
    public int ProgressPercent { get; set; }
    public string CurrentStep { get; set; } = "";
    public List<string> Logs { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool AutoConfiguredProvider { get; set; }
}

/// <summary>
/// 本地工具信息
/// </summary>
public class LocalToolInfoDto
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public bool IsInstalled { get; set; }
    public string? Version { get; set; }
    public bool IsRunning { get; set; }
    public string? DefaultModelPath { get; set; }
    public string? InstallGuideUrl { get; set; }
}

/// <summary>
/// 下载源信息
/// </summary>
public class DownloadSourceDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public bool IsChinaMirror { get; set; }
    public bool IsAvailable { get; set; }
    public int? LatencyMs { get; set; }
}

/// <summary>
/// 下载目录配置
/// </summary>
public class DownloadDirectoryConfigDto
{
    public string DownloadDirectory { get; set; } = "";
    public string PreferredSource { get; set; } = "auto";
    public bool UseChinaMirror { get; set; } = true;
    public string PlatformDefaultDirectory { get; set; } = "";
}

/// <summary>
/// 硬件等级枚举
/// </summary>
public enum HardwareTier
{
    Unknown = 0,
    CpuOnly = 1,      // 仅CPU
    LowEndGpu = 2,    // < 4GB VRAM
    MidRangeGpu = 3,  // 4-8GB VRAM
    HighEndGpu = 4,   // 8-16GB VRAM
    TopTierGpu = 5,   // >= 16GB VRAM
}

/// <summary>
/// 运行中模型信息 DTO
/// </summary>
public class RunningModelDto
{
    public string ToolId { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Status { get; set; } = "running";

    /// <summary>承载该模型的服务端口（如 OVMS 8000 / Ollama 11434；未知为 null）</summary>
    public int? Port { get; set; }
    public long SizeBytes { get; set; }
    public long? VramBytes { get; set; }
    public long? RamBytes { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public int? ContextLength { get; set; }
    public string? Family { get; set; }
    public double SizeGiB => Math.Round(SizeBytes / (1024.0 * 1024 * 1024), 2);
    public double? VramGiB => VramBytes.HasValue ? Math.Round(VramBytes.Value / (1024.0 * 1024 * 1024), 2) : null;
    public double? RamGiB => RamBytes.HasValue ? Math.Round(RamBytes.Value / (1024.0 * 1024 * 1024), 2) : null;
}

/// <summary>
/// 加载模型请求
/// </summary>
public class LoadModelRequest
{
    public string ToolId { get; set; } = "";
    public string ModelName { get; set; } = "";
    public int KeepAliveMinutes { get; set; } = 5;
}

/// <summary>
/// 卸载模型请求
/// </summary>
public class UnloadModelRequest
{
    public string ToolId { get; set; } = "";
    public string ModelName { get; set; } = "";
}

/// <summary>
/// 已下载模型信息
/// </summary>
public class DownloadedModelDto
{
    public string Name { get; set; } = "";
    public string ToolId { get; set; } = "";
    public string ToolName { get; set; } = "";
    public long SizeBytes { get; set; }
    public double SizeGiB => Math.Round(SizeBytes / (1024.0 * 1024 * 1024), 2);
    public DateTime? ModifiedAt { get; set; }
    public string? Digest { get; set; }
    public bool IsRunning { get; set; }
    public string? ParameterSize { get; set; }
    public string? Quantization { get; set; }
    public string? Usage { get; set; }
}

/// <summary>
/// 模型详情 DTO
/// </summary>
public class ModelDetailsDto
{
    public string Name { get; set; } = "";
    public string ToolId { get; set; } = "";
    public string? Modelfile { get; set; }
    public string? Parameters { get; set; }
    public string? Template { get; set; }
    public string? License { get; set; }
    public string? DetailsJson { get; set; }
}

/// <summary>
/// 删除模型请求
/// </summary>
public class DeleteModelRequest
{
    public string ToolId { get; set; } = "";
    public string ModelName { get; set; } = "";
}

/// <summary>
/// 查看模型详情请求
/// </summary>
public class ModelDetailsRequest
{
    public string ToolId { get; set; } = "";
    public string ModelName { get; set; } = "";
}

/// <summary>
/// 本地大模型注册表条目（Agent 经 MCP 写入，WebUI 只读展示）。
/// </summary>
public class LocalModelRegistryDto
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
    public DateTime RegisteredAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
