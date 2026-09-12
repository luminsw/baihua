using Microsoft.EntityFrameworkCore;
using Baihua.Data.Entities;

namespace Baihua.Data;

public class FamilyDbContext : DbContext
{
    public FamilyDbContext(DbContextOptions<FamilyDbContext> options) : base(options)
    {
    }

    public FamilyDbContext()
    {
    }

    public DbSet<TaskEntity> Tasks => Set<TaskEntity>();
    public DbSet<OpenClawTask> OpenClawTasks => Set<OpenClawTask>();
    public DbSet<LocalModelRegistry> LocalModelRegistries => Set<LocalModelRegistry>();
    public DbSet<LearnerProfile> LearnerProfiles => Set<LearnerProfile>();
    public DbSet<Achievement> Achievements => Set<Achievement>();
    public DbSet<StudyActivity> StudyActivities => Set<StudyActivity>();
    public DbSet<CardReviewState> CardReviewStates => Set<CardReviewState>();
    public DbSet<OnboardingState> OnboardingStates => Set<OnboardingState>();
    public DbSet<InitTaskProgress> InitTaskProgresses => Set<InitTaskProgress>();

    public DbSet<AuthorizedDevice> AuthorizedDevices => Set<AuthorizedDevice>();
    public DbSet<DeviceSyncLog> DeviceSyncLogs => Set<DeviceSyncLog>();
    public DbSet<ServerAddressSetting> ServerAddressSettings => Set<ServerAddressSetting>();
    public DbSet<ChatMemoryEntry> ChatMemoryEntries => Set<ChatMemoryEntry>();

    public DbSet<Master> Masters => Set<Master>();
    public DbSet<MasterConversation> MasterConversations => Set<MasterConversation>();
    public DbSet<StageSummary> StageSummaries => Set<StageSummary>();
    public DbSet<ApprenticeProfile> ApprenticeProfiles => Set<ApprenticeProfile>();
    public DbSet<ExamCheckpoint> ExamCheckpoints => Set<ExamCheckpoint>();

    public DbSet<VaultFocusState> VaultFocusStates => Set<VaultFocusState>();
    public DbSet<VaultFreeState> VaultFreeStates => Set<VaultFreeState>();
    public DbSet<CheckinMakeupRecord> CheckinMakeupRecords => Set<CheckinMakeupRecord>();
    public DbSet<FamilyReward> FamilyRewards => Set<FamilyReward>();
    public DbSet<RewardClaim> RewardClaims => Set<RewardClaim>();
    public DbSet<TodoItem> TodoItems => Set<TodoItem>();
    public DbSet<TodoGoal> TodoGoals => Set<TodoGoal>();

    // 家庭病历本：成员档案 + 病历记录 + AI 诊断
    public DbSet<MedicalMember> MedicalMembers => Set<MedicalMember>();
    public DbSet<MedicalRecord> MedicalRecords => Set<MedicalRecord>();
    public DbSet<AiDiagnosis> AiDiagnoses => Set<AiDiagnosis>();

    public DbSet<ServerPeer> ServerPeers => Set<ServerPeer>();
    public DbSet<ServerMessage> ServerMessages => Set<ServerMessage>();

    // 测速历史（BenchmarkRepository 使用）
    public DbSet<BenchmarkSessionEntity> BenchmarkSessions => Set<BenchmarkSessionEntity>();

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

        modelBuilder.Entity<TaskEntity>(entity =>
        {
            entity.ToTable("Tasks");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TaskId).IsUnique();
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);

            entity.Property(e => e.TaskId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.TaskType).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(50).IsRequired().HasDefaultValue("Pending");
            entity.Property(e => e.Progress).HasDefaultValue(0);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<OpenClawTask>(entity =>
        {
            entity.ToTable("OpenClawTasks");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TaskId).IsUnique();
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);

            entity.Property(e => e.TaskId).HasMaxLength(20).IsRequired();
            entity.Property(e => e.Prompt).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(50).IsRequired().HasDefaultValue("pending");
            entity.Property(e => e.ReportPath).HasMaxLength(1000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        });

        // 本地大模型注册表（Agent 经 MCP 写入，(Tool, ModelId) 幂等 upsert）
        modelBuilder.Entity<LocalModelRegistry>(entity =>
        {
            entity.ToTable("LocalModelRegistries");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.Tool, e.ModelId }).IsUnique();

            entity.Property(e => e.Tool).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ModelId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.DisplayName).HasMaxLength(128).IsRequired();
            entity.Property(e => e.Endpoint).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ParameterSize).HasMaxLength(32);
            entity.Property(e => e.Quantization).HasMaxLength(32);
            entity.Property(e => e.Usage).HasMaxLength(32);
            entity.Property(e => e.Capabilities).HasMaxLength(256);
            entity.Property(e => e.Notes).HasMaxLength(512);
            entity.Property(e => e.RegisteredBy).HasMaxLength(64).IsRequired().HasDefaultValue("");
            entity.Property(e => e.RegisteredAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<OnboardingState>(entity =>
        {
            entity.ToTable("OnboardingStates");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.IsCompleted).HasDefaultValue(false);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<InitTaskProgress>(entity =>
        {
            entity.ToTable("InitTaskProgresses");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TaskId).IsUnique();

            entity.Property(e => e.TaskId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.TaskType).HasMaxLength(50).IsRequired();
            entity.Property(e => e.IsCompleted).HasDefaultValue(false);
            entity.Property(e => e.IsSkipped).HasDefaultValue(false);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<LearnerProfile>(entity =>
        {
            entity.ToTable("LearnerProfiles");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name);

            entity.Property(e => e.Name).HasMaxLength(50).IsRequired();
            entity.Property(e => e.AvatarEmoji).HasMaxLength(10);
            entity.Property(e => e.Color).HasMaxLength(20);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<Achievement>(entity =>
        {
            entity.ToTable("Achievements");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.LearnerId, e.Key }).IsUnique();

            entity.Property(e => e.Key).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Icon).HasMaxLength(20);
            entity.Property(e => e.Tier).HasMaxLength(20);
            entity.Property(e => e.Category).HasMaxLength(20);
            entity.Property(e => e.UnlockedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<StudyActivity>(entity =>
        {
            entity.ToTable("StudyActivities");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.LearnerId);
            entity.HasIndex(e => new { e.LearnerId, e.VaultId, e.CreatedAt });

            entity.Property(e => e.VaultId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ActivityType).HasMaxLength(30).IsRequired();
            entity.Property(e => e.CardId).HasMaxLength(100);
            entity.Property(e => e.Result).HasMaxLength(20);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<CardReviewState>(entity =>
        {
            entity.ToTable("CardReviewStates");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.LearnerId, e.VaultId, e.CardId }).IsUnique();
            entity.HasIndex(e => new { e.LearnerId, e.VaultId, e.NextReviewDate });

            entity.Property(e => e.VaultId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.CardId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.LastResult).HasMaxLength(20);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<AuthorizedDevice>(entity =>
        {
            entity.ToTable("AuthorizedDevices");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.DeviceId).IsUnique();
            entity.HasIndex(e => e.AccessToken).IsUnique();
            entity.HasIndex(e => e.Status);

            entity.Property(e => e.DeviceId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.DeviceName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.AccessToken).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(50).IsRequired().HasDefaultValue("Authorized");
            entity.Property(e => e.IpAddress).HasMaxLength(50);
            entity.Property(e => e.AuthorizedTime).HasDefaultValueSql("now()");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<DeviceSyncLog>(entity =>
        {
            entity.ToTable("DeviceSyncLogs");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.DeviceId);
            entity.HasIndex(e => e.SyncTime);

            entity.Property(e => e.DeviceId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.DeviceName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.IpAddress).HasMaxLength(50);
            entity.Property(e => e.VaultId).HasMaxLength(50);
            entity.Property(e => e.SyncType).HasMaxLength(50).IsRequired().HasDefaultValue("manifest");
            entity.Property(e => e.SyncTime).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<ServerAddressSetting>(entity =>
        {
            entity.ToTable("ServerAddressSettings");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Domain).HasMaxLength(500).IsRequired().HasDefaultValue("");
            entity.Property(e => e.Url).HasMaxLength(500).IsRequired().HasDefaultValue("");
            entity.Property(e => e.DisplayName).HasMaxLength(200).IsRequired().HasDefaultValue("");
            entity.Property(e => e.ServerInstanceId).HasMaxLength(100).IsRequired().HasDefaultValue("");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<ChatMemoryEntry>(entity =>
        {
            entity.ToTable("ChatMemoryEntries");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.SessionId, e.Round });

            entity.Property(e => e.SessionId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.UserSummary).IsRequired();
            entity.Property(e => e.AssistantSummary).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<Master>(entity =>
        {
            entity.ToTable("Masters");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.MasterId).IsUnique();

            entity.Property(e => e.MasterId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.MasterName).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Goal).IsRequired();
            entity.Property(e => e.Industry).HasMaxLength(50).IsRequired();
            entity.Property(e => e.CurrentStage).HasMaxLength(20).IsRequired().HasDefaultValue("入道");
            entity.Property(e => e.GraduatedStagesJson).IsRequired().HasDefaultValue("[]");
            entity.Property(e => e.Status).HasMaxLength(20).IsRequired().HasDefaultValue("active");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<MasterConversation>(entity =>
        {
            entity.ToTable("MasterConversations");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.MasterId);
            entity.HasIndex(e => new { e.MasterId, e.CreatedAt });

            entity.Property(e => e.MasterId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Role).HasMaxLength(20).IsRequired();
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.Stage).HasMaxLength(20);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<StageSummary>(entity =>
        {
            entity.ToTable("StageSummaries");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.MasterId, e.StageName }).IsUnique();

            entity.Property(e => e.MasterId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.StageName).HasMaxLength(20).IsRequired();
            entity.Property(e => e.Summary).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<ApprenticeProfile>(entity =>
        {
            entity.ToTable("ApprenticeProfiles");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.MasterId).IsUnique();

            entity.Property(e => e.MasterId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<ExamCheckpoint>(entity =>
        {
            entity.ToTable("ExamCheckpoints");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.MasterId);
            entity.HasIndex(e => new { e.MasterId, e.StageName });

            entity.Property(e => e.MasterId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.StageName).HasMaxLength(20).IsRequired();
            entity.Property(e => e.WeakPointsJson).IsRequired().HasDefaultValue("[]");
            entity.Property(e => e.Advice).IsRequired().HasDefaultValue("");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<VaultFocusState>(entity =>
        {
            entity.ToTable("VaultFocusStates");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.MasterId);
            entity.HasIndex(e => new { e.MasterId, e.VaultId }).IsUnique();

            entity.Property(e => e.MasterId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.VaultId).IsRequired();
            entity.Property(e => e.State).HasMaxLength(20).IsRequired().HasDefaultValue("focused");
            entity.Property(e => e.StageName).HasMaxLength(20);
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<VaultFreeState>(entity =>
        {
            entity.ToTable("VaultFreeStates");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.VaultId).IsUnique();

            entity.Property(e => e.VaultId).IsRequired();
            entity.Property(e => e.State).HasMaxLength(20).IsRequired().HasDefaultValue("discovered");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<TodoGoal>(entity =>
        {
            entity.ToTable("TodoGoals");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).HasMaxLength(200).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            // 删除目标时级联删除其下全部待办（SQLite 单级级联，无环，安全）
            entity.HasMany(e => e.Items)
                .WithOne(e => e.Goal)
                .HasForeignKey(e => e.GoalId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TodoItem>(entity =>
        {
            entity.ToTable("TodoItems");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Note).HasMaxLength(1000);
            entity.HasIndex(e => e.GoalId);
        });

        // 家庭病历本：成员档案 + 病历记录 + AI 诊断（删除成员时级联删除其病历与诊断）
        modelBuilder.Entity<MedicalMember>(entity =>
        {
            entity.ToTable("MedicalMembers");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name);

            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<MedicalRecord>(entity =>
        {
            entity.ToTable("MedicalRecords");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.MemberId);
            entity.HasIndex(e => e.OccurredAt);

            entity.Property(e => e.Title).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            entity.HasOne(e => e.Member)
                .WithMany(m => m.Records)
                .HasForeignKey(e => e.MemberId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiDiagnosis>(entity =>
        {
            entity.ToTable("AiDiagnoses");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.MemberId);
            entity.HasIndex(e => new { e.MemberId, e.CreatedAt });

            entity.Property(e => e.SymptomText).IsRequired();
            entity.Property(e => e.AiResponse).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasOne(e => e.Member)
                .WithMany(m => m.Diagnoses)
                .HasForeignKey(e => e.MemberId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 服务器互联（百花 ↔ 百花 互发消息）
        modelBuilder.Entity<ServerPeer>(entity =>
        {
            entity.ToTable("ServerPeers");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ServerId);

            entity.Property(e => e.ServerId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.BaseUrl).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Token).HasMaxLength(500);
            entity.Property(e => e.Source).HasMaxLength(20).HasDefaultValue("manual");
            entity.Property(e => e.AddedAtUtc).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<ServerMessage>(entity =>
        {
            entity.ToTable("ServerMessages");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PeerId);
            entity.HasIndex(e => new { e.PeerServerId, e.SentAtUtc });

            entity.Property(e => e.PeerServerId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.PeerName).HasMaxLength(200);
            entity.Property(e => e.Direction).HasMaxLength(10).IsRequired();
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.SentAtUtc).HasDefaultValueSql("now()");
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
            if (entry.Entity is TaskEntity task)
                task.UpdatedAt = DateTime.UtcNow;
            else if (entry.Entity is OnboardingState onboarding)
                onboarding.UpdatedAt = DateTime.UtcNow;
            else if (entry.Entity is InitTaskProgress initTask)
                initTask.UpdatedAt = DateTime.UtcNow;
            else if (entry.Entity is CardReviewState reviewState)
                reviewState.UpdatedAt = DateTime.UtcNow;
            else if (entry.Entity is AuthorizedDevice device)
                device.UpdatedAt = DateTime.UtcNow;
            else if (entry.Entity is ServerAddressSetting setting)
                setting.UpdatedAt = DateTime.UtcNow;
            else if (entry.Entity is Master master)
                master.UpdatedAt = DateTime.UtcNow;
            else if (entry.Entity is ApprenticeProfile profile)
                profile.UpdatedAt = DateTime.UtcNow;
            else if (entry.Entity is MedicalMember medicalMember)
                medicalMember.UpdatedAt = DateTime.UtcNow;
            else if (entry.Entity is MedicalRecord medicalRecord)
                medicalRecord.UpdatedAt = DateTime.UtcNow;
        }
    }
}
