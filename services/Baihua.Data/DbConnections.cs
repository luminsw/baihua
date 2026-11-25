using Baihua.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Baihua.Data;

/// <summary>
/// 百花单库配置（SQLite）：整个后端只有一个数据库文件，三个模块的 DbContext 共用它。
///
/// 默认位置 <c>$BAIHUA_HOME/db/baihua.db</c>，可用 <c>BAIHUA_DATABASE_PATH</c> 覆盖
/// （备份恢复、迁移工具、测试用它指向别的文件）。
///
/// 选 SQLite 的原因：家庭单机部署只需要一个进程 + 一个文件，没有独立数据库服务要启动、
/// 没有启动顺序问题；备份就是复制文件。代价是单写者模型：启动时确保 WAL（读不阻塞写），
/// 每个连接用 busy_timeout 排队等锁（见 <see cref="SqlitePragmasInterceptor"/>）。
/// </summary>
public static class DbConnections
{
    /// <summary>默认数据库文件名</summary>
    public const string DefaultDatabaseName = "baihua.db";

    /// <summary>数据库文件绝对路径</summary>
    public static string DatabasePath
    {
        get
        {
            var configuredPath = Environment.GetEnvironmentVariable("BAIHUA_DATABASE_PATH");
            var path = string.IsNullOrWhiteSpace(configuredPath)
                ? Path.Combine(BaihuaPaths.Db, DefaultDatabaseName)
                : configuredPath;
            return Path.GetFullPath(path);
        }
    }

    /// <summary>数据库文件名（日志/健康检查展示用）</summary>
    public static string DatabaseName => Path.GetFileName(DatabasePath);

    /// <summary>百花数据库连接串（宿主与各模块的 DbContext 共用）</summary>
    public static string Baihua => Build(DatabasePath);

    /// <summary>
    /// 按文件路径拼连接串。仅用于工具/脚本（如备份恢复、迁移到临时库），业务代码请用 <see cref="Baihua"/>。
    /// </summary>
    public static string Build(string databasePath) => new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(databasePath),
        Mode = SqliteOpenMode.ReadWriteCreate,
        ForeignKeys = true,
        DefaultTimeout = 30,
        Pooling = true
    }.ToString();

    /// <summary>
    /// 所有 DbContext 共用的 SQLite 配置：连接串 + 逐连接的 busy_timeout/synchronous
    /// （见 <see cref="SqlitePragmasInterceptor"/>）+ 忽略"模型与库可能不同步"的告警
    /// （表由 <see cref="DatabaseInitializer"/> 统一建/补，不用 EF Migrations）。
    /// </summary>
    public static void Configure(DbContextOptionsBuilder options, string? connectionString = null)
    {
        options.UseSqlite(connectionString ?? Baihua)
            .AddInterceptors(SqlitePragmasInterceptor.Instance)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
    }
}
