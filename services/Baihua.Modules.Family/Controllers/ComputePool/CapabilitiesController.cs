using Baihua.Contracts.ComputePool;
using Baihua.Core;
using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Modules.Family.Services;
using Baihua.Modules.Family.Services.ComputePool;
using Microsoft.AspNetCore.Mvc;

namespace Baihua.Modules.Family.Controllers.ComputePool;

/// <summary>
/// 本机算力能力广播端点（/mg/capabilities，X-Server-Token 鉴权，公开路径自校验）。
/// 供局域网内其他百花服务器发现本机的 AI 提供方/模型/算力，实现跨机选用。
/// </summary>
[ApiController]
public class CapabilitiesController : ControllerBase
{
    private readonly ServerAddressService _serverAddress;
    private readonly AiSettingsService _aiSettings;
    private readonly IAiConfigService _aiConfig;
    private readonly BenchmarkRepository _benchmarkRepository;
    private readonly ComfyDrawService _draw;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CapabilitiesController> _logger;

    public CapabilitiesController(
        ServerAddressService serverAddress,
        AiSettingsService aiSettings,
        IAiConfigService aiConfig,
        BenchmarkRepository benchmarkRepository,
        ComfyDrawService draw,
        IConfiguration configuration,
        ILogger<CapabilitiesController> logger)
    {
        _serverAddress = serverAddress;
        _aiSettings = aiSettings;
        _aiConfig = aiConfig;
        _benchmarkRepository = benchmarkRepository;
        _draw = draw;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>本机能力清单（对端服务器用 X-Server-Token 拉取）</summary>
    [HttpGet("/mg/capabilities")]
    public async Task<ActionResult<ComputeNodeCapabilitiesDto>> GetCapabilities(CancellationToken ct)
    {
        var localToken = _configuration["BAIHUA_SERVER_MSG_TOKEN"] ?? "";
        var token = Request.Headers["X-Server-Token"].FirstOrDefault();
        if (!string.IsNullOrEmpty(localToken) && !string.Equals(token, localToken, StringComparison.Ordinal))
        {
            _logger.LogWarning("[ComputePool] capabilities rejected: 口令校验失败");
            return Unauthorized(new { error = "口令校验失败" });
        }

        var settings = _serverAddress.GetSettings();
        var hostIp = _configuration["BAIHUA_HOST_IP"];
        var hostUrl = !string.IsNullOrWhiteSpace(hostIp)
            ? $"http://{hostIp}"
            : _serverAddress.GetLocalPublicBaseUrl();
        // 对外推理端点：统一网关 /mg/pool/v1（按模型名路由全网最快节点）；
        // 显式配置 BAIHUA_PUBLIC_OPENAI_BASE_URL 可覆盖
        var openAiBaseUrl = _configuration["BAIHUA_PUBLIC_OPENAI_BASE_URL"];
        if (string.IsNullOrWhiteSpace(openAiBaseUrl))
            openAiBaseUrl = $"{hostUrl}/mg/pool/v1";

        // 本机可对外提供的模型 = 本机 Family 直接可用的提供方 + AI 服务（shim 路由的提供方）
        // （shim 按模型名在 AI 服务内路由，因此以 AI 服务的提供方为准，合并去重）
        // 只广播本地算力（Tier1/2，非 peer-）：云端模型各机器可直连、peer- 是别人的模型，
        // 两者进池只会让算力池模型重复。
        var models = new List<ComputeModelDto>();
        var providerGroups = _aiSettings.GetAiProviders()
            .Where(p => p.Models is { Count: > 0 })
            .Where(ComputePoolShareFilter.IsShareable)
            .Select(p => new ComputeProviderDto
            {
                Id = p.Id,
                Name = p.Name,
                Tier = ((int)p.Tier).ToString(),
                Models = p.Models.Select(m => new ComputeModelDto
                {
                    Name = m.Name,
                    IsMain = m.IsMain,
                    TokensPerSecond = GetBenchmarkTps(m.Name)
                }).ToList()
            })
            .ToList();

        foreach (var remote in await GetAiServiceProvidersAsync())
        {
            if (!ComputePoolShareFilter.IsShareable(remote))
                continue;
            if (providerGroups.Any(g => string.Equals(g.Id, remote.Id, StringComparison.OrdinalIgnoreCase)))
                continue;
            providerGroups.Add(remote);
        }

        // 绘图能力：对端可据以判断是否调用 /mg/pool/v1/draw/*
        var comfyOnline = await _draw.IsAvailableAsync(ct);
        var drawCapability = new DrawCapabilityDto
        {
            ComfyOnline = comfyOnline,
            Image = comfyOnline,
            Video = comfyOnline,
            ImageCheckpoint = comfyOnline ? ComfyWorkflowBuilder.DefaultImageCheckpoint : null,
            VideoCheckpoint = comfyOnline ? ComfyWorkflowBuilder.DefaultVideoCheckpoint : null
        };

        return Ok(new ComputeNodeCapabilitiesDto
        {
            ServerId = _serverAddress.GetServerInstanceId(),
            Name = string.IsNullOrWhiteSpace(settings.DisplayName) ? Environment.MachineName : settings.DisplayName,
            HostUrl = hostUrl,
            OpenAiBaseUrl = openAiBaseUrl,
            Providers = providerGroups,
            Draw = drawCapability,
            GpuName = null,
            GpuVramGb = null,
            CpuCores = Environment.ProcessorCount,
            UpdatedAt = DateTime.UtcNow
        });
    }

    /// <summary>查排行榜里该模型最近一次实测 token/s（无记录返回 null）。</summary>
    private double? GetBenchmarkTps(string modelName)
    {
        try
        {
            return _benchmarkRepository.GetLeaderboard()
                .Where(h => string.Equals(h.ModelName, modelName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(h => h.LastTestedAt)
                .Select(h => h.AvgTokensPerSecond)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>本机可对外提供的算力（提供方配置归 AI 模块，进程内经 IAiConfigService 读取）。</summary>
    private Task<List<ComputeProviderDto>> GetAiServiceProvidersAsync()
    {
        try
        {
            var providers = _aiConfig.GetProviders();
            return Task.FromResult(providers
                .Where(p => p.Models is { Count: > 0 })
                .Select(p => new ComputeProviderDto
                {
                    Id = p.Id,
                    Name = p.Name,
                    Tier = ((int)p.Tier).ToString(),
                    Models = p.Models.Select(m => new ComputeModelDto
                    {
                        Name = m.Name,
                        IsMain = m.IsMain,
                        TokensPerSecond = GetBenchmarkTps(m.Name)
                    }).ToList()
                })
                .ToList());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取本机 AI 提供方失败（不影响 capabilities 主流程）");
            return Task.FromResult(new List<ComputeProviderDto>());
        }
    }
}
