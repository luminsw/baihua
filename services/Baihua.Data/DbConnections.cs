namespace Baihua.Data;

/// <summary>
/// PostgreSQL 连接配置：<b>整个百花只有一个数据库</b>（默认 baihua）。
///
/// 合并前的 family / vault / ai 三库已归一到一个库、一个 public schema；
/// 模块边界靠各自的 DbContext（实体归属）而不是靠物理分库来隔离，
/// 因此备份、恢复、连接配置、部署都只需处理一个库。
/// </summary>
public static class DbConnections
{
    /// <summary>默认数据库名</summary>
    public const string DefaultDatabaseName = "baihua";

    /// <summary>数据库名（可用 PG_DATABASE 覆盖）</summary>
    public static string DatabaseName =>
        Environment.GetEnvironmentVariable("PG_DATABASE") ?? DefaultDatabaseName;

    /// <summary>百花数据库连接串（宿主与各模块的 DbContext 共用）</summary>
    public static string Baihua => Build(DatabaseName);

    /// <summary>
    /// 按库名拼连接串。仅用于工具/脚本（如备份恢复到临时库），业务代码请用 <see cref="Baihua"/>。
    /// </summary>
    public static string Build(string databaseName)
    {
        var host = Environment.GetEnvironmentVariable("PG_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("PG_USER") ?? "baihua";
        var pw = Environment.GetEnvironmentVariable("PG_PASSWORD") ?? "Baihua2026Pg!";
        return $"Host={host};Port=5432;Database={databaseName};Username={user};Password={pw};";
    }
}
