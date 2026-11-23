namespace Baihua.Contracts.Capability;

/// <summary>
/// 机器能力等级
/// </summary>
public enum MachineCapability
{
    /// <summary>配置过低，只能使用云端 API</summary>
    Insufficient = 0,
    /// <summary>纯 CPU，可跑 Embedding 和极小模型</summary>
    CpuOnly = 1,
    /// <summary>低端 GPU，可跑 7B Q4 及以下</summary>
    LowEndGpu = 2,
    /// <summary>中端 GPU，可跑 14B-32B</summary>
    MidEndGpu = 3,
    /// <summary>高端 GPU，可跑 70B+</summary>
    HighEndGpu = 4,
}

/// <summary>
/// 机器能力信息（前后端共享）
/// </summary>
public class CapabilityInfo
{
    /// <summary>机器能力等级</summary>
    public MachineCapability Level { get; set; }

    /// <summary>总内存 GB</summary>
    public double TotalRamGiB { get; set; }

    /// <summary>最大显存 GB</summary>
    public double MaxVramGiB { get; set; }

    /// <summary>GPU 名称</summary>
    public string GpuName { get; set; } = "";

    /// <summary>是否存在 Intel GPU（OpenVINO 本地推理的前提，用于前端隐藏/置灰相关入口）</summary>
    public bool IsIntelGpu { get; set; }

    /// <summary>当前部署是否具备可用 OpenVINO 推理能力。
    /// OpenVINO 推理已可拆分为独立 OVMS 服务（远程），因此该项不再等同于 IsIntelGpu：
    /// = 存在 Intel GPU（本地推理） 或 已配置远程 OVMS（如 k8s 的 bh-openvino:8000）。</summary>
    public bool OpenVinoAvailable { get; set; }

    /// <summary>可用功能名称列表</summary>
    public List<string> AvailableFeatures { get; set; } = new();

    /// <summary>被限制的功能及原因</summary>
    public Dictionary<string, string> RestrictedFeatures { get; set; } = new();
}
