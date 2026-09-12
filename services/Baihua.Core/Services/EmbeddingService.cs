using Baihua.Contracts.Search;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Contracts.Ai;

namespace Baihua.Core.Services
{
    /// <summary>
    /// 语义向量服务：基于 SQLite BLOB 缓存 + IEmbeddingGenerator 抽象，对关键词搜索结果按相似度重排
    ///
    /// 合并为单进程后：Embedding 配置（EmbeddingConfigs 表）仍归 AI 模块所有，
    /// 本服务经 <see cref="Baihua.Core.Modules.IEmbeddingConfigProvider"/> 接口在进程内读取。
    /// </summary>
    public class EmbeddingService
    {
        private readonly AiClientService _aiClientService;
        private readonly AiSettingsService _aiSettings;
        private readonly VaultSettingsService _vaultSettings;
        private readonly IDbContextFactory<VaultDbContext> _vaultDbFactory;
        private readonly Baihua.Core.Modules.IEmbeddingConfigProvider _embeddingConfigProvider;
        private readonly ILogger<EmbeddingService> _logger;

        // Embedding 配置短缓存（向量索引/重排高频调用，避免反复查库）
        private EmbeddingConfig? _cachedEmbeddingConfig;
        private DateTime _embeddingConfigFetchedAtUtc;
        private readonly object _embeddingConfigLock = new();
        private static readonly TimeSpan EmbeddingConfigCacheTtl = TimeSpan.FromSeconds(30);

        private const int MaxNotesToRerank = 50;

        /// <summary>向量写库批量提交条数</summary>
        private const int EmbeddingBatchSize = 50;

        /// <summary>
        /// per-vault 索引并发闸：同一知识库的索引任务串行执行，不同知识库可并行。
        /// key 忽略大小写（vaultId 与文件相对路径的忽略大小写语义保持一致）。
        /// </summary>
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _vaultIndexLocks = new(StringComparer.OrdinalIgnoreCase);

        public EmbeddingService(
            AiClientService aiClientService,
            AiSettingsService aiSettings,
            VaultSettingsService vaultSettings,
            IDbContextFactory<VaultDbContext> vaultDbFactory,
            Baihua.Core.Modules.IEmbeddingConfigProvider embeddingConfigProvider,
            ILogger<EmbeddingService> logger)
        {
            _aiClientService = aiClientService;
            _aiSettings = aiSettings;
            _vaultSettings = vaultSettings;
            _vaultDbFactory = vaultDbFactory;
            _embeddingConfigProvider = embeddingConfigProvider;
            _logger = logger;
        }

        /// <summary>
        /// 获取当前活跃知识库 ID
        /// </summary>
        private string? GetActiveVaultId()
        {
            try
            {
                return _vaultSettings.GetActiveVault()?.Id;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 获取语义搜索配置（优先 AI 服务 EmbeddingConfig 表，经 HTTP 读取）
        /// </summary>
        public bool IsSemanticSearchEnabled()
        {
            var config = GetEmbeddingConfig();
            if (config != null)
                return !string.IsNullOrEmpty(config.BaseUrl) && !string.IsNullOrEmpty(config.Model);
            return !string.IsNullOrEmpty(_aiSettings.SemanticEmbeddingUrl) && 
                   !string.IsNullOrEmpty(_aiSettings.SemanticEmbeddingModel);
        }

        /// <summary>
        /// 读取 Embedding 配置（进程内直调 AI 模块的 <see cref="Baihua.Core.Modules.IEmbeddingConfigProvider"/>）。
        /// 带 30s 短缓存：向量索引/重排高频调用，避免反复查库。
        /// </summary>
        private EmbeddingConfig? GetEmbeddingConfig()
        {
            lock (_embeddingConfigLock)
            {
                if (_cachedEmbeddingConfig != null &&
                    DateTime.UtcNow - _embeddingConfigFetchedAtUtc < EmbeddingConfigCacheTtl)
                {
                    return _cachedEmbeddingConfig;
                }

                EmbeddingConfig? config = null;
                try
                {
                    var settings = _embeddingConfigProvider.GetAsync().GetAwaiter().GetResult();
                    config = new EmbeddingConfig
                    {
                        ProviderId = settings.ProviderId,
                        Model = settings.Model,
                        BaseUrl = settings.BaseUrl,
                        IsEnabled = settings.IsEnabled,
                        Dimensions = settings.Dimensions,
                        EncryptedApiKey = null // 密钥不出 AI 模块
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "读取 EmbeddingConfig 失败");
                }

                // 失败也短暂缓存 null，避免每次调用都重复失败
                _cachedEmbeddingConfig = config;
                _embeddingConfigFetchedAtUtc = DateTime.UtcNow;
                return config;
            }
        }

        /// <summary>
        /// 调用 Embedding API 获取向量
        /// </summary>
        public async Task<List<double>?> GetEmbeddingAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            if (!IsSemanticSearchEnabled())
                return null;

            var sw = Stopwatch.StartNew();
            try
            {
                var config = GetEmbeddingConfig();
                IEmbeddingGenerator<string, Embedding<float>> generator;
                string providerId = "embedding";
                string modelName;

                if (config != null && config.IsEnabled && !string.IsNullOrEmpty(config.BaseUrl) && !string.IsNullOrEmpty(config.Model))
                {
                    // 一服务一数据库：HTTP 配置不含 key（掩码）；本地嵌入模型（bge/Ollama/OpenVINO）无需 Key，
                    // CreateEmbeddingGenerator 无 key 时使用占位凭证。
                    generator = _aiClientService.CreateEmbeddingGenerator(config.BaseUrl, config.Model, apiKey: null);
                    providerId = config.ProviderId;
                    modelName = config.Model;
                }
                else
                {
                    generator = _aiClientService.CreateEmbeddingGenerator();
                    modelName = _aiSettings.SemanticEmbeddingModel;
                }

                var result = await generator.GenerateAsync([text.Trim()]);
                sw.Stop();

                if (result.Count > 0)
                {
                    _logger.LogDebug("Embedding 成功：{Provider}/{Model}，{Latency}ms", providerId, modelName, sw.ElapsedMilliseconds);
                    return result[0].Vector.ToArray().Select(v => (double)v).ToList();
                }

                sw.Stop();
                _logger.LogDebug("Embedding 返回空结果：{Provider}/{Model}", providerId, modelName);
                return null;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogDebug(ex, "获取 Embedding 失败（{Provider}/{Model}）", "embedding", _aiSettings.SemanticEmbeddingModel);
                return null;
            }
        }

        /// <summary>
        /// 计算文本向量（protected virtual 注入点，便于测试注入假实现/统计 API 调用次数；
        /// 参考 VaultNoteIndexer.ReadNoteContentAsync 的做法）
        /// </summary>
        protected virtual Task<List<double>?> ComputeEmbeddingAsync(string text, CancellationToken ct = default)
            => GetEmbeddingAsync(text);

        /// <summary>
        /// 纯向量检索：对知识库全部已索引笔记做余弦相似度排序（无需关键词命中）
        /// </summary>
        /// <param name="query">查询文本</param>
        /// <param name="vaultId">知识库 ID</param>
        /// <param name="vaultPath">知识库磁盘路径（用于读取笔记标题/预览）</param>
        /// <param name="topK">返回条数</param>
        public async Task<List<SearchResult>> VectorSearchAsync(string query, string vaultId, string vaultPath, int topK = 20)
        {
            var result = new List<SearchResult>();
            if (string.IsNullOrWhiteSpace(query) || !IsSemanticSearchEnabled())
                return result;

            try
            {
                var queryEmbedding = await GetEmbeddingAsync(query);
                if (queryEmbedding == null)
                    return result;

                // 1) 读出该知识库全部笔记向量
                List<(string NotePath, List<double> Vector)> vectors;
                using (var db = await _vaultDbFactory.CreateDbContextAsync())
                {
                    vectors = await db.NoteEmbeddings
                        .Where(e => e.VaultId == vaultId && e.Dimensions > 0)
                        .Select(e => new { e.NotePath, e.VectorJson })
                        .ToListAsync()
                        .ContinueWith(t => t.Result
                            .Select(x => (x.NotePath, DeserializeVector(x.VectorJson)))
                            .Where(x => x.Item2 != null)
                            .Select(x => (x.NotePath, x.Item2!))
                            .ToList());
                }

                if (vectors.Count == 0)
                {
                    _logger.LogInformation("纯向量检索：知识库 {VaultId} 无向量缓存，需先索引", vaultId);
                    return result;
                }

                // 2) 余弦相似度排序
                var scored = new List<(string NotePath, double Score)>();
                foreach (var (notePath, vector) in vectors)
                {
                    var sim = CosineSimilarity(queryEmbedding, vector);
                    if (sim > 0)
                        scored.Add((notePath, sim));
                }

                scored = scored.OrderByDescending(x => x.Score).Take(topK).ToList();

                // 3) 从磁盘读笔记构建结果（标题/预览/相对路径）
                foreach (var (notePath, score) in scored)
                {
                    var fullPath = System.IO.Path.Combine(vaultPath, notePath);
                    if (!System.IO.File.Exists(fullPath))
                        continue;

                    string content;
                    try { content = await System.IO.File.ReadAllTextAsync(fullPath); }
                    catch { continue; }

                    var title = System.IO.Path.GetFileNameWithoutExtension(fullPath);
                    var relativePath = notePath.Replace('\\', '/');
                    result.Add(new SearchResult
                    {
                        Id = title,
                        Title = title,
                        Path = relativePath,
                        Preview = ExtractFirstText(content),
                        Score = (int)Math.Round(score * 10),
                    });
                }

                _logger.LogDebug("纯向量检索完成：{Count} 条（topK={TopK}）", result.Count, topK);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "纯向量检索失败");
            }
            return result;
        }

        private static List<double>? DeserializeVector(string json)
        {
            try { return JsonSerializer.Deserialize<List<double>>(json); }
            catch { return null; }
        }

        /// <summary>提取笔记正文首段文字作为预览（剔除 frontmatter/标题）</summary>
        private static string ExtractFirstText(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return "";
            var lines = content.Split('\n');
            var sb = new System.Text.StringBuilder();
            var inFm = false;
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (i == 0 && line == "---") { inFm = true; continue; }
                if (inFm && line == "---") { inFm = false; continue; }
                if (inFm) continue;
                if (line.StartsWith("#")) continue;          // 跳过标题
                if (string.IsNullOrEmpty(line)) { if (sb.Length > 0) break; continue; }
                sb.Append(line).Append(' ');
                if (sb.Length > 160) break;
            }
            var text = sb.ToString().Trim();
            return text.Length > 0 ? text : content.Replace("\n", " ").Trim();
        }

        /// <summary>
        /// 获取知识库已索引向量条数
        /// </summary>
        public async Task<int> GetIndexedCountAsync(string vaultId)
        {
            try
            {
                using var db = await _vaultDbFactory.CreateDbContextAsync();
                return await db.NoteEmbeddings.CountAsync(e => e.VaultId == vaultId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "查询向量索引数失败");
                return 0;
            }
        }

        /// <summary>
        /// 全量向量索引：对知识库所有 .md 笔记生成向量并缓存（纯向量检索的前提）。
        /// 等价于以 null 快照调用增量重载；同一知识库的索引任务由 per-vault 锁串行化。
        /// </summary>
        public async Task<(int Indexed, int Failed)> IndexVaultAsync(string vaultId, string vaultPath, CancellationToken ct = default)
        {
            var result = await IndexVaultCoreAsync(vaultId, vaultPath, previousSnapshot: null, ct);
            return (result.Indexed, result.Failed);
        }

        /// <summary>
        /// 增量向量索引：以笔记文件 mtime/size 快照对比为准，仅对新增/变更的笔记调用
        /// embedding API 并 upsert，对已删除的笔记删除其向量行，未变更笔记零调用；
        /// previousSnapshot 为 null 或为空（首次索引 / 快照丢失）时退化为整库重建。
        /// 同一知识库的索引任务由 per-vault 锁串行化（不同知识库可并行）。
        /// </summary>
        public async Task<VaultIndexChangeResult> IndexVaultAsync(
            string vaultId,
            string vaultPath,
            IReadOnlyDictionary<string, NoteFileStamp>? previousSnapshot,
            CancellationToken ct = default)
        {
            var result = await IndexVaultCoreAsync(vaultId, vaultPath, previousSnapshot, ct);
            return new VaultIndexChangeResult(
                result.Snapshot, result.IsFullRebuild,
                result.Added, result.Updated, result.Removed, result.Unchanged);
        }

        /// <summary>一次向量索引运行的内部结果</summary>
        private sealed record IndexRunResult(
            Dictionary<string, NoteFileStamp> Snapshot,
            bool IsFullRebuild,
            int Indexed,
            int Failed,
            int Added,
            int Updated,
            int Removed,
            int Unchanged)
        {
            public static IndexRunResult Empty(IReadOnlyDictionary<string, NoteFileStamp>? previousSnapshot)
                => new(
                    previousSnapshot == null
                        ? new Dictionary<string, NoteFileStamp>(StringComparer.OrdinalIgnoreCase)
                        : previousSnapshot.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
                    false, 0, 0, 0, 0, 0, 0);
        }

        /// <summary>
        /// 向量索引主流程：per-vault 锁内串行执行（try/finally 保证释放），
        /// 无快照 → 整库重建，有快照 → 增量 diff
        /// </summary>
        private async Task<IndexRunResult> IndexVaultCoreAsync(
            string vaultId,
            string vaultPath,
            IReadOnlyDictionary<string, NoteFileStamp>? previousSnapshot,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(vaultId) || !Directory.Exists(vaultPath) || !IsSemanticSearchEnabled())
                return IndexRunResult.Empty(previousSnapshot);

            var gate = _vaultIndexLocks.GetOrAdd(vaultId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                if (previousSnapshot == null || previousSnapshot.Count == 0)
                    return await RebuildEmbeddingsAsync(vaultId, vaultPath, ct);

                return await IncrementalEmbeddingsAsync(vaultId, vaultPath, previousSnapshot, ct);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>整库重建：计算全部笔记向量并批量写入，同时清理磁盘上已不存在的旧向量行</summary>
        private async Task<IndexRunResult> RebuildEmbeddingsAsync(string vaultId, string vaultPath, CancellationToken ct)
        {
            var current = VaultNoteIndexer.ScanVaultShared(vaultPath);
            _logger.LogInformation("向量索引全量重建开始：{VaultId} 共 {Count} 篇笔记", vaultId, current.Count);

            int indexed = 0, failed = 0;
            var embeddings = new List<(string Path, List<double> Vector)>();

            foreach (var (relativePath, _) in current)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var embedding = await BuildAndEmbedAsync(vaultPath, relativePath, ct);
                    if (embedding != null)
                    {
                        embeddings.Add((relativePath, embedding));
                        indexed++;
                    }
                    else
                    {
                        failed++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "索引笔记失败：{Path}", relativePath);
                    failed++;
                }
            }

            using var db = await _vaultDbFactory.CreateDbContextAsync(ct);

            // 清理磁盘上已不存在的旧向量行
            var dbPaths = await db.NoteEmbeddings
                .Where(e => e.VaultId == vaultId)
                .Select(e => e.NotePath)
                .ToListAsync(ct);
            var orphanPaths = dbPaths.Where(p => !current.ContainsKey(p)).ToList();
            if (orphanPaths.Count > 0)
            {
                await db.NoteEmbeddings
                    .Where(e => e.VaultId == vaultId && orphanPaths.Contains(e.NotePath))
                    .ExecuteDeleteAsync(ct);
            }

            await UpsertEmbeddingsAsync(db, vaultId, embeddings, ct);

            _logger.LogInformation("向量索引全量重建完成：{VaultId} 成功 {Indexed} 失败 {Failed}", vaultId, indexed, failed);
            return new IndexRunResult(current, true, indexed, failed, indexed, 0, orphanPaths.Count, 0);
        }

        /// <summary>
        /// 增量索引：按快照 diff 只对新增/变更笔记调用 embedding API 并批量 upsert，
        /// 对删除笔记删除其向量行，未变更笔记零调用
        /// </summary>
        private async Task<IndexRunResult> IncrementalEmbeddingsAsync(
            string vaultId,
            string vaultPath,
            IReadOnlyDictionary<string, NoteFileStamp> previousSnapshot,
            CancellationToken ct)
        {
            var current = VaultNoteIndexer.ScanVaultShared(vaultPath);

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
                _logger.LogDebug("知识库 {VaultId} 向量索引无变化，跳过", vaultId);
                return new IndexRunResult(current, false, 0, 0, 0, 0, 0, unchanged);
            }

            _logger.LogInformation("知识库 {VaultId} 向量增量索引：新增 {Added} 更新 {Updated} 删除 {Removed}",
                vaultId, added.Count, updated.Count, removed.Count);

            int indexed = 0, failed = 0;
            var embeddings = new List<(string Path, List<double> Vector)>();

            foreach (var path in added.Concat(updated))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var embedding = await BuildAndEmbedAsync(vaultPath, path, ct);
                    if (embedding != null)
                    {
                        embeddings.Add((path, embedding));
                        indexed++;
                    }
                    else
                    {
                        failed++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "增量索引笔记失败：{Path}", path);
                    failed++;
                }
            }

            using var db = await _vaultDbFactory.CreateDbContextAsync(ct);

            // 删除已移除笔记的向量行
            if (removed.Count > 0)
            {
                await db.NoteEmbeddings
                    .Where(e => e.VaultId == vaultId && removed.Contains(e.NotePath))
                    .ExecuteDeleteAsync(ct);
            }

            // 新增/变更笔记批量 upsert
            await UpsertEmbeddingsAsync(db, vaultId, embeddings, ct);

            return new IndexRunResult(current, false, indexed, failed, added.Count, updated.Count, removed.Count, unchanged);
        }

        /// <summary>读取笔记并计算向量（标题 + 首段正文）</summary>
        private async Task<List<double>?> BuildAndEmbedAsync(string vaultPath, string relativePath, CancellationToken ct)
        {
            var fullPath = Path.Combine(vaultPath, relativePath);
            var content = await File.ReadAllTextAsync(fullPath, ct);
            var title = Path.GetFileNameWithoutExtension(relativePath);
            var textToEmbed = $"{title}\n{ExtractFirstText(content)}".Trim();
            if (string.IsNullOrEmpty(textToEmbed))
                return null;
            return await ComputeEmbeddingAsync(textToEmbed, ct);
        }

        /// <summary>
        /// 批量 upsert 向量：已存在行更新（保留 CreatedAt），新增行 AddRange 后
        /// 按 EmbeddingBatchSize 分批 SaveChanges
        /// </summary>
        private async Task UpsertEmbeddingsAsync(
            VaultDbContext db,
            string vaultId,
            IReadOnlyList<(string Path, List<double> Vector)> embeddings,
            CancellationToken ct)
        {
            if (embeddings.Count == 0)
                return;

            var paths = embeddings.Select(e => e.Path).ToList();
            var existing = await db.NoteEmbeddings
                .Where(e => e.VaultId == vaultId && paths.Contains(e.NotePath))
                .ToDictionaryAsync(e => e.NotePath, StringComparer.OrdinalIgnoreCase, ct);

            var now = DateTime.UtcNow;
            var added = new List<NoteEmbedding>();

            foreach (var (path, vector) in embeddings)
            {
                var json = JsonSerializer.Serialize(vector);
                if (existing.TryGetValue(path, out var row))
                {
                    row.VectorJson = json;
                    row.Dimensions = vector.Count;
                    row.UpdatedAt = now;
                }
                else
                {
                    added.Add(new NoteEmbedding
                    {
                        VaultId = vaultId,
                        NotePath = path,
                        VectorJson = json,
                        Dimensions = vector.Count,
                        CreatedAt = now,
                        UpdatedAt = now,
                    });
                }
            }

            for (int i = 0; i < added.Count; i += EmbeddingBatchSize)
            {
                ct.ThrowIfCancellationRequested();
                db.NoteEmbeddings.AddRange(added.Skip(i).Take(EmbeddingBatchSize));
                await db.SaveChangesAsync(ct);
            }

            // 只有更新无新增时也需要落库
            if (added.Count == 0 && existing.Count > 0)
                await db.SaveChangesAsync(ct);
        }

        public async Task<List<SearchResult>> RerankBySimilarityAsync(
            string query, 
            List<SearchResult> results)
        {
            if (results.Count == 0 || !IsSemanticSearchEnabled())
                return results;

            try
            {
                var queryEmbedding = await GetEmbeddingAsync(query);
                if (queryEmbedding == null)
                    return results;

                var toRerank = results.Count > MaxNotesToRerank
                    ? results.Take(MaxNotesToRerank).ToList()
                    : results;
                
                var rest = results.Count > MaxNotesToRerank
                    ? results.Skip(MaxNotesToRerank).ToList()
                    : new List<SearchResult>();

                var scoredResults = new List<(SearchResult result, double score)>();
                
                foreach (var result in toRerank)
                {
                    var noteEmbedding = await GetNoteEmbeddingAsync(result.Path, result.Title, result.Preview);
                    
                    if (noteEmbedding != null)
                    {
                        var similarity = CosineSimilarity(queryEmbedding, noteEmbedding);
                        scoredResults.Add((result, similarity));
                    }
                    else
                    {
                        scoredResults.Add((result, result.Score));
                    }
                }

                var reranked = scoredResults
                    .OrderByDescending(x => x.score)
                    .Select(x => 
                    {
                        x.result.Score = (int)(x.score * 10);
                        return x.result;
                    })
                    .ToList();

                reranked.AddRange(rest);

                _logger.LogDebug("语义重排完成：{Count} 条结果", reranked.Count);
                return reranked;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "语义重排失败，返回原顺序");
                return results;
            }
        }

        /// <summary>
        /// 获取笔记的向量（从 SQLite 缓存或计算）
        /// </summary>
        private async Task<List<double>?> GetNoteEmbeddingAsync(string path, string title, string preview)
        {
            var vaultId = GetActiveVaultId();
            if (string.IsNullOrEmpty(vaultId))
                return null;

            try
            {
                // 从 SQLite 读取
                using var db = await _vaultDbFactory.CreateDbContextAsync();
                var cached = await db.NoteEmbeddings
                    .FirstOrDefaultAsync(e => e.VaultId == vaultId && e.NotePath == path);

                if (cached != null)
                {
                    var vector = JsonSerializer.Deserialize<List<double>>(cached.VectorJson);
                    if (vector != null && vector.Count > 0)
                        return vector;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "从 SQLite 读取向量缓存失败");
            }

            // 计算新向量
            var textToEmbed = $"{title}\n{preview}".Trim();
            if (string.IsNullOrEmpty(textToEmbed))
                return null;

            var embedding = await GetEmbeddingAsync(textToEmbed);
            if (embedding != null)
            {
                // 缓存写入与索引任务共用 per-vault 闸门，避免与整库索引并发写同一行触发唯一索引冲突。
                // 限时等待：索引任务长时间持锁时跳过缓存写（仅丢一次缓存，不影响本次重排结果）。
                var gate = _vaultIndexLocks.GetOrAdd(vaultId, _ => new SemaphoreSlim(1, 1));
                if (await gate.WaitAsync(TimeSpan.FromSeconds(5)))
                {
                    try
                    {
                        await SaveNoteEmbeddingAsync(vaultId, path, embedding);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
            }

            return embedding;
        }

        /// <summary>
        /// 保存笔记向量到 SQLite
        /// </summary>
        private async Task SaveNoteEmbeddingAsync(string vaultId, string path, List<double> vector)
        {
            try
            {
                using var db = await _vaultDbFactory.CreateDbContextAsync();
                var existing = await db.NoteEmbeddings
                    .FirstOrDefaultAsync(e => e.VaultId == vaultId && e.NotePath == path);

                var json = JsonSerializer.Serialize(vector);

                if (existing != null)
                {
                    existing.VectorJson = json;
                    existing.Dimensions = vector.Count;
                    existing.UpdatedAt = DateTime.UtcNow;
                    db.NoteEmbeddings.Update(existing);
                }
                else
                {
                    db.NoteEmbeddings.Add(new NoteEmbedding
                    {
                        VaultId = vaultId,
                        NotePath = path,
                        VectorJson = json,
                        Dimensions = vector.Count,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });
                }

                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "保存向量缓存到 SQLite 失败（不影响主流程）");
            }
        }

        /// <summary>
        /// 计算余弦相似度
        /// </summary>
        private static double CosineSimilarity(List<double> a, List<double> b)
        {
            if (a.Count != b.Count || a.Count == 0)
                return 0;

            double dotProduct = 0;
            double magnitudeA = 0;
            double magnitudeB = 0;

            for (int i = 0; i < a.Count; i++)
            {
                dotProduct += a[i] * b[i];
                magnitudeA += a[i] * a[i];
                magnitudeB += b[i] * b[i];
            }

            magnitudeA = Math.Sqrt(magnitudeA);
            magnitudeB = Math.Sqrt(magnitudeB);

            if (magnitudeA == 0 || magnitudeB == 0)
                return 0;

            return dotProduct / (magnitudeA * magnitudeB);
        }
    }
}
