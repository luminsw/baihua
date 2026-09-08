using Microsoft.EntityFrameworkCore;
using Baihua.Data.Entities;

namespace Baihua.Data;

/// <summary>
/// AI 研究域数据库上下文
/// </summary>
public class AIDbContext : DbContext
{
    private readonly string _dbPath;

    public AIDbContext(DbContextOptions<AIDbContext> options) : base(options)
    {
        _dbPath = Database.GetDbConnection().ConnectionString;
    }

    public AIDbContext()
    {
        _dbPath = GetDefaultDbPath();
    }

    public DbSet<AiProviderSetting> AiProviderSettings => Set<AiProviderSetting>();
    public DbSet<AiUsageMetric> AiUsageMetrics => Set<AiUsageMetric>();
    public DbSet<BenchmarkSessionEntity> BenchmarkSessions => Set<BenchmarkSessionEntity>();
    public DbSet<EmbeddingConfig> EmbeddingConfigs => Set<EmbeddingConfig>();
    public DbSet<ComfyArtworkEntity> ComfyArtworks => Set<ComfyArtworkEntity>();



    public string DatabasePath => _dbPath;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            var dbPath = GetDefaultDbPath();
            optionsBuilder.UseNpgsql(Baihua.Data.DbConnections.For("ai"));
        }
    }

    private static string GetDefaultDbPath()
    {
        var dataDir = ResolveDataDir();
        Directory.CreateDirectory(dataDir);
        return Path.Combine(dataDir, "ai.db");
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

        modelBuilder.Entity<AiProviderSetting>(entity =>
        {
            entity.ToTable("AiProviderSettings");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProviderId).IsUnique();
            entity.HasIndex(e => e.IsMain);

            entity.Property(e => e.ProviderId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ProviderName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.BaseUrl).HasMaxLength(500).IsRequired();
            entity.Property(e => e.EncryptedApiKey).HasMaxLength(2000);
            entity.Property(e => e.ModelsJson).IsRequired().HasDefaultValue("[]");
            entity.Property(e => e.SortOrder).HasDefaultValue(0);
            entity.Property(e => e.IsEnabled).HasDefaultValue(true);
            entity.Property(e => e.IsMain).HasDefaultValue(false);
            entity.Property(e => e.Tier).HasDefaultValue(0);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<AiUsageMetric>(entity =>
        {
            entity.ToTable("AiUsageMetrics");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.CalledAt);
            entity.HasIndex(e => e.ProviderId);
            entity.HasIndex(e => e.ModelId);
            entity.HasIndex(e => e.Operation);

            entity.Property(e => e.ProviderId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ProviderName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.ModelId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Operation).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
            entity.Property(e => e.CalledAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<BenchmarkSessionEntity>(entity =>
        {
            entity.ToTable("BenchmarkSessions");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SessionId).IsUnique();
            entity.HasIndex(e => e.Category);
            entity.HasIndex(e => e.TestedAt);

            entity.Property(e => e.SessionId).HasMaxLength(20).IsRequired();
            entity.Property(e => e.ModelName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Category).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ProviderId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ModelId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.ResultsJson).IsRequired().HasDefaultValue("[]");
            entity.Property(e => e.TestedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<EmbeddingConfig>(entity =>
        {
            entity.ToTable("EmbeddingConfigs");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProviderId);

            entity.Property(e => e.ProviderId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Model).HasMaxLength(200).IsRequired();
            entity.Property(e => e.BaseUrl).HasMaxLength(500).IsRequired();
            entity.Property(e => e.EncryptedApiKey).HasMaxLength(2000);
            entity.Property(e => e.IsEnabled).HasDefaultValue(true);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<ComfyArtworkEntity>(entity =>
        {
            entity.ToTable("ComfyArtworks");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.Kind);
            entity.HasIndex(e => e.PromptId).IsUnique();

            entity.Property(e => e.Kind).HasMaxLength(10).IsRequired();
            entity.Property(e => e.Prompt).HasMaxLength(2000).IsRequired();
            entity.Property(e => e.Model).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ParamsJson).IsRequired().HasDefaultValue("{}");
            entity.Property(e => e.FileName).HasMaxLength(300).IsRequired();
            entity.Property(e => e.Subfolder).HasMaxLength(300).HasDefaultValue("");
            entity.Property(e => e.FileType).HasMaxLength(20).HasDefaultValue("output");
            entity.Property(e => e.PromptId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
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
            if (entry.Entity is AiProviderSetting provider)
            {
                provider.UpdatedAt = DateTime.UtcNow;
            }
            else if (entry.Entity is EmbeddingConfig embedding)
            {
                embedding.UpdatedAt = DateTime.UtcNow;
            }

        }
    }
}
