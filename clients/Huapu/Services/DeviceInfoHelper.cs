namespace MobileApp.Maui.Services;

/// <summary>
/// 设备信息辅助类。跨平台获取设备 ID 和名称。
/// ID 在首次初始化时从安全存储读取或生成，后续从内存缓存返回。
/// 花记名（展示名）持久化在 Preferences，与安卓/鸿蒙端词库一致。
/// </summary>
public static class DeviceInfoHelper
{
    private const string DeviceIdKey = "persistent_device_id";
    private const string DisplayNameKey = "huaji_device_display_name";
    private static string? _cachedDeviceId;
    private static string? _cachedDisplayName;

    private static readonly string[] CulturalPrefixes =
    {
        "听风", "望月", "拾光", "寻芳", "踏雪", "观云", "沐雨", "临水",
        "知秋", "迎春", "听雨", "看花", "折柳", "采菊", "抚琴", "煮茶",
        "清心", "静雅", "悠然", "闲云", "素心", "雅韵", "墨香", "竹影",
        "松风", "梅韵", "兰心", "菊韵", "荷香", "桃夭", "杏雨", "梨云"
    };

    private static readonly string[] CulturalSuffixes = { "笺", "语", "阁", "砚", "轩", "庐", "箫", "影" };

    /// <summary>
    /// 异步初始化设备 ID。应在 App 启动早期调用一次。
    /// </summary>
    public static async Task InitializeAsync()
    {
        if (_cachedDeviceId != null)
            return;

        var stored = await SecureStorage.Default.GetAsync(DeviceIdKey).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(stored))
        {
            _cachedDeviceId = stored;
            return;
        }

        var newId = Guid.NewGuid().ToString("N")[..16];
        await SecureStorage.Default.SetAsync(DeviceIdKey, newId).ConfigureAwait(false);
        _cachedDeviceId = newId;
    }

    /// <summary>
    /// 获取设备 ID。首次调用时自动完成初始化（同步等待）。
    /// </summary>
    public static string GetDeviceId()
    {
        if (_cachedDeviceId == null)
        {
            // 在 MauiProgram 中改为后台延迟初始化；若仍有同步调用场景，
            // 通过 Task.Run 在线程池上执行以避免 UI SynchronizationContext 死锁。
            Task.Run(() => InitializeAsync()).GetAwaiter().GetResult();
        }

        if (_cachedDeviceId == null)
            throw new InvalidOperationException("DeviceInfoHelper 初始化失败，无法获取设备 ID。");

        return _cachedDeviceId;
    }

    /// <summary>
    /// 系统设备名（如“HUAWEI P60”），与花记名互补展示。
    /// </summary>
    public static string GetDeviceName()
    {
#if ANDROID
        return Android.OS.Build.Model ?? "Android Device";
#elif IOS
        return UIKit.UIDevice.CurrentDevice.Name ?? "iPhone";
#else
        return "Unknown Device";
#endif
    }

    /// <summary>
    /// 花记名（展示名）。首次调用时生成诗意短名并持久化，后续从缓存/Preferences 返回。
    /// 与安卓/鸿蒙端 DeviceNameStore 语义一致。
    /// </summary>
    public static string GetDisplayName()
    {
        if (_cachedDisplayName != null)
            return _cachedDisplayName;

        var stored = Preferences.Default.Get<string?>(DisplayNameKey, null);
        if (!string.IsNullOrEmpty(stored))
        {
            _cachedDisplayName = stored;
            return stored;
        }

        var random = new Random();
        var name = CulturalPrefixes[random.Next(CulturalPrefixes.Length)]
                   + CulturalSuffixes[random.Next(CulturalSuffixes.Length)];
        Preferences.Default.Set(DisplayNameKey, name);
        _cachedDisplayName = name;
        return name;
    }
}
