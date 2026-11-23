namespace MobileContract.Devices;

/// <summary>
/// 移动端整体统计数据
/// </summary>
public record MobileStats
{
    public int TotalDevices { get; init; }
    public long TotalSyncs { get; init; }
    public long TotalSyncFiles { get; init; }
    public int ActiveDevices7Days { get; init; }
    public int ActiveDevices30Days { get; init; }
    public IReadOnlyList<DeviceStat> Devices { get; init; } = Array.Empty<DeviceStat>();
}
