namespace Baihua.Core.Services;

/// <summary>
/// 获取配置文件目录。
/// 未设 BAIHUA_DATA_DIR 时使用应用基础目录。
/// 日志目录统一使用 BaihuaPaths.Logs。
/// </summary>
public static class AppPaths
{
    public static string GetConfigDirectory()
    {
        var dataDir = Environment.GetEnvironmentVariable("BAIHUA_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(dataDir))
        {
            var dir = dataDir.TrimEnd('/', '\\');
            Directory.CreateDirectory(dir);
            return dir;
        }

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        Directory.CreateDirectory(baseDir);
        return baseDir;
    }

    public static string GetLogsDirectory()
    {
        Baihua.Contracts.BaihuaPaths.Reset();
        var dir = Baihua.Contracts.BaihuaPaths.Logs;
        Directory.CreateDirectory(dir);
        return dir;
    }
}
