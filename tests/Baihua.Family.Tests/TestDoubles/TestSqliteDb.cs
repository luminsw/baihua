using Baihua.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Baihua.Family.Tests.TestDoubles;

/// <summary>
/// SQLite 测试库辅助。
/// 产品已迁移 PostgreSQL（默认值 SQL 为 PG 的 now()），SQLite 原生不认识 now()，
/// 测试统一在此注册自定义 now() 函数（返回与 datetime('now') 一致的 "yyyy-MM-dd HH:mm:ss"）。
/// </summary>
public static class TestSqliteDb
{
    /// <summary>打开内存 SQLite 连接并注册 now() 函数（等效 PG 的 now()，返回当前时间字符串）。</summary>
    public static SqliteConnection OpenInMemory()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        RegisterNow(conn);
        return conn;
    }

    public static void RegisterNow(SqliteConnection conn)
        => conn.CreateFunction("now", () => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

    /// <summary>只用于建表（EnsureCreated）的选项：不涉及 now() 默认值</summary>
    public static DbContextOptions<FamilyDbContext> FamilyOptions(string dbPath)
        => new DbContextOptionsBuilder<FamilyDbContext>()
            .UseSqlite($"Data Source={dbPath};Foreign Keys=True;Default Timeout=10;")
            .Options;

    /// <summary>只用于建表（EnsureCreated）的选项：不涉及 now() 默认值</summary>
    public static DbContextOptions<VaultDbContext> VaultOptions(string dbPath)
        => new DbContextOptionsBuilder<VaultDbContext>()
            .UseSqlite($"Data Source={dbPath};Foreign Keys=True;Default Timeout=10;")
            .Options;

    /// <summary>
    /// 把 host 的 DbContext 注册从 PostgreSQL 替换为 SQLite（WebApplicationFactory 测试用）。
    /// 覆盖顺序：WebApplicationFactory 的 ConfigureServices 在 Program 注册之后执行。
    /// 注意：EF 的 AddDbContextFactory(optionsAction) 会把 IDbContextOptionsConfiguration 注册为
    /// scoped，单例工厂从 root provider 解析 CreateDbContext 会抛 "Cannot resolve scoped service"；
    /// 因此这里用纯单例工厂 + 固定 SQLite 连接（不经过 EF 的 options 机制）。
    ///
    /// 合并后的产品是"一个库 + 三个模块各自的 DbContext"：<paramref name="aiDbPath"/> 默认与
    /// family 同库（真实单库语义），AI 模块的表由 host 的 DatabaseInitializer 建出。
    /// </summary>
    public static void ConfigureSqlite(IServiceCollection services, string familyDbPath, string vaultDbPath, string? aiDbPath = null)
    {
        aiDbPath ??= familyDbPath;

        services.RemoveAll<FamilyDbContext>();
        services.RemoveAll<IDbContextFactory<FamilyDbContext>>();
        services.RemoveAll<IDbContextOptionsConfiguration<FamilyDbContext>>();
        services.RemoveAll<VaultDbContext>();
        services.RemoveAll<IDbContextFactory<VaultDbContext>>();
        services.RemoveAll<IDbContextOptionsConfiguration<VaultDbContext>>();
        services.RemoveAll<AIDbContext>();
        services.RemoveAll<IDbContextFactory<AIDbContext>>();
        services.RemoveAll<IDbContextOptionsConfiguration<AIDbContext>>();

        services.AddSingleton<IDbContextFactory<FamilyDbContext>>(new PlainSqliteFactory<FamilyDbContext>(familyDbPath));
        services.AddSingleton<IDbContextFactory<VaultDbContext>>(new PlainSqliteFactory<VaultDbContext>(vaultDbPath));
        services.AddSingleton<IDbContextFactory<AIDbContext>>(new PlainSqliteFactory<AIDbContext>(aiDbPath));

        // 保留 scoped 上下文注册（构造注入用），内部复用同一 SQLite 库
        services.AddScoped<FamilyDbContext>(sp => sp.GetRequiredService<IDbContextFactory<FamilyDbContext>>().CreateDbContext());
        services.AddScoped<VaultDbContext>(sp => sp.GetRequiredService<IDbContextFactory<VaultDbContext>>().CreateDbContext());
        services.AddScoped<AIDbContext>(sp => sp.GetRequiredService<IDbContextFactory<AIDbContext>>().CreateDbContext());
    }

    /// <summary>
    /// 固定库文件的纯工厂：不经 EF options 配置，可从 root provider 安全解析。
    /// 每个工厂持有一个已注册 now() 的常开连接，供其创建的所有上下文复用
    /// —— 否则 SQLite 下 PG 的 now() 默认值会报 "unknown function: now()"。
    /// </summary>
    private sealed class PlainSqliteFactory<T>(string dbPath) : IDbContextFactory<T> where T : DbContext
    {
        private readonly DbContextOptions<T> _options = BuildOptions(dbPath);

        private static DbContextOptions<T> BuildOptions(string path)
        {
            var connection = new SqliteConnection($"Data Source={path};Foreign Keys=True;Default Timeout=10;");
            connection.Open();
            RegisterNow(connection);
            return new DbContextOptionsBuilder<T>().UseSqlite(connection).Options;
        }

        public T CreateDbContext()
            => (T)Activator.CreateInstance(typeof(T), _options)!;

        public Task<T> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
