using System.Security.Cryptography;
using System.Text;
using Baihua.Contracts.ComputePool;
using Baihua.Contracts.Draw;
using Baihua.Core.Security;
using Baihua.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Baihua.Modules.Family.Controllers.ComputePool;

/// <summary>
/// 算力池绘图网关（/mg/pool/v1/draw/*，X-Server-Token 或 Bearer 鉴权）。
/// 让局域网内其它百花服务器 / DSH 插件能跨机调用本机的文生图/文生视频（本地 ComfyUI）。
/// 与 /mg/capabilities 广播的 DrawCapability 配套：对端发现本机可绘图后再调用。
/// </summary>
[ApiController]
public class DrawGatewayController : ControllerBase
{
    private readonly ComfyDrawService _draw;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DrawGatewayController> _logger;

    public DrawGatewayController(
        ComfyDrawService draw,
        IConfiguration configuration,
        ILogger<DrawGatewayController> logger)
    {
        _draw = draw;
        _configuration = configuration;
        _logger = logger;
    }

    private bool Authorize()
    {
        var expected = _configuration["BAIHUA_AI_EXTERNAL_TOKEN"] ?? "";

        // 回环 + 管理允许网段免鉴权（本机 DSH 等信任面），否则才要求 token（跨机安全）
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        if (remoteIp != null)
        {
            var allowed = AdminNetworkPolicy.ParseNets(
                Environment.GetEnvironmentVariable(AdminNetworkPolicy.AdminAllowedNetsEnv));
            if (AdminNetworkPolicy.IsAllowed(remoteIp, allowed)) return true;
        }

        if (string.IsNullOrEmpty(expected)) return true;

        var auth = Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return string.Equals(auth["Bearer ".Length..].Trim(), expected, StringComparison.Ordinal);

        var token = Request.Headers["X-Server-Token"].FirstOrDefault();
        if (string.Equals(token, expected, StringComparison.Ordinal)) return true;

        // 浏览器直接打开文件 URL 时无法带自定义 Header，允许 ?token= 或 ?x-server-token=
        var queryToken = Request.Query["token"].FirstOrDefault() ?? Request.Query["x-server-token"].FirstOrDefault();
        return string.Equals(queryToken, expected, StringComparison.Ordinal);
    }

    private const int FileLinkTtlSeconds = 600;

    /// <summary>生成短时签名下载 URL（避免把原始 API token 暴露在浏览器链接里）。</summary>
    private string? BuildFileUrl(string filename, string subfolder, string type)
    {
        var expected = _configuration["BAIHUA_AI_EXTERNAL_TOKEN"] ?? "";
        if (string.IsNullOrEmpty(expected)) return null;

        var expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + FileLinkTtlSeconds;
        var sig = SignFileLink(expected, filename, subfolder, type, expires);
        return $"{Request.Scheme}://{Request.Host}{Request.PathBase}/mg/pool/v1/draw/file" +
               $"?filename={Uri.EscapeDataString(filename)}&subfolder={Uri.EscapeDataString(subfolder)}" +
               $"&type={Uri.EscapeDataString(type)}&expires={expires}&sig={sig}";
    }

    private bool AuthorizeFileLink(string filename, string subfolder, string type)
    {
        var expected = _configuration["BAIHUA_AI_EXTERNAL_TOKEN"] ?? "";
        if (string.IsNullOrEmpty(expected)) return true;

        var expiresRaw = Request.Query["expires"].FirstOrDefault();
        var sig = Request.Query["sig"].FirstOrDefault();
        if (string.IsNullOrEmpty(expiresRaw) || string.IsNullOrEmpty(sig)) return false;
        if (!long.TryParse(expiresRaw, out var expires)) return false;
        if (expires < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;

        var expectedSig = SignFileLink(expected, filename, subfolder, type, expires);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedSig);
        var actualBytes = Encoding.UTF8.GetBytes(sig.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static string SignFileLink(string secret, string filename, string subfolder, string type, long expires)
    {
        var message = $"{filename}\n{subfolder}\n{type}\n{expires}";
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(message));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>绘图能力（ComfyUI 在线 + 支持图像/视频 + checkpoint/UNet/CLIP/VAE 模型清单）。对端发现用。</summary>
    [HttpGet("/mg/pool/v1/draw/capabilities")]
    public async Task<ActionResult<DrawCapabilityDto>> Capabilities(CancellationToken ct)
    {
        if (!Authorize()) return Unauthorized(new { error = "invalid token" });
        return Ok(await _draw.GetCapabilitiesAsync(ct));
    }

    /// <summary>文生图（跨机调用）。</summary>
    [HttpPost("/mg/pool/v1/draw/image")]
    public async Task<ActionResult<DrawResultDto>> Image([FromBody] DrawImageRequest request, CancellationToken ct)
    {
        if (!Authorize()) return Unauthorized(new { error = "invalid token" });
        if (string.IsNullOrWhiteSpace(request.Prompt))
            return BadRequest(new DrawResultDto { Success = false, Error = "prompt 不能为空" });
        _logger.LogInformation("[ComputePool] 对端文生图请求: Prompt={Prompt}", request.Prompt);
        var result = await _draw.GenerateImageAsync(request, ct);
        if (result.Success && !string.IsNullOrEmpty(result.FileName))
            result.FileUrl = BuildFileUrl(result.FileName!, "", "output");
        return Ok(result);
    }

    /// <summary>文生视频（跨机调用）。</summary>
    [HttpPost("/mg/pool/v1/draw/video")]
    public async Task<ActionResult<DrawResultDto>> Video([FromBody] DrawVideoRequest request, CancellationToken ct)
    {
        if (!Authorize()) return Unauthorized(new { error = "invalid token" });
        if (string.IsNullOrWhiteSpace(request.Prompt))
            return BadRequest(new DrawResultDto { Success = false, Error = "prompt 不能为空" });
        _logger.LogInformation("[ComputePool] 对端文生视频请求: Prompt={Prompt}", request.Prompt);
        var result = await _draw.GenerateVideoAsync(request, ct);
        if (result.Success && !string.IsNullOrEmpty(result.FileName))
            result.FileUrl = BuildFileUrl(result.FileName!, "", "output");
        return Ok(result);
    }

    /// <summary>取生成的文件（图片/视频字节），经本机中转（对端客户端无需直连 ComfyUI）。</summary>
    [HttpGet("/mg/pool/v1/draw/file")]
    public async Task<IActionResult> File(string filename, string subfolder = "", string type = "output", CancellationToken ct = default)
    {
        if (!Authorize() && !AuthorizeFileLink(filename, subfolder, type))
            return Unauthorized(new { error = "invalid token" });
        if (string.IsNullOrWhiteSpace(filename))
            return BadRequest("filename 不能为空");
        try
        {
            var bytes = await _draw.GetFileAsync(filename, subfolder, type, ct);
            return File(bytes, MimeFor(filename));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[ComputePool] 读取绘图文件失败: {File} ({Msg})", filename, ex.Message);
            return NotFound(new { error = "文件不存在或 ComfyUI 不可达" });
        }
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
