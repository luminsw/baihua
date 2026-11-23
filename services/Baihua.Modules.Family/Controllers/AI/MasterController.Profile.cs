using Baihua.Core.Models;
using Baihua.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Baihua.Contracts.Ai;
using Baihua.Contracts.Master;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Modules.Family.Services;

namespace Baihua.Modules.Family.Controllers;

/// <summary>
/// 师父档案管理：创建、查看、更新、评估、列表、删除
/// </summary>
public partial class MasterController
{
    /// <summary>
    /// 创建师父（包含 AI 生成欢迎词和初始对话）
    /// </summary>
    [HttpPost("create")]
    public async Task<ActionResult<CreateMasterResponse>> Create([FromBody] CreateMasterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Goal))
            return BadRequest(new CreateMasterResponse { Success = false, Message = _loc["Master_GoalRequired"] });
        if (string.IsNullOrWhiteSpace(request.Industry))
            return BadRequest(new CreateMasterResponse { Success = false, Message = _loc["Master_IndustryRequired"] });

        try
        {
            var masterId = Guid.NewGuid().ToString("N");
            var masterName = _promptBuilder.ResolveMasterName(request.Industry);
            var outline = _promptBuilder.MatchExamOutline(request.Goal, request.Industry);
            var stages = _promptBuilder.GetStagesForOutline(outline);

            var (provider, model) = ResolveProviderAndModel(null, null);

            var outlineContext = _promptBuilder.GetOutlineContext(outline, "入道");
            var systemPrompt = _promptBuilder.BuildSystemPrompt(
                request.Goal, request.Industry, masterName, "入道", null, outlineContext);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, string.Format(_loc["Master_CreateGreetingTemplate"], request.Goal))
            };

            var options = AiClientService.BuildChatOptions(temperature: 0.7f, maxOutputTokens: 500);
            var response = await _aiClientService.GetChatResponseWithAutoStartAsync(
                provider, model, messages, options, HttpContext.RequestAborted, operation: "master-create");

            var greeting = response.Text ?? _loc["Master_CreateFallbackGreeting"];

            await using var db = await _dbFactory.CreateDbContextAsync();
            db.Masters.Add(new Master
            {
                MasterId = masterId,
                MasterName = masterName,
                Goal = request.Goal,
                Industry = request.Industry,
                CurrentStage = "入道",
                GraduatedStagesJson = "[]"
            });
            db.MasterConversations.Add(new MasterConversation
            {
                MasterId = masterId,
                Role = "assistant",
                Content = greeting,
                Stage = "入道"
            });
            await db.SaveChangesAsync();

            _logger.LogInformation("师父创建成功：{MasterName}（{Industry}），目标：{Goal}", masterName, request.Industry, request.Goal);

            return Ok(new CreateMasterResponse
            {
                Success = true,
                Message = greeting,
                MasterId = masterId,
                MasterName = masterName,
                Stages = stages
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建师父失败");
            var detail = UnwrapExceptionMessage(ex);
            return StatusCode(500, new CreateMasterResponse { Success = false, Message = string.Format(_loc["Master_CreateFailedDetail"], detail) });
        }
    }

    /// <summary>
    /// 获取师父列表（仅 active 状态，按创建时间倒序）
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<MasterListItem>>> List()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var masters = await db.Masters
            .Where(m => m.Status == "active")
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync();

        var items = masters.Select(m => new MasterListItem
        {
            MasterId = m.MasterId,
            MasterName = m.MasterName,
            Goal = m.Goal,
            Industry = m.Industry,
            CurrentStage = m.CurrentStage,
            CurrentStageOrder = GetStageOrder(m.CurrentStage),
            GraduatedStages = System.Text.Json.JsonSerializer.Deserialize<List<string>>(m.GraduatedStagesJson) ?? new(),
            CreatedAt = m.CreatedAt
        }).ToList();

        return Ok(items);
    }

    /// <summary>
    /// 删除师父（软删除：设置 Status="deleted"）
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest();

        await using var db = await _dbFactory.CreateDbContextAsync();
        var master = await db.Masters.FirstOrDefaultAsync(m => m.MasterId == id);
        if (master == null)
            return NotFound();

        master.Status = "deleted";
        await db.SaveChangesAsync();

        return Ok(new { Success = true });
    }

    /// <summary>
    /// 获取学徒画像
    /// </summary>
    [HttpGet("{id}/profile")]
    public async Task<ActionResult<ApprenticeProfileResponse>> GetProfile(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest(new ApprenticeProfileResponse { Success = false, Message = _loc["Master_IdRequired"] });

        await using var db = await _dbFactory.CreateDbContextAsync();
        var master = await db.Masters.FirstOrDefaultAsync(m => m.MasterId == id);
        if (master == null)
            return NotFound(new ApprenticeProfileResponse { Success = false, Message = _loc["Master_NotFound"] });

        var profile = await db.ApprenticeProfiles.FirstOrDefaultAsync(p => p.MasterId == id);
        var graduated = System.Text.Json.JsonSerializer.Deserialize<List<string>>(master.GraduatedStagesJson) ?? new();

        return Ok(new ApprenticeProfileResponse
        {
            Success = true,
            Message = _loc["Master_ProfileFetchSuccess"],
            MasterId = id,
            Goal = master.Goal,
            Foundation = profile?.Foundation,
            LearningStyle = profile?.LearningStyle,
            Strengths = profile?.Strengths,
            Weaknesses = profile?.Weaknesses,
            GraduatedStages = graduated,
            CurrentStage = master.CurrentStage,
            UpdatedAt = (profile?.UpdatedAt ?? master.UpdatedAt).ToString("yyyy-MM-dd HH:mm:ss")
        });
    }

    /// <summary>
    /// 更新学徒画像
    /// </summary>
    [HttpPut("{id}/profile")]
    public async Task<ActionResult<ApprenticeProfileResponse>> UpdateProfile(string id, [FromBody] UpdateProfileRequest request)
    {
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest(new ApprenticeProfileResponse { Success = false, Message = _loc["Master_IdRequired"] });

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var master = await db.Masters.FirstOrDefaultAsync(m => m.MasterId == id);
            if (master == null)
                return NotFound(new ApprenticeProfileResponse { Success = false, Message = _loc["Master_NotFound"] });

            var profile = await db.ApprenticeProfiles.FirstOrDefaultAsync(p => p.MasterId == id);
            if (profile == null)
            {
                profile = new ApprenticeProfile { MasterId = id };
                db.ApprenticeProfiles.Add(profile);
            }

            if (request.Foundation != null) profile.Foundation = request.Foundation;
            if (request.LearningStyle != null) profile.LearningStyle = request.LearningStyle;
            if (request.Strengths != null) profile.Strengths = request.Strengths;
            if (request.Weaknesses != null) profile.Weaknesses = request.Weaknesses;
            profile.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();

            var graduated = System.Text.Json.JsonSerializer.Deserialize<List<string>>(master.GraduatedStagesJson) ?? new();

            return Ok(new ApprenticeProfileResponse
            {
                Success = true,
                Message = _loc["Master_ProfileUpdateSuccess"],
                MasterId = id,
                Goal = master.Goal,
                Foundation = profile.Foundation,
                LearningStyle = profile.LearningStyle,
                Strengths = profile.Strengths,
                Weaknesses = profile.Weaknesses,
                GraduatedStages = graduated,
                CurrentStage = master.CurrentStage,
                UpdatedAt = profile.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss")
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新画像失败");
            return StatusCode(500, new ApprenticeProfileResponse { Success = false, Message = string.Format(_loc["Master_UpdateFailed"], ex.Message) });
        }
    }

    /// <summary>
    /// 能力评估
    /// </summary>
    [HttpPost("{id}/assess")]
    public async Task<ActionResult<AssessResponse>> Assess(string id, [FromBody] AssessRequest request)
    {
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest(new AssessResponse { Success = false, Message = _loc["Master_IdRequired"] });

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var master = await db.Masters.FirstOrDefaultAsync(m => m.MasterId == id);
            if (master == null)
                return NotFound(new AssessResponse { Success = false, Message = _loc["Master_NotFound"] });

            var (provider, model) = ResolveProviderAndModel(null, null);

            var assessPrompt = request.Type switch
            {
                "daily" => _loc["Master_AssessDaily"],
                "weekly" => _loc["Master_AssessWeekly"],
                "stage" => _loc["Master_AssessStage"],
                _ => _loc["Master_AssessDefault"]
            };

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, _loc["Master_AssessSystemPrompt"]),
                new(ChatRole.User, assessPrompt)
            };

            var options = AiClientService.BuildChatOptions(temperature: 0.3f, maxOutputTokens: 1000);
            var response = await _aiClientService.GetChatResponseWithAutoStartAsync(
                provider, model, messages, options, HttpContext.RequestAborted, operation: "master-assess");

            var result = response.Text ?? "";

            double passProbability = 0;
            var weakPoints = new List<string>();
            var advice = "";
            var report = result;

            try
            {
                var jsonStart = result.IndexOf('{');
                var jsonEnd = result.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var json = result.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("report", out var r))
                        report = r.GetString() ?? report;
                    if (doc.RootElement.TryGetProperty("passProbability", out var p))
                        passProbability = p.GetDouble();
                    if (doc.RootElement.TryGetProperty("weakPoints", out var w))
                        weakPoints = w.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
                    if (doc.RootElement.TryGetProperty("advice", out var a))
                        advice = a.GetString() ?? "";
                }
            }
            catch { }

            db.ExamCheckpoints.Add(new ExamCheckpoint
            {
                MasterId = id,
                StageName = master.CurrentStage,
                Score = 0,
                PassProbability = passProbability,
                WeakPointsJson = System.Text.Json.JsonSerializer.Serialize(weakPoints),
                Advice = advice
            });
            await db.SaveChangesAsync();

            return Ok(new AssessResponse
            {
                Success = true,
                Message = _loc["Master_AssessComplete"],
                Report = report,
                PassProbability = passProbability,
                WeakPoints = weakPoints,
                Advice = advice
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "能力评估失败");
            return StatusCode(500, new AssessResponse { Success = false, Message = string.Format(_loc["Master_AssessFailed"], ex.Message) });
        }
    }

    /// <summary>
    /// 获取知识库关联
    /// </summary>
    [HttpGet("{id}/vault-focus")]
    public async Task<ActionResult<VaultFocusListResponse>> GetVaultFocus(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest(new VaultFocusListResponse { Success = false, Message = _loc["Master_IdRequired"] });

        await using var db = await _dbFactory.CreateDbContextAsync();
        var master = await db.Masters.FirstOrDefaultAsync(m => m.MasterId == id);
        if (master == null)
            return NotFound(new VaultFocusListResponse { Success = false, Message = _loc["Master_NotFound"] });

        var focusStates = await db.VaultFocusStates
            .Where(v => v.MasterId == id && v.State == "focused")
            .OrderByDescending(v => v.UpdatedAt)
            .ToListAsync();

        var vaults = _vaultSettings.GetVaults();
        var vaultNameMap = vaults.ToDictionary(v => v.Id, v => v.Name);

        var items = focusStates.Select(v => new VaultFocusItem
        {
            VaultId = v.VaultId,
            VaultName = vaultNameMap.GetValueOrDefault(v.VaultId, _loc["Master_UnknownVault"]),
            State = v.State,
            StageName = v.StageName,
            UpdatedAt = v.UpdatedAt
        }).ToList();

        return Ok(new VaultFocusListResponse
        {
            Success = true,
            Message = _loc["Master_VaultFocusFetchSuccess"],
            Items = items
        });
    }

    /// <summary>
    /// 更新知识库关联
    /// </summary>
    [HttpPost("{id}/vault-focus")]
    public async Task<ActionResult<VaultFocusUpdateResponse>> UpdateVaultFocus(string id, [FromBody] VaultFocusUpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest(new VaultFocusUpdateResponse { Success = false, Message = _loc["Master_IdRequired"] });
        if (string.IsNullOrWhiteSpace(request.VaultId))
            return BadRequest(new VaultFocusUpdateResponse { Success = false, Message = _loc["Master_VaultIdRequired"] });

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var master = await db.Masters.FirstOrDefaultAsync(m => m.MasterId == id);
            if (master == null)
                return NotFound(new VaultFocusUpdateResponse { Success = false, Message = _loc["Master_NotFound"] });

            var existing = await db.VaultFocusStates
                .FirstOrDefaultAsync(v => v.MasterId == id && v.VaultId == request.VaultId);

            if (existing == null)
            {
                db.VaultFocusStates.Add(new VaultFocusState
                {
                    MasterId = id,
                    VaultId = request.VaultId,
                    State = request.State,
                    StageName = request.StageName,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            else
            {
                existing.State = request.State;
                existing.StageName = request.StageName;
                existing.UpdatedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync();

            _logger.LogInformation("师父 {MasterId} 知识库关联更新：{VaultId} -> {State}", id, request.VaultId, request.State);

            return Ok(new VaultFocusUpdateResponse
            {
                Success = true,
                Message = request.State == "focused" ? _loc["Master_VaultFocused"] : _loc["Master_VaultUnfocused"]
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "知识库关联更新失败");
            return StatusCode(500, new VaultFocusUpdateResponse { Success = false, Message = string.Format(_loc["Master_OperationFailed"], ex.Message) });
        }
    }

    /// <summary>
    /// 删除知识库关联
    /// </summary>
    [HttpDelete("{id}/vault-focus/{vaultId}")]
    public async Task<ActionResult<VaultFocusUpdateResponse>> RemoveVaultFocus(string id, string vaultId)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(vaultId))
            return BadRequest(new VaultFocusUpdateResponse { Success = false, Message = _loc["Master_ParamsRequired"] });

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var existing = await db.VaultFocusStates
                .FirstOrDefaultAsync(v => v.MasterId == id && v.VaultId == vaultId);

            if (existing == null)
                return NotFound(new VaultFocusUpdateResponse { Success = false, Message = _loc["Master_VaultFocusNotFound"] });

            existing.State = "archived";
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Ok(new VaultFocusUpdateResponse { Success = true, Message = _loc["Master_VaultFocusRemoved"] });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取消知识库关联失败");
            return StatusCode(500, new VaultFocusUpdateResponse { Success = false, Message = string.Format(_loc["Master_OperationFailed"], ex.Message) });
        }
    }
}
