using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Baihua.Data.Entities;

/// <summary>
/// 家庭病历本：成员健康档案（身体状况、过敏史、慢性病）。
/// 列表型字段（过敏史/慢性病）以 JSON 字符串存储（如 "[]"），由服务层序列化/反序列化。
/// </summary>
public class MedicalMember
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>姓名</summary>
    [Required]
    public string Name { get; set; } = "";

    /// <summary>性别（男 / 女 / 未知）</summary>
    public string Gender { get; set; } = "";

    /// <summary>出生日期（用于计算年龄）</summary>
    public DateTime? BirthDate { get; set; }

    /// <summary>血型（A / B / AB / O / 未知）</summary>
    public string BloodType { get; set; } = "";

    /// <summary>过敏史 JSON 数组（如 ["青霉素过敏"]）</summary>
    public string AllergiesJson { get; set; } = "[]";

    /// <summary>慢性病 / 基础疾病 JSON 数组</summary>
    public string ChronicDiseasesJson { get; set; } = "[]";

    /// <summary>其他备注</summary>
    public string? Notes { get; set; }

    /// <summary>身高（cm）</summary>
    public double? HeightCm { get; set; }

    /// <summary>体重（kg）</summary>
    public double? WeightKg { get; set; }

    /// <summary>职业</summary>
    public string? Occupation { get; set; }

    /// <summary>生活起居习惯</summary>
    public string? LifeHabits { get; set; }

    /// <summary>运动损伤史 JSON 数组（如 ["踝关节扭伤"]）</summary>
    public string SportsInjuriesJson { get; set; } = "[]";

    /// <summary>中医体质画像 JSON（{Primary,Secondary,Note}）</summary>
    public string? ConstitutionJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<MedicalRecord> Records { get; set; } = new();
    public List<AiDiagnosis> Diagnoses { get; set; } = new();
}

/// <summary>
/// 病历记录：一次就诊 / 发病 / 症状发作。
/// 症状、医生诊断、用药均以 JSON 字符串存储，用药为 {Name,Dosage,Frequency,Note} 对象数组。
/// </summary>
public class MedicalRecord
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>所属成员</summary>
    public int MemberId { get; set; }

    public MedicalMember? Member { get; set; }

    /// <summary>发生日期（就诊 / 发病时间）</summary>
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>标题（如"感冒发烧"）</summary>
    [Required]
    public string Title { get; set; } = "";

    /// <summary>症状 JSON 数组</summary>
    public string SymptomsJson { get; set; } = "[]";

    /// <summary>医生诊断 JSON 数组</summary>
    public string DiagnosesJson { get; set; } = "[]";

    /// <summary>用药 JSON 数组（{Name,Dosage,Frequency,Note,Ingredients,DecoctionMethod,Principle,Course,Effect}）</summary>
    public string MedicationsJson { get; set; } = "[]";

    /// <summary>四诊结构化 JSON（舌象/脉象/寒热/二便等，可空）</summary>
    public string? FourDiagnosticsJson { get; set; }

    /// <summary>备注（医院、科室、医生、注意事项）</summary>
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// AI 诊断记录：保存用户提交的症状与 AI 生成的分析。
/// 仅作参考，不可代替医生——免责声明由系统提示词与前端文案双重保证。
/// </summary>
public class AiDiagnosis
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>所属成员</summary>
    public int MemberId { get; set; }

    public MedicalMember? Member { get; set; }

    /// <summary>用户提交的症状描述</summary>
    [Required]
    public string SymptomText { get; set; } = "";

    /// <summary>AI 生成的分析（Markdown 文本，向后兼容）</summary>
    [Required]
    public string AiResponse { get; set; } = "";

    /// <summary>
    /// 结构化诊断结果 JSON（ nullable）。当 AI 返回结构化 JSON 时存储原始 JSON，
    /// 供前端解析为卡片式展示。格式见 StructuredDiagnosisResult。
    /// </summary>
    public string? StructuredResultJson { get; set; }

    /// <summary>使用的模型标识（如 "biancang" 或 "main"）</summary>
    public string ModelUsed { get; set; } = "main";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
