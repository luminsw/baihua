using Microsoft.AspNetCore.Mvc;
using Baihua.Contracts.Draw;
using Baihua.Core.Services;

namespace Baihua.Modules.Family.Controllers;

/// <summary>
/// 本地 AI 绘图 API：文生图（SD）与文生视频（LTX Video），走本机 ComfyUI。
/// 生成同步等待完成（图片约 20-60s，视频 1-5 分钟），完成后经 /api/draw/file 取文件。
/// </summary>
[ApiController]
[Route("api/draw")]
public class DrawController : ControllerBase
{
    private readonly ComfyDrawService _draw;
    private readonly ILogger<DrawController> _logger;

    public DrawController(ComfyDrawService draw, ILogger<DrawController> logger)
    {
        _draw = draw;
        _logger = logger;
    }

    /// <summary>绘图能力状态：ComfyUI 在线与否 + 可用 checkpoint/UNet/CLIP/VAE。</summary>
    [HttpGet("status")]
    public async Task<ActionResult<DrawStatusDto>> GetStatus(CancellationToken ct)
    {
        var dto = new DrawStatusDto { ComfyUiOnline = await _draw.IsAvailableAsync(ct) };
        if (dto.ComfyUiOnline)
        {
            var checkpoints = await _draw.GetCheckpointsAsync(ct);
            foreach (var ck in checkpoints)
            {
                var lower = ck.ToLowerInvariant();
                if (lower.Contains("ltx") || lower.Contains("wan") || lower.Contains("hunyuan") || lower.Contains("cog") || lower.Contains("mochi"))
                    dto.VideoCheckpoints.Add(ck);
                else
                    dto.ImageCheckpoints.Add(ck);
            }
            if (dto.VideoCheckpoints.Count == 0)
                dto.VideoCheckpoints.Add(ComfyWorkflowBuilder.DefaultVideoCheckpoint);
            if (dto.ImageCheckpoints.Count == 0)
                dto.ImageCheckpoints.Add(ComfyWorkflowBuilder.DefaultImageCheckpoint);
            dto.UnetModels = await _draw.GetUnetNamesAsync(ct);
            dto.ClipModels = await _draw.GetClipNamesAsync(ct);
            dto.VaeModels = await _draw.GetVaeNamesAsync(ct);
        }
        return Ok(dto);
    }

    /// <summary>文生图（txt2img）。</summary>
    [HttpPost("image")]
    public async Task<ActionResult<DrawResultDto>> GenerateImage([FromBody] DrawImageRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            return BadRequest(new DrawResultDto { Success = false, Error = "prompt 不能为空" });
        return Ok(await _draw.GenerateImageAsync(request, ct));
    }

    /// <summary>文生视频（txt2video，LTX）。</summary>
    [HttpPost("video")]
    public async Task<ActionResult<DrawResultDto>> GenerateVideo([FromBody] DrawVideoRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            return BadRequest(new DrawResultDto { Success = false, Error = "prompt 不能为空" });
        return Ok(await _draw.GenerateVideoAsync(request, ct));
    }

    /// <summary>取生成的文件（图片/视频字节），经百花中转避免客户端直连 ComfyUI。</summary>
    [HttpGet("file")]
    public async Task<IActionResult> GetFile(string filename, string subfolder = "", string type = "output", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return BadRequest("filename 不能为空");

        byte[] bytes;
        try
        {
            bytes = await _draw.GetFileAsync(filename, subfolder, type, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 ComfyUI 文件失败: {File}", filename);
            return NotFound(new { error = "文件不存在或 ComfyUI 不可达" });
        }
        return File(bytes, MimeFor(filename));
    }

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
