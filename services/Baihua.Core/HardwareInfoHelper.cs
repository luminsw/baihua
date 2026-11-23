using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Baihua.Core.Services;

/// <summary>
/// 硬件信息检测辅助方法
/// </summary>
public static class HardwareInfoHelper
{
    public static string GetOsName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "Windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "Linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macOS";
        return "Unknown";
    }

    public static bool DetectWsl()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;
        try
        {
            var release = File.ReadAllText("/proc/sys/kernel/osrelease");
            return release.Contains("WSL", StringComparison.OrdinalIgnoreCase) ||
                   release.Contains("microsoft", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static string? RunCommand(string fileName, string arguments, int timeoutMs, Dictionary<string, string>? env = null)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (env != null)
            {
                foreach (var kv in env)
                {
                    psi.EnvironmentVariables[kv.Key] = kv.Value;
                }
            }

            using var process = Process.Start(psi);
            if (process == null) return null;

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            return process.StandardOutput.ReadToEnd();
        }
        catch { return null; }
    }

    public static string? ExtractWmicValue(string output, string key)
    {
        var match = Regex.Match(output, $"{key}=\\s*(.+?)(?:\\r?\\n|$)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    public static string? ExtractLineValue(string text, string prefix)
    {
        var lines = text.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return trimmed.Substring(prefix.Length).Trim();
        }
        return null;
    }

    public static string? ExtractRegex(string text, string pattern, RegexOptions options = RegexOptions.None)
    {
        var match = Regex.Match(text, pattern, options);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    public static string ExtractLspciDeviceName(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return "";

        var afterBus = line;
        var firstSpace = line.IndexOf(' ');
        if (firstSpace >= 0)
            afterBus = line.Substring(firstSpace + 1).TrimStart();

        var firstColon = afterBus.IndexOf(':');
        if (firstColon >= 0)
            afterBus = afterBus.Substring(firstColon + 1).TrimStart();

        var revIdx = afterBus.IndexOf("(rev ", StringComparison.OrdinalIgnoreCase);
        if (revIdx >= 0)
            afterBus = afterBus.Substring(0, revIdx).Trim();

        return afterBus;
    }

    public static long ParseMeminfoKB(string text, string key)
    {
        var pattern = $"{key}\\s*:\\s*(\\d+)\\s*kB";
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return match.Success && long.TryParse(match.Groups[1].Value, out var val) ? val : 0;
    }

    public static long ParseVmStat(string text, string key)
    {
        var pattern = $"{key}\\s*:\\s*(\\d+)";
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return match.Success && long.TryParse(match.Groups[1].Value, out var val) ? val : 0;
    }

    public static long? ParseNvidiaMemory(string text)
    {
        var match = Regex.Match(text, @"([\d.]+)\s*(MiB|MB|GiB|GB)");
        if (!match.Success) return null;

        var val = double.Parse(match.Groups[1].Value);
        var unit = match.Groups[2].Value.ToUpperInvariant();
        return unit.StartsWith("GI") || unit.StartsWith("GB")
            ? (long)(val * 1024 * 1024 * 1024)
            : (long)(val * 1024 * 1024);
    }

    /// <summary>
    /// 从 GPU 设备名解析显存容量（如 "Intel(R) Arc(TM) 130T GPU (16GB)"、"RTX 4090 24GB"）。
    /// WMI AdapterRAM 是 32 位字段（上限 4GB），大显存卡会回绕成错误值，设备名更可靠。
    /// </summary>
    public static long? ParseVramFromName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        // 括号形式：(16GB) / (16 GB) / (12,288 MB)
        var match = Regex.Match(name, @"\(\s*(\d+(?:[.,]\d+)?)\s*(GB|GiB|MB|MiB)\s*\)", RegexOptions.IgnoreCase);
        if (!match.Success)
            // 裸形式："24GB"、"16 GB"、"8GB"（末尾或单词边界）
            match = Regex.Match(name, @"(\d+(?:[.,]\d+)?)\s*(GB|GiB|MB|MiB)\b", RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        var val = double.Parse(match.Groups[1].Value.Replace(",", "."));
        var unit = match.Groups[2].Value.ToUpperInvariant();
        return unit.StartsWith("GI") || unit.StartsWith("GB")
            ? (long)(val * 1024 * 1024 * 1024)
            : (long)(val * 1024 * 1024);
    }

    public static long? ParseMacMemoryString(string text)
    {
        var match = Regex.Match(text, @"([\d.]+)\s*(MB|GB|TB)");
        if (!match.Success) return null;

        var val = double.Parse(match.Groups[1].Value);
        var unit = match.Groups[2].Value.ToUpperInvariant();
        return unit switch
        {
            "MB" => (long)(val * 1024 * 1024),
            "GB" => (long)(val * 1024 * 1024 * 1024),
            "TB" => (long)(val * 1024L * 1024 * 1024 * 1024),
            _ => null
        };
    }

    public static long ParseSizeWithUnit(string value, string unit)
    {
        if (!double.TryParse(value, out var val)) return 0;
        return unit.ToUpperInvariant() switch
        {
            "K" => (long)(val * 1024),
            "M" => (long)(val * 1024 * 1024),
            "G" => (long)(val * 1024 * 1024 * 1024),
            "T" => (long)(val * 1024L * 1024 * 1024 * 1024),
            _ => 0
        };
    }

    public static string InferVendor(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("nvidia") || lower.Contains("geforce") || lower.Contains("rtx") || lower.Contains("quadro"))
            return "NVIDIA";
        if (lower.Contains("amd") || lower.Contains("radeon") || lower.Contains("firepro"))
            return "AMD";
        if (lower.Contains("intel") || lower.Contains("arc") || lower.Contains("iris") || lower.Contains("hd graphics") || lower.Contains("uhd"))
            return "Intel";
        if (lower.Contains("apple"))
            return "Apple";
        return "Unknown";
    }

    public static bool IsIntegratedGpu(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("intel") &&
               (lower.Contains("hd graphics") || lower.Contains("uhd") || lower.Contains("iris") || lower.Contains("intel graphics")) ||
               lower.Contains("integrated");
    }
}
