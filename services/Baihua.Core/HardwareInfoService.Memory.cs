using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using Baihua.Contracts.LocalModels;

namespace Baihua.Core.Services;

public partial class HardwareInfoService
{
        // ===== Windows 内存检测（P/Invoke，兼容 wmic 已被移除的 Win11 24H2+）=====
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MemoryStatusEx
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;

            public MemoryStatusEx()
            {
                dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

        private MemoryInfoDto GetMemoryInfo()
        {
            var mem = new MemoryInfoDto();
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    EnrichMemoryWindows(mem);
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    EnrichMemoryLinux(mem);
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    EnrichMemoryMac(mem);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "内存信息检测失败");
            }
            return mem;
        }

        private void EnrichMemoryWindows(MemoryInfoDto mem)
        {
            try
            {
                var status = new MemoryStatusEx();
                if (GlobalMemoryStatusEx(status))
                {
                    mem.TotalBytes = (long)status.ullTotalPhys;
                    mem.AvailableBytes = (long)status.ullAvailPhys;
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "GlobalMemoryStatusEx 失败，回退 wmic");
            }

            // 回退：wmic（Win11 24H2 已移除，仅兼容旧系统）
            var output = HardwareInfoHelper.RunCommand("wmic", "computersystem get TotalPhysicalMemory /value", 5000);
            if (long.TryParse(HardwareInfoHelper.ExtractWmicValue(output ?? "", "TotalPhysicalMemory"), out var total))
                mem.TotalBytes = total;

            var osOutput = HardwareInfoHelper.RunCommand("wmic", "os get FreePhysicalMemory /value", 5000);
            if (long.TryParse(HardwareInfoHelper.ExtractWmicValue(osOutput ?? "", "FreePhysicalMemory"), out var freeKb))
                mem.AvailableBytes = freeKb * 1024;
        }

        private void EnrichMemoryLinux(MemoryInfoDto mem)
        {
            try
            {
                var meminfo = File.ReadAllText("/proc/meminfo");
                mem.TotalBytes = HardwareInfoHelper.ParseMeminfoKB(meminfo, "MemTotal") * 1024;
                mem.AvailableBytes = HardwareInfoHelper.ParseMeminfoKB(meminfo, "MemAvailable") * 1024;

                // 如果 MemAvailable 不存在（旧内核），用 MemFree + Buffers + Cached
                if (mem.AvailableBytes == 0)
                {
                    var free = HardwareInfoHelper.ParseMeminfoKB(meminfo, "MemFree");
                    var buffers = HardwareInfoHelper.ParseMeminfoKB(meminfo, "Buffers");
                    var cached = HardwareInfoHelper.ParseMeminfoKB(meminfo, "Cached");
                    mem.AvailableBytes = (free + buffers + cached) * 1024;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "读取 /proc/meminfo 失败");
            }
        }

        private void EnrichMemoryMac(MemoryInfoDto mem)
        {
            if (long.TryParse(HardwareInfoHelper.RunCommand("sysctl", "-n hw.memsize", 5000)?.Trim(), out var total))
                mem.TotalBytes = total;

            // vm_statistics64 for available memory
            var vmStats = HardwareInfoHelper.RunCommand("vm_stat", "", 5000);
            if (!string.IsNullOrEmpty(vmStats))
            {
                var pageSize = 4096L; // default
                if (long.TryParse(HardwareInfoHelper.RunCommand("sysctl", "-n vm.pagesize", 5000)?.Trim(), out var ps))
                    pageSize = ps;

                var freePages = HardwareInfoHelper.ParseVmStat(vmStats, "Pages free");
                var inactivePages = HardwareInfoHelper.ParseVmStat(vmStats, "Pages inactive");
                mem.AvailableBytes = (freePages + inactivePages) * pageSize;
            }
        }

    }
