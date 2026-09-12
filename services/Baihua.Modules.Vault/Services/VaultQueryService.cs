using System.Diagnostics;
using Baihua.Contracts.Search;
using Baihua.Contracts.Vaults;
using Baihua.Core.Localization;
using Baihua.Core.Modules;
using Baihua.Core.Services;
using Microsoft.Extensions.Localization;

namespace Baihua.Modules.Vault.Services;

/// <summary>
/// 知识库查询服务：检索与笔记读写的唯一实现（控制器与 MCP 工具共用，避免两份逻辑）。
/// 检索顺序：语义向量 → FTS5 → obsidian-cli → 直接扫描文件。
/// </summary>
public sealed class VaultQueryService : IVaultQueryService
{
    private readonly VaultSettingsService _vaultSettings;
    private readonly EmbeddingService _embeddingService;
    private readonly VaultNoteIndexer _vaultNoteIndexer;
    private readonly ILogger<VaultQueryService> _logger;
    private readonly IStringLocalizer<SharedResources> _loc;

    public VaultQueryService(
        VaultSettingsService vaultSettings,
        EmbeddingService embeddingService,
        VaultNoteIndexer vaultNoteIndexer,
        ILogger<VaultQueryService> logger,
        IStringLocalizer<SharedResources> loc)
    {
        _vaultSettings = vaultSettings;
        _embeddingService = embeddingService;
        _vaultNoteIndexer = vaultNoteIndexer;
        _logger = logger;
        _loc = loc;
    }

    /// <inheritdoc />
    public string? ResolveVaultPath(string? vaultId)
    {
        if (string.IsNullOrEmpty(vaultId))
        {
            return null;
        }

        var target = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == vaultId);
        if (target != null && !string.IsNullOrEmpty(target.Path))
        {
            return target.Path;
        }

        _logger.LogWarning("指定的知识库不存在或路径为空：{VaultId}", vaultId);
        return null;
    }

    /// <inheritdoc />
    public async Task<VaultSearchOutcome> SearchAsync(string query, string vaultId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(vaultId))
        {
            return new VaultSearchOutcome([], new SearchStatusInfo
            {
                VaultConfigured = false,
                SearchMethod = "none",
                ErrorMessage = _loc["Vault_Required"].Value
            });
        }

        var vaultPath = ResolveVaultPath(vaultId);
        if (string.IsNullOrEmpty(vaultPath) || !Directory.Exists(vaultPath))
        {
            _logger.LogWarning("知识库路径无效：VaultId={VaultId}, Path={Path}", vaultId, vaultPath);
            return new VaultSearchOutcome([], new SearchStatusInfo
            {
                VaultConfigured = !string.IsNullOrEmpty(vaultPath),
                VaultExists = !string.IsNullOrEmpty(vaultPath) && Directory.Exists(vaultPath),
                SearchMethod = "none",
                ErrorMessage = string.IsNullOrEmpty(vaultPath)
                    ? _loc["Vault_NotFound"].Value
                    : _loc["Search_VaultPathNotExists", vaultPath].Value
            });
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return new VaultSearchOutcome([], new SearchStatusInfo { VaultConfigured = true, VaultExists = true });
        }

        _logger.LogInformation("搜索知识库：{Query}", query);

        var canUseCli = ObsidianExecutableResolver.TryGetPath(out var obsidianExe);
        var obsidianRunning = Process.GetProcessesByName("Obsidian").Length > 0;
        var searchMethod = "file-scan";
        string? errorMessage = null;

        if (canUseCli && obsidianRunning)
        {
            var vaultName = Path.GetFileName(vaultPath.TrimEnd('/'));
            var cliResults = await SearchWithObsidianCli(obsidianExe, vaultName, query);

            if (cliResults != null && cliResults.Count > 0)
            {
                _logger.LogInformation("obsidian-cli 搜索成功：找到 {Count} 条结果", cliResults.Count);
                return new VaultSearchOutcome(cliResults, new SearchStatusInfo
                {
                    VaultConfigured = true,
                    VaultExists = true,
                    ObsidianRunning = true,
                    SearchMethod = "obsidian-cli"
                });
            }

            if (cliResults == null)
            {
                _logger.LogDebug("obsidian-cli 搜索失败或超时，回退到文件扫描");
            }
        }
        else if (canUseCli && !obsidianRunning)
        {
            _logger.LogDebug("Obsidian 未运行，使用文件扫描");
        }
        else
        {
            _logger.LogDebug("obsidian-cli 不可用，使用文件扫描");
        }

        // 纯向量检索优先：语义搜索启用时，即使无关键词命中也能按语义召回
        if (_embeddingService.IsSemanticSearchEnabled())
        {
            var vectorResults = await _embeddingService.VectorSearchAsync(query, vaultId, vaultPath, topK: 20);
            if (vectorResults.Count > 0)
            {
                _logger.LogInformation("纯向量检索：找到 {Count} 条结果", vectorResults.Count);
                return new VaultSearchOutcome(vectorResults, new SearchStatusInfo
                {
                    VaultConfigured = true,
                    VaultExists = true,
                    ObsidianRunning = obsidianRunning,
                    SearchMethod = "semantic"
                });
            }
            _logger.LogInformation("纯向量检索无结果（可能未索引），回退 FTS");
        }

        // 尝试 FTS5 全文搜索
        var ftsResults = await _vaultNoteIndexer.SearchAsync(vaultId, query, cancellationToken);
        if (ftsResults.Count > 0)
        {
            _logger.LogInformation("FTS5 搜索成功：找到 {Count} 条结果", ftsResults.Count);
            searchMethod = "fts5";

            if (_embeddingService.IsSemanticSearchEnabled())
            {
                var reranked = await _embeddingService.RerankBySimilarityAsync(query, ftsResults);
                return new VaultSearchOutcome(reranked, new SearchStatusInfo
                {
                    VaultConfigured = true,
                    VaultExists = true,
                    ObsidianRunning = obsidianRunning,
                    SearchMethod = "fts5+semantic"
                });
            }

            return new VaultSearchOutcome(ftsResults, new SearchStatusInfo
            {
                VaultConfigured = true,
                VaultExists = true,
                ObsidianRunning = obsidianRunning,
                SearchMethod = searchMethod
            });
        }

        // 回退到直接扫描文件
        var fileResults = await SearchByScanningFiles(vaultPath, query);
        _logger.LogInformation("文件扫描完成：找到 {Count} 条结果", fileResults.Count);

        if (_embeddingService.IsSemanticSearchEnabled() && fileResults.Count > 0)
        {
            var reranked = await _embeddingService.RerankBySimilarityAsync(query, fileResults);
            return new VaultSearchOutcome(reranked, new SearchStatusInfo
            {
                VaultConfigured = true,
                VaultExists = true,
                ObsidianRunning = obsidianRunning,
                SearchMethod = "semantic"
            });
        }

        if (fileResults.Count == 0)
        {
            errorMessage = _loc["Vault_NoMatchFound"].Value;
        }

        return new VaultSearchOutcome(fileResults, new SearchStatusInfo
        {
            VaultConfigured = true,
            VaultExists = true,
            ObsidianRunning = obsidianRunning,
            SearchMethod = searchMethod,
            ErrorMessage = errorMessage
        });
    }

    /// <inheritdoc />
    public async Task<VaultNoteResponse> ReadNoteAsync(string path, string vaultId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new VaultQueryException(VaultErrorCode.PathRequired, _loc["Vault_PathRequired"].Value);
        }

        var baseVaultPath = ResolveVaultPath(vaultId)
            ?? throw new VaultQueryException(VaultErrorCode.VaultRequired, _loc["Vault_Required"].Value);

        try
        {
            path = path.TrimEnd('/', '\\');
            if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^3];
            }

            var notesPath = Path.Combine(baseVaultPath, "notes");
            var filePath = Path.Combine(notesPath, path + ".md");

            if (!File.Exists(filePath))
            {
                throw new VaultQueryException(VaultErrorCode.NoteNotFound, _loc["Vault_NoteNotFound", path].Value);
            }

            var content = await File.ReadAllTextAsync(filePath, cancellationToken);
            var frontmatter = VaultNoteFrontmatter.Parse(content);

            return new VaultNoteResponse
            {
                Path = path,
                Title = Path.GetFileNameWithoutExtension(path),
                Content = content,
                Modified = File.GetLastWriteTime(filePath),
                Tags = frontmatter.Tags,
                AiGenerated = frontmatter.AiGenerated,
                AiProvider = frontmatter.AiProvider,
                AiModel = frontmatter.AiModel,
                GeneratedAt = frontmatter.GeneratedAt
            };
        }
        catch (VaultQueryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取笔记失败：{Path}", path);
            throw new VaultQueryException(VaultErrorCode.Failure, _loc["Common_ReadFailed"].Value);
        }
    }

    /// <inheritdoc />
    public async Task WriteNoteAsync(string path, string vaultId, string content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new VaultQueryException(VaultErrorCode.PathRequired, _loc["Vault_PathRequired"].Value);
        }

        if (content == null)
        {
            throw new VaultQueryException(VaultErrorCode.ContentRequired, _loc["Vault_ContentRequired"].Value);
        }

        var baseVaultPath = ResolveVaultPath(vaultId)
            ?? throw new VaultQueryException(VaultErrorCode.VaultRequired, _loc["Vault_Required"].Value);

        try
        {
            path = path.TrimEnd('/', '\\');
            if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^3];
            }

            // 路径安全检查：阻止目录遍历
            path = path.Replace("\\", "/");
            if (path.Contains(".."))
            {
                _logger.LogWarning("写入操作检测到目录遍历尝试: {Path}", path);
                throw new VaultQueryException(VaultErrorCode.IllegalPath, _loc["Vault_IllegalPath"].Value);
            }

            var notesRoot = Path.Combine(baseVaultPath, "notes");
            var filePath = Path.GetFullPath(Path.Combine(notesRoot, path + ".md"));
            var baseFullPath = Path.GetFullPath(baseVaultPath);

            if (!filePath.StartsWith(baseFullPath, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("写入路径遍历被阻止: {FilePath} 不在 {BasePath} 内", filePath, baseFullPath);
                throw new VaultQueryException(VaultErrorCode.IllegalPath, _loc["Vault_IllegalPath"].Value);
            }

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(filePath, content, cancellationToken);
        }
        catch (VaultQueryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入笔记失败：{Path}", path);
            throw new VaultQueryException(VaultErrorCode.Failure, _loc["Common_WriteFailed"].Value);
        }
    }

    private async Task<List<SearchResult>?> SearchWithObsidianCli(string obsidianExe, string vaultName, string query)
    {
        try
        {
            var escapedQuery = query.Replace("\"", "\\\"");
            var searchProcess = Process.Start(new ProcessStartInfo
            {
                FileName = obsidianExe,
                Arguments = $"vault=\"{vaultName}\" search query=\"{escapedQuery}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // 关键：obsidian-cli 输出编码在 Windows 上经常不是 UTF-8
                // 退回到系统默认编码，避免中文路径乱码。
                StandardOutputEncoding = System.Text.Encoding.Default,
                StandardErrorEncoding = System.Text.Encoding.Default
            });

            if (searchProcess == null) return null;

            // 15 秒超时
            var timeoutTask = Task.Delay(15000);
            var outputTask = searchProcess.StandardOutput.ReadToEndAsync();

            var completed = await Task.WhenAny(outputTask, timeoutTask);
            if (completed == timeoutTask)
            {
                _logger.LogWarning("obsidian-cli 搜索超时");
                searchProcess.Kill();
                return null;
            }

            var output = await outputTask;
            await searchProcess.WaitForExitAsync();

            if (searchProcess.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return null;

            var results = new List<SearchResult>();
            foreach (var line in output.Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l)))
            {
                if (line.StartsWith("path") || line.StartsWith("-") || line.StartsWith("id"))
                    continue;

                var path = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrEmpty(path) && path.EndsWith(".md"))
                {
                    var relativePath = VaultNotePath.NormalizeRelative(path, removeNotesPrefix: true);
                    var title = System.IO.Path.GetFileNameWithoutExtension(path);

                    results.Add(new SearchResult
                    {
                        Id = title,
                        Title = title,
                        Path = relativePath,
                        Preview = $"[obsidian-cli] {path}",
                        Score = 10
                    });
                }
            }

            return results.Count > 0 ? results : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "obsidian-cli 搜索失败");
            return null;
        }
    }

    /// <summary>直接扫描文件搜索（无索引时的兜底）</summary>
    private async Task<List<SearchResult>> SearchByScanningFiles(string vaultPath, string query)
    {
        var results = new List<SearchResult>();
        var queryLower = query.ToLower();

        var files = Directory.GetFiles(vaultPath, "*.md", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            try
            {
                var fileName = System.IO.Path.GetFileName(file);
                if (fileName.Equals("README.md", StringComparison.OrdinalIgnoreCase))
                    continue;

                var content = await File.ReadAllTextAsync(file);
                var title = System.IO.Path.GetFileNameWithoutExtension(file);
                var relativePath = VaultNotePath.NormalizeRelative(
                    file.Substring(vaultPath.Length),
                    removeNotesPrefix: true
                );

                var titleMatch = title.ToLower().Contains(queryLower);
                var contentMatch = content.ToLower().Contains(queryLower);

                if (titleMatch || contentMatch)
                {
                    results.Add(new SearchResult
                    {
                        Id = title,
                        Title = title,
                        Path = relativePath,
                        Preview = ExtractPreview(content, queryLower),
                        Score = titleMatch ? 10 : 5
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "读取文件失败：{File}", file);
            }
        }

        return results.OrderByDescending(r => r.Score).Take(50).ToList();
    }

    private static string ExtractPreview(string content, string query)
    {
        var index = content.ToLower().IndexOf(query);
        if (index < 0) index = 0;

        var start = Math.Max(0, index - 50);
        var length = Math.Min(200, content.Length - start);
        var preview = content.Substring(start, length);

        preview = preview.Replace("\n", " ").Replace("#", "").Replace("*", "");

        if (start > 0) preview = "..." + preview;
        if (start + length < content.Length) preview = preview + "...";

        return preview.Trim();
    }
}
