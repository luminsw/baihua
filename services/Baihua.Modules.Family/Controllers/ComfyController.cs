using System.Text.Json;
using Baihua.Core.Services;
using Baihua.Core.Modules;
using Baihua.Contracts.Ai;
using Microsoft.AspNetCore.Mvc;

namespace Baihua.Modules.Family.Controllers;

/// <summary>
/// AI 绘图（ComfyUI）接口：提交生成任务、查询历史、获取生成文件。
/// 一服务一数据库：生成历史（ComfyArtworks 表）存于 AI 服务 ai.db，经 AiComfyArtworksClient HTTP 读写。
/// </summary>
[ApiController]
[Route("api/comfy")]
public class ComfyController : ControllerBase
{
    private readonly ComfyUiClient _comfy;
    private readonly IComfyArtworkStore _artworks;
    private readonly ILogger<ComfyController> _logger;

    public ComfyController(
        ComfyUiClient comfy,
        IComfyArtworkStore artworks,
        ILogger<ComfyController> logger)
    {
        _comfy = comfy;
        _artworks = artworks;
        _logger = logger;
    }

    /// <summary>ComfyUI 服务是否在线</summary>
    [HttpGet("status")]
    public async Task<ActionResult<object>> Status(CancellationToken ct)
    {
        var ok = await _comfy.IsAvailableAsync(ct);
        return new { available = ok };
    }

    public record GenerateImageRequest(
        string Prompt,
        string NegativePrompt = "",
        int Width = 512,
        int Height = 512,
        int Steps = 20,
        int Seed = 0);

    /// <summary>SD1.5 文生图：提交工作流并同步等待完成</summary>
    [HttpPost("generate-image")]
    public async Task<ActionResult<object>> GenerateImage(GenerateImageRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt))
            return BadRequest(new { error = "Prompt is required" });

        var seed = req.Seed == 0 ? Random.Shared.Next(1, int.MaxValue) : req.Seed;
        var negative = string.IsNullOrWhiteSpace(req.NegativePrompt)
            ? "blurry, low quality, watermark, text, deformed, ugly"
            : req.NegativePrompt;

        var workflow = new Dictionary<string, object>
        {
            ["1"] = new Dictionary<string, object> { ["class_type"] = "CheckpointLoaderSimple", ["inputs"] = new Dictionary<string, object> { ["ckpt_name"] = "v1-5-pruned-emaonly.safetensors" } },
            ["2"] = new Dictionary<string, object> { ["class_type"] = "CLIPTextEncode", ["inputs"] = new Dictionary<string, object> { ["text"] = req.Prompt, ["clip"] = new object[] { "1", 1 } } },
            ["3"] = new Dictionary<string, object> { ["class_type"] = "CLIPTextEncode", ["inputs"] = new Dictionary<string, object> { ["text"] = negative, ["clip"] = new object[] { "1", 1 } } },
            ["4"] = new Dictionary<string, object> { ["class_type"] = "EmptyLatentImage", ["inputs"] = new Dictionary<string, object> { ["width"] = req.Width, ["height"] = req.Height, ["batch_size"] = 1 } },
            ["5"] = new Dictionary<string, object> { ["class_type"] = "KSampler", ["inputs"] = new Dictionary<string, object> { ["model"] = new object[] { "1", 0 }, ["positive"] = new object[] { "2", 0 }, ["negative"] = new object[] { "3", 0 }, ["latent_image"] = new object[] { "4", 0 }, ["seed"] = seed, ["steps"] = req.Steps, ["cfg"] = 7.0, ["sampler_name"] = "euler", ["scheduler"] = "normal", ["denoise"] = 1.0 } },
            ["6"] = new Dictionary<string, object> { ["class_type"] = "VAEDecode", ["inputs"] = new Dictionary<string, object> { ["samples"] = new object[] { "5", 0 }, ["vae"] = new object[] { "1", 2 } } },
            ["7"] = new Dictionary<string, object> { ["class_type"] = "SaveImage", ["inputs"] = new Dictionary<string, object> { ["images"] = new object[] { "6", 0 }, ["filename_prefix"] = "baihua_art" } }
        };

        var startedAt = DateTime.UtcNow;
        string promptId;
        try
        {
            promptId = await _comfy.SubmitAsync(workflow, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ComfyUI submit failed");
            return StatusCode(502, new { error = "ComfyUI 提交失败：" + ex.Message });
        }

        // 轮询等待完成（图片较快，最多 5 分钟）
        ComfyExecutionResult? result = null;
        for (var i = 0; i < 150; i++)
        {
            await Task.Delay(2000, ct);
            result = await _comfy.GetResultAsync(promptId, ct);
            if (result != null) break;
        }

        var duration = (DateTime.UtcNow - startedAt).TotalSeconds;
        if (result == null)
        {
            await SaveRecordAsync(req, "image", "v1-5-pruned-emaonly.safetensors", promptId, false, "timeout", duration, seed, null);
            return StatusCode(504, new { error = "生成超时" });
        }

        if (result.IsError || result.Files.Count == 0)
        {
            await SaveRecordAsync(req, "image", "v1-5-pruned-emaonly.safetensors", promptId, false, result.Error ?? "no output", duration, seed, null);
            return StatusCode(502, new { error = "生成失败：" + result.Error });
        }

        var file = result.Files[0];
        var record = await SaveRecordAsync(req, "image", "v1-5-pruned-emaonly.safetensors", promptId, true, null, duration, seed, file);

        return Ok(new
        {
            id = record.Id,
            fileName = file.Filename,
            subfolder = file.Subfolder,
            url = $"/api/comfy/file?filename={Uri.EscapeDataString(file.Filename)}&subfolder={Uri.EscapeDataString(file.Subfolder)}",
            durationSeconds = Math.Round(duration, 1)
        });
    }

    public record GenerateVideoRequest(string Prompt, string NegativePrompt = "", int Width = 768, int Height = 512, int Frames = 97);

    /// <summary>LTX-Video 2B 文生视频：提交工作流并同步等待完成（较慢，最多 10 分钟）</summary>
    [HttpPost("generate-video")]
    public async Task<ActionResult<object>> GenerateVideo(GenerateVideoRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt))
            return BadRequest(new { error = "Prompt is required" });

        var negative = string.IsNullOrWhiteSpace(req.NegativePrompt)
            ? "low quality, worst quality, deformed, distorted, disfigured, motion smear, motion artifacts, fused fingers, bad anatomy, weird hand, ugly"
            : req.NegativePrompt;

        var workflow = new Dictionary<string, object>
        {
            ["38"] = new Dictionary<string, object> { ["class_type"] = "CLIPLoader", ["inputs"] = new Dictionary<string, object> { ["clip_name"] = "t5xxl_fp8_e4m3fn.safetensors", ["type"] = "ltxv" } },
            ["44"] = new Dictionary<string, object> { ["class_type"] = "CheckpointLoaderSimple", ["inputs"] = new Dictionary<string, object> { ["ckpt_name"] = "ltx-video-2b-v0.9.safetensors" } },
            ["6"] = new Dictionary<string, object> { ["class_type"] = "CLIPTextEncode", ["inputs"] = new Dictionary<string, object> { ["text"] = req.Prompt, ["clip"] = new object[] { "38", 0 } } },
            ["7"] = new Dictionary<string, object> { ["class_type"] = "CLIPTextEncode", ["inputs"] = new Dictionary<string, object> { ["text"] = negative, ["clip"] = new object[] { "38", 0 } } },
            ["70"] = new Dictionary<string, object> { ["class_type"] = "EmptyLTXVLatentVideo", ["inputs"] = new Dictionary<string, object> { ["width"] = req.Width, ["height"] = req.Height, ["length"] = req.Frames, ["batch_size"] = 1 } },
            ["71"] = new Dictionary<string, object> { ["class_type"] = "LTXVScheduler", ["inputs"] = new Dictionary<string, object> { ["steps"] = 30, ["max_shift"] = 2.05, ["base_shift"] = 0.95, ["stretch"] = true, ["terminal"] = 0.1 } },
            ["73"] = new Dictionary<string, object> { ["class_type"] = "KSamplerSelect", ["inputs"] = new Dictionary<string, object> { ["sampler_name"] = "euler" } },
            ["72"] = new Dictionary<string, object> { ["class_type"] = "SamplerCustom", ["inputs"] = new Dictionary<string, object> { ["model"] = new object[] { "44", 0 }, ["add_noise"] = true, ["noise_seed"] = Random.Shared.Next(1, int.MaxValue), ["cfg"] = 3.0, ["positive"] = new object[] { "69", 0 }, ["negative"] = new object[] { "69", 1 }, ["sampler"] = new object[] { "73", 0 }, ["sigmas"] = new object[] { "71", 0 }, ["latent_image"] = new object[] { "70", 0 } } },
            ["69"] = new Dictionary<string, object> { ["class_type"] = "LTXVConditioning", ["inputs"] = new Dictionary<string, object> { ["positive"] = new object[] { "6", 0 }, ["negative"] = new object[] { "7", 0 }, ["frame_rate"] = 25.0 } },
            ["8"] = new Dictionary<string, object> { ["class_type"] = "VAEDecode", ["inputs"] = new Dictionary<string, object> { ["samples"] = new object[] { "72", 0 }, ["vae"] = new object[] { "44", 2 } } },
            ["78"] = new Dictionary<string, object> { ["class_type"] = "CreateVideo", ["inputs"] = new Dictionary<string, object> { ["images"] = new object[] { "8", 0 }, ["fps"] = 24.0 } },
            ["79"] = new Dictionary<string, object> { ["class_type"] = "SaveVideo", ["inputs"] = new Dictionary<string, object> { ["video"] = new object[] { "78", 0 }, ["filename_prefix"] = "baihua_video", ["format"] = "auto", ["codec"] = "auto" } }
        };

        var startedAt = DateTime.UtcNow;
        string promptId;
        try
        {
            promptId = await _comfy.SubmitAsync(workflow, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ComfyUI video submit failed");
            return StatusCode(502, new { error = "ComfyUI 提交失败：" + ex.Message });
        }

        // 轮询等待（视频慢，最多 10 分钟）
        ComfyExecutionResult? result = null;
        for (var i = 0; i < 300; i++)
        {
            await Task.Delay(2000, ct);
            result = await _comfy.GetResultAsync(promptId, ct);
            if (result != null) break;
        }

        var duration = (DateTime.UtcNow - startedAt).TotalSeconds;
        if (result == null)
        {
            await SaveVideoRecordAsync(req, promptId, false, "timeout", duration, null);
            return StatusCode(504, new { error = "生成超时" });
        }

        if (result.IsError || result.Files.Count == 0)
        {
            await SaveVideoRecordAsync(req, promptId, false, result.Error ?? "no output", duration, null);
            return StatusCode(502, new { error = "生成失败：" + result.Error });
        }

        var file = result.Files[0];
        var record = await SaveVideoRecordAsync(req, promptId, true, null, duration, file);

        return Ok(new
        {
            id = record.Id,
            fileName = file.Filename,
            subfolder = file.Subfolder,
            url = $"/api/comfy/file?filename={Uri.EscapeDataString(file.Filename)}&subfolder={Uri.EscapeDataString(file.Subfolder)}",
            durationSeconds = Math.Round(duration, 1)
        });
    }

    private async Task<AiComfyArtworkDto?> SaveVideoRecordAsync(
        GenerateVideoRequest req, string promptId, bool success, string? error, double duration, ComfyOutputFile? file)
    {
        return await _artworks.CreateAsync(new SaveAiComfyArtworkRequest
        {
            Kind = "video",
            Prompt = req.Prompt,
            Model = "ltx-video-2b-v0.9.safetensors",
            ParamsJson = JsonSerializer.Serialize(new { req.Width, req.Height, req.Frames }),
            FileName = file?.Filename ?? "",
            Subfolder = file?.Subfolder ?? "",
            FileType = file?.Type ?? "output",
            PromptId = promptId,
            IsSuccess = success,
            ErrorMessage = error,
            DurationSeconds = Math.Round(duration, 1)
        });
    }

    /// <summary>获取生成文件（图片/视频）</summary>
    [HttpGet("file")]
    public async Task<IActionResult> GetFile(string filename, string subfolder = "", string type = "output", CancellationToken ct = default)
    {
        try
        {
            var bytes = await _comfy.GetFileAsync(filename, subfolder, type, ct);
            var ext = Path.GetExtension(filename).ToLowerInvariant();
            var contentType = ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".mp4" => "video/mp4",
                ".webm" => "video/webm",
                ".gif" => "image/gif",
                _ => "application/octet-stream"
            };
            return File(bytes, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ComfyUI file fetch failed: {File}", filename);
            // 显式 text/plain 404：ApiController 默认会把空 NotFound() 转成 problem+json，
            // 跨端口 <img> 加载 JSON 响应会触发浏览器 ORB 拦截（ERR_BLOCKED_BY_ORB）
            return StatusCode(StatusCodes.Status404NotFound, "");
        }
    }

    /// <summary>历史生成记录（最近 N 条）</summary>
    [HttpGet("history")]
    public async Task<ActionResult<object>> History(int limit = 50, string? kind = null, CancellationToken ct = default)
    {
        var items = await _artworks.ListAsync(limit, kind, ct);
        return Ok(items.Select(e => new
        {
            e.Id, e.Kind, e.Prompt, e.Model, e.FileName, e.Subfolder, e.IsSuccess, e.ErrorMessage,
            e.DurationSeconds, e.CreatedAt,
            url = $"/api/comfy/file?filename={Uri.EscapeDataString(e.FileName)}&subfolder={Uri.EscapeDataString(e.Subfolder)}"
        }));
    }

    /// <summary>删除一条历史记录</summary>
    [HttpDelete("history/{id}")]
    public async Task<IActionResult> DeleteHistory(int id, CancellationToken ct = default)
    {
        var ok = await _artworks.DeleteAsync(id, ct);
        return ok ? Ok(new { ok = true }) : NotFound();
    }

    private async Task<AiComfyArtworkDto?> SaveRecordAsync(
        GenerateImageRequest req, string kind, string model, string promptId,
        bool success, string? error, double duration, int seed, ComfyOutputFile? file)
    {
        return await _artworks.CreateAsync(new SaveAiComfyArtworkRequest
        {
            Kind = kind,
            Prompt = req.Prompt,
            Model = model,
            ParamsJson = JsonSerializer.Serialize(new { req.Width, req.Height, req.Steps, seed }),
            FileName = file?.Filename ?? "",
            Subfolder = file?.Subfolder ?? "",
            FileType = file?.Type ?? "output",
            PromptId = promptId,
            IsSuccess = success,
            ErrorMessage = error,
            DurationSeconds = Math.Round(duration, 1)
        });
    }
}
