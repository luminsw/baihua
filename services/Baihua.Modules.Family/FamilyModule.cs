using Baihua.AI.Provider;
using Baihua.Modules.Family.Services.Strategies;
using Baihua.Core;
using Baihua.Core.Hubs;
using Baihua.Core.Modules;
using Baihua.Core.Notifications;
using Baihua.Core.Security;
using Baihua.Core.Services;
using Baihua.Core.Services.Strategies;
using Baihua.Core.Time;
using Baihua.Core.WebSocket;
using Baihua.Modules.Family.Controllers.AI.Stages;
using Baihua.Modules.Family.Services;
using Baihua.Modules.Family.Services.ComputePool;
using Baihua.Modules.Family.Services.Medical;
using Baihua.Modules.Family.Services.ServerMessaging;
using Baihua.Modules.Family.Services.Todo;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Baihua.Modules.Family;

/// <summary>
/// 家庭模块装配（原先由独立的 Baihua.Family 服务在自己的 Program.cs 中注册）。
/// 共享基础设施（DbContext、TaskManager/DeviceService/VaultSettingsService、AI 客户端、缓存等）由宿主统一注册，
/// 本模块只注册自己的业务服务与后台任务。
/// </summary>
public sealed class FamilyModule : IBaihuaModule
{
    /// <inheritdoc />
    public string Name => "Family";

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        // === AI 调用与提示词（家庭域业务封装，底层 AiClientService 由宿主注册）===
        services.AddSingleton<DefaultPromptProvider>();
        services.AddSingleton<AiFunctionService>();
        services.AddSingleton<StockAdvisorService>();
        services.AddSingleton<TopicSuggestionService>();
        services.AddSingleton<FamilyBudgetService>();
        services.Configure<LocalAiOptions>(configuration.GetSection("LocalAI"));
        services.AddSingleton<UserActivityService>();
        services.AddSingleton<AiDetailSettingsService>();

        // === 绘图 ===
        services.AddHttpClient<ComfyUiClient>(client =>
        {
            // ComfyUI 生成：图片约 20-60s（含模型冷加载）、视频 3-5 分钟，默认 30s 硬超时不够
            client.Timeout = TimeSpan.FromMinutes(6);
        });
        services.AddSingleton<ComfyDrawService>();

        // === 知识库内容加工（笔记解析/卡片/RAG）===
        services.AddSingleton<NoteParser>();
        services.AddSingleton<CardRepository>();
        services.AddSingleton<AtomNoteSplitter>();
        services.AddSingleton<RagService>();
        services.AddSingleton<ChatMemoryService>();
        services.AddSingleton<MasterPromptBuilder>();
        services.AddSingleton<StageStrategyFactory>();
        services.AddHostedService<MasterDataRetentionService>();
        services.AddSingleton<AnkiCardGenerator>();
        services.AddSingleton<ITimeProvider, SystemTimeProvider>();

        // === 学习/成长 ===
        services.AddSingleton<DailyCardService>();
        services.AddSingleton<LearnerService>();
        services.AddSingleton<AchievementEngine>();
        services.AddSingleton<LeaderboardService>();
        services.AddSingleton<CheckinService>();
        services.AddSingleton<LeaderboardSettingsService>();
        services.AddSingleton<RewardService>();
        services.AddSingleton<QuizService>();
        services.AddHostedService<StudyRecordMigrationService>();

        // === 待办 / 病历本 ===
        services.AddSingleton<TodoService>();
        services.AddSingleton<TodoAiService>();
        services.AddSingleton<MedicalService>();
        services.AddSingleton<MedicalAiService>();

        // === 设备配对与实时推送 ===
        services.AddSingleton<DeviceWebSocketHub>();
        services.AddSingleton<LocalModelRegistryService>();
        services.AddSingleton<PairingService>();
        services.AddSingleton<IPairingStrategy, FamilyPairingStrategy>();
        services.AddSingleton<MobileContract.Services.IPairingService, Services.Adapters.MobileDeviceServiceAdapter>();
        services.AddSingleton<MobileContract.Admin.IDeviceAdminService, Services.Adapters.MobileDeviceServiceAdapter>();
        services.AddSingleton<MobileContract.Admin.IPushAdminService, Services.Adapters.MobileDeviceServiceAdapter>();

        // === 服务器互联 / 算力池 ===
        services.AddSingleton<ServerMessageService>();
        services.AddHostedService<ServerDiscoveryHostedService>();
        services.AddSingleton<ComputePoolService>();
        services.AddHostedService(sp => sp.GetRequiredService<ComputePoolService>());

        // === 备份 / 恢复 ===
        services.AddSingleton<RestoreService>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<DeviceBackupService>();

        // === 本地模型（OpenClaw / 本地 AI 配置）===
        services.AddSingleton<OpenClawConfigService>();
        services.AddSingleton<ILocalAiConfigService, LocalAiConfigService>();
        services.AddSingleton<IOpenClawTaskService, OpenClawTaskService>();
        services.AddSingleton<BenchmarkRepository>();

        // === 宿主级诊断 ==
        services.AddSingleton<SystemHealthService>();
        services.AddSingleton<LogSinkConfigService>();

        // === 后台任务 ===
        services.AddHostedService<TaskCleanupService>();
        services.AddHostedService<BackupSchedulerService>();
        services.AddHostedService<StartupOrchestratorHostedService>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // SignalR 集线器
        endpoints.MapHub<TaskProgressHub>("/hubs/task-progress");
        endpoints.MapHub<DeviceHub>("/hubs/devices");
        endpoints.MapHub<ServerMessageHub>("/hubs/server-messages");

        // 纯 WebSocket 端点（供移动端使用，无需 SignalR 协议）
        endpoints.Map("/ws/devices", async (HttpContext context, DeviceWebSocketHub hub, ServerAddressService sas) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            var deviceName = context.Request.Query["deviceName"].ToString();
            // 设备 id（客户端 ANDROID_ID/鸿蒙设备 id）：服务端据此定向推送设备状态事件（授权/拒绝/撤销）
            var deviceId = context.Request.Query["deviceId"].ToString();
            // 握手携带服务器自身身份（serverId/serverName），供移动端校验是否已添加过的服务器
            var settings = sas.GetSettings();
            var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            await hub.AcceptAsync(webSocket, deviceName, deviceId, settings.ServerInstanceId, settings.DisplayName);
        });
    }
}
