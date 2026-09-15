using Microsoft.EntityFrameworkCore;
using Baihua.Data.Entities;

namespace Baihua.Data;

public class VaultDbContext : DbContext
{
    public VaultDbContext(DbContextOptions<VaultDbContext> options) : base(options)
    {
    }

    public VaultDbContext()
    {
    }

    public DbSet<Vault> Vaults => Set<Vault>();
    public DbSet<NoteEmbedding> NoteEmbeddings => Set<NoteEmbedding>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseNpgsql(Baihua.Data.DbConnections.Baihua);
        }
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
            // 未删除的知识库：路径与名称都必须唯一。
            // 没有这两个约束时，并发的「扫描磁盘登记知识库」（启动编排 + /vault-settings/vaults/sync）
            // 会各自读到空快照后重复插入，界面上同一条知识库出现两张卡片。
            // 部分唯一索引（PostgreSQL）只约束未删除行，回收站允许多条同名/同路径。
            entity.HasIndex(e => e.Path)
                .IsUnique()
                .HasDatabaseName("UX_Vaults_Path_Active")
                .HasFilter("\"IsDeleted\" = false");
            entity.HasIndex(e => e.Name)
                .IsUnique()
                .HasDatabaseName("UX_Vaults_Name_Active")
                .HasFilter("\"IsDeleted\" = false");

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