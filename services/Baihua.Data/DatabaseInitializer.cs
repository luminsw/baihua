using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Baihua.Data;

/// <summary>
/// 单一数据库的表结构初始化。
///
/// 合并前每个服务各占一个库、各自 <c>EnsureCreated()</c>；合并后三个 DbContext 共用一个库，
/// 而 <c>EnsureCreated()</c> 在"库已存在"时直接返回，会导致后两个上下文的表永远建不出来。
/// 这里改为：先确保库存在，再对每个上下文按自己的模型判断并建表。
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
    /// 初始化百花数据库：建库（不存在时）+ 为每个上下文建表（该上下文的表一张都不存在时）。
    /// 单个上下文失败不会抛出（按上下文隔离记录），由调用方根据 <see cref="InitResult.Failed"/> 决定重试。
    /// </summary>
    public static async Task<InitResult> InitializeAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
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
                    logger.LogInformation("已创建数据库 {Database}", DbConnections.DatabaseName);
                }

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
                    // 半成品表结构（典型成因：首次启动时数据库短暂不可用，建表中断）：
                    // 用 EF 生成的建表脚本把"缺的那几张"补齐，而不是放弃 —— 否则后端会一直缺表报 42P01。
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

            // 提供程序无关：PostgreSQL / SQLite / 其它关系库各自的"表清单"系统表
            command.CommandText = context.Database.ProviderName switch
            {
                var p when p != null && p.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) =>
                    $"SELECT table_name FROM information_schema.tables WHERE table_schema = current_schema() AND lower(table_name) IN ({names})",
                var p when p != null && p.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) =>
                    $"SELECT name FROM sqlite_master WHERE type = 'table' AND lower(name) IN ({names})",
                _ => ""
            };

            if (string.IsNullOrEmpty(command.CommandText))
            {
                // 未知提供程序：不做存在性判断（交由 CreateTables 兜底，失败按上下文隔离记录）
                return new List<string>();
            }

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
