using System.Data;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Baihua.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

return await DatabaseMigration.RunAsync(args);

internal static class DatabaseMigration
{
    private static readonly BigInteger HashModulus = BigInteger.One << 256;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 1 && args[0] is "-h" or "--help")
        {
            PrintUsage();
            return 0;
        }

        string? targetArgument = null;
        var rewrites = new List<(string From, string To)>();
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--target" when index + 1 < args.Length:
                    targetArgument = args[++index];
                    break;
                case "--rewrite-prefix" when index + 1 < args.Length:
                    var spec = args[++index];
                    var separator = spec.IndexOf('=');
                    if (separator <= 0 || separator == spec.Length - 1)
                    {
                        Console.Error.WriteLine($"--rewrite-prefix needs OLD=NEW, got: {spec}");
                        return 2;
                    }
                    rewrites.Add((spec[..separator], spec[(separator + 1)..]));
                    break;
                default:
                    PrintUsage();
                    return 2;
            }
        }

        if (string.IsNullOrWhiteSpace(targetArgument))
        {
            PrintUsage();
            return 2;
        }

        var targetPath = Path.GetFullPath(targetArgument);
        var targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("The target database path has no parent directory.");
        Directory.CreateDirectory(targetDirectory);
        if (File.Exists(targetPath))
        {
            Console.Error.WriteLine($"Refusing to overwrite existing SQLite database: {targetPath}");
            return 2;
        }

        var temporaryPath = Path.Combine(targetDirectory, $".baihua-migrate-{Guid.NewGuid():N}.db");
        try
        {
            var sourceConnectionString = BuildSourceConnectionString();
            await CreateSqliteSchemaAsync(temporaryPath);
            await CopyAndValidateAsync(sourceConnectionString, temporaryPath, rewrites);
            File.Move(temporaryPath, targetPath);
            RestrictPermissions(targetPath);
            Console.WriteLine($"Migration complete. PostgreSQL was not modified; SQLite database: {targetPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Migration failed: {ex.Message}");
            return 1;
        }
        finally
        {
            DeleteIfExists(temporaryPath);
            DeleteIfExists(temporaryPath + "-journal");
            DeleteIfExists(temporaryPath + "-wal");
            DeleteIfExists(temporaryPath + "-shm");
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage: dotnet run --project tools/db-migrate -- \
                     --target <new-sqlite-file> [--rewrite-prefix OLD=NEW ...]

            Source connection is read from BAIHUA_MIGRATION_POSTGRES_CONNECTION, or from
            PG_HOST, PG_PORT, PG_DATABASE, PG_USER, and PG_PASSWORD. The target must not exist.
            The PostgreSQL source is opened in a read-only repeatable-read transaction.

            --rewrite-prefix 重写路径列（列名以 Path 结尾）里的旧根目录，例如把容器里的
            /app/data/vaults 改成原生部署的 ~/.baihua/vaults。可重复传多组。

            Derived/local-only tables (VaultNoteFts) are skipped and rebuilt by the server.
            Retired tables can be skipped explicitly: BAIHUA_MIGRATION_SKIP_TABLES=Table1,Table2
            """);
    }

    private static string BuildSourceConnectionString()
    {
        var configured = Environment.GetEnvironmentVariable("BAIHUA_MIGRATION_POSTGRES_CONNECTION");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var password = Environment.GetEnvironmentVariable("PG_PASSWORD");
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(
                "Set BAIHUA_MIGRATION_POSTGRES_CONNECTION or PG_PASSWORD for the PostgreSQL source.");
        }

        return new NpgsqlConnectionStringBuilder
        {
            Host = Environment.GetEnvironmentVariable("PG_HOST") ?? "localhost",
            Port = int.TryParse(Environment.GetEnvironmentVariable("PG_PORT"), out var port) ? port : 5432,
            Database = Environment.GetEnvironmentVariable("PG_DATABASE") ?? "baihua",
            Username = Environment.GetEnvironmentVariable("PG_USER") ?? "baihua",
            Password = password,
            Timeout = 15
        }.ConnectionString;
    }

    private static async Task CreateSqliteSchemaAsync(string path)
    {
        Environment.SetEnvironmentVariable("BAIHUA_DATABASE_PATH", path);
        var connectionString = new SqliteConnectionStringBuilder(DbConnections.Baihua)
        {
            Pooling = false
        }.ToString();

        var services = new ServiceCollection();
        services.AddDbContext<FamilyDbContext>(options => options.UseSqlite(connectionString)
            .AddInterceptors(SqlitePragmasInterceptor.Instance));
        services.AddDbContext<VaultDbContext>(options => options.UseSqlite(connectionString)
            .AddInterceptors(SqlitePragmasInterceptor.Instance));
        services.AddDbContext<AIDbContext>(options => options.UseSqlite(connectionString)
            .AddInterceptors(SqlitePragmasInterceptor.Instance));
        using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var result = await DatabaseInitializer.InitializeAsync(
            scope.ServiceProvider, NullLogger.Instance);
        if (!result.AllSucceeded)
        {
            throw new InvalidOperationException(
                $"Could not create the SQLite schema: {string.Join("; ", result.Messages)}");
        }
    }

    private static async Task CopyAndValidateAsync(
        string sourceConnectionString,
        string targetPath,
        IReadOnlyList<(string From, string To)> rewrites)
    {
        var sqliteConnectionString = new SqliteConnectionStringBuilder(DbConnections.Baihua)
        {
            Pooling = false
        }.ToString();

        await using var source = new NpgsqlConnection(sourceConnectionString);
        await source.OpenAsync();
        await using var sourceTransaction =
            (NpgsqlTransaction)await source.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", source, sourceTransaction))
        {
            await readOnly.ExecuteNonQueryAsync();
        }

        await using var target = new SqliteConnection(sqliteConnectionString);
        await target.OpenAsync();
        await using (var disableForeignKeys = target.CreateCommand())
        {
            disableForeignKeys.CommandText = "PRAGMA foreign_keys = OFF";
            await disableForeignKeys.ExecuteNonQueryAsync();
        }

        await using var targetTransaction = (SqliteTransaction)await target.BeginTransactionAsync();
        var targetTables = await GetSqliteTablesAsync(target, targetTransaction);
        var sourceTables = await GetPostgresTablesAsync(source, sourceTransaction);
        var skipTables = GetSkipTables();
        long copiedRows = 0;
        var skippedTables = 0;

        foreach (var table in sourceTables)
        {
            if (skipTables.Contains(table))
            {
                var skippedRows = await CountPostgresRowsAsync(source, sourceTransaction, table);
                Console.WriteLine($"[skip] {table}: {skippedRows} rows (derived/retired table, not migrated)");
                skippedTables++;
                continue;
            }

            if (!targetTables.Contains(table))
            {
                throw new InvalidOperationException(
                    $"PostgreSQL table '{table}' is not in the current SQLite model; refusing to omit its data. " +
                    $"If it is a retired table, list it in {SkipTablesEnvironmentVariable}.");
            }

            var sourceColumns = await GetPostgresColumnsAsync(source, sourceTransaction, table);
            var targetColumns = await GetSqliteColumnsAsync(target, targetTransaction, table);
            var targetColumnNames = targetColumns.Select(column => column.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sourceColumnNames = sourceColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var extraColumns = sourceColumns.Where(column => !targetColumnNames.Contains(column)).ToList();
            foreach (var column in extraColumns)
            {
                await ExecuteSqliteAsync(target, targetTransaction,
                    $"ALTER TABLE {Quote(table)} ADD COLUMN {Quote(column)} BLOB");
            }

            var missingRequiredColumns = targetColumns
                .Where(column => !sourceColumnNames.Contains(column.Name)
                    && column.NotNull
                    && column.DefaultValue is null
                    && !column.IsPrimaryKey)
                .Select(column => column.Name)
                .ToList();
            if (missingRequiredColumns.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Table '{table}' is missing required source columns: {string.Join(", ", missingRequiredColumns)}.");
            }

            var sourceSignature = await CopyTableAsync(
                source, sourceTransaction, target, targetTransaction, table, sourceColumns, rewrites);
            var targetSignature = await HashSqliteTableAsync(
                target, targetTransaction, table, sourceColumns);
            // HashSignature 没有重载 ==/!=：必须用 Equals，否则永远判不等（迁移 100% 失败）
            if (!sourceSignature.Equals(targetSignature))
            {
                var difference = await DescribeFirstDifferenceAsync(
                    source, sourceTransaction, target, targetTransaction, table, sourceColumns, rewrites);
                throw new InvalidOperationException($"Data validation failed for table '{table}': {difference}");
            }

            if (extraColumns.Count > 0)
            {
                Console.WriteLine($"[preserve] {table}: retained legacy columns {string.Join(", ", extraColumns)}");
            }
            Console.WriteLine($"[copy] {table}: {sourceSignature.Count} rows; data digest verified");
            copiedRows += sourceSignature.Count;
        }

        await targetTransaction.CommitAsync();
        await sourceTransaction.CommitAsync();

        await using (var enableForeignKeys = target.CreateCommand())
        {
            enableForeignKeys.CommandText = "PRAGMA foreign_keys = ON";
            await enableForeignKeys.ExecuteNonQueryAsync();
        }

        await using (var checkForeignKeys = target.CreateCommand())
        {
            checkForeignKeys.CommandText = "PRAGMA foreign_key_check";
            await using var violations = await checkForeignKeys.ExecuteReaderAsync();
            if (await violations.ReadAsync())
            {
                throw new InvalidOperationException(
                    $"SQLite foreign-key validation failed for table '{violations.GetString(0)}'.");
            }
        }

        await using (var integrityCheck = target.CreateCommand())
        {
            integrityCheck.CommandText = "PRAGMA integrity_check";
            var result = Convert.ToString(await integrityCheck.ExecuteScalarAsync());
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"SQLite integrity check failed: {result}");
            }
        }

        Console.WriteLine($"[done] {sourceTables.Count - skippedTables} tables, {copiedRows} rows copied and verified");
    }

    private static async Task<HashSignature> CopyTableAsync(
        NpgsqlConnection source,
        NpgsqlTransaction sourceTransaction,
        SqliteConnection target,
        SqliteTransaction targetTransaction,
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<(string From, string To)> rewrites)
    {
        var selectedColumns = string.Join(", ", columns.Select(Quote));
        await using var select = new NpgsqlCommand(
            $"SELECT {selectedColumns} FROM public.{Quote(table)}", source, sourceTransaction);
        await using var reader = await select.ExecuteReaderAsync();

        var parameters = columns.Select((_, index) => $"@p{index}").ToArray();
        await using var insert = new SqliteCommand(
            $"INSERT INTO {Quote(table)} ({selectedColumns}) VALUES ({string.Join(", ", parameters)})",
            target, targetTransaction);
        foreach (var parameter in parameters)
        {
            insert.Parameters.Add(new SqliteParameter(parameter, DBNull.Value));
        }
        await insert.PrepareAsync();

        var signature = new HashSignature();
        var values = new object[columns.Count];
        while (await reader.ReadAsync())
        {
            reader.GetValues(values);
            var normalized = new object?[values.Length];
            for (var index = 0; index < values.Length; index++)
            {
                normalized[index] = NormalizeValue(values[index], columns[index], rewrites);
                insert.Parameters[index].Value = normalized[index] ?? DBNull.Value;
            }

            signature.Add(normalized);
            await insert.ExecuteNonQueryAsync();
        }

        return signature;
    }

    private static async Task<HashSignature> HashSqliteTableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        IReadOnlyList<string> columns)
    {
        var selectedColumns = string.Join(", ", columns.Select(Quote));
        await using var command = new SqliteCommand(
            $"SELECT {selectedColumns} FROM {Quote(table)}", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        var signature = new HashSignature();
        var values = new object[columns.Count];
        while (await reader.ReadAsync())
        {
            reader.GetValues(values);
            signature.Add(values);
        }
        return signature;
    }

    private static async Task<HashSet<string>> GetSqliteTablesAsync(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        await using var command = new SqliteCommand(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'",
            connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        return tables;
    }

    private static async Task<List<string>> GetPostgresTablesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand("""
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'public' AND table_type = 'BASE TABLE'
            ORDER BY table_name
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        return tables;
    }

    private static async Task<List<string>> GetPostgresColumnsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string table)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT * FROM public.{Quote(table)} LIMIT 0", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        return Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
    }

    private static readonly List<string> DefaultSkippedTables = ["VaultNoteFts"];

    private const string SkipTablesEnvironmentVariable = "BAIHUA_MIGRATION_SKIP_TABLES";

    private static HashSet<string> GetSkipTables()
    {
        var tables = new HashSet<string>(DefaultSkippedTables, StringComparer.OrdinalIgnoreCase);
        var extra = Environment.GetEnvironmentVariable(SkipTablesEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(extra))
        {
            foreach (var name in extra.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                tables.Add(name);
            }
        }
        return tables;
    }

    private static async Task<long> CountPostgresRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM public.{Quote(table)}", connection, transaction);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<List<SqliteColumn>> GetSqliteColumnsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        await using var command = new SqliteCommand(
            $"PRAGMA table_info({Quote(table)})", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<SqliteColumn>();
        while (await reader.ReadAsync())
        {
            columns.Add(new SqliteColumn(
                reader.GetString(1),
                reader.GetInt64(3) != 0,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5) != 0));
        }
        return columns;
    }

    private static async Task ExecuteSqliteAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        await using var command = new SqliteCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 把 PostgreSQL 读出的值转成 EF Core SQLite 写入时的**同一文本表示**，否则迁移校验会误报、
    /// 且同一列出现两种格式（'T' 与 ' ' 分隔）会破坏排序/比较。
    /// 格式来自 EF Core SQLite 的类型映射（已实测核对）：
    /// DateTime  "yyyy-MM-dd HH:mm:ss.FFFFFFF"（UTC），DateTimeOffset 同格式 + zzz，
    /// decimal   "0.0###########################"，Guid 大写 D，bool 1/0，TimeSpan "c"。
    /// </summary>
    private static object? ToSqliteValue(object value) => value switch
    {
        DBNull => null,
        DateTime timestamp => ToUtcKind(timestamp).ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset timestamp => timestamp.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", System.Globalization.CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("HH:mm:ss.FFFFFFF", System.Globalization.CultureInfo.InvariantCulture),
        TimeSpan duration => duration.ToString("c", System.Globalization.CultureInfo.InvariantCulture),
        Guid guid => guid.ToString("D").ToUpperInvariant(),
        bool boolean => boolean ? 1L : 0L,
        decimal number => number.ToString("0.0###########################", System.Globalization.CultureInfo.InvariantCulture),
        byte[] bytes => bytes,
        // SQLite 的 REAL 是 double：float 必须先提升，否则写进去的是 1.1f 的 double 近似值，
        // 校验时按 float 重新格式化会和读回的 double 对不上。
        float number => (double)number,
        Array array => JsonSerializer.Serialize(array),
        _ => value
    };

    private static object? NormalizeValue(
        object? value,
        string column,
        IReadOnlyList<(string From, string To)> rewrites)
    {
        var converted = ToSqliteValue(value);
        if (converted is string text && IsPathColumn(column))
        {
            converted = RewritePath(text, rewrites);
        }
        return converted;
    }

    /// <summary>
    /// 校验失败时定位第一条差异：迁移工具最怕"只报某个表失败"却看不出是哪列——
    /// 常见原因是某种类型的文本表示与 EF 不一致，必须能一眼看到源值/目标值。
    /// </summary>
    private static async Task<string> DescribeFirstDifferenceAsync(
        NpgsqlConnection source,
        NpgsqlTransaction sourceTransaction,
        SqliteConnection target,
        SqliteTransaction targetTransaction,
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<(string From, string To)> rewrites)
    {
        var selectedColumns = string.Join(", ", columns.Select(Quote));

        var sourceRows = new List<object?[]>();
        await using (var select = new NpgsqlCommand(
            $"SELECT {selectedColumns} FROM public.{Quote(table)}", source, sourceTransaction))
        await using (var reader = await select.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var values = new object[columns.Count];
                reader.GetValues(values);
                sourceRows.Add(values.Select((value, index) => NormalizeValue(value, columns[index], rewrites)).ToArray());
            }
        }

        var targetRows = new List<object?[]>();
        await using (var select = new SqliteCommand(
            $"SELECT {selectedColumns} FROM {Quote(table)}", target, targetTransaction))
        await using (var reader = await select.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var values = new object[columns.Count];
                reader.GetValues(values);
                targetRows.Add(values.Select(value => value is DBNull ? null : (object?)value).ToArray());
            }
        }

        if (sourceRows.Count != targetRows.Count)
        {
            return $"row count differs: source={sourceRows.Count}, target={targetRows.Count}";
        }

        for (var row = 0; row < sourceRows.Count; row++)
        {
            for (var column = 0; column < columns.Count; column++)
            {
                var expected = sourceRows[row][column];
                var actual = targetRows[row][column];
                var expectedText = expected is byte[] bytes ? Convert.ToBase64String(bytes) : expected?.ToString();
                var actualText = actual is byte[] actualBytes ? Convert.ToBase64String(actualBytes) : actual?.ToString();
                if (!string.Equals(expectedText, actualText, StringComparison.Ordinal))
                {
                    return $"row {row + 1}, column {columns[column]}: expected '{expectedText}', stored '{actualText}'";
                }
            }
        }

        return "digests differ but row-by-row comparison found no difference (ordering is not stable)";
    }

    /// <summary>只重写看起来是文件系统路径的列，避免误改正文/JSON 里恰好出现的同名字符串。</summary>
    private static bool IsPathColumn(string column) =>
        column.EndsWith("Path", StringComparison.OrdinalIgnoreCase)
        || column.Contains("Directory", StringComparison.OrdinalIgnoreCase);

    private static string RewritePath(string value, IReadOnlyList<(string From, string To)> rewrites)
    {
        foreach (var (from, to) in rewrites)
        {
            if (value.Length >= from.Length
                && value.StartsWith(from, StringComparison.Ordinal)
                && (value.Length == from.Length || value[from.Length] is '/' or '\\'))
            {
                value = to.TrimEnd('/') + value[from.Length..];
            }
        }
        return value;
    }

    /// <summary>PostgreSQL 的 timestamptz 读出来是 UTC；Unspecified（timestamp 类型）按 UTC 处理。</summary>
    private static DateTime ToUtcKind(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value
    };


    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    /// <summary>数据库里有家庭隐私数据：迁移产物只允许当前用户读写。</summary>
    private static void RestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[warn] Could not restrict permissions on {path}: {ex.Message}");
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private sealed record SqliteColumn(string Name, bool NotNull, string? DefaultValue, bool IsPrimaryKey);

    private sealed class HashSignature : IEquatable<HashSignature>
    {
        // 只用于「整表内容是否一致」：异或 + 模 2^256 求和，与行顺序无关。

        private readonly byte[] _xor = new byte[32];
        private BigInteger _sum;
        public long Count { get; private set; }

        public void Add(IReadOnlyList<object?> values)
        {
            var canonical = string.Join("\u001f", values.Select(Canonicalize));
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            for (var index = 0; index < _xor.Length; index++) _xor[index] ^= hash[index];
            _sum = (_sum + new BigInteger(hash, isUnsigned: true, isBigEndian: true)) % HashModulus;
            Count++;
        }

        public bool Equals(HashSignature? other) =>
            other is not null
            && Count == other.Count
            && _sum == other._sum
            && _xor.AsSpan().SequenceEqual(other._xor);

        public override bool Equals(object? obj) => obj is HashSignature other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Count, _sum);

        private static string Canonicalize(object? value) => value switch
        {
            null or DBNull => "null",
            byte[] bytes => "b:" + Convert.ToBase64String(bytes),
            bool boolean => boolean ? "n:1" : "n:0",
            string text => "s:" + text,
            decimal number => "n:" + number.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
            float number => "n:" + number.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            double number => "n:" + number.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            IFormattable number => "n:" + number.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new NotSupportedException($"Cannot validate SQLite value type {value.GetType().FullName}.")
        };
    }
}
