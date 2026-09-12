using Baihua.Modules.Family.Services.ComputePool;
using Microsoft.AspNetCore.Mvc;

namespace Baihua.Modules.Family.Controllers;

/// <summary>DSH（DeepSeek Harness）自举/目录端点。</summary>
[ApiController]
[Route("api/dsh")]
public class DshController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly ComputePoolService _poolService;
    private readonly ILogger<DshController> _logger;

    public DshController(IConfiguration configuration, ComputePoolService poolService, ILogger<DshController> logger)
    {
        _configuration = configuration;
        _poolService = poolService;
        _logger = logger;
    }

    /// <summary>
    /// 返回本机百花拓扑，供本机 DSH 插件「一个入口 + 免配置」自举。
    /// 鉴权：本机（回环 + BAIHUA_ADMIN_ALLOWED_NETS）免鉴权；其余来源要求
    /// BAIHUA_AI_EXTERNAL_TOKEN（Bearer / X-Server-Token / ?token=）。
    /// 返回的地址：base 字段即客户端本次访问所用的主机（如 http://127.0.0.1），
    /// 服务路径（/mg/pool/v1、/api、/mg/ai/v1 等）均可直接拼接在 base 上；
    /// vault / ai 为固定 ClusterIP（宿主机直连、跨部署不变）。
    /// </summary>
    [HttpGet("config")]
    public ActionResult<object> GetConfig()
    {
        if (!Authorize()) return Unauthorized(new { error = "unauthorized" });

        var baseUrl = $"{Request.Scheme}://{Request.Host}".TrimEnd('/');
        var vault = Environment.GetEnvironmentVariable("BAIHUA_VAULT_URL") ?? "http://127.0.0.1:8790";
        var ai = Environment.GetEnvironmentVariable("BAIHUA_AI_URL") ?? "http://127.0.0.1:8791";
        var externalToken = _configuration["BAIHUA_AI_EXTERNAL_TOKEN"] ?? "";

        return Ok(new
        {
            ok = true,
            baseUrl,                                  // 宿主机访问本机百花的入口（客户端本次所用）
            familyUrl = baseUrl,                      // 本机 family（/api、/mg/*）
            vaultUrl = vault.TrimEnd('/'),            // 知识库保存/检索（固定 ClusterIP）
            aiUrl = ai.TrimEnd('/'),                  // AI 服务（固定 ClusterIP）
            webUrl = baseUrl,                         // WebUI（Traefik :80，/ 直达）
            drawGatewayUrl = baseUrl,                 // 绘图网关（comfy 客户端会拼接 /mg/pool/v1/draw/*）
            poolUrl = $"{baseUrl}/mg/pool/v1",        // 算力池统一网关（供 local-ai 探测 /models）
            aiShimUrl = $"{baseUrl}/mg/ai/v1",        // OpenAI 兼容 shim（Traefik /mg/ai/ → ai）
            drawToken = externalToken,                // 绘图/池网关 token（本地免鉴权时为空）
            poolToken = externalToken,
            comfyModelType = _configuration["BAIHUA_COMFY_MODEL_TYPE"] ?? "z-image-turbo",
            comfyCheckpoint = _configuration["BAIHUA_COMFY_CHECKPOINT"] ?? "v1-5-pruned-emaonly.safetensors",
        });
    }

    /// <summary>
    /// 算力池目录：peer 名 → 能力/网关地址。供 DSH「按节点名跨机调用」，
    /// 如 baihua_draw(target="节点名")。本机 + 各对端节点的 Name/HostUrl/Draw/models。
    /// 鉴权与 config 相同（本机免鉴权，否则要 token）。
    /// </summary>
    [HttpGet("pool")]
    public async Task<ActionResult<object>> GetPool(CancellationToken ct)
    {
        if (!Authorize()) return Unauthorized(new { error = "unauthorized" });
        var view = await _poolService.GetPoolViewAsync(ct);
        // 简化给 DSH：每节点暴露 名字/入口/绘图网关/在线 状态
        var nodes = view.Nodes.Select(n => new
        {
            id = n.ServerId,
            name = n.Name,
            hostUrl = n.HostUrl,
            isLocal = n.IsLocal,
            online = n.Online,
            drawGatewayUrl = string.IsNullOrWhiteSpace(n.HostUrl) ? "" : $"{n.HostUrl.TrimEnd('/')}/mg/pool/v1/draw",
            draw = n.Draw != null ? new { comfyOnline = n.Draw.ComfyOnline, image = n.Draw.Image, video = n.Draw.Video, imageCheckpoint = n.Draw.ImageCheckpoint, videoCheckpoint = n.Draw.VideoCheckpoint } : null,
            models = n.Providers.SelectMany(p => p.Models ?? new()).Select(m => m.Name).Distinct().ToList(),
        });
        return Ok(new { ok = true, updatedAt = view.UpdatedAt, nodes });
    }

    private bool Authorize()
    {
        // 本机（回环 + 管理允许网段）免鉴权
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        if (remoteIp != null)
        {
            var allowed = Baihua.Core.Security.AdminNetworkPolicy.ParseNets(
                Environment.GetEnvironmentVariable(Baihua.Core.Security.AdminNetworkPolicy.AdminAllowedNetsEnv));
            if (Baihua.Core.Security.AdminNetworkPolicy.IsAllowed(remoteIp, allowed))
                return true;
        }

        // 否则要求 BAIHUA_AI_EXTERNAL_TOKEN
        var expected = _configuration["BAIHUA_AI_EXTERNAL_TOKEN"] ?? "";
        if (string.IsNullOrEmpty(expected))
            return true;

        var auth = Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return string.Equals(auth["Bearer ".Length..].Trim(), expected, StringComparison.Ordinal);
        var token = Request.Headers["X-Server-Token"].FirstOrDefault();
        if (string.Equals(token, expected, StringComparison.Ordinal)) return true;
        var q = Request.Query["token"].FirstOrDefault() ?? Request.Query["x-server-token"].FirstOrDefault();
        return string.Equals(q, expected, StringComparison.Ordinal);
    }
}
