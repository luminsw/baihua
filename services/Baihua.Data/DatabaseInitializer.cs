using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Baihua.Data;

/// <summary>
/// Shared SQLite database schema initialization. Each module context is initialized
/// independently because all contexts share one database file.
/// </summary>
public static class DatabaseInitializer
{
    /// <summary>本库中的所有 DbContext（合并后各模块的表结构集合）</summary>
    public static readonly Type[] ContextTypes =
    [
        typeof(FamilyDbContext),
        typeof(VaultDbContext),
        typeof(AIDbContext)
    ];

    /// <summary>初始化结果：失败数为 0 表示全部就绪（宿主据此决定是否重试）</summary>
    public sealed record InitResult(int Succeeded, int Failed, IReadOnlyList<string> Messages)
    {
        /// <summary>是否全部成功</summary>
        public bool AllSucceeded => Failed == 0;

        /// <summary>是否全部失败（典型场景：数据库还没起来）——宿主应重试</summary>
        public bool AllFailed => Succeeded == 0;
    }

    /// <summary>
    /// Create the database directory/file as needed and initialize each context's tables.
    /// 单个上下文失败不会抛出（按上下文隔离记录），由调用方根据 <see cref="InitResult.Failed"/> 决定重试。
    /// </summary>
    public static async Task<InitResult> InitializeAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbConnections.DatabasePath)!);

        var results = new List<string>();
        var succeeded = 0;
        var failed = 0;

        foreach (var contextType in ContextTypes)
        {
            var name = contextType.Name;
            try
            {
                var context = (DbContext)services.GetRequiredService(contextType);

                // 非关系库（如测试用 InMemory）：交由各自的测试装配建表，这里不介入
                if (!context.Database.IsRelational())
                {
                    results.Add($"{name}: 非关系型提供程序，跳过");
                    continue;
                }

                var creator = (RelationalDatabaseCreator)context.Database.GetService<IDatabaseCreator>();

                if (!await creator.ExistsAsync(cancellationToken))
                {
                    await creator.CreateAsync(cancellationToken);
                    logger.LogInformation("已创建 SQLite 数据库 {Database}", DbConnections.DatabasePath);
                }

                // 单文件数据库没有服务端配置：并发语义（WAL / 忙等待 / 外键）必须在连接上显式设置。
                // WAL 持久化在库文件里，重复执行无副作用；其它 PRAGMA 是连接级的，每次启动都要设。
                await ConfigureSqliteAsync(context, logger, cancellationToken);

                var tables = context.Model.GetEntityTypes()
                    .Select(e => e.GetTableName())
                    .Where(t => !string.IsNullOrEmpty(t))
                    .Select(t => t!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (tables.Count == 0)
                {
                    results.Add($"{name}: 无实体");
                    succeeded++;
                    continue;
                }

                var existing = await GetExistingTablesAsync(context, tables, cancellationToken);

                if (existing.Count == tables.Count)
                {
                    results.Add($"{name}: {tables.Count} 张表已就绪");
                    succeeded++;
                    continue;
                }

                if (existing.Count > 0)
                {
                    // 半成品表结构：用 EF 生成的建表脚本补齐缺表，而不是放弃初始化。
                    var missing = tables.Except(existing, StringComparer.OrdinalIgnoreCase).ToList();
                    var filled = await CreateMissingTablesAsync(context, missing, logger, cancellationToken);
                    results.Add(filled == missing.Count
                        ? $"{name}: 已补齐缺失的 {filled} 张表（原有 {existing.Count} 张）"
                        : $"{name}: 补齐 {filled}/{missing.Count} 张缺失表（其余失败，见日志）");
                    if (filled == missing.Count) succeeded++; else failed++;
                    continue;
                }

                await creator.CreateTablesAsync(cancellationToken);
                results.Add($"{name}: 已建 {tables.Count} 张表");
                logger.LogInformation("已为 {Context} 建表 {Count} 张", name, tables.Count);
                succeeded++;
            }
            catch (Exception ex)
            {
                failed++;
                results.Add($"{name}: 初始化失败（{ex.Message}）");
                logger.LogError(ex, "{Context} 表结构初始化失败", name);
            }
        }

        return new InitResult(succeeded, failed, results);
    }

    /// <summary>
    /// SQLite 单写者模型的运行参数：
    /// <list type="bullet">
    /// <item>journal_mode=WAL：读不阻塞写（持久化在库文件里，重复执行无副作用）；</item>
    /// <item>busy_timeout=15s：写冲突时等锁，而不是立刻抛 "database is locked"；</item>
    /// <item>synchronous=NORMAL：WAL 下的推荐档位，掉电最多丢最后一个事务、不会损坏库；</item>
    /// <item>foreign_keys=ON：SQLite 默认关闭外键，EF 的级联删除/约束依赖它。</item>
    /// </list>
    /// </summary>
    private static async Task ConfigureSqliteAsync(
        DbContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!context.Database.IsSqlite()) return;

        try
        {
            await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
            await context.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=15000;", cancellationToken);
            await context.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", cancellationToken);
            await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;", cancellationToken);
        }
        catch (Exception ex)
        {
            // 例如数据库放在不支持 WAL 的网络盘：降级为默认 journal，不阻断启动
            logger.LogWarning(ex, "SQLite 运行参数设置失败（{Database}），继续使用默认值",
                DbConnections.DatabasePath);
        }
    }

    /// <summary>
    /// 只补齐缺失的表：用 EF 的建表脚本（GenerateCreateScript）筛出与缺失表相关的语句执行。
    /// 先建表、再建索引/约束；已存在的对象报错时忽略（幂等）。
    /// </summary>
    private static async Task<int> CreateMissingTablesAsync(
        DbContext context,
        IReadOnlyList<string> missingTables,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (missingTables.Count == 0) return 0;

        var script = context.Database.GenerateCreateScript();
        var statements = script
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToList();

        bool MentionsMissing(string sql) =>
            missingTables.Any(t => sql.Contains($"\"{t}\"", StringComparison.OrdinalIgnoreCase));

        bool IsCreateTableOf(string sql, string table) =>
            sql.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains($"\"{table}\"", StringComparison.OrdinalIgnoreCase);

        // 1) 先建缺失表的 CREATE TABLE，2) 再补引用这些表的索引/约束语句
        var tableStatements = statements
            .Where(s => missingTables.Any(t => IsCreateTableOf(s, t)))
            .ToList();
        var followUpStatements = statements
            .Where(s => MentionsMissing(s) && !tableStatements.Contains(s))
            .ToList();

        var createdTables = 0;
        foreach (var sql in tableStatements.Concat(followUpStatements))
        {
            try
            {
                // ExecuteSqlRaw 会把 {} 当格式占位符：脚本里可能有 DEFAULT '{}' 之类的字面量
                var escaped = sql.Replace("{", "{{").Replace("}", "}}");
                await context.Database.ExecuteSqlRawAsync(escaped + ";", cancellationToken);
                if (tableStatements.Contains(sql)) createdTables++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "补建表结构语句失败（多半是对象已存在，可忽略）：{Sql}",
                    sql.Length > 120 ? sql[..120] : sql);
            }
        }

        logger.LogInformation("补齐缺失表：目标 {Missing} 张，成功 {Created} 张", missingTables.Count, createdTables);
        return createdTables;
    }

    /// <summary>查询给定表名中已存在的那些（按数据库提供程序选择系统表）</summary>
    private static async Task<List<string>> GetExistingTablesAsync(
        DbContext context,
        IReadOnlyList<string> tables,
        CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            var names = string.Join(",", tables.Select((_, i) => $"@p{i}"));

            command.CommandText =
                $"SELECT name FROM sqlite_master WHERE type = 'table' AND lower(name) IN ({names})";

            for (var i = 0; i < tables.Count; i++)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = $"@p{i}";
                parameter.Value = tables[i].ToLowerInvariant();
                command.Parameters.Add(parameter);
            }

            var found = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                found.Add(reader.GetString(0));
            }
            return found;
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }
}
