using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Localization;
using Microsoft.EntityFrameworkCore;
using Baihua.Core.Localization;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Contracts.Vaults;

namespace Baihua.Core.Services;

/// <summary>
/// 知识库配置服务 - 从 SettingsService 中提取，专注管理 Vault 配置
/// </summary>
public partial class VaultSettingsService
{
    private readonly IDbContextFactory<VaultDbContext> _dbContextFactory;
    private readonly ILogger<VaultSettingsService> _logger;
    private readonly IStringLocalizer<SharedResources> _loc;
    private readonly object _vaultPathLock = new();

    public VaultSettingsService(
        IDbContextFactory<VaultDbContext> dbContextFactory,
        ILogger<VaultSettingsService> logger,
        IStringLocalizer<SharedResources> loc)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _loc = loc;
    }

    // 兼容旧构造函数（历史测试/调用可能只传入两个参数）
    public VaultSettingsService(
        IDbContextFactory<VaultDbContext> dbContextFactory,
        ILogger<VaultSettingsService> logger)
        : this(dbContextFactory, logger, new SimpleLocalizer())
    {
    }

    private sealed class SimpleLocalizer : IStringLocalizer<SharedResources>
    {
        public LocalizedString this[string name] => new LocalizedString(name, name, resourceNotFound: true);
        public LocalizedString this[string name, params object[] arguments] => new LocalizedString(name, string.Format(name, arguments), resourceNotFound: true);
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Array.Empty<LocalizedString>();
        public IStringLocalizer WithCulture(System.Globalization.CultureInfo culture) => this;
    }

    public string VaultRootPathPreference
    {
        get
        {
            var dir = Baihua.Contracts.BaihuaPaths.Vaults;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public IReadOnlyList<VaultConfig> GetVaults()
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        lock (_vaultPathLock)
        {
            return dbContext.Vaults
                .Where(v => !v.IsDeleted)
                .OrderBy(v => v.CreatedAt)
                .Select(v => new VaultConfig
                {
                    Id = v.VaultId,
                    Name = v.Name,
                    Path = v.Path,
                    CreatedAt = v.CreatedAt,
                    Tags = ParseTags(v.Tags),
                    Industry = v.Industry,
                    Source = v.Source,
                    PushedByDeviceId = v.PushedByDeviceId,
                    PushedByDeviceName = v.PushedByDeviceName,
                    PushedAt = v.PushedAt
                })
                .ToList();
        }
    }

    public VaultConfig? GetActiveVault()
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        lock (_vaultPathLock)
        {
            var vault = dbContext.Vaults
                .Where(v => !v.IsDeleted)
                .OrderBy(v => v.CreatedAt)
                .FirstOrDefault();

            if (vault == null) return null;
            return new VaultConfig
            {
                Id = vault.VaultId,
                Name = vault.Name,
                Path = vault.Path,
                CreatedAt = vault.CreatedAt,
                Tags = ParseTags(vault.Tags)
            };
        }
    }

    public IReadOnlyList<VaultConfig> GetTrashVaults()
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        lock (_vaultPathLock)
        {
            return dbContext.Vaults
                .Where(v => v.IsDeleted)
                .OrderByDescending(v => v.DeletedAt)
                .Select(v => new VaultConfig
                {
                    Id = v.VaultId,
                    Name = v.Name,
                    Path = v.Path,
                    CreatedAt = v.CreatedAt,
                    Tags = ParseTags(v.Tags),
                    Industry = v.Industry
                })
                .ToList();
        }
    }

    public VaultConfig AddVault(string name, string path, string industry)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        lock (_vaultPathLock)
        {
            var vaultId = Guid.NewGuid().ToString("N");
            var trimmedName = name.Trim();
            var trimmedIndustry = industry.Trim();

            var normalizedPath = path.Trim();
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                var vaultRoot = VaultRootPathPreference;
                if (string.IsNullOrWhiteSpace(vaultRoot))
                {
                    vaultRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "vaults");
                }
                normalizedPath = Path.Combine(vaultRoot, "local", trimmedIndustry, trimmedName);
            }

            var existingByName = dbContext.Vaults
                .FirstOrDefault(v => v.Name == trimmedName && !v.IsDeleted);
            if (existingByName != null)
            {
                throw new InvalidOperationException(_loc["Vault_NameExists", trimmedName]);
            }

            var existingByPath = dbContext.Vaults
                .FirstOrDefault(v => v.Path == normalizedPath && !v.IsDeleted);
            if (existingByPath != null)
            {
                throw new InvalidOperationException(_loc["Vault_PathOccupied", normalizedPath, existingByPath.Name]);
            }

            var vault = new Vault
            {
                VaultId = vaultId,
                Name = trimmedName,
                Path = normalizedPath,
                IsActive = false,
                Industry = trimmedIndustry
            };

            dbContext.Vaults.Add(vault);
            dbContext.SaveChanges();

            _logger.LogInformation("新建知识库: {Name} ({Path})", trimmedName, normalizedPath);

            return new VaultConfig
            {
                Id = vaultId,
                Name = trimmedName,
                Path = normalizedPath,
                Industry = trimmedIndustry
            };
        }
    }
}
