using Microsoft.EntityFrameworkCore;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Text;
using Serilog;
using Baihua.Core;
using Baihua.Core.Services;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.AspNetCore.Localization;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

// 显式设置监听地址，确保命令行 --urls 和环境变量 ASPNETCORE_URLS 覆盖 appsettings 默认值
var urls = builder.Configuration["urls"]                                   // dotnet run --urls
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
    ?? builder.Configuration["Kestrel:Endpoints:Http:Url"]
    ?? "http://0.0.0.0:8791";
builder.WebHost.UseUrls(urls);

// 百花统一数据根目录 BAIHUA_HOME 由 Core.Shared.BaihuaPaths 管理
// 已迁移至 BAIHUA_HOME，详见 services/Baihua.Contracts/BaihuaPaths.cs


// 添加控制器与 JSON 序列化
builder.Services.AddLocalization();

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    })
    .ConfigureApiBehaviorOptions(options =>
    {
        // 禁用 [ApiController] 自动把 4xx/5xx 包装成 ProblemDetails
        // 本项目所有 API 统一使用 { error: "本地化错误消息" } 格式，ProblemDetails 会覆盖自定义 JSON
        options.SuppressMapClientErrors = true;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Baihua AI API",
        Version = "v1",
        Description = "Baihua AI Service - Models, Chat, Search, Metrics"
    });
});

// AI 域数据库上下文
builder.Services.AddDbContext<Baihua.Data.AIDbContext>(options =>
{
    options.UseNpgsql(Baihua.Data.DbConnections.For("ai"))
           .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
}, ServiceLifetime.Scoped, ServiceLifetime.Singleton);

builder.Services.AddDbContextFactory<Baihua.Data.AIDbContext>(options =>
{
    options.UseNpgsql(Baihua.Data.DbConnections.For("ai"))
           .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
}, ServiceLifetime.Singleton);

builder.Services.AddHttpClient();
builder.Services.AddMemoryCache(o => o.SizeLimit = 200 * 1024 * 1024); // 200MB TTS 音频缓存
// AI 服务是转发代理：不缓存推理响应（缓存由调用方 Family 承担），避免无限内存缓存 OOM
builder.Services.AddSingleton<Microsoft.Extensions.Caching.Distributed.IDistributedCache, Baihua.Core.Services.NoOpDistributedCache>();

// AI 配置与客户端基础设施（共享自 Core.Shared）
builder.Services.AddSingleton<AiSettingsService>();
// 任务分类模型指派（每类一个模型配置，JSON 持久化，聊天按分类路由）
builder.Services.AddSingleton<Baihua.Core.Services.AiCategorySettingsService>();
// 一服务一数据库：AI 服务是 ai.db 唯一持有者；IAiConfigService 映射到同一 AiConfigService 单例
builder.Services.AddSingleton<AiConfigService>();
builder.Services.AddSingleton<IAiConfigService>(sp => sp.GetRequiredService<AiConfigService>());
builder.Services.AddSingleton<MigrationService>();
builder.Services.AddSingleton<Baihua.Core.Security.DataEncryptionService>();
builder.Services.AddSingleton<Baihua.Core.Notifications.WebUINotificationService>();
builder.Services.AddSingleton<Baihua.Core.Services.HardwareInfoService>();
builder.Services.AddSingleton<Baihua.Core.Services.CapabilityService>();
builder.Services.AddAiClientServices();


// OVMS（OpenVINO Model Server）端点：统一承载 LLM 文本/视觉/嵌入推理
builder.Services.Configure<Baihua.AI.Provider.OpenVino.OmsOptions>(
    builder.Configuration.GetSection("OpenVinoOms"));

// 健康检查
builder.Services.AddHealthChecks()
    .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy());

// CORS（仅本地/内网）
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.SetIsOriginAllowed(origin =>
            {
                if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                {
                    return uri.Host is "localhost" or "127.0.0.1" or "::1";
                }
                return false;
            })
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

// 日志
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

var serilogConfig = new Serilog.LoggerConfiguration()
    .MinimumLevel.Is(Serilog.Events.LogEventLevel.Information)
    .Enrich.WithProperty("Service", "Baihua.AI")
    .Filter.ByExcluding(e => e.Properties.ContainsKey("SourceContext") &&
        ((Serilog.Events.LogEventPropertyValue)e.Properties["SourceContext"]).ToString()
            .StartsWith("\"Microsoft.AspNetCore") ||
        ((Serilog.Events.LogEventPropertyValue)e.Properties["SourceContext"]).ToString()
            .StartsWith("\"System.Net.Http") ||
        ((Serilog.Events.LogEventPropertyValue)e.Properties["SourceContext"]).ToString()
            .StartsWith("\"Microsoft.EntityFrameworkCore"))
    .WriteTo.Console();

builder.Logging.AddSerilog(serilogConfig.CreateLogger(), dispose: true);
builder.Logging.SetMinimumLevel(builder.Environment.IsDevelopment() ? LogLevel.Debug : LogLevel.Information);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
builder.Logging.AddFilter("Baihua.AI", LogLevel.Information);

// OpenTelemetry 导出到 OpenObserve
var openobserveEnabled = builder.Configuration.GetValue<bool?>("OpenObserve:Enabled") ?? true;
var openobserveUrl = builder.Configuration["OpenObserve:WebUrl"] ?? "";
var openobserveUser = builder.Configuration["OpenObserve:User"] ?? "";
var openobservePass = builder.Configuration["OpenObserve:Password"] ?? "";

{
    var otelBuilder = builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService("Baihua.AI"));

    if (openobserveEnabled && !string.IsNullOrWhiteSpace(openobserveUrl))
    {
        var baseUrl = openobserveUrl.TrimEnd('/');
        var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{openobserveUser}:{openobservePass}"));
        var authHeader = $"Authorization=Basic {authValue}";
        var isDevelopment = builder.Environment.IsDevelopment();

        otelBuilder.WithMetrics(metrics =>
        {
            metrics.AddMeter("Baihua.AI")
                   .AddMeter("Microsoft.Extensions.AI")
                   .AddView("ai.latency_ms", new OpenTelemetry.Metrics.ExplicitBucketHistogramConfiguration
                   {
                       Boundaries = new double[] { 0, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 30000, 60000 }
                   })
                   .AddOtlpExporter(options =>
                   {
                       options.Endpoint = new Uri($"{baseUrl}/api/default/v1/metrics");
                       options.Protocol = OtlpExportProtocol.HttpProtobuf;
                       options.TimeoutMilliseconds = 30000;
                       options.Headers = authHeader;
                   });
        })
        .WithLogging(logging =>
        {
            logging.AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri($"{baseUrl}/api/default/v1/logs");
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
                options.TimeoutMilliseconds = 30000;
                options.Headers = authHeader;
            });
        })
        .WithTracing(tracing =>
        {
            tracing
                .AddSource("Baihua.AI")
                .AddSource("Microsoft.Extensions.AI")
                // 个人开发环境全量采样；如未来生产流量大再改 TraceIdRatioBasedSampler
                .SetSampler(new AlwaysOnSampler())
                .AddOtlpExporter(options =>
                {
                    options.Endpoint = new Uri($"{baseUrl}/api/default/v1/traces");
                    options.Protocol = OtlpExportProtocol.HttpProtobuf;
                    options.TimeoutMilliseconds = 30000;
                    options.Headers = authHeader;
                });
        });
    }
}

// AI 配置服务依赖（ApiKey 加密）
builder.Services.AddDataProtection();
builder.Services.AddSingleton<Baihua.Core.Security.ApiKeyProtectionService>();

// 反向代理头部转发：默认只信任 loopback 代理，其他网段经 BAIHUA_TRUSTED_PROXY_NETS 显式声明
builder.Services.Configure<ForwardedHeadersOptions>(options =>
    Baihua.Core.Security.AdminNetworkPolicy.ConfigureForwardedHeaders(options, builder.Configuration));

// 管理 API 允许访问的网段（默认空 = 仅 loopback；容器部署在 compose/k8s 中显式配置）
var adminAllowedNets = Baihua.Core.Security.AdminNetworkPolicy.ParseNets(
    builder.Configuration[Baihua.Core.Security.AdminNetworkPolicy.AdminAllowedNetsEnv]);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Baihua AI API V1");
        c.RoutePrefix = "swagger";
    });
}

app.UseForwardedHeaders();
app.UseHealthChecks("/health");
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture("zh-CN"),
    SupportedCultures = new[] { new CultureInfo("zh-CN"), new CultureInfo("en") },
    SupportedUICultures = new[] { new CultureInfo("zh-CN"), new CultureInfo("en") }
});

app.UseCors("AllowAll");

// 访问控制：非公开路径仅允许 loopback
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();

    var publicPaths = new[] { "/health", "/swagger" };
    if (publicPaths.Any(p => path.StartsWith(p)))
    {
        await next();
        return;
    }

    var remoteIp = context.Connection.RemoteIpAddress;
    // 仅 loopback 或 BAIHUA_ADMIN_ALLOWED_NETS 显式放行网段可访问（容器部署需显式配置）
    if (Baihua.Core.Security.AdminNetworkPolicy.IsAllowed(remoteIp, adminAllowedNets))
    {
        await next();
        return;
    }

    logger.LogWarning("[AccessControl] Blocked non-loopback request to AI API from {RemoteIP}: {Path}",
        remoteIp?.ToString(), path);
    context.Response.StatusCode = 403;
    await context.Response.WriteAsJsonAsync(new { error = "AI Management API only allows local access." });
});

app.UseAuthorization();
app.MapControllers();

// 执行 AI 数据库迁移与 API Key 加密密钥迁移
var logger = app.Services.GetRequiredService<ILogger<Program>>();

// 先迁移 API Key（不依赖 Migrate，Migrate 可能因表已存在而失败）
try
{
    using var scope = app.Services.CreateScope();
    var aiDb = scope.ServiceProvider.GetRequiredService<Baihua.Data.AIDbContext>();
    var migrationService = scope.ServiceProvider.GetRequiredService<MigrationService>();
    migrationService.MigrateApiKeysIfNeeded(aiDb);
}
catch (Exception ex)
{
    logger.LogWarning(ex, "API Key 迁移失败（不影响启动）");
}

// 建库建表（PostgreSQL：EnsureCreated 生成完整 schema；不做迁移历史，schema 演进后续引入 Npgsql 迁移）
try
{
    using var scope = app.Services.CreateScope();
    var aiDb = scope.ServiceProvider.GetRequiredService<Baihua.Data.AIDbContext>();
    aiDb.Database.EnsureCreated();
    // 恢复的编程 Agent / AI 绘图表可能不存在于已有数据库，幂等建表
    aiDb.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "CodeAgentSessions" (
            "Id" SERIAL PRIMARY KEY,
            "CreatedAt" TIMESTAMP NOT NULL DEFAULT now(),
            "Prompt" VARCHAR(8000) NOT NULL,
            "Language" VARCHAR(100),
            "ProviderId" VARCHAR(50),
            "Model" VARCHAR(100),
            "ToolMode" VARCHAR(20) NOT NULL DEFAULT 'All',
            "IsPipeline" BOOLEAN NOT NULL DEFAULT FALSE,
            "PlanPro" BOOLEAN NOT NULL DEFAULT FALSE,
            "Output" TEXT,
            "Research" TEXT,
            "Code" TEXT,
            "Review" TEXT,
            "FileName" VARCHAR(300),
            "SessionStateJson" TEXT
        );
        CREATE INDEX IF NOT EXISTS "IX_CodeAgentSessions_CreatedAt" ON "CodeAgentSessions" ("CreatedAt");
        CREATE INDEX IF NOT EXISTS "IX_CodeAgentSessions_IsPipeline" ON "CodeAgentSessions" ("IsPipeline");
        CREATE TABLE IF NOT EXISTS "ComfyArtworks" (
            "Id" SERIAL PRIMARY KEY,
            "CreatedAt" TIMESTAMP NOT NULL DEFAULT now(),
            "Kind" VARCHAR(10) NOT NULL,
            "Prompt" VARCHAR(2000) NOT NULL,
            "Model" VARCHAR(200) NOT NULL,
            "ParamsJson" TEXT NOT NULL DEFAULT '{}',
            "FileName" VARCHAR(300) NOT NULL,
            "Subfolder" VARCHAR(300) DEFAULT '',
            "FileType" VARCHAR(20) DEFAULT 'output',
            "PromptId" VARCHAR(64) NOT NULL,
            "IsSuccess" BOOLEAN NOT NULL DEFAULT TRUE,
            "ErrorMessage" VARCHAR(2000),
            "DurationSeconds" DOUBLE PRECISION NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS "IX_ComfyArtworks_CreatedAt" ON "ComfyArtworks" ("CreatedAt");
        CREATE INDEX IF NOT EXISTS "IX_ComfyArtworks_Kind" ON "ComfyArtworks" ("Kind");
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ComfyArtworks_PromptId" ON "ComfyArtworks" ("PromptId");
        """);
    logger.LogInformation("AI 数据库初始化完成");
}
catch (Exception ex)
{
    logger.LogWarning(ex, "AI 数据库初始化失败（不影响启动）");
}
logger.LogInformation("===========================================");
logger.LogInformation("Baihua.AI Service Starting...");
logger.LogInformation("Environment: {Environment}", app.Environment.EnvironmentName);
logger.LogInformation("Health: /health");
logger.LogInformation("===========================================");

try
{
    app.Run();
}
catch (Exception ex)
{
    try
    {
        var logger2 = app.Services.GetService<ILogger<Program>>();
        logger2?.LogCritical(ex, "Baihua.AI terminated unexpectedly");
    }
    catch { }
    throw;
}
