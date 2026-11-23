using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Contracts.Vaults;

namespace Baihua.Core.Services;


public partial class VaultSettingsService
{
    public bool ActivateVault(string vaultId)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        lock (_vaultPathLock)
        {
            var vault = dbContext.Vaults.FirstOrDefault(v => v.VaultId == vaultId);
            if (vault == null) return false;

            vault.IsActive = true;
            dbContext.SaveChanges();
            return true;
        }
    }

    public bool RemoveVault(string vaultId)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        lock (_vaultPathLock)
        {
            var vault = dbContext.Vaults.FirstOrDefault(v => v.VaultId == vaultId);
            if (vault == null || vault.IsDeleted) return false;

            vault.IsDeleted = true;
            vault.DeletedAt = DateTime.UtcNow;
            dbContext.SaveChanges();
            return true;
        }
    }

    public bool RestoreVault(string vaultId)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        lock (_vaultPathLock)
        {
            var vault = dbContext.Vaults.FirstOrDefault(v => v.VaultId == vaultId && v.IsDeleted);
            if (vault == null) return false;

            vault.IsDeleted = false;
            vault.DeletedAt = null;
            dbContext.SaveChanges();
            return true;
        }
    }

    public bool EmptyTrash()
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        lock (_vaultPathLock)
        {
            var trashVaults = dbContext.Vaults.Where(v => v.IsDeleted).ToList();
            if (trashVaults.Count == 0) return false;

            foreach (var vault in trashVaults)
            {
                dbContext.Vaults.Remove(vault);
            }
            dbContext.SaveChanges();
            return true;
        }
    }

    public bool UpdateVaultName(string vaultId, string newName)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        lock (_vaultPathLock)
        {
            var vault = dbContext.Vaults.FirstOrDefault(v => v.VaultId == vaultId);
            if (vault == null) return false;

            vault.Name = newName.Trim();
            dbContext.SaveChanges();
            return true;
        }
    }

    public bool UpdateVaultPath(string vaultId, string newPath)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        lock (_vaultPathLock)
        {
            var vault = dbContext.Vaults.FirstOrDefault(v => v.VaultId == vaultId);
            if (vault == null) return false;

            vault.Path = newPath.Trim();
            dbContext.SaveChanges();
            return true;
        }
    }

    public bool UpdateVaultTags(string vaultId, string tags)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        lock (_vaultPathLock)
        {
            var vault = dbContext.Vaults.FirstOrDefault(v => v.VaultId == vaultId);
            if (vault == null) return false;

            vault.Tags = tags;
            dbContext.SaveChanges();
            return true;
        }
    }

    public bool UpdateVaultIndustry(string vaultId, string industry)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        lock (_vaultPathLock)
        {
            var vault = dbContext.Vaults.FirstOrDefault(v => v.VaultId == vaultId);
            if (vault == null) return false;

            vault.Industry = industry.Trim();
            dbContext.SaveChanges();
            return true;
        }
    }

    public void SetVaultPath(string? vaultPath)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        lock (_vaultPathLock)
        {
            var vault = dbContext.Vaults.FirstOrDefault();
            if (vault != null)
            {
                vault.Path = vaultPath ?? "";
                dbContext.SaveChanges();
            }
        }
    }
}
