namespace Baihua.Core.Services;

/// <summary>
/// ComfyUI 工作流构建器：生成 txt2img / txt2video 的标准 API 格式工作流。
/// 图片用 SD 系列 checkpoint（默认 v1-5），视频用 LTX Video checkpoint（默认 ltx-video-2b）。
/// 已在本机 ComfyUI 0.33 实测通过。
/// </summary>
public static class ComfyWorkflowBuilder
{
    public const string DefaultImageCheckpoint = "v1-5-pruned-emaonly.safetensors";
    public const string DefaultVideoCheckpoint = "ltx-video-2b-v0.9.safetensors";
    public const string DefaultImageModelType = "sd15";
    public const string ZImageTurboModelType = "z-image-turbo";
    public const string DefaultZImageTurboUnet = "z_image_turbo_bf16.safetensors";
    public const string DefaultZImageTurboClip = "qwen_3_4b.safetensors";
    public const string DefaultZImageTurboVae = "ae.safetensors";

    /// <summary>LTX 的 T5 文本编码器（models/text_encoders 下，CLIPLoader type=ltxv）。</summary>
    public const string LtxT5Clip = "t5xxl_fp8_e4m3fn.safetensors";

    /// <summary>构建 txt2img 工作流（SD1.5：CheckpointLoaderSimple + CLIPTextEncode + KSampler + VAEDecode + SaveImage）。</summary>
    public static Dictionary<string, object> BuildTxt2Image(
        string prompt,
        string? negativePrompt,
        int width,
        int height,
        int steps,
        long seed,
        string checkpoint,
        double cfg = 7.0,
        string sampler = "euler",
        string scheduler = "normal")
    {
        return new Dictionary<string, object>
        {
            ["3"] = new Dictionary<string, object>
            {
                ["class_type"] = "KSampler",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["seed"] = seed,
                    ["steps"] = steps,
                    ["cfg"] = cfg,
                    ["sampler_name"] = sampler,
                    ["scheduler"] = scheduler,
                    ["denoise"] = 1.0,
                    ["model"] = new object[] { "4", 0 },
                    ["positive"] = new object[] { "6", 0 },
                    ["negative"] = new object[] { "7", 0 },
                    ["latent_image"] = new object[] { "5", 0 }
                }
            },
            ["4"] = new Dictionary<string, object>
            {
                ["class_type"] = "CheckpointLoaderSimple",
                ["inputs"] = new Dictionary<string, object> { ["ckpt_name"] = checkpoint }
            },
            ["5"] = new Dictionary<string, object>
            {
                ["class_type"] = "EmptyLatentImage",
                ["inputs"] = new Dictionary<string, object> { ["width"] = width, ["height"] = height, ["batch_size"] = 1 }
            },
            ["6"] = new Dictionary<string, object>
            {
                ["class_type"] = "CLIPTextEncode",
                ["inputs"] = new Dictionary<string, object> { ["text"] = prompt, ["clip"] = new object[] { "4", 1 } }
            },
            ["7"] = new Dictionary<string, object>
            {
                ["class_type"] = "CLIPTextEncode",
                ["inputs"] = new Dictionary<string, object> { ["text"] = negativePrompt ?? "", ["clip"] = new object[] { "4", 1 } }
            },
            ["8"] = new Dictionary<string, object>
            {
                ["class_type"] = "VAEDecode",
                ["inputs"] = new Dictionary<string, object> { ["samples"] = new object[] { "3", 0 }, ["vae"] = new object[] { "4", 2 } }
            },
            ["9"] = new Dictionary<string, object>
            {
                ["class_type"] = "SaveImage",
                ["inputs"] = new Dictionary<string, object> { ["images"] = new object[] { "8", 0 }, ["filename_prefix"] = "baihua-draw" }
            }
        };
    }

    /// <summary>
    /// 构建 Z-Image-Turbo txt2img 工作流（UNETLoader + CLIPLoader[Qwen3 4B, lumina2] + VAELoader +
    /// EmptySD3LatentImage + ModelSamplingAuraFlow + KSampler[cfg 1, res_multistep/simple]）。
    /// 对应 ComfyUI 默认 Z-Image-Turbo 模板，实测 1024x1024 / 8 步出图。
    /// </summary>
    public static Dictionary<string, object> BuildTxt2ImageZImageTurbo(
        string prompt,
        string? negativePrompt,
        int width,
        int height,
        int steps,
        long seed,
        string unetName,
        string clipName,
        string vaeName,
        double cfg = 1.0,
        string sampler = "res_multistep",
        string scheduler = "simple")
    {
        return new Dictionary<string, object>
        {
            ["10"] = new Dictionary<string, object>
            {
                ["class_type"] = "UNETLoader",
                ["inputs"] = new Dictionary<string, object> { ["unet_name"] = unetName, ["weight_dtype"] = "default" }
            },
            ["11"] = new Dictionary<string, object>
            {
                ["class_type"] = "CLIPLoader",
                ["inputs"] = new Dictionary<string, object> { ["clip_name"] = clipName, ["type"] = "lumina2", ["device"] = "default" }
            },
            ["12"] = new Dictionary<string, object>
            {
                ["class_type"] = "VAELoader",
                ["inputs"] = new Dictionary<string, object> { ["vae_name"] = vaeName }
            },
            ["13"] = new Dictionary<string, object>
            {
                ["class_type"] = "CLIPTextEncode",
                ["inputs"] = new Dictionary<string, object> { ["text"] = prompt, ["clip"] = new object[] { "11", 0 } }
            },
            ["14"] = new Dictionary<string, object>
            {
                ["class_type"] = "CLIPTextEncode",
                ["inputs"] = new Dictionary<string, object> { ["text"] = negativePrompt ?? "", ["clip"] = new object[] { "11", 0 } }
            },
            ["15"] = new Dictionary<string, object>
            {
                ["class_type"] = "EmptySD3LatentImage",
                ["inputs"] = new Dictionary<string, object> { ["width"] = width, ["height"] = height, ["batch_size"] = 1 }
            },
            ["16"] = new Dictionary<string, object>
            {
                ["class_type"] = "ModelSamplingAuraFlow",
                ["inputs"] = new Dictionary<string, object> { ["model"] = new object[] { "10", 0 }, ["shift"] = 3.0 }
            },
            ["17"] = new Dictionary<string, object>
            {
                ["class_type"] = "KSampler",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["seed"] = seed,
                    ["steps"] = steps,
                    ["cfg"] = cfg,
                    ["sampler_name"] = sampler,
                    ["scheduler"] = scheduler,
                    ["denoise"] = 1.0,
                    ["model"] = new object[] { "16", 0 },
                    ["positive"] = new object[] { "13", 0 },
                    ["negative"] = new object[] { "14", 0 },
                    ["latent_image"] = new object[] { "15", 0 }
                }
            },
            ["18"] = new Dictionary<string, object>
            {
                ["class_type"] = "VAEDecode",
                ["inputs"] = new Dictionary<string, object> { ["samples"] = new object[] { "17", 0 }, ["vae"] = new object[] { "12", 0 } }
            },
            ["19"] = new Dictionary<string, object>
            {
                ["class_type"] = "SaveImage",
                ["inputs"] = new Dictionary<string, object> { ["images"] = new object[] { "18", 0 }, ["filename_prefix"] = "baihua-draw" }
            }
        };
    }


    /// <summary>
    /// 构建 txt2video 工作流（LTX Video 2B：CheckpointLoaderSimple[unet+vae] + CLIPLoader[ltxv t5xxl] +
    /// LTXVConditioning + EmptyLTXVLatentVideo + KSampler + VAEDecode + CreateVideo + SaveVideo）。
    /// </summary>
    public static Dictionary<string, object> BuildTxt2Video(
        string prompt,
        string? negativePrompt,
        int width,
        int height,
        int length,
        int fps,
        int steps,
        long seed,
        string checkpoint,
        double cfg = 4.0,
        string sampler = "euler",
        string scheduler = "sgm_uniform")
    {
        return new Dictionary<string, object>
        {
            // 1: LTX checkpoint（含 DiT + VAE，不含文本编码器）
            ["1"] = new Dictionary<string, object>
            {
                ["class_type"] = "CheckpointLoaderSimple",
                ["inputs"] = new Dictionary<string, object> { ["ckpt_name"] = checkpoint }
            },
            // 2: T5 文本编码器（LTX 专用 type）
            ["2"] = new Dictionary<string, object>
            {
                ["class_type"] = "CLIPLoader",
                ["inputs"] = new Dictionary<string, object> { ["clip_name"] = LtxT5Clip, ["type"] = "ltxv" }
            },
            ["3"] = new Dictionary<string, object>
            {
                ["class_type"] = "CLIPTextEncode",
                ["inputs"] = new Dictionary<string, object> { ["text"] = prompt, ["clip"] = new object[] { "2", 0 } }
            },
            ["4"] = new Dictionary<string, object>
            {
                ["class_type"] = "CLIPTextEncode",
                ["inputs"] = new Dictionary<string, object> { ["text"] = negativePrompt ?? "", ["clip"] = new object[] { "2", 0 } }
            },
            // 5: LTX 条件（frame_rate 同步 fps）
            ["5"] = new Dictionary<string, object>
            {
                ["class_type"] = "LTXVConditioning",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["positive"] = new object[] { "3", 0 },
                    ["negative"] = new object[] { "4", 0 },
                    ["frame_rate"] = (double)fps
                }
            },
            // 6: 空视频 latent
            ["6"] = new Dictionary<string, object>
            {
                ["class_type"] = "EmptyLTXVLatentVideo",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["width"] = width,
                    ["height"] = height,
                    ["length"] = length,
                    ["batch_size"] = 1
                }
            },
            // 7: 采样（LTX 用 euler + sgm_uniform，cfg 建议 3-6）
            ["7"] = new Dictionary<string, object>
            {
                ["class_type"] = "KSampler",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["model"] = new object[] { "1", 0 },
                    ["seed"] = seed,
                    ["steps"] = steps,
                    ["cfg"] = cfg,
                    ["sampler_name"] = sampler,
                    ["scheduler"] = scheduler,
                    ["positive"] = new object[] { "5", 0 },
                    ["negative"] = new object[] { "5", 1 },
                    ["latent_image"] = new object[] { "6", 0 },
                    ["denoise"] = 1.0
                }
            },
            // 8: 解码（LTX checkpoint 自带 VAE）
            ["8"] = new Dictionary<string, object>
            {
                ["class_type"] = "VAEDecode",
                ["inputs"] = new Dictionary<string, object> { ["samples"] = new object[] { "7", 0 }, ["vae"] = new object[] { "1", 2 } }
            },
            // 9: 帧序列 → 视频
            ["9"] = new Dictionary<string, object>
            {
                ["class_type"] = "CreateVideo",
                ["inputs"] = new Dictionary<string, object> { ["images"] = new object[] { "8", 0 }, ["fps"] = (double)fps }
            },
            // 10: 保存 mp4
            ["10"] = new Dictionary<string, object>
            {
                ["class_type"] = "SaveVideo",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["video"] = new object[] { "9", 0 },
                    ["filename_prefix"] = "baihua-draw-video",
                    ["format"] = "auto",
                    ["codec"] = "h264"
                }
            }
        };
    }
}
