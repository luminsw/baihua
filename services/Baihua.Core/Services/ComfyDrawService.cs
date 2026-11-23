using Baihua.Contracts.ComputePool;
using Baihua.Contracts.Draw;
using Microsoft.Extensions.Logging;

namespace Baihua.Core.Services;

/// <summary>
/// ComfyUI 绘图服务：提交 txt2img / txt2video 工作流并同步轮询到完成。
/// 供 Family 的 /api/draw/*（本机管理 API）与 /mg/pool/v1/draw/*（算力池对端网关）复用。
/// </summary>
public class ComfyDrawService
{
    private const int PollIntervalMs = 3000;
    private static readonly TimeSpan ImageTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan VideoTimeout = TimeSpan.FromMinutes(5.5);

    private readonly ComfyUiClient _comfy;
    private readonly ILogger<ComfyDrawService> _logger;

    public ComfyDrawService(ComfyUiClient comfy, ILogger<ComfyDrawService> logger)
    {
        _comfy = comfy;
        _logger = logger;
    }

    /// <summary>ComfyUI 是否在线。</summary>
    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => _comfy.IsAvailableAsync(ct);

    /// <summary>可用 checkpoint 列表。</summary>
    public Task<List<string>> GetCheckpointsAsync(CancellationToken ct = default) => _comfy.GetCheckpointsAsync(ct);

    /// <summary>可用 UNet 模型（Z-Image-Turbo 等）。</summary>
    public Task<List<string>> GetUnetNamesAsync(CancellationToken ct = default) => _comfy.GetUnetNamesAsync(ct);

    /// <summary>可用 CLIP 文本编码器。</summary>
    public Task<List<string>> GetClipNamesAsync(CancellationToken ct = default) => _comfy.GetClipNamesAsync(ct);

    /// <summary>可用 VAE。</summary>
    public Task<List<string>> GetVaeNamesAsync(CancellationToken ct = default) => _comfy.GetVaeNamesAsync(ct);

    /// <summary>绘图能力（ComfyUI 在线 + 图像/视频 checkpoint 全集 + Z-Image-Turbo 的 UNet/CLIP/VAE）。供本地状态与对端网关复用。</summary>
    public async Task<DrawCapabilityDto> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        var online = await _comfy.IsAvailableAsync(ct);
        var dto = new DrawCapabilityDto
        {
            ComfyOnline = online,
            Image = online,
            Video = online,
            ImageCheckpoint = ComfyWorkflowBuilder.DefaultImageCheckpoint,
            VideoCheckpoint = ComfyWorkflowBuilder.DefaultVideoCheckpoint
        };
        if (!online) return dto;

        foreach (var ck in await _comfy.GetCheckpointsAsync(ct))
        {
            var lower = ck.ToLowerInvariant();
            if (lower.Contains("ltx") || lower.Contains("wan") || lower.Contains("hunyuan") || lower.Contains("cog") || lower.Contains("mochi"))
                dto.VideoCheckpoints.Add(ck);
            else
                dto.ImageCheckpoints.Add(ck);
        }
        if (dto.VideoCheckpoints.Count == 0) dto.VideoCheckpoints.Add(ComfyWorkflowBuilder.DefaultVideoCheckpoint);
        if (dto.ImageCheckpoints.Count == 0) dto.ImageCheckpoints.Add(ComfyWorkflowBuilder.DefaultImageCheckpoint);

        dto.UnetModels = await _comfy.GetUnetNamesAsync(ct);
        dto.ClipModels = await _comfy.GetClipNamesAsync(ct);
        dto.VaeModels = await _comfy.GetVaeNamesAsync(ct);
        return dto;
    }

    /// <summary>获取生成的文件（图片/视频字节）。</summary>
    public Task<byte[]> GetFileAsync(string filename, string subfolder = "", string type = "output", CancellationToken ct = default)
        => _comfy.GetFileAsync(filename, subfolder, type, ct);

    /// <summary>文生图：按模型类型提交 SD1.5 或 Z-Image-Turbo 工作流并等待完成，返回结果 DTO。</summary>
    public async Task<DrawResultDto> GenerateImageAsync(DrawImageRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            return new DrawResultDto { Success = false, Error = "prompt 不能为空" };

        var modelType = NormalizeImageModelType(request.ModelType);
        var isTurbo = modelType == ComfyWorkflowBuilder.ZImageTurboModelType;
        var seed = request.Seed is > 0 ? request.Seed.Value : Random.Shared.NextInt64(0, long.MaxValue);
        var width = request.Width is > 0 and <= 2048 ? request.Width.Value : (isTurbo ? 1024 : 512);
        var height = request.Height is > 0 and <= 2048 ? request.Height.Value : (isTurbo ? 1024 : 512);
        var steps = request.Steps is > 0 and <= 100 ? request.Steps.Value : (isTurbo ? 8 : 20);

        Dictionary<string, object> workflow;
        if (isTurbo)
        {
            var unetName = string.IsNullOrWhiteSpace(request.UnetName)
                ? ComfyWorkflowBuilder.DefaultZImageTurboUnet
                : request.UnetName!;
            var clipName = string.IsNullOrWhiteSpace(request.ClipName)
                ? ComfyWorkflowBuilder.DefaultZImageTurboClip
                : request.ClipName!;
            var vaeName = string.IsNullOrWhiteSpace(request.VaeName)
                ? ComfyWorkflowBuilder.DefaultZImageTurboVae
                : request.VaeName!;
            workflow = ComfyWorkflowBuilder.BuildTxt2ImageZImageTurbo(
                request.Prompt, request.NegativePrompt, width, height, steps, seed, unetName, clipName, vaeName,
                cfg: request.Cfg is > 0 ? request.Cfg.Value : 1.0,
                sampler: string.IsNullOrWhiteSpace(request.Sampler) ? "res_multistep" : request.Sampler!,
                scheduler: string.IsNullOrWhiteSpace(request.Scheduler) ? "simple" : request.Scheduler!);
        }
        else
        {
            var checkpoint = string.IsNullOrWhiteSpace(request.Checkpoint)
                ? ComfyWorkflowBuilder.DefaultImageCheckpoint
                : request.Checkpoint!;
            workflow = ComfyWorkflowBuilder.BuildTxt2Image(
                request.Prompt, request.NegativePrompt, width, height, steps, seed, checkpoint,
                cfg: request.Cfg is > 0 ? request.Cfg.Value : 7.0,
                sampler: string.IsNullOrWhiteSpace(request.Sampler) ? "euler" : request.Sampler!,
                scheduler: string.IsNullOrWhiteSpace(request.Scheduler) ? "normal" : request.Scheduler!);
        }

        return await GenerateAndWaitAsync(workflow, ImageTimeout, ct);
    }

    private static string NormalizeImageModelType(string? modelType)
    {
        var v = modelType?.Trim().ToLowerInvariant();
        return v is "z-image-turbo" or "zimage-turbo" or "z_image_turbo" or "z-image" or "zimage"
            ? ComfyWorkflowBuilder.ZImageTurboModelType
            : ComfyWorkflowBuilder.DefaultImageModelType;
    }

    /// <summary>文生视频：提交 LTX 工作流并等待完成，返回结果 DTO。</summary>
    public async Task<DrawResultDto> GenerateVideoAsync(DrawVideoRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            return new DrawResultDto { Success = false, Error = "prompt 不能为空" };

        var seed = request.Seed is > 0 ? request.Seed.Value : Random.Shared.NextInt64(0, long.MaxValue);
        var width = request.Width is > 0 and <= 768 ? request.Width.Value : 512;
        var height = request.Height is > 0 and <= 768 ? request.Height.Value : 512;
        var length = request.Length is > 0 and <= 257 ? request.Length.Value : 97;
        var fps = request.Fps is > 0 and <= 60 ? request.Fps.Value : 25;
        var steps = request.Steps is > 0 and <= 100 ? request.Steps.Value : 20;
        var checkpoint = string.IsNullOrWhiteSpace(request.Checkpoint)
            ? ComfyWorkflowBuilder.DefaultVideoCheckpoint
            : request.Checkpoint!;

        var workflow = ComfyWorkflowBuilder.BuildTxt2Video(
            request.Prompt, request.NegativePrompt, width, height, length, fps, steps, seed, checkpoint,
            cfg: request.Cfg is > 0 ? request.Cfg.Value : 4.0,
            sampler: string.IsNullOrWhiteSpace(request.Sampler) ? "euler" : request.Sampler!,
            scheduler: string.IsNullOrWhiteSpace(request.Scheduler) ? "sgm_uniform" : request.Scheduler!);
        return await GenerateAndWaitAsync(workflow, VideoTimeout, ct);
    }

    private async Task<DrawResultDto> GenerateAndWaitAsync(Dictionary<string, object> workflow, TimeSpan timeout, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        try
        {
            var promptId = await _comfy.SubmitAsync(workflow, ct);
            _logger.LogInformation("ComfyUI 生成已提交: promptId={PromptId}", promptId);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            while (true)
            {
                var result = await _comfy.GetResultAsync(promptId, deadline.Token);
                if (result == null)
                {
                    await Task.Delay(PollIntervalMs, deadline.Token);
                    continue;
                }
                if (result.IsError)
                    return new DrawResultDto { Success = false, Error = result.Error ?? "ComfyUI 执行出错", ElapsedSeconds = Elapsed(started) };

                var file = result.Files.FirstOrDefault();
                if (file == null)
                    return new DrawResultDto { Success = false, Error = "生成完成但未找到输出文件", ElapsedSeconds = Elapsed(started) };

                _logger.LogInformation("ComfyUI 生成完成: promptId={PromptId}, file={File}, elapsed={Elapsed}s", promptId, file.Filename, Elapsed(started));
                return new DrawResultDto
                {
                    Success = true,
                    FileName = file.Filename,
                    ContentType = MimeFor(file.Filename),
                    ElapsedSeconds = Elapsed(started)
                };
            }
        }
        catch (OperationCanceledException)
        {
            return new DrawResultDto { Success = false, Error = "生成超时（请稍后重试或调低分辨率/帧数）", ElapsedSeconds = Elapsed(started) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ComfyUI 绘图失败");
            return new DrawResultDto { Success = false, Error = $"ComfyUI 调用失败：{ex.Message}", ElapsedSeconds = Elapsed(started) };
        }
    }

    private static double Elapsed(DateTime started) => Math.Round((DateTime.UtcNow - started).TotalSeconds, 1);

    private static string MimeFor(string filename)
    {
        var ext = System.IO.Path.GetExtension(filename).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            _ => "application/octet-stream",
        };
    }
}
