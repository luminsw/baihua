using System.Text.Json;
using Baihua.Contracts.ComputePool;
using Baihua.Modules.Family.Services.ComputePool;
using Microsoft.AspNetCore.Mvc;

namespace Baihua.Modules.Family.Controllers.ComputePool;

/// <summary>
/// 算力池管理端点（管理 API，WebUI /compute 页用）。
/// </summary>
[ApiController]
[Route("api/compute-pool")]
public class ComputePoolController : ControllerBase
{
    private readonly ComputePoolService _poolService;
    private readonly ILogger<ComputePoolController> _logger;

    public ComputePoolController(ComputePoolService poolService, ILogger<ComputePoolController> logger)
    {
        _poolService = poolService;
        _logger = logger;
    }

    /// <summary>算力池总览（本机 + 各对端节点与模型）。</summary>
    [HttpGet]
    public async Task<ActionResult<ComputePoolViewDto>> GetPool(CancellationToken ct)
    {
        return Ok(await _poolService.GetPoolViewAsync(ct));
    }

    /// <summary>立即刷新对端能力（可选，正常每 60s 自动刷新）。</summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        await _poolService.RefreshAsync(ct);
        return Ok(new { success = true });
    }

    /// <summary>选用某个节点+模型为本机主 AI 提供方。</summary>
    [HttpPost("select")]
    public async Task<IActionResult> Select([FromBody] SelectComputeModelRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ServerId) || string.IsNullOrWhiteSpace(request.ModelName))
            return BadRequest(new { error = "缺少 serverId 或 modelName" });

        var (ok, error) = await _poolService.SelectModelAsync(request.ServerId.Trim(), request.ModelName.Trim(), ct);
        if (!ok)
            return BadRequest(new { error });
        return Ok(new { success = true, message = $"已选用 {request.ModelName}" });
    }

    /// <summary>跨机测速：在指定节点（本机或对端）运行该模型的快速 benchmark。</summary>
    [HttpPost("benchmark")]
    public async Task<ActionResult<BenchmarkRunResultDto>> Benchmark([FromBody] SelectComputeModelRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ServerId) || string.IsNullOrWhiteSpace(request.ModelName))
            return BadRequest(new { error = "缺少 serverId 或 modelName" });

        var result = await _poolService.RunPeerBenchmarkAsync(request.ServerId.Trim(), request.ModelName.Trim(), ct);
        return result != null ? Ok(result) : Ok(new BenchmarkRunResultDto { Success = false, Error = "测速失败（节点不可达或未就绪）", ModelName = request.ModelName.Trim() });
    }

    /// <summary>算力池深度任务：指定模型+提示词，经统一网关（全网路由+速度优先+failover）执行。</summary>
    [HttpPost("chat")]
    public async Task<IActionResult> PoolChat([FromBody] PoolChatRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ModelName) || string.IsNullOrWhiteSpace(request.Prompt))
            return BadRequest(new { error = "缺少 modelName 或 prompt" });

        var hostIp = Environment.GetEnvironmentVariable("BAIHUA_HOST_IP");
        var localUrl = !string.IsNullOrWhiteSpace(hostIp) ? $"http://{hostIp}" : "http://127.0.0.1";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var payload = new
            {
                model = request.ModelName.Trim(),
                messages = new object[]
                {
                    new { role = "user", content = request.Prompt }
                }
            };
            using var resp = await client.PostAsJsonAsync($"{localUrl}/mg/pool/v1/chat/completions", payload, ct);
            if (!resp.IsSuccessStatusCode)
                return StatusCode((int)resp.StatusCode, new { error = await resp.Content.ReadAsStringAsync(ct) });
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
            var text = "";
            if (json.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var msg = choices[0].TryGetProperty("message", out var m) ? m : default;
                text = msg.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            }
            return Ok(new { success = true, text, model = request.ModelName.Trim() });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
    }

    /// <summary>从对端拉取模型（模型商店）。</summary>
    [HttpPost("pull-model")]
    public async Task<IActionResult> PullModel([FromBody] PullModelRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ServerId) || string.IsNullOrWhiteSpace(request.ModelName))
            return BadRequest(new { error = "缺少 serverId 或 modelName" });

        var (ok, error) = await _poolService.PullPeerModelAsync(request.ServerId.Trim(), request.ModelName.Trim(), ct);
        return ok ? Ok(new { success = true, message = $"已拉取 {request.ModelName}" }) : BadRequest(new { error });
    }

    /// <summary>跨机布署：本机已有模型 → 对端拉取并启动运行时（常驻推理服务）。</summary>
    [HttpPost("deploy")]
    public async Task<ActionResult<DeployModelResultDto>> Deploy([FromBody] DeployModelRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ServerId) || string.IsNullOrWhiteSpace(request.ModelName))
            return BadRequest(new DeployModelResultDto { Success = false, Error = "缺少 serverId 或 modelName", ModelName = request.ModelName });

        var result = await _poolService.DeployPeerModelAsync(request.ServerId.Trim(), request.ModelName.Trim(), request.Device, ct);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>删除对端登记（同步清理该对端自动登记的对端提供方，供算力池页清理过期节点）。</summary>
    [HttpDelete("peers/{id:guid}")]
    public async Task<IActionResult> DeletePeer(Guid id, CancellationToken ct)
    {
        var (ok, error) = await _poolService.DeletePeerAsync(id, ct);
        return ok ? Ok(new { success = true }) : NotFound(new { error });
    }
}
