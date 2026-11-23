using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Baihua.Contracts.LocalModels;

namespace Baihua.Core.Services;

/// <summary>
/// 硬件信息检测服务
/// </summary>
public partial class HardwareInfoService
{
    private readonly ILogger<HardwareInfoService> _logger;
    private readonly IMemoryCache _cache;
    private const string CacheKey = "hardware_info";

    private static readonly Dictionary<string, (int CudaCores, double BandwidthGbps, double TflopsFp16)> GpuSpecDb = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RTX 4090"] = (16384, 1008, 82.6),
        ["RTX 4080"] = (9728, 716.8, 48.7),
        ["RTX 4070 Ti"] = (7680, 504, 40.1),
        ["RTX 4070"] = (5888, 504, 29.1),
        ["RTX 4060 Ti"] = (4352, 288, 22.1),
        ["RTX 4060"] = (3072, 272, 15.1),
        ["RTX 3090"] = (10496, 936, 35.6),
        ["RTX 3080"] = (8704, 760, 29.8),
        ["RTX 3070"] = (5888, 608, 20.3),
        ["RTX 3060 Ti"] = (4864, 448, 16.2),
        ["RTX 3060"] = (3584, 360, 12.7),
        ["RTX 2080 Ti"] = (4352, 616, 26.9),
        ["RTX 2080"] = (2944, 448, 14.2),
        ["RTX 2070"] = (2304, 448, 10.1),
        ["GTX 1080 Ti"] = (3584, 484, 11.3),
        ["GTX 1080"] = (2560, 320, 8.9),
        ["GTX 1070"] = (1920, 256, 6.5),
        ["GTX 1060"] = (1280, 192, 4.4),
        ["A100"] = (6912, 1555, 77.9),
        ["A10"] = (9216, 600, 31.2),
        ["H100"] = (16896, 3350, 197.9),
        ["MI100"] = (7680, 1228, 46.1),
        ["MI200"] = (14080, 3200, 95.7),
        ["Apple M1"] = (8, 68, 5.3),
        ["Apple M1 Pro"] = (16, 200, 10.6),
        ["Apple M1 Max"] = (32, 400, 21.2),
        ["Apple M1 Ultra"] = (64, 800, 42.4),
        ["Apple M2"] = (10, 100, 7.4),
        ["Apple M2 Pro"] = (19, 200, 13.6),
        ["Apple M2 Max"] = (38, 400, 27.2),
        ["Apple M2 Ultra"] = (76, 800, 54.4),
        ["Apple M3"] = (10, 100, 8.2),
        ["Apple M3 Pro"] = (18, 200, 14.0),
        ["Apple M3 Max"] = (40, 400, 30.2),
        ["Apple M3 Ultra"] = (80, 800, 60.4),
    };

    public HardwareInfoService(ILogger<HardwareInfoService> logger, IMemoryCache cache)
    {
        _logger = logger;
        _cache = cache;
    }

    public HardwareInfoDto GetHardwareInfo()
    {
        return _cache.GetOrCreate(CacheKey, entry =>
        {
            entry.SlidingExpiration = TimeSpan.FromMinutes(5);
            return DetectHardwareInfo();
        }) ?? new HardwareInfoDto();
    }

    public HardwareInfoDto RefreshHardwareInfo()
    {
        _cache.Remove(CacheKey);
        return GetHardwareInfo();
    }

    public void WarmupCache()
    {
        _ = GetHardwareInfo();
    }

    private HardwareInfoDto DetectHardwareInfo()
    {
        try
        {
            var info = new HardwareInfoDto
            {
                OsPlatform = HardwareInfoHelper.GetOsName(),
                OsVersion = RuntimeInformation.OSDescription,
                IsWsl = HardwareInfoHelper.DetectWsl(),
                Cpu = GetCpuInfo(),
                Memory = GetMemoryInfo(),
                Gpus = GetGpuInfo(),
            };

            // 检测系统内存带宽（集成显卡的"显存带宽"就是系统内存带宽）
            var systemMemoryBandwidth = DetectSystemMemoryBandwidth();

            // 为集成显卡补充显存估算（集成显卡共享系统内存，lspci 等工具获取的 BAR 大小不准确）
            foreach (var gpu in info.Gpus)
            {
                if (gpu.IsIntegrated && (!gpu.VramBytes.HasValue || gpu.VramBytes.Value < 512L * 1024 * 1024))
                {
                    // 估算为系统内存的 50%（集成显卡通常可使用大量系统内存作为显存）
                    gpu.VramBytes = info.Memory.TotalBytes / 2;
                }

                // ---- AI 算力与性能估算 ----
                MatchGpuSpecs(gpu);

                // 集成显卡未匹配到规格库时，使用系统内存带宽（而非根据显存大小瞎猜）
                if (!gpu.MemoryBandwidthGBps.HasValue && gpu.IsIntegrated && !gpu.IsAppleSilicon)
                {
                    gpu.MemoryBandwidthGBps = systemMemoryBandwidth ?? EstimateBandwidthFromCpu(info.Cpu);
                }

                EstimateGpuPerformance(gpu);
            }

            // 如果没有任何 GPU，或只有集成显卡，给 HardwareInfoDto 附加 CPU 估算
            if (info.Gpus.Count == 0 || info.Gpus.All(g => g.IsIntegrated && !g.IsAppleSilicon))
            {
                info.EstimatedCpuTokensPerSecond = EstimateCpuTokensPerSecond(info.Cpu, info.Memory);
            }

            // 磁盘空间（使用应用所在目录）
            var diskInfo = GetDiskSpace();
            info.TotalDiskSpaceBytes = diskInfo.Total;
            info.AvailableDiskSpaceBytes = diskInfo.Available;

            _logger.LogInformation(
                "硬件检测完成: CPU={Cpu}, 核心={Cores}, 内存={MemGiB:F1}GB, GPU={GpuCount}, 平台={Platform}",
                info.Cpu.Name, info.Cpu.LogicalProcessorCount, info.Memory.TotalGiB,
                info.Gpus.Count, info.OsPlatform);

            return info;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "硬件信息检测失败");
            return new HardwareInfoDto();
        }
    }

    public static HardwareTier GetHardwareTier(HardwareInfoDto hardware)
    {
        var gpus = hardware.Gpus;
        var totalRamGiB = hardware.Memory.TotalGiB;

        // 检测是否有独立显卡及其显存
        var dedicatedGpu = gpus.FirstOrDefault(g => !g.IsIntegrated && !g.IsAppleSilicon);
        if (dedicatedGpu != null && dedicatedGpu.VramGiB.HasValue)
        {
            var vram = dedicatedGpu.VramGiB.Value;
            if (vram >= 16) return HardwareTier.TopTierGpu;
            if (vram >= 8) return HardwareTier.HighEndGpu;
            if (vram >= 4) return HardwareTier.MidRangeGpu;
            return HardwareTier.LowEndGpu;
        }

        // Apple Silicon 统一内存
        var appleGpu = gpus.FirstOrDefault(g => g.IsAppleSilicon);
        if (appleGpu != null && appleGpu.VramGiB.HasValue)
        {
            var vram = appleGpu.VramGiB.Value;
            if (vram >= 16) return HardwareTier.TopTierGpu;
            if (vram >= 8) return HardwareTier.HighEndGpu;
            if (vram >= 4) return HardwareTier.MidRangeGpu;
            return HardwareTier.LowEndGpu;
        }

        // 集成显卡（Intel/AMD iGPU）
        var igpu = gpus.FirstOrDefault(g => g.IsIntegrated);
        if (igpu != null)
        {
            // 有集成显卡但无法确定显存，看内存
            if (totalRamGiB >= 16) return HardwareTier.LowEndGpu;
            return HardwareTier.CpuOnly;
        }

        // 无任何 GPU 信息，按内存判断
        if (totalRamGiB >= 32) return HardwareTier.LowEndGpu;
        return HardwareTier.CpuOnly;
    }
}
