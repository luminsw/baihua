using Xunit;
using System.Data.Common;
using Baihua.Data;
using Microsoft.EntityFrameworkCore;

namespace Baihua.Family.Tests.Storage;

/// <summary>
/// SQLite 并发/持久性参数的回归锁：这些 PRAGMA 是连接级的，漏了不会编译报错，
/// 只会在"后台任务与 HTTP 请求同时写"时表现为 database is locked。
/// </summary>
public class SqlitePragmasTests
{
    [Fact]
    public void ConfiguredContext_SetsBusyTimeoutSynchronousAndWal()
    {
        var path = Path.Combine(Path.GetTempPath(), $"baihua-pragmas-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<FamilyDbContext>();
            DbConnections.Configure(options, $"Data Source={path};Foreign Keys=True;Default Timeout=10;");

            using var db = new FamilyDbContext(options.Options);
            db.Database.EnsureCreated();

            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) connection.Open();

            Assert.Equal(15000L, ReadLong(connection, "PRAGMA busy_timeout"));
            Assert.Equal(1L, ReadLong(connection, "PRAGMA synchronous"));   // 1 = NORMAL
            Assert.Equal(1L, ReadLong(connection, "PRAGMA foreign_keys"));
            Assert.Equal("wal", ReadString(connection, "PRAGMA journal_mode"));
        }
        finally
        {
            DeleteIfExists(path);
            DeleteIfExists(path + "-wal");
            DeleteIfExists(path + "-shm");
        }
    }

    private static long ReadLong(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string ReadString(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? "";
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // 清理失败不影响断言结果
        }
    }
}
