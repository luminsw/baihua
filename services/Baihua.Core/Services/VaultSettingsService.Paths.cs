using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Contracts.Vaults;

namespace Baihua.Core.Services;


public partial class VaultSettingsService
{
    public string VaultPath
    {
        get
        {
            var active = GetActiveVault();
            if (active != null && !string.IsNullOrEmpty(active.Path))
                return active.Path;
            return Baihua.Contracts.BaihuaPaths.Vaults;
        }
    }

    public string NotesPath
    {
        get
        {
            var active = GetActiveVault();
            if (active != null && !string.IsNullOrEmpty(active.Path))
                return Path.Combine(active.Path, "notes");
            return "";
        }
    }

    public string CardsPath
    {
        get
        {
            var active = GetActiveVault();
            if (active != null && !string.IsNullOrEmpty(active.Path))
                return Path.Combine(active.Path, "cards");
            return "";
        }
    }

    /// <summary>
    /// 扫描串行锁：启动编排与 /vault-settings/vaults/sync 会并发进入本方法，
    /// 而「查 dbVaults 快照 → 逐个 AddVault」是无锁的 check-then-insert，
    /// 并发时会重复登记同一路径（界面出现重复卡片）。数据库侧另有条件唯一索引兜底。
    /// 静态锁保证同一进程内的扫描串行（服务为单例）。
    /// </summary>
    private static readonly object SyncScanLock = new();

    public (int added, int removed) SyncVaultsWithFilesystem(string rootPath)
    {
        lock (SyncScanLock)
        {
            return SyncVaultsWithFilesystemCore(rootPath);
        }
    }

    private (int added, int removed) SyncVaultsWithFilesystemCore(string rootPath)
    {
        int added = 0, removed = 0;

        if (!Directory.Exists(rootPath))
            return (added, removed);

        var dbVaults = GetVaults().ToDictionary(v => v.Path, v => v);
        var fsVaults = new HashSet<string>();

        // 递归查找所有包含 notes 或 cards 子目录的知识库
        void ScanDirectory(string currentDir)
        {
            foreach (var dir in Directory.EnumerateDirectories(currentDir))
            {
                var dirName = Path.GetFileName(dir);
                // 跳过遗留/内部目录：
                //  - mobile-uploads：旧版移动端上传目录（目录名即 vault id），
                //    当前约定是 <root>/mobile/<名称>，登记它只会得到一串 id 当名字；
                //  - 32 位 id 命名：内部 vault id，不是用户知识库名。
                if (string.Equals(dirName, "mobile-uploads", StringComparison.OrdinalIgnoreCase)) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(dirName, "^[0-9a-fA-F]{32}$")) continue;

                var notesDir = Path.Combine(dir, "notes");
                var cardsDir = Path.Combine(dir, "cards");
                if (Directory.Exists(notesDir) || Directory.Exists(cardsDir))
                {
                    fsVaults.Add(dir);
                    if (!dbVaults.ContainsKey(dir))
                    {
                        var name = Path.GetFileName(dir);
                        // 从父目录名推断行业：.../vaults/local/{industry}/{name}
                        var industry = InferIndustryFromPath(dir, rootPath);
                        try
                        {
                            AddVault(name, dir, industry);
                            added++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "同步知识库时跳过重复: {Path}", dir);
                        }
                    }
                    else
                    {
                        // 已有知识库：校正行业（如果路径暗示的行业与数据库不符）
                        var expectedIndustry = InferIndustryFromPath(dir, rootPath);
                        var existing = dbVaults[dir];
                        if (!string.Equals(existing.Industry, expectedIndustry, StringComparison.Ordinal))
                        {
                            UpdateVaultIndustryByPath(dir, expectedIndustry);
                        }
                    }
                }
                else
                {
                    // 继续递归检查子目录
                    ScanDirectory(dir);
                }
            }
        }

        ScanDirectory(rootPath);

        foreach (var dbVault in dbVaults.Values)
        {
            if (!fsVaults.Contains(dbVault.Path) && !dbVault.Path.Contains("builtin"))
            {
                RemoveVault(dbVault.Id);
                removed++;
            }
        }

        return (added, removed);
    }

    private static string InferIndustryFromPath(string vaultDir, string rootPath)
    {
        // 物理结构：{rootPath}/local/{industry}/{vaultName}
        // 父目录名即为行业名
        var parentDir = Directory.GetParent(vaultDir);
        if (parentDir == null) return "其他";

        var parentName = parentDir.Name;
        // 如果父目录是 "local" 或等于根目录名，说明不在标准结构中，回退到 "其他"
        if (string.IsNullOrWhiteSpace(parentName) ||
            string.Equals(parentName, "local", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(parentName, Path.GetFileName(rootPath), StringComparison.OrdinalIgnoreCase))
            return "其他";

        return parentName;
    }

    private void UpdateVaultIndustryByPath(string vaultPath, string newIndustry)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        lock (_vaultPathLock)
        {
            var vault = dbContext.Vaults.FirstOrDefault(v => v.Path == vaultPath && !v.IsDeleted);
            if (vault == null) return;

            if (string.Equals(vault.Industry, newIndustry, StringComparison.Ordinal))
                return;

            _logger.LogInformation("校正知识库行业: {Path} \"{Old}\" → \"{New}\"",
                vaultPath, vault.Industry, newIndustry);
            vault.Industry = newIndustry;
            dbContext.SaveChanges();
        }
    }

    private static List<string> ParseTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags)) return new List<string>();
        return tags.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
    }
}
