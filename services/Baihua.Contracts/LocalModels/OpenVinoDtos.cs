namespace Baihua.Contracts.LocalModels;

/// <summary>OpenVINO 可下载模型目录条目（页面用）</summary>
public class OpenVinoCatalogItemDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ParameterSize { get; set; } = "";
    public string Quantization { get; set; } = "";
    public double SizeGiB { get; set; }
    public string? Description { get; set; }
    public bool IsVision { get; set; }
    public bool IsTts { get; set; }
    public bool IsMedical { get; set; }
    public string ModelScopeRepo { get; set; } = "";
    /// <summary>源模型格式：openvino（直接可用）/ safetensors（需本地转换）</summary>
    public string Format { get; set; } = "openvino";
    public bool Installed { get; set; }

    /// <summary>已下载时的本地目录路径</summary>
    public string? Path { get; set; }

    /// <summary>已下载时的实际占用字节数</summary>
    public long SizeBytes { get; set; }

    /// <summary>是否正在运行（已下载模型才有意义）</summary>
    public bool IsRunning { get; set; }

    /// <summary>运行端口（正在运行时）</summary>
    public int? Port { get; set; }

    /// <summary>已下载时的最后修改时间</summary>
    public DateTime? LastModified { get; set; }

    /// <summary>是否由 OVMS 常驻托管（注册即运行，不可从此页停止/启动子进程）</summary>
    public bool IsOmsHosted { get; set; }

    /// <summary>所需显存（GiB）。null = 不依赖 GPU 显存（如 TTS/嵌入）</summary>
    public double? MinVramGiB { get; set; }
}

/// <summary>OpenVINO 可下载模型目录条目（网上仓库：ModelScope / HuggingFace）</summary>
public class OpenVinoCatalogEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ParameterSize { get; set; } = "";
    public string Quantization { get; set; } = "INT4";
    public double SizeGiB { get; set; }
    public string? Description { get; set; }
    public bool IsVision { get; set; }
    public bool IsTts { get; set; }
    public bool IsMedical { get; set; }
    public string ModelScopeRepo { get; set; } = "";
    public string HuggingFaceRepo { get; set; } = "";
    /// <summary>源模型格式：openvino（直接可用）/ safetensors（需本地转换）</summary>
    public string Format { get; set; } = "openvino";
    /// <summary>下载到本地模型根目录后的子目录名</summary>
    public string DirectoryName { get; set; } = "";

    /// <summary>所需显存（GiB）。null = 不依赖 GPU 显存（如 TTS/嵌入）</summary>
    public double? MinVramGiB { get; set; }

    /// <summary>场景分类（对话 / 视觉 / 代码 / 医疗 / TTS / 嵌入）</summary>
    public string Category { get; set; } = "";
}

/// <summary>OpenVINO 模型下载请求</summary>
public class OpenVinoDownloadRequest
{
    public string ModelId { get; set; } = "";
}

/// <summary>下载任务状态</summary>
public class OpenVinoDownloadTaskDto
{
    public string TaskId { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string Status { get; set; } = "pending"; // pending/running/completed/failed/cancelled
    public int ProgressPercent { get; set; }
    public long DownloadedBytes { get; set; }
    public long TotalBytes { get; set; }
    public string CurrentFile { get; set; } = "";
    public double SpeedMBps { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public List<string> Logs { get; set; } = new();
}

/// <summary>已下载的 OpenVINO 模型（扫描模型根目录）</summary>
public class OpenVinoInstalledModelDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
    public bool HasOpenVinoBin { get; set; }
    public bool IsRunning { get; set; }
    public int? Port { get; set; }
    public DateTime? LastModified { get; set; }

    /// <summary>是否由 OVMS 常驻托管（注册即运行，不可从此页停止/启动子进程）</summary>
    public bool IsOmsHosted { get; set; }
}

/// <summary>运行 OpenVINO 模型请求</summary>
public class OpenVinoRunRequest
{
    public string ModelPath { get; set; } = "";
    public string Device { get; set; } = "GPU";

    /// <summary>停止时用：模型监听端口</summary>
    public int? Port { get; set; }
}

/// <summary>运行结果</summary>
public class OpenVinoRunResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int Port { get; set; }
    public int? ProcessId { get; set; }
    public string Endpoint { get; set; } = "";
}
