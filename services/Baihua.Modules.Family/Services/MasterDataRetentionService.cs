using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;

namespace Baihua.Modules.Family.Services;

public class MasterDataRetentionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MasterDataRetentionService> _logger;
    private readonly IStringLocalizer<SharedResources> _loc;

    public MasterDataRetentionService(IServiceScopeFactory scopeFactory, ILogger<MasterDataRetentionService> logger, IStringLocalizer<SharedResources> loc)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _loc = loc;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunRetentionAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "师父数据淘汰任务失败");
            }

            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }

    private async Task RunRetentionAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FamilyDbContext>>();
        var aiClientService = scope.ServiceProvider.GetRequiredService<AiClientService>();
        var aiSettings = scope.ServiceProvider.GetRequiredService<AiSettingsService>();
        var promptBuilder = scope.ServiceProvider.GetRequiredService<MasterPromptBuilder>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var activeMasters = await db.Masters.Where(m => m.Status == "active").ToListAsync(ct);

        foreach (var master in activeMasters)
        {
            var graduated = System.Text.Json.JsonSerializer.Deserialize<List<string>>(master.GraduatedStagesJson) ?? new();

            var compressCutoff = DateTime.UtcNow.AddDays(-7);
            foreach (var stage in graduated)
            {
                var existingSummary = await db.StageSummaries
                    .FirstOrDefaultAsync(s => s.MasterId == master.MasterId && s.StageName == stage, ct);
                if (existingSummary != null) continue;

                var conversations = await db.MasterConversations
                    .Where(c => c.MasterId == master.MasterId && c.Stage == stage && c.CreatedAt < compressCutoff)
                    .ToListAsync(ct);
                if (conversations.Count == 0) continue;

                try
                {
                    var (provider, model) = ResolveProviderAndModel(aiSettings, null, null);
                    var convText = string.Join("\n", conversations.Select(c => $"{c.Role}: {c.Content}"));
                    var summaryPrompt = $"请为以下对话生成简洁摘要（200字以内）：\n\n{(convText.Length > 3000 ? convText[..3000] + "..." : convText)}";
                    var messages = new List<Microsoft.Extensions.AI.ChatMessage>
                    {
                        new(Microsoft.Extensions.AI.ChatRole.System, "你是一位学习记录整理专家。"),
                        new(Microsoft.Extensions.AI.ChatRole.User, summaryPrompt)
                    };
                    var options = AiClientService.BuildChatOptions(temperature: 0.3f, maxOutputTokens: 500);
                    var response = await aiClientService.GetChatResponseWithAutoStartAsync(provider, model, messages, options, ct, operation: "master-bg-compress");

                    db.StageSummaries.Add(new Data.Entities.StageSummary
                    {
                        MasterId = master.MasterId,
                        StageName = stage,
                        Summary = response.Text ?? ""
                    });
                    db.MasterConversations.RemoveRange(conversations);
                    await db.SaveChangesAsync(ct);
                    _logger.LogInformation("后台压缩：师父 {MasterId} 阶段 {Stage}", master.MasterId, stage);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "后台压缩失败：师父 {MasterId} 阶段 {Stage}", master.MasterId, stage);
                }
            }

            var evictCutoff = DateTime.UtcNow.AddDays(-30);
            var profile = await db.ApprenticeProfiles.FirstOrDefaultAsync(p => p.MasterId == master.MasterId, ct);
            if (profile == null)
            {
                profile = new Data.Entities.ApprenticeProfile { MasterId = master.MasterId };
                db.ApprenticeProfiles.Add(profile);
            }

            foreach (var stage in graduated)
            {
                var summary = await db.StageSummaries
                    .FirstOrDefaultAsync(s => s.MasterId == master.MasterId && s.StageName == stage, ct);
                if (summary == null || summary.CreatedAt > evictCutoff) continue;

                try
                {
                    var (provider, model) = ResolveProviderAndModel(aiSettings, null, null);
                    var profilePrompt = $"根据阶段「{stage}」的摘要提取能力画像，JSON格式：{{\"foundation\":\"...\",\"learningStyle\":\"...\",\"strengths\":\"...\",\"weaknesses\":\"...\"}}\n\n摘要：{summary.Summary}";
                    var messages = new List<Microsoft.Extensions.AI.ChatMessage>
                    {
                        new(Microsoft.Extensions.AI.ChatRole.System, "你是学习评估专家。"),
                        new(Microsoft.Extensions.AI.ChatRole.User, profilePrompt)
                    };
                    var options = AiClientService.BuildChatOptions(temperature: 0.3f, maxOutputTokens: 500);
                    var response = await aiClientService.GetChatResponseWithAutoStartAsync(provider, model, messages, options, ct, operation: "master-bg-evict");

                    var result = response.Text ?? "";
                    try
                    {
                        var jsonStart = result.IndexOf('{');
                        var jsonEnd = result.LastIndexOf('}');
                        if (jsonStart >= 0 && jsonEnd > jsonStart)
                        {
                            var json = result.Substring(jsonStart, jsonEnd - jsonStart + 1);
                            var doc = System.Text.Json.JsonDocument.Parse(json);
                            if (doc.RootElement.TryGetProperty("foundation", out var f) && !string.IsNullOrEmpty(f.GetString()))
                                profile.Foundation = (profile.Foundation ?? "") + $"[{stage}] {f.GetString()}; ";
                            if (doc.RootElement.TryGetProperty("learningStyle", out var ls) && !string.IsNullOrEmpty(ls.GetString()))
                                profile.LearningStyle = ls.GetString();
                            if (doc.RootElement.TryGetProperty("strengths", out var s) && !string.IsNullOrEmpty(s.GetString()))
                                profile.Strengths = (profile.Strengths ?? "") + $"[{stage}] {s.GetString()}; ";
                            if (doc.RootElement.TryGetProperty("weaknesses", out var w) && !string.IsNullOrEmpty(w.GetString()))
                                profile.Weaknesses = (profile.Weaknesses ?? "") + $"[{stage}] {w.GetString()}; ";
                        }
                    }
                    catch { }

                    db.StageSummaries.Remove(summary);
                    var remainingConvs = await db.MasterConversations
                        .Where(c => c.MasterId == master.MasterId && c.Stage == stage)
                        .ToListAsync(ct);
                    db.MasterConversations.RemoveRange(remainingConvs);
                    await db.SaveChangesAsync(ct);
                    _logger.LogInformation("后台淘汰：师父 {MasterId} 阶段 {Stage}", master.MasterId, stage);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "后台淘汰失败：师父 {MasterId} 阶段 {Stage}", master.MasterId, stage);
                }
            }
        }
    }

    private (Baihua.Core.Models.AiProviderConfig Provider, string Model) ResolveProviderAndModel(
        AiSettingsService aiSettings, string? providerId, string? model)
    {
        var providers = aiSettings.GetAiProviders();
        var provider = string.IsNullOrEmpty(providerId)
            ? providers.FirstOrDefault(p => p.IsMain) ?? providers.FirstOrDefault()
            : providers.FirstOrDefault(p => p.Id == providerId);
        if (provider == null) throw new Exception(_loc["AiProvider_NotFound"]);
        var modelOptions = provider.GetModelOptions();
        var resolvedModel = !string.IsNullOrEmpty(model)
            ? model
            : modelOptions.FirstOrDefault(m => m.IsMain)?.Name ?? modelOptions.FirstOrDefault()?.Name ?? "Qwen/Qwen2.5-14B-Instruct";
        return (provider, resolvedModel);
    }
}
