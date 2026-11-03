using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Baihua.Data.Entities;

// ─────────────────────────────────────────────
// 多行业选择题题库（学习模块）：行业无关表结构，中医（经方）是第一个实例。
// 判定零 AI：正确项由 OptionsJson 数据静态声明，判定＝比对所选 key。
// 内容字段（选项/题干结构/区分点/知识条目内容）走 JSON 列，随题库包文件整体导入导出。
// ─────────────────────────────────────────────

/// <summary>行业（如 tcm = 中医）</summary>
public class QuizIndustry
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(64)]
    public string Code { get; set; } = "";

    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = "";

    [MaxLength(500)]
    public string Description { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>题库（如 jingfang = 经方），属于某行业</summary>
public class QuizBank
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int IndustryId { get; set; }

    public QuizIndustry? Industry { get; set; }

    [Required]
    [MaxLength(64)]
    public string Code { get; set; } = "";

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "";

    [MaxLength(20)]
    public string Version { get; set; } = "";

    [MaxLength(500)]
    public string Description { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>知识条目：题库的核心知识单元（中医实例以药方为核心，itemType = formula）</summary>
public class QuizItem
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int BankId { get; set; }

    public QuizBank? Bank { get; set; }

    [Required]
    [MaxLength(64)]
    public string ItemCode { get; set; } = "";

    [Required]
    [MaxLength(32)]
    public string ItemType { get; set; } = "concept";

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "";

    [MaxLength(500)]
    public string Summary { get; set; } = "";

    /// <summary>全量内容 JSON（条文/组成/煎服法/病机/治法/禁忌/鉴别——行业自定义结构）</summary>
    [Required]
    public string ContentJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>选择题（单选 MVP）</summary>
public class QuizQuestion
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int BankId { get; set; }

    public QuizBank? Bank { get; set; }

    /// <summary>关联知识条目（可空：概念题无条目）</summary>
    public int? ItemId { get; set; }

    public QuizItem? Item { get; set; }

    [Required]
    [MaxLength(64)]
    public string QuestionCode { get; set; } = "";

    [Required]
    [MaxLength(32)]
    public string QuestionType { get; set; } = "single_choice";

    /// <summary>提问目标：formula（选方）/ pattern（选证型·病机）/ 行业自定义</summary>
    [Required]
    [MaxLength(32)]
    public string Target { get; set; } = "formula";

    /// <summary>题干（完整病例/情境描述）</summary>
    [Required]
    public string Stem { get; set; } = "";

    /// <summary>结构化题干 JSON（患者概况 + 体征要点）</summary>
    public string? StemJson { get; set; }

    /// <summary>选项 JSON（含正确项标记与各选项解析——判定的唯一事实来源）</summary>
    [Required]
    public string OptionsJson { get; set; } = "[]";

    /// <summary>解析（正确结果 + 完整分析）</summary>
    [Required]
    public string Analysis { get; set; } = "";

    /// <summary>区分点 JSON（与干扰项的关键鉴别）</summary>
    public string? DiscriminationJson { get; set; }

    public int Difficulty { get; set; } = 3;

    [MaxLength(200)]
    public string? SourceRef { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>答题记录（仅家庭成员——LearnerProfile）</summary>
public class QuizAttempt
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int LearnerId { get; set; }

    public LearnerProfile? Learner { get; set; }

    public int QuestionId { get; set; }

    public QuizQuestion? Question { get; set; }

    public int BankId { get; set; }

    [Required]
    [MaxLength(8)]
    public string SelectedKey { get; set; } = "";

    public bool IsCorrect { get; set; }

    public int? TimeCostMs { get; set; }

    /// <summary>刷题会话标识（同一次连做归组）</summary>
    [MaxLength(64)]
    public string? SessionId { get; set; }

    public DateTime AnsweredAt { get; set; } = DateTime.UtcNow;
}