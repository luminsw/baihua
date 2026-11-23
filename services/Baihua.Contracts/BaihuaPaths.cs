namespace Baihua.Contracts;

/// <summary>
/// 百花数据根目录 — 由 BAIHUA_HOME 环境变量指定
///
/// 目录结构：
///   $BAIHUA_HOME/
///   ├── vaults/     知识库文件
///   ├── db/         数据库 + 密钥
///   └── logs/       运行日志
///
/// 默认值（未设 BAIHUA_HOME 时）：
///   Windows: %USERPROFILE%\.baihua
///   Linux:   ~/.baihua
///   Docker:  /opt/baihua/data
/// </summary>
public static class BaihuaPaths
{
    private static string? _home;
    private static readonly object _lock = new();

    public static string Home
    {
        get
        {
            if (_home != null) return _home;
            lock (_lock)
            {
                if (_home != null) return _home;

                var home = Environment.GetEnvironmentVariable("BAIHUA_HOME");
                if (!string.IsNullOrWhiteSpace(home))
                {
                    _home = home.TrimEnd('/', '\\');
                    return _home;
                }

                _home = GetDefaultHome();
                return _home;
            }
        }
    }

    public static string Vaults => Path.Combine(Home, "vaults");
    public static string Db => Path.Combine(Home, "db");
    public static string Logs => Path.Combine(Home, "logs");

    /// <summary>
    /// 主密钥文件：db/.baihua-key
    /// </summary>
    public static string KeyFile => Path.Combine(Db, ".baihua-key");

    public static void Reset() { lock (_lock) { _home = null; } }

    private static string GetDefaultHome()
    {
        if (OperatingSystem.IsLinux())
        {
            var isDocker = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true"
                           || File.Exists("/.dockerenv");
            if (isDocker)
                return "/opt/baihua/data";

            var home = Environment.GetEnvironmentVariable("HOME");
            return !string.IsNullOrEmpty(home) ? Path.Combine(home, ".baihua") : "/opt/baihua/data";
        }

        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            return !string.IsNullOrEmpty(home) ? Path.Combine(home, ".baihua")
                                               : Path.Combine("/Users", Environment.UserName, ".baihua");
        }

        // Windows
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".baihua");
    }
}
