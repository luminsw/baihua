using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Baihua.Data.Entities;

public class Master
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(64)]
    public string MasterId { get; set; } = "";

    [Required]
    [MaxLength(50)]
    public string MasterName { get; set; } = "";

    [Required]
    public string Goal { get; set; } = "";

    [Required]
    [MaxLength(50)]
    public string Industry { get; set; } = "";

    [Required]
    [MaxLength(20)]
    public string CurrentStage { get; set; } = "入道";

    public string GraduatedStagesJson { get; set; } = "[]";

    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "active";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class MasterConversation
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(64)]
    public string MasterId { get; set; } = "";

    [Required]
    [MaxLength(20)]
    public string Role { get; set; } = "";

    [Required]
    public string Content { get; set; } = "";

    [MaxLength(20)]
    public string Stage { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class StageSummary
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(64)]
    public string MasterId { get; set; } = "";

    [Required]
    [MaxLength(20)]
    public string StageName { get; set; } = "";

    [Required]
    public string Summary { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ApprenticeProfile
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(64)]
    public string MasterId { get; set; } = "";

    public string? Foundation { get; set; }
    public string? LearningStyle { get; set; }
    public string? Strengths { get; set; }
    public string? Weaknesses { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class ExamCheckpoint
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(64)]
    public string MasterId { get; set; } = "";

    [Required]
    [MaxLength(20)]
    public string StageName { get; set; } = "";

    public double Score { get; set; }
    public double PassProbability { get; set; }
    public string WeakPointsJson { get; set; } = "[]";
    public string Advice { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class VaultFocusState
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(64)]
    public string MasterId { get; set; } = "";

    [Required]
    public string VaultId { get; set; } = "";

    [Required]
    [MaxLength(20)]
    public string State { get; set; } = "focused";

    [MaxLength(20)]
    public string? StageName { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class VaultFreeState
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    public string VaultId { get; set; } = "";

    [Required]
    [MaxLength(20)]
    public string State { get; set; } = "discovered";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
