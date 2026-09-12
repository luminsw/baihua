using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

public class HardwareInfoHelperTests
{
    [Fact]
    public void GetOsName_ReturnsValidOsName()
    {
        var osName = HardwareInfoHelper.GetOsName();
        Assert.True(osName == "Windows" || osName == "Linux" || osName == "macOS" || osName == "Unknown");
    }

    [Fact]
    public void ExtractWmicValue_WithValidOutput_ReturnsValue()
    {
        var output = "Name= Intel Core i7\r\n";
        var result = HardwareInfoHelper.ExtractWmicValue(output, "Name");
        Assert.Equal("Intel Core i7", result);
    }

    [Fact]
    public void ExtractWmicValue_WithNoMatch_ReturnsNull()
    {
        var output = "OtherKey= Value\r\n";
        var result = HardwareInfoHelper.ExtractWmicValue(output, "Name");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractLineValue_WithMatchingPrefix_ReturnsValue()
    {
        var text = "Model: NVIDIA GeForce RTX 3080\n";
        var result = HardwareInfoHelper.ExtractLineValue(text, "Model:");
        Assert.Equal("NVIDIA GeForce RTX 3080", result);
    }

    [Fact]
    public void ExtractLineValue_WithNoMatch_ReturnsNull()
    {
        var text = "Other: Value\n";
        var result = HardwareInfoHelper.ExtractLineValue(text, "Model:");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractRegex_WithMatch_ReturnsValue()
    {
        var text = "Memory: 16 GB";
        var result = HardwareInfoHelper.ExtractRegex(text, "Memory:\\s*(.+)");
        Assert.Equal("16 GB", result);
    }

    [Fact]
    public void ExtractRegex_WithNoMatch_ReturnsNull()
    {
        var text = "Other: Value";
        var result = HardwareInfoHelper.ExtractRegex(text, "Memory:\\s*(.+)");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractLspciDeviceName_WithValidLine_ReturnsDeviceName()
    {
        var line = "00:02.0 VGA compatible controller: NVIDIA GeForce RTX 3080 (rev a1)";
        var result = HardwareInfoHelper.ExtractLspciDeviceName(line);
        Assert.Equal("NVIDIA GeForce RTX 3080", result);
    }

    [Fact]
    public void ExtractLspciDeviceName_WithEmptyLine_ReturnsEmpty()
    {
        var result = HardwareInfoHelper.ExtractLspciDeviceName("");
        Assert.Equal("", result);
    }

    [Fact]
    public void ParseMeminfoKB_WithValidKey_ReturnsValue()
    {
        var text = "MemTotal:       16384 kB\n";
        var result = HardwareInfoHelper.ParseMeminfoKB(text, "MemTotal");
        Assert.Equal(16384, result);
    }

    [Fact]
    public void ParseMeminfoKB_WithNoMatch_ReturnsZero()
    {
        var text = "OtherKey: 123 kB\n";
        var result = HardwareInfoHelper.ParseMeminfoKB(text, "MemTotal");
        Assert.Equal(0, result);
    }

    [Fact]
    public void ParseNvidiaMemory_WithMiB_ReturnsBytes()
    {
        var text = "1024 MiB";
        var result = HardwareInfoHelper.ParseNvidiaMemory(text);
        Assert.Equal(1024L * 1024 * 1024, result);
    }

    [Fact]
    public void ParseNvidiaMemory_WithGiB_ReturnsBytes()
    {
        var text = "1 GiB";
        var result = HardwareInfoHelper.ParseNvidiaMemory(text);
        Assert.Equal(1024L * 1024 * 1024, result);
    }

    [Fact]
    public void ParseNvidiaMemory_WithNoMatch_ReturnsNull()
    {
        var text = "invalid";
        var result = HardwareInfoHelper.ParseNvidiaMemory(text);
        Assert.Null(result);
    }

    [Fact]
    public void ParseMacMemoryString_WithGB_ReturnsBytes()
    {
        var text = "16 GB";
        var result = HardwareInfoHelper.ParseMacMemoryString(text);
        Assert.Equal(16L * 1024 * 1024 * 1024, result);
    }

    [Fact]
    public void ParseMacMemoryString_WithMB_ReturnsBytes()
    {
        var text = "512 MB";
        var result = HardwareInfoHelper.ParseMacMemoryString(text);
        Assert.Equal(512L * 1024 * 1024, result);
    }

    [Fact]
    public void ParseSizeWithUnit_WithK_ReturnsBytes()
    {
        var result = HardwareInfoHelper.ParseSizeWithUnit("1024", "K");
        Assert.Equal(1024L * 1024, result);
    }

    [Fact]
    public void ParseSizeWithUnit_WithM_ReturnsBytes()
    {
        var result = HardwareInfoHelper.ParseSizeWithUnit("1", "M");
        Assert.Equal(1024L * 1024, result);
    }

    [Fact]
    public void ParseSizeWithUnit_WithG_ReturnsBytes()
    {
        var result = HardwareInfoHelper.ParseSizeWithUnit("1", "G");
        Assert.Equal(1024L * 1024 * 1024, result);
    }

    [Fact]
    public void InferVendor_WithNvidia_ReturnsNVIDIA()
    {
        Assert.Equal("NVIDIA", HardwareInfoHelper.InferVendor("NVIDIA GeForce RTX 3080"));
        Assert.Equal("NVIDIA", HardwareInfoHelper.InferVendor("geforce gtx 1060"));
        Assert.Equal("NVIDIA", HardwareInfoHelper.InferVendor("RTX 4090"));
        Assert.Equal("NVIDIA", HardwareInfoHelper.InferVendor("Quadro P4000"));
    }

    [Fact]
    public void InferVendor_WithAMD_ReturnsAMD()
    {
        Assert.Equal("AMD", HardwareInfoHelper.InferVendor("AMD Radeon RX 6800"));
        Assert.Equal("AMD", HardwareInfoHelper.InferVendor("radeon pro w5700"));
        Assert.Equal("AMD", HardwareInfoHelper.InferVendor("FirePro W9100"));
    }

    [Fact]
    public void InferVendor_WithIntel_ReturnsIntel()
    {
        Assert.Equal("Intel", HardwareInfoHelper.InferVendor("Intel UHD Graphics 630"));
        Assert.Equal("Intel", HardwareInfoHelper.InferVendor("Intel Iris Xe"));
        Assert.Equal("Intel", HardwareInfoHelper.InferVendor("Intel Arc A770"));
    }

    [Fact]
    public void InferVendor_WithApple_ReturnsApple()
    {
        Assert.Equal("Apple", HardwareInfoHelper.InferVendor("Apple M1 GPU"));
    }

    [Fact]
    public void InferVendor_WithUnknown_ReturnsUnknown()
    {
        Assert.Equal("Unknown", HardwareInfoHelper.InferVendor("Some Unknown GPU"));
    }

    [Fact]
    public void IsIntegratedGpu_WithIntelUHD_ReturnsTrue()
    {
        Assert.True(HardwareInfoHelper.IsIntegratedGpu("Intel UHD Graphics 630"));
        Assert.True(HardwareInfoHelper.IsIntegratedGpu("Intel HD Graphics"));
        Assert.True(HardwareInfoHelper.IsIntegratedGpu("Intel Iris Graphics"));
    }

    [Fact]
    public void IsIntegratedGpu_WithDiscrete_ReturnsFalse()
    {
        Assert.False(HardwareInfoHelper.IsIntegratedGpu("NVIDIA GeForce RTX 3080"));
        Assert.False(HardwareInfoHelper.IsIntegratedGpu("AMD Radeon RX 6800"));
    }
}