using Baihua.Core.Models;
using Baihua.Core.Services;
using Microsoft.EntityFrameworkCore;
using Baihua.Data;

namespace Baihua.Modules.Family.Services;

public class StartupOrchestratorHostedService : IHostedService
{
    private readonly IDbContextFactory<FamilyDbContext> _familyDbContextFactory;
    private readonly IDbContextFactory<VaultDbContext> _vaultDbContextFactory;
    private readonly VaultSettingsService _vaultSettings;
    private readonly ILogger<StartupOrchestratorHostedService> _logger;

    public StartupOrchestratorHostedService(
        IDbContextFactory<FamilyDbContext> familyDbContextFactory,
        IDbContextFactory<VaultDbContext> vaultDbContextFactory,
        VaultSettingsService vaultSettings,
        ILogger<StartupOrchestratorHostedService> logger)
    {
        _familyDbContextFactory = familyDbContextFactory;
        _vaultDbContextFactory = vaultDbContextFactory;
        _vaultSettings = vaultSettings;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 每一步单独兜底：某一步失败（存量脏数据）不能连累后续步骤一起跳过。
        RunStep("服务器身份", () =>
        {
            using var familyDb = _familyDbContextFactory.CreateDbContext();
            EnsureSingleServerIdentity(familyDb);
        });

        RunStep("知识库去重与迁移", () =>
        {
            using var vaultDb = _vaultDbContextFactory.CreateDbContext();
            EnsureVaultsUniqueAndMigrateLegacy(vaultDb);
        });

        RunStep("知识库扫描", TrySyncVaultsOnStartup);

        return Task.CompletedTask;
    }

    private void RunStep(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动编排「{Step}」失败，已记录错误并继续启动", step);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Keep a single persisted server identity and enforce uniqueness for non-empty instance IDs.
    /// </summary>
    private void EnsureSingleServerIdentity(FamilyDbContext db)
    {
        var duplicates = db.ServerAddressSettings
            .OrderBy(setting => setting.Id)
            .Skip(1)
            .ToList();
        if (duplicates.Count > 0)
        {
            db.ServerAddressSettings.RemoveRange(duplicates);
            db.SaveChanges();
        }

        db.Database.ExecuteSqlRaw("""
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_ServerAddressSettings_ServerInstanceId"
            ON "ServerAddressSettings" ("ServerInstanceId")
            WHERE "ServerInstanceId" IS NOT NULL AND "ServerInstanceId" <> '';
            """);
    }

    /// <summary>
    /// 知识库去重 + 历史遗留目录迁移 + 加条件唯一索引。
    ///
    /// 背景：`SyncVaultsWithFilesystem` 是 check-then-insert，无唯一约束；
    /// 并发的两次扫描会重复登记同一路径，界面上同一条知识库出现两张卡片。
    /// 另：旧版移动端上传目录 `mobile-uploads/{id}` 会被扫描器当成本地知识库，
    /// 名字取目录名（一串 id），需要迁移到当前的 &lt;root&gt;/mobile/&lt;名称&gt; 约定。
    ///
    /// 幂等：重复执行无副作用。删除重复行前先把引用它的子表指到保留行，避免丢数据。
    /// </summary>
    private void EnsureVaultsUniqueAndMigrateLegacy(VaultDbContext db)
    {
        // 1) 迁移 id 命名 / 旧 mobile-uploads 结构的知识库（改名 + 移动目录）
        MigrateLegacyVaultNames(db);

        // 2) 去重：每个 Path 保留 Id 最小的一条，其余子表改指后删除
        try
        {
            using var transaction = db.Database.BeginTransaction();
            var duplicateGroups = db.Vaults
                .Where(vault => !vault.IsDeleted)
                .OrderBy(vault => vault.Id)
                .ToList()
                .GroupBy(vault => vault.Path, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .ToList();

            var changed = false;
            foreach (var group in duplicateGroups)
            {
                var keep = group.First();
                var duplicates = group.Skip(1).ToList();
                foreach (var duplicate in duplicates)
                {
                    // 子表存的是 VaultId（字符串 GUID），不是 Vaults 的自增 Id
                    ReassignVaultReferences(db, keep.VaultId, duplicate.VaultId);
                }

                db.Vaults.RemoveRange(duplicates);
                changed = true;
            }
            if (changed) db.SaveChanges();
            transaction.Commit();
        }
        catch (Exception ex)
        {
            // 去重失败（例如子表有唯一约束冲突）时回滚，保留原始数据，启动继续；
            // 后面的唯一索引会因重复数据建不上并记警告，业务照常可用。
            _logger.LogWarning(ex, "知识库去重失败，已回滚；请在界面手工合并重复的知识库");
        }

        // 3) 条件唯一索引：未删除的知识库，路径与名称都唯一（回收站不受限）。
        // 单独 try：索引建不上（存量脏数据）不应连累启动，只需记警告。
        // 数据侧由 SyncVaultsWithFilesystem 串行锁 + 扫描跳过遗留目录保证不再产生重复。
        try
        {
            db.Database.ExecuteSqlRaw("""
                CREATE UNIQUE INDEX IF NOT EXISTS "UX_Vaults_Path_Active"
                ON "Vaults" ("Path") WHERE "IsDeleted" = 0;
                CREATE UNIQUE INDEX IF NOT EXISTS "UX_Vaults_Name_Active"
                ON "Vaults" ("Name") WHERE "IsDeleted" = 0;
                """);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vaults 唯一索引创建失败（存量重复数据），已跳过");
        }
    }

    private static void ReassignVaultReferences(VaultDbContext db, string keepVaultId, string duplicateVaultId)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose) connection.Open();

        try
        {
            using var listTables = connection.CreateCommand();
            listTables.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name <> 'Vaults'";
            using var tables = listTables.ExecuteReader();
            var tableNames = new List<string>();
            while (tables.Read()) tableNames.Add(tables.GetString(0));
            tables.Close();

            foreach (var tableName in tableNames)
            {
                using var columnsCommand = connection.CreateCommand();
                columnsCommand.CommandText = $"PRAGMA table_info(\"{tableName.Replace("\"", "\"\"")}\")";
                using var columns = columnsCommand.ExecuteReader();
                var hasVaultId = false;
                while (columns.Read())
                {
                    if (string.Equals(columns.GetString(1), "VaultId", StringComparison.OrdinalIgnoreCase))
                    {
                        hasVaultId = true;
                        break;
                    }
                }

                if (!hasVaultId) continue;

                using var update = connection.CreateCommand();
                var safeTableName = tableName.Replace("\"", "\"\"");
                update.CommandText = $"UPDATE \"{safeTableName}\" SET \"VaultId\" = @keepId WHERE \"VaultId\" = @duplicateId";
                var keepParameter = update.CreateParameter();
                keepParameter.ParameterName = "@keepId";
                keepParameter.Value = keepVaultId;
                update.Parameters.Add(keepParameter);
                var duplicateParameter = update.CreateParameter();
                duplicateParameter.ParameterName = "@duplicateId";
                duplicateParameter.Value = duplicateVaultId;
                update.Parameters.Add(duplicateParameter);
                update.ExecuteNonQuery();
            }
        }
        finally
        {
            if (shouldClose) connection.Close();
        }
    }

    /// <summary>
    /// 把「名字是 32 位 id」的历史知识库改成可读名称，并迁移到当前移动端目录约定。
    /// 旧版移动端上传落在 <c>&lt;root&gt;/mobile-uploads/&lt;id&gt;</c>，目录名即 vault id。
    ///
    /// 关键点：同一个 Path 往往有重复行，必须整组一起处理，否则只改了其中一行、
    /// 分组被拆开，后续按 Path 去重就失效了（会出现一行新路径 + 一行旧路径）。
    /// 只更新 Id 最小的那一行为目标名/新路径，其余重复行由去重步骤删除，
    /// 这样也不会触碰「未删除名称唯一」索引。
    /// </summary>
    private void MigrateLegacyVaultNames(VaultDbContext db)
    {
        var legacy = db.Vaults
            .Where(v => !v.IsDeleted)
            .ToList()
            .Where(v => System.Text.RegularExpressions.Regex.IsMatch(v.Name ?? "", "^[0-9a-fA-F]{32}$"))
            .ToList();
        if (legacy.Count == 0) return;

        var root = Baihua.Contracts.BaihuaPaths.Vaults;
        var changed = false;

        foreach (var group in legacy.GroupBy(v => v.Path))
        {
            var canonical = group.OrderBy(v => v.Id).First();
            var parentName = Path.GetFileName(Path.GetDirectoryName(canonical.Path) ?? "") ?? "";
            var isLegacyUpload = parentName.Equals("mobile-uploads", StringComparison.OrdinalIgnoreCase);
            var baseName = isLegacyUpload ? "移动上传" : "移动端知识库";

            // 目标名需在未删除集合内唯一（排除本组自身的重复行）
            var groupIds = group.Select(v => v.Id).ToHashSet();
            var name = baseName;
            var suffix = 2;
            while (db.Vaults.Any(v => !v.IsDeleted && !groupIds.Contains(v.Id) && v.Name == name))
            {
                name = $"{baseName}{suffix++}";
            }

            var newPath = canonical.Path;
            if (isLegacyUpload)
            {
                // 简化显示：按备注取一个可读名字（取自「某症的常见病因」这类标题）
                var note = Directory.Exists(Path.Combine(canonical.Path, "notes"))
                    ? Directory.EnumerateFiles(Path.Combine(canonical.Path, "notes"), "*.md", SearchOption.AllDirectories).FirstOrDefault()
                    : null;
                if (note != null)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(
                        Path.GetFileNameWithoutExtension(note), @"^(.+?)的(常见病因|辨证分型|病因病机|治则治法|诊断方法|养生调护|外治法|方剂与药物|基础概念|关联与区别|预防调护)");
                    if (m.Success && m.Groups[1].Value.Length is > 0 and <= 12)
                    {
                        name = m.Groups[1].Value;
                    }
                }

                var targetDir = Path.Combine(root, "mobile", name.Length > 80 ? name[..80] : name);
                try
                {
                    if (Directory.Exists(canonical.Path) && !Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
                        Directory.Move(canonical.Path, targetDir);
                        newPath = targetDir;
                    }
                    else if (Directory.Exists(targetDir))
                    {
                        newPath = targetDir;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "迁移历史移动端知识库目录失败，仅改数据库名称: {Path}", canonical.Path);
                }
            }

            _logger.LogInformation("迁移历史知识库: \"{OldName}\" -> \"{NewName}\" ({OldPath} -> {NewPath})",
                canonical.Name, name, canonical.Path, newPath);

            canonical.Name = name;
            canonical.Path = newPath;
            if (isLegacyUpload)
            {
                canonical.Source = "mobile";
                canonical.Industry = "mobile";
                canonical.IsActive = true;
            }
            canonical.UpdatedAt = DateTime.UtcNow;
            changed = true;
        }

        if (changed) db.SaveChanges();
    }

    private void TrySyncVaultsOnStartup()
    {
        var rootPath = _vaultSettings.VaultRootPathPreference;
        if (string.IsNullOrWhiteSpace(rootPath)) return;
        if (!Directory.Exists(rootPath)) return;

        try
        {
            var (added, removed) = _vaultSettings.SyncVaultsWithFilesystem(rootPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动时自动同步知识库失败");
        }
    }
}
