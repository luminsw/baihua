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

    /// <summary>
    /// 初始化百花数据库：建库（不存在时）+ 为每个上下文建表（该上下文的表一张都不存在时）。
    /// 返回每个上下文的结果描述，便于启动日志与诊断。
    /// </summary>
    public static async Task<IReadOnlyList<string>> InitializeAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var results = new List<string>();

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
                    continue;
                }

                var existing = await GetExistingTablesAsync(context, tables, cancellationToken);

                if (existing.Count == tables.Count)
                {
                    results.Add($"{name}: {tables.Count} 张表已就绪");
                    continue;
                }

                if (existing.Count > 0)
                {
                    // 半成品表结构（历史遗留）：不冒险自动补，交给 DBA/迁移脚本处理
                    var missing = tables.Except(existing, StringComparer.OrdinalIgnoreCase).ToList();
                    logger.LogWarning("{Context} 表结构不完整，缺失 {Count} 张表：{Tables}",
                        name, missing.Count, string.Join(", ", missing.Take(10)));
                    results.Add($"{name}: 部分缺失（{missing.Count}/{tables.Count}）");
                    continue;
                }

                await creator.CreateTablesAsync(cancellationToken);
                results.Add($"{name}: 已建 {tables.Count} 张表");
                logger.LogInformation("已为 {Context} 建表 {Count} 张", name, tables.Count);
            }
            catch (Exception ex)
            {
                results.Add($"{name}: 初始化失败（{ex.Message}）");
                logger.LogError(ex, "{Context} 表结构初始化失败", name);
            }
        }

        return results;
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
