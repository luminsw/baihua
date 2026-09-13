namespace Baihua.Data;

/// <summary>
/// 数据库连接配置：<b>整个百花只有一个数据库</b>（默认 baihua）。
///
/// 支持 PostgreSQL（默认）与 SQLite（嵌入式，单文件）两种 provider，由 <c>BAIHUA_DB_PROVIDER</c> 环境变量切换。
/// SQLite 模式下数据库是单个文件（默认 ./baihua.db），无需外部数据库服务，适合单二进制部署体验。
/// </summary>
public static class DbConnections
{
    /// <summary>默认数据库名（PG）</summary>
    public const string DefaultDatabaseName = "baihua";

    /// <summary>默认 SQLite 文件路径</summary>
    public const string DefaultSqlitePath = "baihua.db";

    /// <summary>当前 provider：postgres（默认）或 sqlite</summary>
    public static string Provider =>
        (Environment.GetEnvironmentVariable("BAIHUA_DB_PROVIDER") ?? "postgres").ToLowerInvariant();

    /// <summary>是否 SQLite</summary>
    public static bool IsSqlite => Provider == "sqlite";

    /// <summary>数据库名（可用 PG_DATABASE 覆盖）</summary>
    public static string DatabaseName =>
        Environment.GetEnvironmentVariable("PG_DATABASE") ?? DefaultDatabaseName;

    /// <summary>SQLite 文件路径（可用 SQLITE_PATH 覆盖）</summary>
    public static string SqlitePath =>
        Environment.GetEnvironmentVariable("SQLITE_PATH") ?? DefaultSqlitePath;

    /// <summary>百花数据库连接串（宿主与各模块的 DbContext 共用）</summary>
    public static string Baihua => IsSqlite ? BuildSqlite() : Build(DatabaseName);

    /// <summary>SQLite 连接串</summary>
    public static string BuildSqlite()
    {
        var path = SqlitePath;
        return $"Data Source={path};";
    }

    /// <summary>
    /// 按库名拼 PostgreSQL 连接串。仅用于工具/脚本（如备份恢复到临时库），业务代码请用 <see cref="Baihua"/>。
    /// </summary>
    public static string Build(string databaseName)
    {
        var host = Environment.GetEnvironmentVariable("PG_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("PG_USER") ?? "baihua";
        var pw = Environment.GetEnvironmentVariable("PG_PASSWORD") ?? "Baihua2026Pg!";
        return $"Host={host};Port=5432;Database={databaseName};Username={user};Password={pw};";
    }
}
