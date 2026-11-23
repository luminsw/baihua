using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Configuration;
using Baihua.Contracts.Capability;
using Baihua.Contracts.LocalModels;
using Baihua.Core.Localization;

namespace Baihua.Core.Services;

/// <summary>
/// 需要本地算力的功能标识
/// </summary>
public enum LocalComputeFeature
{
    LocalModelsPage,
    ModelBenchmark,
    HardwareBenchmark,
    OpenClawLocalConfig,
    SettingsLocalModelDownload,
    MessagesLocalModelSelector,
    AiConfigLocalProviderPresets,
    LocalModelDeployment,
    LocalAiInference,
}

/// <summary>
/// 能力评估服务：根据硬件信息决定哪些功能可以展示
/// </summary>
public class CapabilityService
{
    private readonly HardwareInfoService _hardwareInfo;
    private readonly ILogger<CapabilityService> _logger;
    private readonly IStringLocalizer<SharedResources> _loc;
    private readonly IConfiguration _configuration;
    private MachineCapability? _cachedCapability;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    // 与硬件信息缓存（5 分钟滑动）保持一致的 TTL：
    // 启动瞬间硬件检测可能暂时失败（如驱动未就绪、PowerShell 冷启动超时），
    // 若能力评估缓存不过期，GPU 功能菜单会被永久隐藏，直到手动 POST /api/capability/refresh。
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private readonly object _lock = new();

    public CapabilityService(
        HardwareInfoService hardwareInfo,
        ILogger<CapabilityService> logger,
        IStringLocalizer<SharedResources> loc,
        IConfiguration configuration)
    {
        _hardwareInfo = hardwareInfo;
        _logger = logger;
        _loc = loc;
        _configuration = configuration;
    }

    /// <summary>
    /// 获取当前机器的能力等级（缓存）
    /// </summary>
    public MachineCapability GetCapability()
    {
        var now = DateTimeOffset.UtcNow;
        if (_cachedCapability.HasValue && now - _cachedAt < CacheTtl)
            return _cachedCapability.Value;

        lock (_lock)
        {
            now = DateTimeOffset.UtcNow;
            if (_cachedCapability.HasValue && now - _cachedAt < CacheTtl)
                return _cachedCapability.Value;

            var previous = _cachedCapability;
            _cachedCapability = ComputeCapability();
            _cachedAt = DateTimeOffset.UtcNow;
            if (previous != _cachedCapability)
                _logger.LogInformation("机器能力评估: {Capability}", _cachedCapability.Value);
            return _cachedCapability.Value;
        }
    }

    /// <summary>
    /// 刷新能力评估（硬件变更后调用）
    /// </summary>
    public MachineCapability RefreshCapability()
    {
        lock (_lock)
        {
            _cachedCapability = null;
            _cachedAt = DateTimeOffset.MinValue;
            return GetCapability();
        }
    }

    /// <summary>
    /// 判断指定功能是否可用
    /// </summary>
    public bool CanUse(LocalComputeFeature feature)
    {
        var cap = GetCapability();
        return feature switch
        {
            // 本地模型页是管理下载/目录/运行的入口——CPU 机器同样需要（慢但可用），
            // 不应因未检测到 GPU 而整个隐藏（低配机器集显未被检测到时尤其常见）
            LocalComputeFeature.LocalModelsPage => cap >= MachineCapability.CpuOnly,
            LocalComputeFeature.ModelBenchmark => cap >= MachineCapability.LowEndGpu,
            LocalComputeFeature.LocalModelDeployment => cap >= MachineCapability.LowEndGpu,
            LocalComputeFeature.LocalAiInference => cap >= MachineCapability.LowEndGpu,
            LocalComputeFeature.HardwareBenchmark => true,
            // OpenClaw 不依赖本地 GPU — 即使没有显卡也能运行（云端 AI / 任务管理等功能均可正常使用）
            LocalComputeFeature.OpenClawLocalConfig => true,
            LocalComputeFeature.SettingsLocalModelDownload => cap >= MachineCapability.CpuOnly,
            LocalComputeFeature.MessagesLocalModelSelector => cap >= MachineCapability.LowEndGpu,
            LocalComputeFeature.AiConfigLocalProviderPresets => cap >= MachineCapability.LowEndGpu,
            _ => true
        };
    }

    /// <summary>
    /// 获取功能限制说明
    /// </summary>
    public string? GetRestrictionReason(LocalComputeFeature feature)
    {
        if (CanUse(feature)) return null;

        var cap = GetCapability();
        return cap switch
        {
            MachineCapability.Insufficient => _loc["Capability_MemoryInsufficient"],
            MachineCapability.CpuOnly => _loc["Capability_NoGpu"],
            _ => _loc["Capability_Insufficient"]
        };
    }

    /// <summary>
    /// 获取完整的能力信息（供前端使用）
    /// </summary>
    public CapabilityInfo GetCapabilityInfo()
    {
        var cap = GetCapability();
        var hardware = _hardwareInfo.GetHardwareInfo();

        var isIntelGpu = hardware.Gpus.Count > 0 && hardware.Gpus.Any(g =>
            string.Equals(g.Vendor, "Intel", StringComparison.OrdinalIgnoreCase));

        return new CapabilityInfo
        {
            Level = cap,
            TotalRamGiB = hardware.Memory.TotalGiB,
            MaxVramGiB = hardware.Gpus
                .Where(g => g.VramGiB.HasValue)
                .Max(g => g.VramGiB) ?? 0,
            GpuName = hardware.Gpus.FirstOrDefault(g => !g.IsIntegrated)?.Name
                ?? hardware.Gpus.FirstOrDefault()?.Name
                ?? "无",
            // OpenVINO 本地推理要求 Intel GPU（Arc 独显 / 核显）；检测失败（无 GPU 信息）时视为不可用，
            // 前端据此隐藏或置灰 OpenVINO 相关入口。
            IsIntelGpu = isIntelGpu,
            // OpenVINO 已可拆分为独立 OVMS 服务（如 k8s 的 bh-openvino）、或本地让 OVMS 跑在 localhost，
            // 因此"可用" = 存在 Intel GPU（本地推理） 或 已配置可达的 OVMS 端点。
            // 否则在"family 容器无 GPU / 无 lspci"的全容器部署里，OpenVINO 虽然可跑却会被误隐藏。
            OpenVinoAvailable = isIntelGpu || IsOmsConfiguredRemote(),
            AvailableFeatures = Enum.GetValues<LocalComputeFeature>()
                .Where(f => CanUse(f))
                .Select(f => f.ToString())
                .ToList(),
            RestrictedFeatures = Enum.GetValues<LocalComputeFeature>()
                .Where(f => !CanUse(f))
                .ToDictionary(
                    f => f.ToString(),
                    f => GetRestrictionReason(f) ?? ""
                )
        };
    }

    /// <summary>
    /// 是否已配置一个"非本机回环"的 OVMS 端点（远程 OpenVINO 服务）。
    /// OpenVinoOms:BaseUrl 是默认键；同时兼容 OPENVINO_LLM_URL / OPENVINO_HOST_URL 环境变量。
    /// 未显式禁用（OpenVinoOms:Enabled=false）且存在非 localhost 端点时视为可用。
    /// </summary>
    private bool IsOmsConfiguredRemote()
    {
        if (!IsOmsEnabled()) return false;

        foreach (var key in new[] { "OpenVinoOms:BaseUrl", "OPENVINO_LLM_URL", "OPENVINO_HOST_URL" })
        {
            var url = _configuration[key];
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (!IsLocalhostUrl(url)) return true;
        }
        return false;
    }

    private bool IsOmsEnabled()
    {
        var enabled = _configuration["OpenVinoOms:Enabled"];
        // 未显式配置时默认开启（与 OmsOptions.Enabled 默认 true 一致）
        return string.IsNullOrWhiteSpace(enabled)
            || !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocalhostUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var host = uri.Host;
            return host is "127.0.0.1" or "localhost" or "::1";
        }
        catch
        {
            // 解析失败（非完整 URL）时保守处理：不算"远程"，避免误判为可用
            return true;
        }
    }

    private MachineCapability ComputeCapability()
    {
        try
        {
            var hardware = _hardwareInfo.GetHardwareInfo();
            var tier = HardwareInfoService.GetHardwareTier(hardware);
            var ramGiB = hardware.Memory.TotalGiB;

            if (ramGiB < 8)
                return MachineCapability.Insufficient;

            return tier switch
            {
                HardwareTier.CpuOnly => MachineCapability.CpuOnly,
                HardwareTier.LowEndGpu => MachineCapability.LowEndGpu,
                HardwareTier.MidRangeGpu => MachineCapability.MidEndGpu,
                HardwareTier.HighEndGpu => MachineCapability.HighEndGpu,
                HardwareTier.TopTierGpu => MachineCapability.HighEndGpu,
                _ => ramGiB >= 8 ? MachineCapability.CpuOnly : MachineCapability.Insufficient
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "能力评估失败，默认返回 Insufficient");
            return MachineCapability.Insufficient;
        }
    }


}
