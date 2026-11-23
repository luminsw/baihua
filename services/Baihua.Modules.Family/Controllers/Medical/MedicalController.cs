using Microsoft.AspNetCore.Mvc;
using Baihua.Contracts.Medical;
using Baihua.Data.Entities;
using Baihua.Modules.Family.Services.Medical;

namespace Baihua.Modules.Family.Controllers.Medical;

/// <summary>
/// 家庭病历本 API：成员健康档案 + 病历记录（症状/诊断/用药）+ AI 诊断（仅作参考，不可代替医生）。
/// 单家庭、极简，无多用户概念。
/// </summary>
[ApiController]
[Route("api/medical")]
public class MedicalController : ControllerBase
{
    private readonly MedicalService _medicalService;
    private readonly MedicalAiService _medicalAiService;

    public MedicalController(MedicalService medicalService, MedicalAiService medicalAiService)
    {
        _medicalService = medicalService;
        _medicalAiService = medicalAiService;
    }

    // ============ 成员档案 ============

    /// <summary>获取全部家庭成员档案</summary>
    [HttpGet("members")]
    public async Task<ActionResult<List<MedicalMemberDto>>> GetMembers(CancellationToken ct)
    {
        var members = await _medicalService.GetMembersAsync(ct);
        return Ok(members.Select(ToMemberDto).ToList());
    }

    /// <summary>获取成员详情（档案 + 病历记录 + AI 诊断历史）</summary>
    [HttpGet("members/{id:int}")]
    public async Task<ActionResult<MedicalMemberDetailDto>> GetMemberDetail(int id, CancellationToken ct)
    {
        var member = await _medicalService.GetMemberAsync(id, ct);
        if (member == null)
            return NotFound(new { error = "家庭成员不存在" });

        var records = await _medicalService.GetRecordsAsync(id, ct);
        var diagnoses = await _medicalService.GetDiagnosesAsync(id, ct);

        return Ok(new MedicalMemberDetailDto
        {
            Member = ToMemberDto(member),
            Records = records.Select(ToRecordDto).ToList(),
            Diagnoses = diagnoses.Select(ToDiagnosisDto).ToList()
        });
    }

    /// <summary>创建家庭成员</summary>
    [HttpPost("members")]
    public async Task<ActionResult<MedicalMemberDto>> CreateMember([FromBody] CreateMedicalMemberRequest request, CancellationToken ct)
    {
        var member = await _medicalService.CreateMemberAsync(
            request?.Name ?? "", request?.Gender ?? "", request?.BirthDate,
            request?.BloodType ?? "", request?.Allergies ?? new List<string>(),
            request?.ChronicDiseases ?? new List<string>(), request?.Notes,
            request?.HeightCm, request?.WeightKg, request?.Occupation, request?.LifeHabits,
            request?.SportsInjuries ?? new List<string>(), request?.Constitution, ct);
        if (member == null)
            return BadRequest(new { error = "姓名不能为空或过长（最多 50 字）" });

        return Ok(ToMemberDto(member));
    }

    /// <summary>更新家庭成员档案</summary>
    [HttpPut("members/{id:int}")]
    public async Task<ActionResult<MedicalMemberDto>> UpdateMember(int id, [FromBody] UpdateMedicalMemberRequest request, CancellationToken ct)
    {
        if (request == null ||
            (request.Name == null && request.Gender == null && request.BirthDate == null &&
             request.BloodType == null && request.Allergies == null &&
             request.ChronicDiseases == null && request.Notes == null &&
             request.HeightCm == null && request.WeightKg == null &&
             request.Occupation == null && request.LifeHabits == null &&
             request.SportsInjuries == null && request.Constitution == null))
            return BadRequest(new { error = "至少需要提供一项要修改的字段" });

        var member = await _medicalService.UpdateMemberAsync(
            id, request.Name, request.Gender, request.BirthDate, request.BloodType,
            request.Allergies, request.ChronicDiseases, request.Notes,
            request.HeightCm, request.WeightKg, request.Occupation, request.LifeHabits,
            request.SportsInjuries, request.Constitution, ct);
        if (member == null)
            return NotFound(new { error = "家庭成员不存在或字段不合法" });

        return Ok(ToMemberDto(member));
    }

    /// <summary>删除家庭成员（级联删除其病历与 AI 诊断）</summary>
    [HttpDelete("members/{id:int}")]
    public async Task<IActionResult> DeleteMember(int id, CancellationToken ct)
    {
        var deleted = await _medicalService.DeleteMemberAsync(id, ct);
        return deleted ? NoContent() : NotFound(new { error = "家庭成员不存在" });
    }

    // ============ 病历记录 ============

    /// <summary>按关键词检索病历记录，limit 可选（默认 50，最大 200）</summary>
    [HttpGet("records/search")]
    public async Task<ActionResult<List<MedicalRecordDto>>> SearchRecords([FromQuery] string q, [FromQuery] int? limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { error = "q 不能为空" });
        var records = await _medicalService.SearchRecordsAsync(q.Trim(), limit ?? 50, ct);
        return Ok(records.Select(ToRecordDto).ToList());
    }

    /// <summary>创建病历记录（挂到指定成员）</summary>
    [HttpPost("members/{memberId:int}/records")]
    public async Task<ActionResult<MedicalRecordDto>> CreateRecord(int memberId, [FromBody] CreateMedicalRecordRequest request, CancellationToken ct)
    {
        var occurredAt = request?.OccurredAt ?? DateTime.UtcNow;

        var record = await _medicalService.CreateRecordAsync(
            memberId, occurredAt, request?.Title ?? "",
            request?.Symptoms ?? new List<string>(), request?.Diagnoses ?? new List<string>(),
            request?.Medications ?? new List<MedicalMedicationItemDto>(),
            request?.Notes, request?.FourDiagnostics, ct);
        if (record == null)
            return BadRequest(new { error = "标题不能为空或过长（最多 200 字），或成员不存在" });

        return Ok(ToRecordDto(record));
    }

    /// <summary>更新病历记录</summary>
    [HttpPut("records/{id:int}")]
    public async Task<ActionResult<MedicalRecordDto>> UpdateRecord(int id, [FromBody] UpdateMedicalRecordRequest request, CancellationToken ct)
    {
        if (request == null ||
            (request.OccurredAt == null && request.Title == null && request.Symptoms == null &&
             request.Diagnoses == null && request.Medications == null && request.Notes == null &&
             request.FourDiagnostics == null))
            return BadRequest(new { error = "至少需要提供一项要修改的字段" });

        var record = await _medicalService.UpdateRecordAsync(
            id, request.OccurredAt, request.Title,
            request.Symptoms, request.Diagnoses, request.Medications,
            request.Notes, request.FourDiagnostics, ct);
        if (record == null)
            return NotFound(new { error = "病历记录不存在或字段不合法" });

        return Ok(ToRecordDto(record));
    }

    /// <summary>删除病历记录</summary>
    [HttpDelete("records/{id:int}")]
    public async Task<IActionResult> DeleteRecord(int id, CancellationToken ct)
    {
        var deleted = await _medicalService.DeleteRecordAsync(id, ct);
        return deleted ? NoContent() : NotFound(new { error = "病历记录不存在" });
    }

    // ============ AI 诊断 ============

    /// <summary>AI 诊断：提交症状，AI 结合成员档案给出仅供参考的健康分析（自动落库）</summary>
    [HttpPost("diagnose")]
    public async Task<ActionResult<AiDiagnoseResultDto>> Diagnose([FromBody] AiDiagnoseRequest request, CancellationToken ct)
    {
        if (request == null)
            return BadRequest(new { error = "请求内容不能为空" });

        var result = await _medicalAiService.DiagnoseAsync(
            request.MemberId, request.SymptomText ?? "", request.ExtraContext, ct);
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    /// <summary>删除 AI 诊断记录</summary>
    [HttpDelete("diagnoses/{id:int}")]
    public async Task<IActionResult> DeleteDiagnosis(int id, CancellationToken ct)
    {
        var deleted = await _medicalService.DeleteDiagnosisAsync(id, ct);
        return deleted ? NoContent() : NotFound(new { error = "诊断记录不存在" });
    }

    // ============ DTO 映射 ============

    private static MedicalMemberDto ToMemberDto(MedicalMember member) => new()
    {
        Id = member.Id,
        Name = member.Name,
        Gender = member.Gender,
        BirthDate = member.BirthDate,
        BloodType = member.BloodType,
        Allergies = MedicalService.DeserializeStringList(member.AllergiesJson),
        ChronicDiseases = MedicalService.DeserializeStringList(member.ChronicDiseasesJson),
        Notes = member.Notes,
        HeightCm = member.HeightCm,
        WeightKg = member.WeightKg,
        Occupation = member.Occupation,
        LifeHabits = member.LifeHabits,
        SportsInjuries = MedicalService.DeserializeStringList(member.SportsInjuriesJson),
        Constitution = MedicalService.DeserializeConstitution(member.ConstitutionJson),
        CreatedAt = member.CreatedAt,
        UpdatedAt = member.UpdatedAt
    };

    private static MedicalRecordDto ToRecordDto(MedicalRecord record) => new()
    {
        Id = record.Id,
        MemberId = record.MemberId,
        OccurredAt = record.OccurredAt,
        Title = record.Title,
        Symptoms = MedicalService.DeserializeStringList(record.SymptomsJson),
        Diagnoses = MedicalService.DeserializeStringList(record.DiagnosesJson),
        Medications = MedicalService.DeserializeMedications(record.MedicationsJson),
        Notes = record.Notes,
        FourDiagnostics = MedicalService.DeserializeFourDiagnostics(record.FourDiagnosticsJson),
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt
    };

    private static AiDiagnosisDto ToDiagnosisDto(AiDiagnosis diagnosis) => new()
    {
        Id = diagnosis.Id,
        MemberId = diagnosis.MemberId,
        SymptomText = diagnosis.SymptomText,
        AiResponse = diagnosis.AiResponse,
        StructuredResultJson = diagnosis.StructuredResultJson,
        ModelUsed = diagnosis.ModelUsed,
        CreatedAt = diagnosis.CreatedAt
    };
}
