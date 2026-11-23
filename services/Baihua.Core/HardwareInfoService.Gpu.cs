using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Baihua.Contracts.LocalModels;

namespace Baihua.Core.Services;

public partial class HardwareInfoService
{
        private List<GpuInfoDto> GetGpuInfo()
        {
            var gpus = new List<GpuInfoDto>();
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    gpus.AddRange(GetGpuInfoWindows());
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    gpus.AddRange(GetGpuInfoLinux());
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    gpus.AddRange(GetGpuInfoMac());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GPU 信息检测失败");
            }
            return gpus;
        }

        private List<GpuInfoDto> GetGpuInfoWindows()
        {
            var list = new List<GpuInfoDto>();

            // 优先尝试 nvidia-smi
            var nvidiaGpus = GetNvidiaGpuInfo();
            if (nvidiaGpus.Count > 0)
                return nvidiaGpus;

            // 非 NVIDIA 回退：PowerShell Get-CimInstance（wmic 已在 Win11 24H2+ 移除）
            var psOutput = HardwareInfoHelper.RunCommand("powershell",
                "-NoProfile -NonInteractive -Command \"Get-CimInstance Win32_VideoController | Select-Object Name,AdapterRAM | ConvertTo-Json -Compress\"",
                10000);
            if (!string.IsNullOrWhiteSpace(psOutput))
            {
                var parsed = ParseGpuJson(psOutput);
                if (parsed.Count > 0)
                    return parsed;
            }

            // 回退到 WMI（仅兼容旧系统）

            var output = HardwareInfoHelper.RunCommand("wmic", "path win32_VideoController get Name,AdapterRAM /value", 5000);
            if (string.IsNullOrEmpty(output)) return list;

            var entries = output.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var entry in entries)
            {
                var name = HardwareInfoHelper.ExtractWmicValue(entry, "Name");
                if (string.IsNullOrEmpty(name)) continue;

                var gpu = new GpuInfoDto
                {
                    Name = name,
                    Vendor = HardwareInfoHelper.InferVendor(name),
                };

                if (long.TryParse(HardwareInfoHelper.ExtractWmicValue(entry, "AdapterRAM"), out var ram))
                {
                    // WMI 有时返回 0xFFFFFFFF 表示大于 4GB
                    if (ram > 0 && ram < 0xFFFFFFFF)
                        gpu.VramBytes = ram;
                }

                // 设备名自带显存（如 "(16GB)"）更可靠，覆盖 AdapterRAM 的 32 位回绕问题
                var nameVram = HardwareInfoHelper.ParseVramFromName(name);
                if (nameVram.HasValue)
                    gpu.VramBytes = nameVram.Value;

                gpu.IsIntegrated = HardwareInfoHelper.IsIntegratedGpu(name);
                list.Add(gpu);
            }

            return list;
        }

        /// <summary>
        /// 解析 PowerShell Get-CimInstance ConvertTo-Json 输出（单个 GPU 返回对象，多个返回数组）
        /// </summary>
        private static List<GpuInfoDto> ParseGpuJson(string json)
        {
            var list = new List<GpuInfoDto>();
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json.Trim());
                var root = doc.RootElement;
                if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    AddGpuFromJson(list, root);
                }
                else if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in root.EnumerateArray())
                    {
                        AddGpuFromJson(list, item);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ParseGpuJson failed: {ex.Message}");
            }
            return list;
        }

        private static void AddGpuFromJson(List<GpuInfoDto> list, System.Text.Json.JsonElement el)
        {
            if (el.ValueKind != System.Text.Json.JsonValueKind.Object)
                return;
            var name = el.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(name))
                return;

            var gpu = new GpuInfoDto
            {
                Name = name.Trim(),
                Vendor = HardwareInfoHelper.InferVendor(name),
                IsIntegrated = HardwareInfoHelper.IsIntegratedGpu(name),
            };

            // WMI AdapterRAM 是 32 位字段（上限 4GB），>4GB 的卡会回绕成错误值（如 16GB 显示 2GB）。
            // 设备名自带显存（如 "Intel(R) Arc(TM) 130T GPU (16GB)"）更可靠，优先解析。
            var nameVram = HardwareInfoHelper.ParseVramFromName(name);
            if (nameVram.HasValue)
            {
                gpu.VramBytes = nameVram.Value;
            }
            else if (el.TryGetProperty("AdapterRAM", out var ramEl) && ramEl.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                var ram = ramEl.GetUInt32();
                if (ram > 0 && ram < 0xFFFFFFFF)
                    gpu.VramBytes = ram;
            }

            list.Add(gpu);
        }

        private List<GpuInfoDto> GetGpuInfoLinux()
        {
            var list = new List<GpuInfoDto>();

            // 优先 nvidia-smi
            var nvidiaGpus = GetNvidiaGpuInfo();
            list.AddRange(nvidiaGpus);

            // 非 NVIDIA GPU 通过 lspci 检测
            if (list.Count == 0)
            {
                var lspci = HardwareInfoHelper.RunCommand("lspci", "", 5000);
                if (!string.IsNullOrEmpty(lspci))
                {
                    var vgaLines = lspci.Split('\n')
                        .Where(l => l.Contains("VGA", StringComparison.OrdinalIgnoreCase) ||
                                    l.Contains("3D controller", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    foreach (var line in vgaLines)
                    {
                        var name = HardwareInfoHelper.ExtractLspciDeviceName(line);
                        if (string.IsNullOrEmpty(name)) continue;

                        var gpu = new GpuInfoDto
                        {
                            Name = name,
                            Vendor = HardwareInfoHelper.InferVendor(name),
                            IsIntegrated = HardwareInfoHelper.IsIntegratedGpu(name),
                        };

                        // 尝试从 lspci -v -s {bus} 获取内存信息
                        var bus = line.Split(' ')[0];
                        if (!string.IsNullOrEmpty(bus))
                        {
                            var detail = HardwareInfoHelper.RunCommand("lspci", $"-v -s {bus}", 5000);
                            if (!string.IsNullOrEmpty(detail))
                            {
                                var prefetch = Regex.Match(detail, @"prefetchable\)\s*\[size=(\d+)([KMGT])\]");
                                if (prefetch.Success)
                                {
                                    gpu.VramBytes = HardwareInfoHelper.ParseSizeWithUnit(
                                        prefetch.Groups[1].Value,
                                        prefetch.Groups[2].Value);
                                }
                            }
                        }

                        list.Add(gpu);
                    }
                }
            }

            return list;
        }

        private List<GpuInfoDto> GetGpuInfoMac()
        {
            var list = new List<GpuInfoDto>();
            var output = HardwareInfoHelper.RunCommand("system_profiler", "SPDisplaysDataType", 8000);
            if (string.IsNullOrEmpty(output)) return list;

            // Apple Silicon: 统一内存，显存 = 系统内存
            var isAppleSilicon = output.Contains("Apple M", StringComparison.OrdinalIgnoreCase);

            var chipMatches = Regex.Matches(output, @"Chipset Model:\s*(.+)", RegexOptions.Multiline);
            var vramMatches = Regex.Matches(output, @"VRAM \(Total\):\s*(.+)", RegexOptions.Multiline);

            for (int i = 0; i < chipMatches.Count; i++)
            {
                var name = chipMatches[i].Groups[1].Value.Trim();
                var gpu = new GpuInfoDto
                {
                    Name = name,
                    Vendor = isAppleSilicon ? "Apple" : HardwareInfoHelper.InferVendor(name),
                    IsAppleSilicon = isAppleSilicon && name.StartsWith("Apple M", StringComparison.OrdinalIgnoreCase),
                };

                if (i < vramMatches.Count)
                {
                    var vramStr = vramMatches[i].Groups[1].Value.Trim();
                    gpu.VramBytes = HardwareInfoHelper.ParseMacMemoryString(vramStr);
                }

                // Apple Silicon 统一内存：显存等于系统内存的一部分（通常动态分配）
                if (gpu.IsAppleSilicon && !gpu.VramBytes.HasValue)
                {
                    // 使用系统总内存作为上限标记
                    if (long.TryParse(HardwareInfoHelper.RunCommand("sysctl", "-n hw.memsize", 5000)?.Trim(), out var totalMem))
                    {
                        gpu.VramBytes = totalMem; // 统一内存架构
                    }
                }

                list.Add(gpu);
            }

            return list;
        }

        private List<GpuInfoDto> GetNvidiaGpuInfo()
        {
            var list = new List<GpuInfoDto>();
            // 扩展查询字段：名称、显存总量、驱动版本、最大图形时钟、PCIe 链路代数
            var output = HardwareInfoHelper.RunCommand("nvidia-smi",
                "--query-gpu=name,memory.total,driver_version,clocks.max.graphics,pcie.link.gen.max --format=csv,noheader", 5000);
            if (string.IsNullOrEmpty(output)) return list;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',').Select(p => p.Trim()).ToArray();
                if (parts.Length < 2) continue;

                var gpu = new GpuInfoDto
                {
                    Name = parts[0],
                    Vendor = "NVIDIA",
                };

                gpu.VramBytes = HardwareInfoHelper.ParseNvidiaMemory(parts[1]);
                if (parts.Length >= 3)
                    gpu.DriverVersion = parts[2];
                if (parts.Length >= 4 && double.TryParse(parts[3].Replace(" MHz", "").Trim(), out var clock))
                    gpu.MaxClockMHz = clock;

                list.Add(gpu);
            }

            return list;
        }

        /// <summary>
        /// 根据 GPU 型号名称匹配规格数据库，补全计算单元、带宽、算力等参数
        /// </summary>
        private static void MatchGpuSpecs(GpuInfoDto gpu)
        {
            var nameLower = gpu.Name.ToLowerInvariant();
            string? matchedKey = null;

            // 优先完全匹配（最长的关键字优先，避免 "rtx 4070" 匹配到 "rtx 4070 ti"）
            foreach (var key in GpuSpecDb.Keys.OrderByDescending(k => k.Length))
            {
                if (nameLower.Contains(key))
                {
                    matchedKey = key;
                    break;
                }
            }

            if (matchedKey != null)
            {
                var spec = GpuSpecDb[matchedKey];
                gpu.ComputeUnits = spec.CudaCores;
                gpu.MemoryBandwidthGBps = spec.BandwidthGbps;
                gpu.EstimatedTflopsFp16 = spec.TflopsFp16;
                gpu.EstimatedTflopsInt8 = spec.TflopsFp16 * 2;
                gpu.EstimatedTflopsInt4 = spec.TflopsFp16 * 4;
            }
            else if (!gpu.IsIntegrated && !gpu.IsAppleSilicon)
            {
                // 未匹配到数据库的独立显卡，根据 Vendor 和 VRAM 粗略推算带宽
                gpu.MemoryBandwidthGBps = InferBandwidthFromVram(gpu);
            }
            // 集成显卡和 Apple Silicon 的带宽由 DetectHardwareInfo 另行处理
        }

        /// <summary>
        /// 检测系统内存带宽（GB/s）。集成显卡没有独立显存，其带宽就是系统内存带宽。
        /// 公式：带宽 = 内存频率(MT/s) × 通道数 × 8 / 1000
        /// </summary>
        private double? DetectSystemMemoryBandwidth()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    var output = HardwareInfoHelper.RunCommand("dmidecode", "-t memory", 8000);
                    if (!string.IsNullOrEmpty(output))
                    {
                        var speeds = new List<double>();
                        string? currentType = null;
                        foreach (var line in output.Split('\n'))
                        {
                            var trimmed = line.Trim();
                            if (trimmed.StartsWith("Type:", StringComparison.OrdinalIgnoreCase))
                            {
                                currentType = trimmed.Substring(5).Trim();
                            }
                            else if ((trimmed.StartsWith("Speed:", StringComparison.OrdinalIgnoreCase) ||
                                      trimmed.StartsWith("Configured Memory Speed:", StringComparison.OrdinalIgnoreCase)) &&
                                     currentType != null &&
                                     (currentType.Contains("DDR", StringComparison.OrdinalIgnoreCase) ||
                                      currentType.Contains("LPDDR", StringComparison.OrdinalIgnoreCase) ||
                                      currentType.Contains("SDRAM", StringComparison.OrdinalIgnoreCase)))
                            {
                                var speedMatch = Regex.Match(trimmed, @"(\d+)\s*MT/s");
                                if (speedMatch.Success && double.TryParse(speedMatch.Groups[1].Value, out var speed) && speed > 0)
                                {
                                    speeds.Add(speed);
                                }
                            }
                        }

                        if (speeds.Count > 0)
                        {
                            var avgSpeed = speeds.Average();
                            var bandwidth = avgSpeed * speeds.Count * 8 / 1000;
                            _logger.LogInformation("检测到系统内存带宽: {Bandwidth:F1} GB/s ({Channels} 通道, {Speed:F0} MT/s)",
                                bandwidth, speeds.Count, avgSpeed);
                            return bandwidth;
                        }
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var output = HardwareInfoHelper.RunCommand("wmic", "memorychip get Speed /value", 5000);
                    if (!string.IsNullOrEmpty(output))
                    {
                        var speeds = new List<double>();
                        foreach (Match match in Regex.Matches(output, @"Speed=(\d+)"))
                        {
                            if (double.TryParse(match.Groups[1].Value, out var speed) && speed > 0)
                                speeds.Add(speed);
                        }
                        if (speeds.Count > 0)
                        {
                            var avgSpeed = speeds.Average();
                            var bandwidth = avgSpeed * speeds.Count * 8 / 1000;
                            _logger.LogInformation("检测到系统内存带宽: {Bandwidth:F1} GB/s ({Channels} 通道, {Speed:F0} MHz)",
                                bandwidth, speeds.Count, avgSpeed);
                            return bandwidth;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "检测系统内存带宽失败");
            }

            return null;
        }

        /// <summary>
        /// 未匹配到规格库时，根据显存大小和厂商经验推算带宽（仅用于独立显卡）
        /// </summary>
        private static double? InferBandwidthFromVram(GpuInfoDto gpu)
        {
            if (!gpu.VramGiB.HasValue) return null;
            var vram = gpu.VramGiB.Value;
            var vendor = gpu.Vendor.ToLowerInvariant();

            // 经验公式：每 GB 显存约对应 40-80 GB/s 带宽（因代际和位宽差异大，仅作粗略估计）
            // 注意：此公式不适用于集成显卡，集成显卡应使用 DetectSystemMemoryBandwidth()
            return vendor switch
            {
                "nvidia" => vram * 65,
                "amd" => vram * 55,
                "intel" => vram * 35,
                "apple" => vram * 12, // Apple 统一内存按总内存算，带宽与内存封装有关
                _ => vram * 50,
            };
        }

        /// <summary>
        /// 无法检测内存带宽时，根据 CPU 信息做保守估算
        /// </summary>
        private static double EstimateBandwidthFromCpu(CpuInfoDto cpu)
        {
            var name = cpu.Name.ToLowerInvariant();
            var cores = cpu.LogicalProcessorCount;

            // 根据 CPU 代际和平台做经验估算
            // 移动/低压平台（LPDDR）
            // 注意：Intel 官方名称是 "Core(TM) Ultra"，Contains("core ultra") 匹配不到中间的 (tm)
            if ((name.Contains("core") && name.Contains("ultra")) || name.Contains("ryzen ai"))
                return 100; // LPDDR5X 双通道 ~100 GB/s
            if (name.Contains("apple m"))
                return 100; // Apple Silicon 基础款 ~100 GB/s

            // 桌面平台（DDR4/DDR5 双通道）
            if (name.Contains("ddr5") || name.Contains("i9-13") || name.Contains("i9-14") || name.Contains("i9-15"))
                return 80;
            if (name.Contains("ryzen 9") || name.Contains("ryzen 7"))
                return 70;

            // 服务器平台
            if (cores >= 32)
                return 200;

            // 保守默认值
            return cores >= 8 ? 60 : 40;
        }

        /// <summary>
        /// 估算 GPU 的 Llama-3-8B Q4_K_M 推理速度（tokens/秒）
        /// 核心公式：瓶颈是显存带宽，每生成 1 token 需要读取全部模型权重
        /// </summary>
        private static void EstimateGpuPerformance(GpuInfoDto gpu)
        {
            const double UtilizationFactor = 0.13;   // 框架效率系数（llama.cpp 等约 13% 的带宽利用率）
            const double AppleUtilizationFactor = 0.16; // Apple Silicon 统一内存效率稍高

            if (gpu.MemoryBandwidthGBps.HasValue && gpu.MemoryBandwidthGBps.Value > 0)
            {
                double factor = gpu.IsAppleSilicon ? AppleUtilizationFactor : UtilizationFactor;
                // 集成显卡效率更低
                if (gpu.IsIntegrated && !gpu.IsAppleSilicon)
                    factor = 0.08;

                gpu.EstimatedTokensPerSecond = Math.Round(gpu.MemoryBandwidthGBps.Value * factor, 1);
            }
            else if (gpu.VramGiB.HasValue)
            {
                // 连带宽都推算不出时，按显存大小做非常粗略的分级估算
                gpu.EstimatedTokensPerSecond = gpu.VramGiB.Value switch
                {
                    >= 24 => 100,
                    >= 16 => 70,
                    >= 12 => 50,
                    >= 8 => 35,
                    >= 6 => 25,
                    >= 4 => 15,
                    _ => 5,
                };
            }
        }

        /// <summary>
        /// 估算纯 CPU 推理速度（无 GPU 或仅集成显卡时）
        /// </summary>
        private static double EstimateCpuTokensPerSecond(CpuInfoDto cpu, MemoryInfoDto memory)
        {
            // CPU 推理受限于内存带宽和 SIMD 能力，经验值：双通道 DDR4/5 约 1-8 tok/s
            var cores = cpu.LogicalProcessorCount;
            if (cores <= 4) return 2;
            if (cores <= 8) return 4;
            if (cores <= 16) return 7;
            return 10; // 高端桌面/服务器 CPU
        }


        #region Disk Space
        #endregion

        private (long Total, long Available) GetDiskSpace()
        {
            try
            {
                var path = AppDomain.CurrentDomain.BaseDirectory;
                var drive = new DriveInfo(Path.GetPathRoot(path) ?? path);
                if (drive.IsReady)
                    return (drive.TotalSize, drive.AvailableFreeSpace);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "获取磁盘空间失败");
            }
            return (0, 0);
        }

    }
