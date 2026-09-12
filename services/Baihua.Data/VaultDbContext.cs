using Microsoft.EntityFrameworkCore;
using Baihua.Data.Entities;

namespace Baihua.Data;

public class VaultDbContext : DbContext
{
    private string? _dbPath;

    public VaultDbContext(DbContextOptions<VaultDbContext> options) : base(options)
    {
    }

    public VaultDbContext()
    {
        _dbPath = GetDefaultDbPath();
    }

    public DbSet<Vault> Vaults => Set<Vault>();
    public DbSet<NoteEmbedding> NoteEmbeddings => Set<NoteEmbedding>();

    public string DatabasePath
    {
        get
        {
            if (_dbPath != null)
                return _dbPath;
            try
            {
                _dbPath = Database.GetDbConnection().ConnectionString;
                return _dbPath;
            }
            catch (InvalidOperationException)
            {
                return "InMemory";
            }
        }
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            var dbPath = GetDefaultDbPath();
            optionsBuilder.UseNpgsql(Baihua.Data.DbConnections.Baihua);
        }
    }

    private static string GetDefaultDbPath()
    {
        var dataDir = ResolveDataDir();
        Directory.CreateDirectory(dataDir);
        return Path.Combine(dataDir, "vault.db");
    }

    internal static string ResolveDataDir()
    {
        var dbDir = Baihua.Contracts.BaihuaPaths.Db;
        Directory.CreateDirectory(dbDir);
        return dbDir;
    }

    public static string GetDbPath()
    {
        return GetDefaultDbPath();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Vault>(entity =>
        {
            entity.ToTable("Vaults");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.VaultId).IsUnique();
            entity.HasIndex(e => e.IsActive);

            entity.Property(e => e.VaultId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Path).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.IsActive).HasDefaultValue(false);
            entity.Property(e => e.Tags).HasMaxLength(500).HasDefaultValue("");
            entity.Property(e => e.Industry).HasMaxLength(100).HasDefaultValue("");
            entity.Property(e => e.Source).HasMaxLength(20).HasDefaultValue("local");
            entity.Property(e => e.PushedByDeviceId).HasMaxLength(100).HasDefaultValue("");
            entity.Property(e => e.PushedByDeviceName).HasMaxLength(200).HasDefaultValue("");
            entity.Property(e => e.PushedAt).IsRequired(false);
            entity.Property(e => e.IsDeleted).HasDefaultValue(false);
            entity.Property(e => e.DeletedAt).IsRequired(false);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<NoteEmbedding>(entity =>
        {
            entity.ToTable("NoteEmbeddings");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.VaultId, e.NotePath }).IsUnique();

            entity.Property(e => e.VaultId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.NotePath).HasMaxLength(500).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        });
    }

    public override int SaveChanges()
    {
        UpdateTimestamps();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        UpdateTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void UpdateTimestamps()
    {
        var entries = ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Modified);

        foreach (var entry in entries)
        {
            if (entry.Entity is Vault vault)
            {
                vault.UpdatedAt = DateTime.UtcNow;
            }
            else if (entry.Entity is NoteEmbedding noteEmbedding)
            {
                noteEmbedding.UpdatedAt = DateTime.UtcNow;
            }
        }
    }
}