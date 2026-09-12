using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Baihua.Contracts.Medical;
using Baihua.Data;
using Baihua.Data.Entities;

namespace Baihua.Modules.Family.Services.Medical;

/// <summary>
/// 家庭病历本数据服务：成员档案 / 病历记录 / AI 诊断的增删改查。
/// 列表型字段（过敏史、慢性病、症状、诊断、用药）以 JSON 字符串存储，
/// 由本服务负责序列化与反序列化。
/// </summary>
public class MedicalService
{
    private readonly IDbContextFactory<FamilyDbContext> _dbFactory;
    private readonly ILogger<MedicalService> _logger;

    public MedicalService(IDbContextFactory<FamilyDbContext> dbFactory, ILogger<MedicalService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    // ============ 成员档案 ============

    /// <summary>获取全部成员（按创建顺序）</summary>
    public async Task<List<MedicalMember>> GetMembersAsync(CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.MedicalMembers.OrderBy(m => m.Id).ToListAsync(ct);
    }

    /// <summary>按 Id 获取成员（不存在返回 null）</summary>
    public async Task<MedicalMember?> GetMemberAsync(int id, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.MedicalMembers.FirstOrDefaultAsync(m => m.Id == id, ct);
    }

    /// <summary>创建成员。姓名空白或超长时返回 null（由控制器转 400）</summary>
    public async Task<MedicalMember?> CreateMemberAsync(
        string name, string gender, DateTime? birthDate, string bloodType,
        IEnumerable<string> allergies, IEnumerable<string> chronicDiseases, string? notes,
        double? heightCm, double? weightKg, string? occupation, string? lifeHabits,
        IEnumerable<string> sportsInjuries, ConstitutionDto? constitution,
        CancellationToken ct = default)
    {
        var trimmedName = name?.Trim() ?? "";
        if (trimmedName.Length == 0 || trimmedName.Length > 50)
            return null;

        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var member = new MedicalMember
        {
            Name = trimmedName,
            Gender = NormalizeShort(gender, 10),
            BirthDate = birthDate?.ToUniversalTime(),
            BloodType = NormalizeShort(bloodType, 10),
            AllergiesJson = SerializeList(allergies),
            ChronicDiseasesJson = SerializeList(chronicDiseases),
            Notes = NormalizeNotes(notes),
            HeightCm = heightCm,
            WeightKg = weightKg,
            Occupation = NormalizeNullable(occupation),
            LifeHabits = NormalizeNullable(lifeHabits),
            SportsInjuriesJson = SerializeList(sportsInjuries),
            ConstitutionJson = SerializeConstitution(constitution)
        };
        db.MedicalMembers.Add(member);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
        {
            _logger.LogError(ex, "创建家庭成员保存失败: Name={Name} | Inner: {InnerType}: {InnerMessage}",
                trimmedName, ex.InnerException?.GetType().Name ?? "none", ex.InnerException?.Message ?? "");
            throw;
        }
        return member;
    }

    /// <summary>
    /// 更新成员（字段均为可选；name/gender/bloodType 传 null 表示不修改，
    /// allergies/chronicDiseases 传 null 表示不修改，传空列表表示清空；notes 传 null 表示不修改，传空字符串表示清空）。
    /// 不存在时返回 null。
    /// </summary>
    public async Task<MedicalMember?> UpdateMemberAsync(
        int id, string? name, string? gender, DateTime? birthDate, string? bloodType,
        List<string>? allergies, List<string>? chronicDiseases, string? notes,
        double? heightCm, double? weightKg, string? occupation, string? lifeHabits,
        List<string>? sportsInjuries, ConstitutionDto? constitution,
        CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var member = await db.MedicalMembers.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (member == null)
            return null;

        if (name != null)
        {
            var trimmed = name.Trim();
            if (trimmed.Length == 0 || trimmed.Length > 50)
                return null;
            member.Name = trimmed;
        }
        if (gender != null)
            member.Gender = NormalizeShort(gender, 10);
        if (birthDate.HasValue)
            member.BirthDate = birthDate.Value.ToUniversalTime();
        if (bloodType != null)
            member.BloodType = NormalizeShort(bloodType, 10);
        if (allergies != null)
            member.AllergiesJson = SerializeList(allergies);
        if (chronicDiseases != null)
            member.ChronicDiseasesJson = SerializeList(chronicDiseases);
        if (notes != null)
        {
            var normalized = NormalizeNotes(notes);
            if (normalized == null && !string.IsNullOrWhiteSpace(notes))
                return null; // 超长，视为非法
            member.Notes = normalized;
        }
        if (heightCm.HasValue)
            member.HeightCm = heightCm.Value;
        if (weightKg.HasValue)
            member.WeightKg = weightKg.Value;
        if (occupation != null)
            member.Occupation = NormalizeNullable(occupation);
        if (lifeHabits != null)
            member.LifeHabits = NormalizeNullable(lifeHabits);
        if (sportsInjuries != null)
            member.SportsInjuriesJson = SerializeList(sportsInjuries);
        if (constitution != null)
            member.ConstitutionJson = SerializeConstitution(constitution);

        await db.SaveChangesAsync(ct);
        return member;
    }

    /// <summary>删除成员（级联删除其病历记录与 AI 诊断）。不存在时返回 false</summary>
    public async Task<bool> DeleteMemberAsync(int id, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var member = await db.MedicalMembers.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (member == null)
            return false;
        db.MedicalMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ============ 病历记录 ============

    /// <summary>按成员获取病历记录（按发生日期倒序）</summary>
    public async Task<List<MedicalRecord>> GetRecordsAsync(int memberId, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.MedicalRecords
            .Where(r => r.MemberId == memberId)
            .OrderByDescending(r => r.OccurredAt)
            .ThenByDescending(r => r.Id)
            .ToListAsync(ct);
    }

    /// <summary>获取单条病历记录（不存在返回 null）</summary>
    public async Task<MedicalRecord?> GetRecordAsync(int id, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.MedicalRecords.FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    /// <summary>
    /// 按关键词检索病历记录（标题/症状/诊断/用药/四诊/备注，不区分大小写）。
    /// 关键词为空返回空列表；limit 截断在 [1, 200]。
    /// </summary>
    public async Task<List<MedicalRecord>> SearchRecordsAsync(string query, int limit = 50, CancellationToken ct = default)
    {
        var needle = (query ?? "").Trim().ToLower();
        if (needle.Length == 0)
            return new List<MedicalRecord>();

        using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.MedicalRecords
            .Where(r => r.Title.ToLower().Contains(needle) ||
                        r.SymptomsJson.ToLower().Contains(needle) ||
                        r.DiagnosesJson.ToLower().Contains(needle) ||
                        r.MedicationsJson.ToLower().Contains(needle) ||
                        (r.FourDiagnosticsJson ?? "").ToLower().Contains(needle) ||
                        (r.Notes ?? "").ToLower().Contains(needle))
            .OrderByDescending(r => r.OccurredAt)
            .ThenByDescending(r => r.Id)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(ct);
    }

    /// <summary>
    /// 创建病历记录（挂到指定成员）。成员不存在或标题非法时返回 null。
    /// symptoms/diagnoses 为症状与诊断文本列表；medications 为用药条目；fourDiagnostics 为四诊结构化（可空）。
    /// </summary>
    public async Task<MedicalRecord?> CreateRecordAsync(
        int memberId, DateTime occurredAt, string title,
        IEnumerable<string> symptoms, IEnumerable<string> diagnoses,
        IEnumerable<MedicalMedicationItemDto> medications,
        string? notes, FourDiagnosticsDto? fourDiagnostics,
        CancellationToken ct = default)
    {
        var trimmedTitle = title?.Trim() ?? "";
        if (trimmedTitle.Length == 0 || trimmedTitle.Length > 200)
            return null;

        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var memberExists = await db.MedicalMembers.AnyAsync(m => m.Id == memberId, ct);
        if (!memberExists)
            return null;

        var record = new MedicalRecord
        {
            MemberId = memberId,
            OccurredAt = occurredAt.ToUniversalTime(),
            Title = trimmedTitle,
            SymptomsJson = SerializeStringList(symptoms),
            DiagnosesJson = SerializeStringList(diagnoses),
            MedicationsJson = SerializeMedications(medications),
            FourDiagnosticsJson = SerializeFourDiagnostics(fourDiagnostics),
            Notes = NormalizeNotes(notes)
        };
        db.MedicalRecords.Add(record);
        await db.SaveChangesAsync(ct);
        return record;
    }

    /// <summary>更新病历记录（字段可选；列表传 null 表示不修改，传空列表表示清空；fourDiagnostics 传 null 表示不修改）。不存在或标题非法时返回 null</summary>
    public async Task<MedicalRecord?> UpdateRecordAsync(
        int id, DateTime? occurredAt, string? title,
        List<string>? symptoms, List<string>? diagnoses,
        List<MedicalMedicationItemDto>? medications,
        string? notes, FourDiagnosticsDto? fourDiagnostics,
        CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var record = await db.MedicalRecords.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (record == null)
            return null;

        if (title != null)
        {
            var trimmed = title.Trim();
            if (trimmed.Length == 0 || trimmed.Length > 200)
                return null;
            record.Title = trimmed;
        }
        if (occurredAt.HasValue)
            record.OccurredAt = occurredAt.Value.ToUniversalTime();
        if (symptoms != null)
            record.SymptomsJson = SerializeStringList(symptoms);
        if (diagnoses != null)
            record.DiagnosesJson = SerializeStringList(diagnoses);
        if (medications != null)
            record.MedicationsJson = SerializeMedications(medications);
        if (fourDiagnostics != null)
            record.FourDiagnosticsJson = SerializeFourDiagnostics(fourDiagnostics);
        if (notes != null)
        {
            var normalized = NormalizeNotes(notes);
            if (normalized == null && !string.IsNullOrWhiteSpace(notes))
                return null;
            record.Notes = normalized;
        }

        await db.SaveChangesAsync(ct);
        return record;
    }

    /// <summary>删除病历记录。不存在时返回 false</summary>
    public async Task<bool> DeleteRecordAsync(int id, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var record = await db.MedicalRecords.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (record == null)
            return false;
        db.MedicalRecords.Remove(record);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ============ AI 诊断 ============

    /// <summary>按成员获取 AI 诊断历史（按时间倒序，最多 50 条）</summary>
    public async Task<List<AiDiagnosis>> GetDiagnosesAsync(int memberId, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.AiDiagnoses
            .Where(d => d.MemberId == memberId)
            .OrderByDescending(d => d.CreatedAt)
            .Take(50)
            .ToListAsync(ct);
    }

    /// <summary>保存一条 AI 诊断记录</summary>
    public async Task<AiDiagnosis> SaveDiagnosisAsync(int memberId, string symptomText, string aiResponse, string modelUsed = "main", string? structuredResultJson = null, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var diagnosis = new AiDiagnosis
        {
            MemberId = memberId,
            SymptomText = symptomText?.Trim() ?? "",
            AiResponse = aiResponse ?? "",
            StructuredResultJson = structuredResultJson,
            ModelUsed = modelUsed ?? "main"
        };
        db.AiDiagnoses.Add(diagnosis);
        await db.SaveChangesAsync(ct);
        return diagnosis;
    }

    /// <summary>删除 AI 诊断记录。不存在时返回 false</summary>
    public async Task<bool> DeleteDiagnosisAsync(int id, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var diagnosis = await db.AiDiagnoses.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (diagnosis == null)
            return false;
        db.AiDiagnoses.Remove(diagnosis);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ============ JSON 序列化辅助 ============

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null
    };

    private static string? SerializeFourDiagnostics(FourDiagnosticsDto? fd)
        => fd == null ? null : JsonSerializer.Serialize(fd, JsonOptions);

    public static FourDiagnosticsDto? DeserializeFourDiagnostics(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<FourDiagnosticsDto>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static string? SerializeConstitution(ConstitutionDto? c)
        => c == null ? null : JsonSerializer.Serialize(c, JsonOptions);

    public static ConstitutionDto? DeserializeConstitution(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ConstitutionDto>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static string SerializeList(IEnumerable<string> items)
        => JsonSerializer.Serialize(items?.Select(TrimItem).Where(x => x.Length > 0).ToList() ?? new List<string>(), JsonOptions);

    private static string SerializeStringList(IEnumerable<string> items)
        => SerializeList(items);

    private static string SerializeMedications(IEnumerable<MedicalMedicationItemDto> items)
    {
        var list = (items ?? Enumerable.Empty<MedicalMedicationItemDto>())
            .Select(m => new
            {
                Name = m.Name?.Trim() ?? "",
                Dosage = NormalizeNullable(m.Dosage),
                Frequency = NormalizeNullable(m.Frequency),
                Note = NormalizeNullable(m.Note),
                Ingredients = (m.Ingredients ?? new List<IngredientDto>())
                    .Select(i => new
                    {
                        Name = i.Name?.Trim() ?? "",
                        Dosage = NormalizeNullable(i.Dosage),
                        Note = NormalizeNullable(i.Note)
                    })
                    .Where(i => i.Name.Length > 0)
                    .ToList(),
                DecoctionMethod = NormalizeNullable(m.DecoctionMethod),
                Principle = NormalizeNullable(m.Principle),
                Course = NormalizeNullable(m.Course),
                Effect = NormalizeNullable(m.Effect)
            })
            .Where(m => m.Name.Length > 0)
            .ToList();
        return JsonSerializer.Serialize(list, JsonOptions);
    }

    /// <summary>反序列化过敏史/慢性病列表（容错：非法 JSON 返回空列表）</summary>
    public static List<string> DeserializeStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    /// <summary>反序列化用药列表（容错：非法 JSON 返回空列表）</summary>
    public static List<MedicalMedicationItemDto> DeserializeMedications(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<MedicalMedicationItemDto>();
        try
        {
            var items = JsonSerializer.Deserialize<List<MedicationJsonItem>>(json, JsonOptions) ?? new List<MedicationJsonItem>();
            return items
                .Where(m => !string.IsNullOrEmpty(m.Name))
                .Select(m => new MedicalMedicationItemDto
                {
                    Name = m.Name ?? "",
                    Dosage = m.Dosage,
                    Frequency = m.Frequency,
                    Note = m.Note,
                    Ingredients = m.Ingredients?
                        .Where(i => !string.IsNullOrEmpty(i.Name))
                        .Select(i => new IngredientDto { Name = i.Name ?? "", Dosage = i.Dosage, Note = i.Note })
                        .ToList(),
                    DecoctionMethod = m.DecoctionMethod,
                    Principle = m.Principle,
                    Course = m.Course,
                    Effect = m.Effect
                })
                .ToList();
        }
        catch (JsonException)
        {
            return new List<MedicalMedicationItemDto>();
        }
    }

    private sealed class MedicationJsonItem
    {
        public string Name { get; set; } = "";
        public string? Dosage { get; set; }
        public string? Frequency { get; set; }
        public string? Note { get; set; }
        public List<IngredientDto>? Ingredients { get; set; }
        public string? DecoctionMethod { get; set; }
        public string? Principle { get; set; }
        public string? Course { get; set; }
        public string? Effect { get; set; }
    }

    private static string TrimItem(string? s) => s?.Trim() ?? "";

    private static string? NormalizeNullable(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        var trimmed = s.Trim();
        return trimmed.Length > 200 ? trimmed[..200] : trimmed;
    }

    private static string NormalizeShort(string? s, int maxLength)
    {
        var trimmed = s?.Trim() ?? "";
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    private static string? NormalizeNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return null;
        var trimmed = notes.Trim();
        return trimmed.Length > 2000 ? trimmed[..2000] : trimmed;
    }
}
