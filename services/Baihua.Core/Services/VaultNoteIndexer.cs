using Baihua.Contracts.Search;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Baihua.Data;


namespace Baihua.Core.Services
{
    /// <summary>
    /// 笔记文件指纹（mtime + 大小），用于增量索引的变更检测
    /// </summary>
    public readonly record struct NoteFileStamp(DateTime LastWriteTimeUtc, long Length);

    /// <summary>
    /// 一次索引更新的结果（Snapshot 为更新后的文件指纹快照，供下次增量对比）
    /// </summary>
    public sealed record VaultIndexChangeResult(
        Dictionary<string, NoteFileStamp> Snapshot,
        bool IsFullRebuild,
        int Added,
        int Updated,
        int Removed,
        int Unchanged)
    {
        public bool Changed => Added > 0 || Updated > 0 || Removed > 0;
    }

    /// <summary>
    /// 知识库笔记 FTS5 全文索引服务
    /// </summary>
    public class VaultNoteIndexer
    {
        private readonly IDbContextFactory<VaultDbContext> _dbContextFactory;
        private readonly ILogger<VaultNoteIndexer> _logger;

        public VaultNoteIndexer(IDbContextFactory<VaultDbContext> dbContextFactory, ILogger<VaultNoteIndexer> logger)
        {
            _dbContextFactory = dbContextFactory;
            _logger = logger;
        }

        /// <summary>
        /// 确保全文索引表已创建。
        /// PostgreSQL：普通表 + vault_id 索引 +（可用时）pg_trgm GIN 索引，配合 ILIKE 子串检索；
        /// SQLite（单测/兼容）：保留 FTS5 虚拟表。
        /// </summary>
        public async Task EnsureFtsTableAsync(CancellationToken ct = default)
        {
            try
            {
                using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct);
                var isNpgsql = IsNpgsql(dbContext.Database);

                if (isNpgsql)
                {
                    // PostgreSQL 建表 + 常规索引（中文无空格，FTS5/tsvector 不适用，改用 ILIKE 子串检索）
                    await dbContext.Database.ExecuteSqlRawAsync(@"
                        CREATE TABLE IF NOT EXISTS VaultNoteFts (
                            title TEXT NOT NULL,
                            content TEXT NOT NULL,
                            vault_id TEXT NOT NULL,
                            file_path TEXT NOT NULL
                        );
                    ", ct);
                    await dbContext.Database.ExecuteSqlRawAsync(
                        "CREATE INDEX IF NOT EXISTS IX_VaultNoteFts_vault_id ON VaultNoteFts (vault_id);", ct);
                    await dbContext.Database.ExecuteSqlRawAsync(
                        "CREATE INDEX IF NOT EXISTS IX_VaultNoteFts_vault_file ON VaultNoteFts (vault_id, file_path);", ct);

                    // pg_trgm GIN 索引加速 ILIKE 子串检索；扩展不可用（权限/未安装）时回退顺序扫描，不影响功能
                    try
                    {
                        await dbContext.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS pg_trgm;", ct);
                        await dbContext.Database.ExecuteSqlRawAsync(
                            "CREATE INDEX IF NOT EXISTS IX_VaultNoteFts_content_trgm ON VaultNoteFts USING gin (content gin_trgm_ops);", ct);
                    }
                    catch (Exception pgEx)
                    {
                        _logger.LogWarning(pgEx, "pg_trgm 扩展不可用，知识库全文检索回退为顺序扫描");
                    }
                }
                else
                {
                    // SQLite（单测/兼容路径）：FTS5 虚拟表
                    await dbContext.Database.ExecuteSqlRawAsync(@"
                        CREATE VIRTUAL TABLE IF NOT EXISTS VaultNoteFts USING fts5(
                            title, content, vault_id UNINDEXED, file_path UNINDEXED,
                            tokenize='unicode61 remove_diacritics 2'
                        );
                    ", ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建全文索引表失败");
                throw;
            }
        }

        private static bool IsNpgsql(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade db)
            => db.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ?? false;

        /// <summary>
        /// 重建指定知识库的全文索引（删除 + 逐条插入在同一个事务内，任一步失败整体回滚，
        /// 不会留下半空索引）
        /// </summary>
        public async Task IndexVaultAsync(string vaultId, string vaultPath, CancellationToken ct = default)
        {
            await RebuildAsync(vaultId, vaultPath, ct);
        }

        /// <summary>
        /// 增量更新指定知识库的全文索引：
        /// 以笔记文件 mtime/size 快照对比为准，仅对新增/变更的笔记重新写入、
        /// 对已删除的笔记删除对应 FTS 行，未变更的笔记不处理；
        /// previousSnapshot 为 null 或为空（首次索引 / 快照丢失）时退化为整库重建。
        /// </summary>
        public async Task<VaultIndexChangeResult> IndexVaultChangesAsync(
            string vaultId,
            string vaultPath,
            IReadOnlyDictionary<string, NoteFileStamp>? previousSnapshot,
            CancellationToken ct = default)
        {
            if (!Directory.Exists(vaultPath))
            {
                _logger.LogWarning("知识库路径不存在：{Path}", vaultPath);
                var keep = previousSnapshot == null
                    ? new Dictionary<string, NoteFileStamp>(StringComparer.OrdinalIgnoreCase)
                    : previousSnapshot.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                return new VaultIndexChangeResult(keep, false, 0, 0, 0, 0);
            }

            await EnsureFtsTableAsync(ct);

            // 无法增量（首次 / 快照丢失）→ 整库重建
            if (previousSnapshot == null || previousSnapshot.Count == 0)
            {
                _logger.LogInformation("知识库 {VaultId} 无可用快照，执行整库重建", vaultId);
                var (indexed, snapshot) = await RebuildAsync(vaultId, vaultPath, ct);
                return new VaultIndexChangeResult(snapshot, true, indexed, 0, 0, 0);
            }

            var current = ScanVault(vaultPath);

            var added = new List<string>();
            var updated = new List<string>();
            var removed = new List<string>();
            var unchanged = 0;

            foreach (var (path, stamp) in current)
            {
                if (previousSnapshot.TryGetValue(path, out var prev))
                {
                    if (prev == stamp) unchanged++;
                    else updated.Add(path);
                }
                else
                {
                    added.Add(path);
                }
            }

            foreach (var path in previousSnapshot.Keys)
            {
                if (!current.ContainsKey(path)) removed.Add(path);
            }

            if (added.Count == 0 && updated.Count == 0 && removed.Count == 0)
            {
                _logger.LogDebug("知识库 {VaultId} 无变化，跳过索引", vaultId);
                return new VaultIndexChangeResult(current, false, 0, 0, 0, unchanged);
            }

            _logger.LogInformation("知识库 {VaultId} 增量索引：新增 {Added} 更新 {Updated} 删除 {Removed}",
                vaultId, added.Count, updated.Count, removed.Count);

            using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct);
            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);

            try
            {
                // 删除已移除笔记的 FTS 行
                foreach (var path in removed)
                {
                    await dbContext.Database.ExecuteSqlRawAsync(
                        "DELETE FROM VaultNoteFts WHERE vault_id = {0} AND file_path = {1}",
                        vaultId, path);
                }

                // 新增/变更笔记：先删旧行（避免重复）再插入
                foreach (var path in added.Concat(updated))
                {
                    ct.ThrowIfCancellationRequested();

                    var fullPath = Path.Combine(vaultPath, path);
                    var title = Path.GetFileNameWithoutExtension(path);
                    var content = await ReadNoteContentAsync(fullPath, ct);

                    await dbContext.Database.ExecuteSqlRawAsync(
                        "DELETE FROM VaultNoteFts WHERE vault_id = {0} AND file_path = {1}",
                        vaultId, path);
                    await dbContext.Database.ExecuteSqlRawAsync(
                        "INSERT INTO VaultNoteFts (title, content, vault_id, file_path) VALUES ({0}, {1}, {2}, {3})",
                        title, content, vaultId, path);
                }

                await transaction.CommitAsync(ct);
            }
            catch
            {
                _logger.LogWarning("知识库 {VaultId} 增量索引失败，已回滚，旧索引保持不变", vaultId);
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }

            return new VaultIndexChangeResult(current, false, added.Count, updated.Count, removed.Count, unchanged);
        }

        /// <summary>
        /// 使用全文索引搜索知识库（PostgreSQL: ILIKE 子串 + pg_trgm；SQLite: FTS5）
        /// </summary>
        public async Task<List<SearchResult>> SearchAsync(string vaultId, string query, CancellationToken ct = default)
        {
            var results = new List<SearchResult>();

            try
            {
                await EnsureFtsTableAsync(ct);

                using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct);
                var connection = dbContext.Database.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                    await connection.OpenAsync(ct);

                // 检查该知识库是否有索引
                using (var countCmd = connection.CreateCommand())
                {
                    countCmd.CommandText = "SELECT COUNT(*) FROM VaultNoteFts WHERE vault_id = @vaultId";
                    var p = countCmd.CreateParameter();
                    p.ParameterName = "@vaultId";
                    p.Value = vaultId;
                    countCmd.Parameters.Add(p);
                    var count = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));
                    _logger.LogDebug("知识库 {VaultId} 全文索引数量: {Count}", vaultId, count);
                    if (count == 0)
                    {
                        _logger.LogWarning("知识库 {VaultId} 尚未建立全文索引", vaultId);
                        return results;
                    }
                }

                // 子串检索：Postgres 用 ILIKE（大小写不敏感，且命中 pg_trgm GIN 索引），SQLite 用 LIKE。
                // 参数化查询 + 转义 % _ \ 通配符，防止 SQL 注入与误匹配。中文无空格，FTS5/tsvector 分词不适用，
                // 故采用子串匹配（对中文与英文均可命中）。
                var likeOp = IsNpgsql(dbContext.Database) ? "ILIKE" : "LIKE";
                var pattern = $"%{EscapeLike(query)}%";

                using var command = connection.CreateCommand();
                command.CommandText = $@"
                    SELECT title, file_path, content
                    FROM VaultNoteFts
                    WHERE vault_id = @vaultId AND (title {likeOp} @pattern ESCAPE '\' OR content {likeOp} @pattern ESCAPE '\')
                    ORDER BY (title {likeOp} @pattern ESCAPE '\') DESC, file_path
                    LIMIT 50
                ";

                var vaultIdParam = command.CreateParameter();
                vaultIdParam.ParameterName = "@vaultId";
                vaultIdParam.Value = vaultId;
                command.Parameters.Add(vaultIdParam);

                var patternParam = command.CreateParameter();
                patternParam.ParameterName = "@pattern";
                patternParam.Value = pattern;
                command.Parameters.Add(patternParam);

                using var reader = await command.ExecuteReaderAsync(ct);

                while (await reader.ReadAsync(ct))
                {
                    var title = reader.GetString(0);
                    var filePath = reader.GetString(1);
                    var content = reader.GetString(2);

                    results.Add(new SearchResult
                    {
                        Id = title,
                        Title = title,
                        Path = filePath,
                        Preview = ExtractPreview(content, query),
                        Score = ComputeScore(title, content, query)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FTS5 搜索失败：{Query}", query);
            }

            return results;
        }

        /// <summary>
        /// 获取指定知识库的索引统计
        /// </summary>
        public async Task<(int Count, DateTime? LastIndexed)> GetIndexStatsAsync(string vaultId, CancellationToken ct = default)
        {
            try
            {
                await EnsureFtsTableAsync(ct);
                using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct);
                var connection = dbContext.Database.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                    await connection.OpenAsync(ct);

                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM VaultNoteFts WHERE vault_id = @vaultId";
                var p = cmd.CreateParameter();
                p.ParameterName = "@vaultId";
                p.Value = vaultId;
                cmd.Parameters.Add(p);
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
                return (count, null);
            }
            catch
            {
                return (0, null);
            }
        }

        /// <summary>
        /// 读取笔记内容（protected virtual 便于测试注入读取失败/统计读取次数）
        /// </summary>
        protected virtual Task<string> ReadNoteContentAsync(string filePath, CancellationToken ct)
            => File.ReadAllTextAsync(filePath, ct);

        /// <summary>
        /// 扫描知识库全部 .md 笔记，返回相对路径 → 文件指纹（跳过 README.md）。
        /// 共享实现：供 EmbeddingService 增量向量索引复用（同命名空间 internal）。
        /// </summary>
        internal static Dictionary<string, NoteFileStamp> ScanVaultShared(string vaultPath)
        {
            var result = new Dictionary<string, NoteFileStamp>(StringComparer.OrdinalIgnoreCase);
            var files = Directory.GetFiles(vaultPath, "*.md", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("README.md", StringComparison.OrdinalIgnoreCase))
                    continue;

                var relativePath = file.Substring(vaultPath.Length).TrimStart('/', '\\');
                var info = new FileInfo(file);
                result[relativePath] = new NoteFileStamp(info.LastWriteTimeUtc, info.Length);
            }
            return result;
        }

        /// <summary>
        /// 扫描知识库全部 .md 笔记，返回相对路径 → 文件指纹（跳过 README.md）
        /// </summary>
        private static Dictionary<string, NoteFileStamp> ScanVault(string vaultPath)
            => ScanVaultShared(vaultPath);

        /// <summary>
        /// 整库重建：删除该知识库全部旧索引后逐条插入，同一事务内完成，任一步失败整体回滚
        /// </summary>
        private async Task<(int Indexed, Dictionary<string, NoteFileStamp> Snapshot)> RebuildAsync(
            string vaultId, string vaultPath, CancellationToken ct)
        {
            if (!Directory.Exists(vaultPath))
            {
                _logger.LogWarning("知识库路径不存在：{Path}", vaultPath);
                return (0, new Dictionary<string, NoteFileStamp>(StringComparer.OrdinalIgnoreCase));
            }

            await EnsureFtsTableAsync(ct);

            var snapshot = ScanVault(vaultPath);

            using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct);
            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);

            try
            {
                // 1. 删除该知识库的旧索引
                await dbContext.Database.ExecuteSqlRawAsync(
                    "DELETE FROM VaultNoteFts WHERE vault_id = {0}",
                    vaultId);

                // 2. 扫描所有 .md 文件
                var files = Directory.GetFiles(vaultPath, "*.md", SearchOption.AllDirectories);
                _logger.LogInformation("开始索引知识库 {VaultId}，共 {Count} 个文件", vaultId, files.Length);

                var indexed = 0;
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();

                    var fileName = Path.GetFileName(file);
                    if (fileName.Equals("README.md", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var title = Path.GetFileNameWithoutExtension(file);
                    var relativePath = file.Substring(vaultPath.Length).TrimStart('/', '\\');
                    var content = await ReadNoteContentAsync(file, ct);

                    // 3. 插入索引（批量插入性能更好，但 FTS5 不支持普通 INSERT 的批量优化）
                    await dbContext.Database.ExecuteSqlRawAsync(
                        "INSERT INTO VaultNoteFts (title, content, vault_id, file_path) VALUES ({0}, {1}, {2}, {3})",
                        title, content, vaultId, relativePath);

                    indexed++;
                    if (indexed % 100 == 0)
                    {
                        _logger.LogDebug("已索引 {Indexed}/{Total} 个文件", indexed, files.Length);
                    }
                }

                await transaction.CommitAsync(ct);
                _logger.LogInformation("知识库 {VaultId} 索引完成：{Indexed}/{Total} 个文件", vaultId, indexed, files.Length);
                return (indexed, snapshot);
            }
            catch
            {
                _logger.LogWarning("知识库 {VaultId} 索引重建失败，已回滚，旧索引保持不变", vaultId);
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        private static string ExtractPreview(string content, string query)
        {
            var queryLower = query.ToLower();
            var contentLower = content.ToLower();
            var idx = contentLower.IndexOf(queryLower);

            if (idx < 0)
            {
                // 尝试匹配第一个关键词
                var firstWord = queryLower.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrEmpty(firstWord))
                    idx = contentLower.IndexOf(firstWord);
            }

            if (idx < 0)
                return content.Length > 200 ? content[..200] + "..." : content;

            var start = Math.Max(0, idx - 80);
            var length = Math.Min(200, content.Length - start);
            var preview = content.Substring(start, length);

            if (start > 0) preview = "..." + preview;
            if (start + length < content.Length) preview += "...";

            return preview;
        }

        /// <summary>转义 LIKE/ILIKE 通配符（% _ \），避免用户输入被当作通配符</summary>
        private static string EscapeLike(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            return s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        }

        /// <summary>简单相关性打分：标题命中优先级高于正文，正文命中按出现位置给分</summary>
        private static int ComputeScore(string title, string content, string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return 0;

            var score = 0;
            if (title.Contains(query, StringComparison.OrdinalIgnoreCase))
                score += 200;

            var idx = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                score += Math.Max(50, 100 - idx / 10);

            return score;
        }
    }
}
