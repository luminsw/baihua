using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Baihua.Data;

/// <summary>
/// SQLite 连接级参数。
///
/// 连接串里没有 pragma 关键字，而这些参数是**逐连接**生效的，所以必须在每个新连接上执行：
/// <list type="bullet">
/// <item><c>busy_timeout=15000</c>：SQLite 默认 0——并发写时第二个写者立刻拿到
///   "database is locked"，而不是排队等锁。服务端有后台任务（索引、备份、发现）+ HTTP 请求
///   同时写，必须让它等。</item>
/// <item><c>synchronous=NORMAL</c>：WAL 下的推荐档位（默认 FULL）。掉电最多丢最后一个事务，
///   不会损坏数据库。</item>
/// </list>
/// <c>journal_mode=WAL</c> 是库级持久设置，由 <see cref="DatabaseInitializer"/> 在启动时保证，
/// 不在每个连接上重复设置（多连接时切换 journal mode 会失败）。
/// </summary>
public sealed class SqlitePragmasInterceptor : DbConnectionInterceptor
{
    public static readonly SqlitePragmasInterceptor Instance = new();

    private const string Pragmas = "PRAGMA busy_timeout=15000; PRAGMA synchronous=NORMAL;";

    private SqlitePragmasInterceptor()
    {
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (connection is not SqliteConnection sqlite) return;

        using var command = sqlite.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (connection is not SqliteConnection sqlite) return;

        await using var command = sqlite.CreateCommand();
        command.CommandText = Pragmas;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
