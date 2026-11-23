using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Baihua.Contracts.LocalModels;

namespace Baihua.Core.Services;

public partial class HardwareInfoService
{
        private CpuInfoDto GetCpuInfo()
        {
            var cpu = new CpuInfoDto
            {
                LogicalProcessorCount = Environment.ProcessorCount,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            };

            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    EnrichCpuInfoWindows(cpu);
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    EnrichCpuInfoLinux(cpu);
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    EnrichCpuInfoMac(cpu);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CPU 信息检测失败");
            }

            if (string.IsNullOrEmpty(cpu.Name))
                cpu.Name = $"Unknown {cpu.Architecture} CPU";

            return cpu;
        }

        private class Win32ProcessorJson
        {
            public string? Name { get; set; }
            public int NumberOfCores { get; set; }
            public int NumberOfLogicalProcessors { get; set; }
            public int MaxClockSpeed { get; set; }
        }

        private class RegistryCpuJson
        {
            public string? ProcessorNameString { get; set; }
            public int MHz { get; set; }
        }

        private void EnrichCpuInfoWindows(CpuInfoDto cpu)
        {
            // 方案一（主）：PowerShell Get-CimInstance + JSON（wmic 已在 Win11 24H2+ 移除）
            var psOutput = HardwareInfoHelper.RunCommand("powershell",
                "-NoProfile -NonInteractive -Command \"Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed | ConvertTo-Json -Compress\"",
                10000);
            if (!string.IsNullOrWhiteSpace(psOutput))
            {
                try
                {
                    // 处理单个对象 vs 数组（多 CPU 插槽）
                    if (psOutput.TrimStart().StartsWith('['))
                    {
                        var arr = JsonSerializer.Deserialize<List<Win32ProcessorJson>>(psOutput);
                        if (arr != null && arr.Count > 0)
                        {
                            var first = arr[0];
                            cpu.Name = first.Name ?? cpu.Name;
                            cpu.CoreCount = arr.Sum(x => x.NumberOfCores);
                            cpu.LogicalProcessorCount = arr.Sum(x => x.NumberOfLogicalProcessors);
                            if (first.MaxClockSpeed > 0)
                                cpu.MaxFrequencyMHz = first.MaxClockSpeed.ToString();
                        }
                    }
                    else
                    {
                        var obj = JsonSerializer.Deserialize<Win32ProcessorJson>(psOutput);
                        if (obj != null)
                        {
                            cpu.Name = obj.Name ?? cpu.Name;
                            cpu.CoreCount = obj.NumberOfCores;
                            cpu.LogicalProcessorCount = obj.NumberOfLogicalProcessors;
                            if (obj.MaxClockSpeed > 0)
                                cpu.MaxFrequencyMHz = obj.MaxClockSpeed.ToString();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "解析 CPU CIM JSON 失败: {Output}", psOutput);
                }
            }

            // 方案二（回退）：PowerShell 读取注册表 HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0
            // 在 CIM 不可用时（极少见），不依赖 wmic 也可获取 CPU 名称/频率
            if (string.IsNullOrEmpty(cpu.Name))
            {
                var regOutput = HardwareInfoHelper.RunCommand("powershell",
                    "-NoProfile -NonInteractive -Command \"Get-ItemProperty 'HKLM:\\HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0' | Select-Object ProcessorNameString,@{N='MHz';E={$_.'~MHz'}} | ConvertTo-Json -Compress\"",
                    5000);
                if (!string.IsNullOrWhiteSpace(regOutput))
                {
                    try
                    {
                        var reg = JsonSerializer.Deserialize<RegistryCpuJson>(regOutput);
                        if (reg != null)
                        {
                            cpu.Name = reg.ProcessorNameString ?? cpu.Name;
                            if (reg.MHz > 0 && string.IsNullOrEmpty(cpu.MaxFrequencyMHz))
                                cpu.MaxFrequencyMHz = reg.MHz.ToString();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "解析注册表 CPU JSON 失败: {Output}", regOutput);
                    }
                }
            }

            // 方案三（兼容旧系统）：WMIC（Win10 1809+ 已废弃、Win11 已移除）
            if (string.IsNullOrEmpty(cpu.Name))
            {
                var output = HardwareInfoHelper.RunCommand("wmic", "cpu get Name,NumberOfCores,NumberOfLogicalProcessors /value", 5000);
                if (!string.IsNullOrEmpty(output))
                {
                    cpu.Name = HardwareInfoHelper.ExtractWmicValue(output, "Name") ?? cpu.Name;
                    if (int.TryParse(HardwareInfoHelper.ExtractWmicValue(output, "NumberOfCores"), out var cores))
                        cpu.CoreCount = cores;
                    if (int.TryParse(HardwareInfoHelper.ExtractWmicValue(output, "NumberOfLogicalProcessors"), out var logical))
                        cpu.LogicalProcessorCount = logical;
                }
            }
        }

        private void EnrichCpuInfoLinux(CpuInfoDto cpu)
        {
            // 优先 lscpu，更可靠。使用 LC_ALL=C 确保输出为英文，不受系统语言影响
            var lscpu = HardwareInfoHelper.RunCommand("lscpu", "", 5000, new Dictionary<string, string> { ["LC_ALL"] = "C" });
            if (!string.IsNullOrEmpty(lscpu))
            {
                cpu.Name = HardwareInfoHelper.ExtractLineValue(lscpu, "Model name:") ?? cpu.Name;
                if (int.TryParse(HardwareInfoHelper.ExtractLineValue(lscpu, "Core(s) per socket:"), out var coresPerSocket))
                {
                    if (int.TryParse(HardwareInfoHelper.ExtractLineValue(lscpu, "Socket(s):"), out var sockets))
                        cpu.CoreCount = coresPerSocket * sockets;
                }
                if (int.TryParse(HardwareInfoHelper.ExtractLineValue(lscpu, "CPU(s):"), out var cpus))
                    cpu.LogicalProcessorCount = cpus;
                cpu.MaxFrequencyMHz = HardwareInfoHelper.ExtractLineValue(lscpu, "CPU max MHz:") ?? HardwareInfoHelper.ExtractLineValue(lscpu, "CPU MHz:");
                return;
            }

            // 回退到 /proc/cpuinfo
            try
            {
                var cpuinfo = File.ReadAllText("/proc/cpuinfo");
                var modelName = HardwareInfoHelper.ExtractRegex(cpuinfo, @"model name\s*:\s*(.+)", RegexOptions.Multiline);
                if (!string.IsNullOrEmpty(modelName))
                    cpu.Name = modelName;

                var physicalIds = Regex.Matches(cpuinfo, @"physical id\s*:\s*(\d+)")
                    .Select(m => m.Groups[1].Value)
                    .Distinct()
                    .Count();
                var coresPerCpu = HardwareInfoHelper.ExtractRegex(cpuinfo, @"cpu cores\s*:\s*(\d+)", RegexOptions.Multiline);
                if (int.TryParse(coresPerCpu, out var cpc) && physicalIds > 0)
                    cpu.CoreCount = cpc * physicalIds;
                else
                    cpu.CoreCount = cpu.LogicalProcessorCount;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "读取 /proc/cpuinfo 失败");
            }
        }

        private void EnrichCpuInfoMac(CpuInfoDto cpu)
        {
            cpu.Name = HardwareInfoHelper.RunCommand("sysctl", "-n machdep.cpu.brand_string", 5000)?.Trim() ?? cpu.Name;
            if (int.TryParse(HardwareInfoHelper.RunCommand("sysctl", "-n hw.physicalcpu", 5000)?.Trim(), out var phys))
                cpu.CoreCount = phys;
            if (int.TryParse(HardwareInfoHelper.RunCommand("sysctl", "-n hw.logicalcpu", 5000)?.Trim(), out var log))
                cpu.LogicalProcessorCount = log;
        }

    }
