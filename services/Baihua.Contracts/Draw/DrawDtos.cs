namespace Baihua.Contracts.Draw;

/// <summary>文生图请求（本地 ComfyUI，SD 系列 checkpoint）。</summary>
public class DrawImageRequest
{
    /// <summary>正向提示词（英文效果更佳）。</summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>负向提示词，可空。</summary>
    public string? NegativePrompt { get; set; }

    /// <summary>宽度，默认 512（建议 256-1024）。</summary>
    public int? Width { get; set; }

    /// <summary>高度，默认 512（建议 256-1024）。</summary>
    public int? Height { get; set; }

    /// <summary>采样步数，默认 20（Z-Image-Turbo 建议 8）。</summary>
    public int? Steps { get; set; }

    /// <summary>图像模型类型：sd15（默认，SD1.5 checkpoint）或 z-image-turbo（Z-Image Turbo diffusion model）。</summary>
    public string? ModelType { get; set; }

    /// <summary>checkpoint 文件名，可空（modelType=sd15 时生效，默认 v1-5-pruned-emaonly.safetensors）。</summary>
    public string? Checkpoint { get; set; }

    /// <summary>Z-Image-Turbo 的 UNet 模型名，可空（默认 z_image_turbo_bf16.safetensors）。</summary>
    public string? UnetName { get; set; }

    /// <summary>Z-Image-Turbo 的 CLIP 模型名，可空（默认 qwen_3_4b.safetensors）。</summary>
    public string? ClipName { get; set; }

    /// <summary>Z-Image-Turbo 的 VAE 模型名，可空（默认 ae.safetensors）。</summary>
    public string? VaeName { get; set; }

    /// <summary>随机种子，可空（默认随机）。</summary>
    public long? Seed { get; set; }

    /// <summary>CFG 引导强度，可空（SD1.5 默认 7，Z-Image-Turbo 默认 1）。</summary>
    public double? Cfg { get; set; }

    /// <summary>采样器名，可空（SD1.5 默认 euler，Z-Image-Turbo 默认 res_multistep）。</summary>
    public string? Sampler { get; set; }

    /// <summary>调度器名，可空（SD1.5 默认 normal，Z-Image-Turbo 默认 simple）。</summary>
    public string? Scheduler { get; set; }
}

/// <summary>文生视频请求（本地 ComfyUI + LTX Video）。</summary>
public class DrawVideoRequest
{
    /// <summary>正向提示词（英文效果更佳）。</summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>负向提示词，可空。</summary>
    public string? NegativePrompt { get; set; }

    /// <summary>宽度，默认 512（LTX 建议 ≤768）。</summary>
    public int? Width { get; set; }

    /// <summary>高度，默认 512（LTX 建议 ≤768）。</summary>
    public int? Height { get; set; }

    /// <summary>视频帧数，默认 97（≈4s @24fps；建议 25-121）。</summary>
    public int? Length { get; set; }

    /// <summary>帧率，默认 25。</summary>
    public int? Fps { get; set; }

    /// <summary>采样步数，默认 20（视频建议 15-30）。</summary>
    public int? Steps { get; set; }

    /// <summary>checkpoint 文件名，可空（默认 LTX Video）。</summary>
    public string? Checkpoint { get; set; }

    /// <summary>随机种子，可空（默认随机）。</summary>
    public long? Seed { get; set; }

    /// <summary>CFG 引导强度，可空（默认 4）。</summary>
    public double? Cfg { get; set; }

    /// <summary>采样器名，可空（默认 euler）。</summary>
    public string? Sampler { get; set; }

    /// <summary>调度器名，可空（默认 sgm_uniform）。</summary>
    public string? Scheduler { get; set; }
}

/// <summary>生成结果（同步等待完成）。</summary>
public class DrawResultDto
{
    /// <summary>是否成功。</summary>
    public bool Success { get; set; }

    /// <summary>生成的文件名（output 目录下），用 GET /api/draw/file 下载。</summary>
    public string? FileName { get; set; }

    /// <summary>可直接在浏览器打开的短时签名下载 URL（配置了鉴权时由网关生成，约 10 分钟有效）。</summary>
    public string? FileUrl { get; set; }

    /// <summary>MIME 类型（image/png、video/mp4 等）。</summary>
    public string? ContentType { get; set; }

    /// <summary>失败原因（Success 为 false 时）。</summary>
    public string? Error { get; set; }

    /// <summary>耗时（秒）。</summary>
    public double ElapsedSeconds { get; set; }
}

/// <summary>绘图能力状态。</summary>
public class DrawStatusDto
{
    /// <summary>本机 ComfyUI 是否在线。</summary>
    public bool ComfyUiOnline { get; set; }

    /// <summary>ComfyUI 版本（在线时）。</summary>
    public string? ComfyUiVersion { get; set; }

    /// <summary>可用 checkpoint（出图）。</summary>
    public List<string> ImageCheckpoints { get; set; } = new();

    /// <summary>可用 checkpoint（出视频）。</summary>
    public List<string> VideoCheckpoints { get; set; } = new();

    /// <summary>可用 UNet 模型（Z-Image-Turbo 等 diffusion model）。</summary>
    public List<string> UnetModels { get; set; } = new();

    /// <summary>可用 CLIP 文本编码器模型（Z-Image-Turbo 用 qwen_3_4b 等）。</summary>
    public List<string> ClipModels { get; set; } = new();

    /// <summary>可用 VAE 模型（Z-Image-Turbo 用 ae 等）。</summary>
    public List<string> VaeModels { get; set; } = new();
}
