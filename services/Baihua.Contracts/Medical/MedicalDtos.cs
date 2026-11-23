namespace Baihua.Contracts.Medical;

/// <summary>舌象（望诊）</summary>
public class TongueDto
{
    public string? Color { get; set; }           // 淡红/淡白/红/绛/紫/暗
    public string? Shape { get; set; }           // 胖大/瘦薄/齿痕/裂纹/点刺/瘀斑
    public string? CoatingColor { get; set; }    // 白/黄/灰/黑
    public string? CoatingThickness { get; set; }// 薄/厚
    public string? CoatingTexture { get; set; }  // 润/燥/腻/腐/剥
    public string? Sublingual { get; set; }      // 正常/怒张
    public string? Note { get; set; }
}

/// <summary>脉象（切诊）</summary>
public class PulseDto
{
    public string? Rate { get; set; }     // 迟/数/缓/疾
    public string? Rhythm { get; set; }   // 结/代/促
    public string? Depth { get; set; }    // 浮/沉
    public string? Strength { get; set; } // 有力/无力
    public string? Quality { get; set; }  // 弦/滑/细/洪/濡/弱/涩/紧
    public string? Position { get; set; } // 左右寸关尺
    public string? Note { get; set; }
}

/// <summary>四诊结构化</summary>
public class FourDiagnosticsDto
{
    public TongueDto? Tongue { get; set; }
    public PulseDto? Pulse { get; set; }
    public string? ColdHeat { get; set; } // 寒热
    public string? Sweat { get; set; }    // 汗
    public string? Thirst { get; set; }   // 口渴
    public string? Urine { get; set; }    // 小便
    public string? Stool { get; set; }    // 大便
    public string? Sleep { get; set; }    // 睡眠
    public string? Appetite { get; set; } // 饮食口味
    public string? Note { get; set; }
}

/// <summary>中医体质</summary>
public class ConstitutionDto
{
    public string? Primary { get; set; }   // 平和/气虚/阳虚/阴虚/痰湿/湿热/血瘀/气郁/特禀
    public string? Secondary { get; set; }
    public string? Note { get; set; }
}

/// <summary>方剂单味药</summary>
public class IngredientDto
{
    public string Name { get; set; } = "";
    public string? Dosage { get; set; }
    public string? Note { get; set; }     // 如 后下/包煎
}

/// <summary>
/// 家庭病历本：家庭成员健康档案（身体状况）。
/// 每个成员维护一份：基本信息 + 过敏史 + 慢性病/基础疾病。
/// </summary>
public class MedicalMemberDto
{
    public int Id { get; set; }

    /// <summary>姓名</summary>
    public string Name { get; set; } = "";

    /// <summary>性别（男 / 女 / 未知）</summary>
    public string Gender { get; set; } = "";

    /// <summary>出生日期（用于计算年龄）</summary>
    public DateTime? BirthDate { get; set; }

    /// <summary>血型（A / B / AB / O / 未知）</summary>
    public string BloodType { get; set; } = "";

    /// <summary>过敏史（如"青霉素过敏""花生过敏"）</summary>
    public List<string> Allergies { get; set; } = new();

    /// <summary>慢性病 / 基础疾病（如"高血压""糖尿病"）</summary>
    public List<string> ChronicDiseases { get; set; } = new();

    /// <summary>其他备注</summary>
    public string? Notes { get; set; }

    /// <summary>身高（cm）</summary>
    public double? HeightCm { get; set; }

    /// <summary>体重（kg）</summary>
    public double? WeightKg { get; set; }

    /// <summary>职业</summary>
    public string? Occupation { get; set; }

    /// <summary>生活起居习惯（饮食/作息/运动等）</summary>
    public string? LifeHabits { get; set; }

    /// <summary>运动损伤史条目</summary>
    public List<string> SportsInjuries { get; set; } = new();

    /// <summary>中医体质画像</summary>
    public ConstitutionDto? Constitution { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>创建成员请求</summary>
public class CreateMedicalMemberRequest
{
    /// <summary>姓名（必填，去空格后 ≤50 字）</summary>
    public string Name { get; set; } = "";

    public string Gender { get; set; } = "";

    public DateTime? BirthDate { get; set; }

    public string BloodType { get; set; } = "";

    /// <summary>过敏史条目</summary>
    public List<string> Allergies { get; set; } = new();

    /// <summary>慢性病条目</summary>
    public List<string> ChronicDiseases { get; set; } = new();

    /// <summary>备注（≤2000 字）</summary>
    public string? Notes { get; set; }

    /// <summary>身高（cm）</summary>
    public double? HeightCm { get; set; }

    /// <summary>体重（kg）</summary>
    public double? WeightKg { get; set; }

    /// <summary>职业</summary>
    public string? Occupation { get; set; }

    /// <summary>生活起居习惯</summary>
    public string? LifeHabits { get; set; }

    /// <summary>运动损伤史条目</summary>
    public List<string> SportsInjuries { get; set; } = new();

    /// <summary>中医体质画像</summary>
    public ConstitutionDto? Constitution { get; set; }
}

/// <summary>更新成员请求（字段均可选，至少传一项有效字段）</summary>
public class UpdateMedicalMemberRequest
{
    public string? Name { get; set; }

    public string? Gender { get; set; }

    public DateTime? BirthDate { get; set; }

    public string? BloodType { get; set; }

    /// <summary>传入 null 表示不修改，传入空列表表示清空</summary>
    public List<string>? Allergies { get; set; }

    /// <summary>传入 null 表示不修改，传入空列表表示清空</summary>
    public List<string>? ChronicDiseases { get; set; }

    /// <summary>传入 null 表示不修改，传入空字符串表示清空</summary>
    public string? Notes { get; set; }

    /// <summary>传入 null 表示不修改</summary>
    public double? HeightCm { get; set; }

    /// <summary>传入 null 表示不修改</summary>
    public double? WeightKg { get; set; }

    /// <summary>传入 null 表示不修改，传入空字符串表示清空</summary>
    public string? Occupation { get; set; }

    /// <summary>传入 null 表示不修改，传入空字符串表示清空</summary>
    public string? LifeHabits { get; set; }

    /// <summary>传入 null 表示不修改，传入空列表表示清空</summary>
    public List<string>? SportsInjuries { get; set; }

    /// <summary>传入 null 表示不修改</summary>
    public ConstitutionDto? Constitution { get; set; }
}

/// <summary>用药条目（每次就医/发病记录中的一种药物）</summary>
public class MedicalMedicationItemDto
{
    /// <summary>药名（必填）</summary>
    public string Name { get; set; } = "";

    /// <summary>剂量（如"每次 1 片"）</summary>
    public string? Dosage { get; set; }

    /// <summary>频次（如"每日 3 次"）</summary>
    public string? Frequency { get; set; }

    /// <summary>备注（如"饭后服用""疗程 5 天"）</summary>
    public string? Note { get; set; }

    /// <summary>方剂组成（单味药列表）</summary>
    public List<IngredientDto>? Ingredients { get; set; }

    /// <summary>煎服方法（如"水煎分 2 次温服"）</summary>
    public string? DecoctionMethod { get; set; }

    /// <summary>方义/治法（如"疏风散寒"）</summary>
    public string? Principle { get; set; }

    /// <summary>疗程（如"5 天"）</summary>
    public string? Course { get; set; }

    /// <summary>功效（如"发汗解表"）</summary>
    public string? Effect { get; set; }
}

/// <summary>
/// 病历记录：一次就诊 / 发病 / 症状发作的记录。
/// 包含症状、诊断、用药，方便日后查阅与就医时向医生陈述。
/// </summary>
public class MedicalRecordDto
{
    public int Id { get; set; }

    public int MemberId { get; set; }

    /// <summary>发生日期（就诊 / 发病时间）</summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>标题（如"感冒发烧""过敏性鼻炎发作"）</summary>
    public string Title { get; set; } = "";

    /// <summary>症状（如"发热 38.5℃""流鼻涕"）</summary>
    public List<string> Symptoms { get; set; } = new();

    /// <summary>医生诊断（如"上呼吸道感染"；无就医记录可为空）</summary>
    public List<string> Diagnoses { get; set; } = new();

    /// <summary>所用药物</summary>
    public List<MedicalMedicationItemDto> Medications { get; set; } = new();

    /// <summary>备注（就诊医院、科室、医生、注意事项等）</summary>
    public string? Notes { get; set; }

    /// <summary>四诊结构化（舌象/脉象/寒热/二便等）</summary>
    public FourDiagnosticsDto? FourDiagnostics { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>创建病历记录请求</summary>
public class CreateMedicalRecordRequest
{
    /// <summary>发生日期（默认当天）</summary>
    public DateTime? OccurredAt { get; set; }

    /// <summary>标题（必填，去空格后 ≤200 字）</summary>
    public string Title { get; set; } = "";

    public List<string> Symptoms { get; set; } = new();

    public List<string> Diagnoses { get; set; } = new();

    public List<MedicalMedicationItemDto> Medications { get; set; } = new();

    /// <summary>备注（≤2000 字）</summary>
    public string? Notes { get; set; }

    /// <summary>四诊结构化（舌象/脉象/寒热/二便等）</summary>
    public FourDiagnosticsDto? FourDiagnostics { get; set; }
}

/// <summary>更新病历记录请求（字段均可选，至少传一项有效字段）</summary>
public class UpdateMedicalRecordRequest
{
    public DateTime? OccurredAt { get; set; }

    public string? Title { get; set; }

    /// <summary>传入 null 表示不修改，传入空列表表示清空</summary>
    public List<string>? Symptoms { get; set; }

    /// <summary>传入 null 表示不修改，传入空列表表示清空</summary>
    public List<string>? Diagnoses { get; set; }

    /// <summary>传入 null 表示不修改，传入空列表表示清空</summary>
    public List<MedicalMedicationItemDto>? Medications { get; set; }

    /// <summary>传入 null 表示不修改，传入空字符串表示清空</summary>
    public string? Notes { get; set; }

    /// <summary>传入 null 表示不修改</summary>
    public FourDiagnosticsDto? FourDiagnostics { get; set; }
}

/// <summary>AI 诊断记录（仅作参考，不可代替医生）</summary>
public class AiDiagnosisDto
{
    public int Id { get; set; }

    public int MemberId { get; set; }

    /// <summary>用户提交的症状描述</summary>
    public string SymptomText { get; set; } = "";

    /// <summary>AI 生成的分析（Markdown 文本，含免责声明）</summary>
    public string AiResponse { get; set; } = "";

    /// <summary>结构化诊断结果 JSON（nullable，新模型才有）</summary>
    public string? StructuredResultJson { get; set; }

    /// <summary>使用的模型标识（"biancang" 或 "main"）</summary>
    public string ModelUsed { get; set; } = "main";

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// 结构化诊断结果：AI 返回的 JSON 解析后的强类型表示，供前端卡片式展示。
/// </summary>
public class StructuredDiagnosisResult
{
    /// <summary>可能原因列表（按可能性从高到低）</summary>
    public List<StructuredPossibleCause> PossibleCauses { get; set; } = new();

    /// <summary>居家护理与观察建议</summary>
    public List<string> HomeCare { get; set; } = new();

    /// <summary>需要立即就医的警示信号（red flags）</summary>
    public List<string> WarningSigns { get; set; } = new();

    /// <summary>是否建议就医</summary>
    public bool SeeDoctor { get; set; }

    /// <summary>建议就医的原因</summary>
    public string? SeeDoctorReason { get; set; }

    /// <summary>结合个体因素的特别注意事项</summary>
    public string? IndividualNotes { get; set; }

    /// <summary>免责声明</summary>
    public string Disclaimer { get; set; } = "";
}

/// <summary>可能原因条目</summary>
public class StructuredPossibleCause
{
    /// <summary>原因名称</summary>
    public string Name { get; set; } = "";

    /// <summary>可能性等级（较高 / 中等 / 较低 / 不能确定）</summary>
    public string Likelihood { get; set; } = "";

    /// <summary>依据与局限说明</summary>
    public string Reasoning { get; set; } = "";
}

/// <summary>AI 诊断请求</summary>
public class AiDiagnoseRequest
{
    /// <summary>成员 Id（必填）</summary>
    public int MemberId { get; set; }

    /// <summary>症状描述（必填，去空格后 5-2000 字）</summary>
    public string SymptomText { get; set; } = "";

    /// <summary>补充背景（可选，如近期检查结果、特殊情况）</summary>
    public string? ExtraContext { get; set; }
}

/// <summary>AI 诊断结果：成功时携带已保存的诊断记录，失败时携带面向用户的错误</summary>
public class AiDiagnoseResultDto
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public AiDiagnosisDto? Diagnosis { get; set; }
}

/// <summary>成员详情：档案 + 病历记录列表 + AI 诊断历史</summary>
public class MedicalMemberDetailDto
{
    public MedicalMemberDto Member { get; set; } = new();
    public List<MedicalRecordDto> Records { get; set; } = new();
    public List<AiDiagnosisDto> Diagnoses { get; set; } = new();
}
