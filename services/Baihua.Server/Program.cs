using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Threading.RateLimiting;
using Baihua.Contracts.Metrics;
using Baihua.Core;
using Baihua.Core.Hubs;
using Baihua.Core.Modules;
using Baihua.Core.Notifications;
using Baihua.Core.Security;
using Baihua.Core.Services;
using Baihua.Core.Services.Strategies;
using Baihua.Data;
using Baihua.Modules.Ai;
using Baihua.Modules.Family;
using Baihua.Modules.Family.Services;
using Baihua.Modules.Family.Services.Mcp;
using Baihua.Modules.Vault;
using Baihua.Server.Logging;
using Baihua.Server.Middleware;
using Baihua.Server.OpenTelemetry;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.AspNetCore;
using Serilog;

// =====================================================================================
// 百花服务器（Baihua.Server）—— 单一后端进程
//
// 合并前：ai(8791) / vault(8790) / family(8788) 三个服务 + 三个数据库 + 服务间 HTTP 互调。
// 合并后：一个进程（默认 8788）+ 一个数据库（baihua），业务按模块组织在 Baihua.Modules.*，
//        模块之间只经 Baihua.Core.Modules 下的接口在进程内直调。
// 本文件只负责：配置与日志/遥测管线、按模块装配依赖、映射端点。
// =====================================================================================

var builder = WebApplication.CreateBuilder(args);

// 单实例互斥：避免同机重复启动抢占端口（测试环境用 BAIHUA_SKIP_MUTEX 跳过）
Mutex? singleInstanceMutex = null;
if (!builder.Configuration.GetValue<bool>("BAIHUA_SKIP_MUTEX", false))
{
    var createdNew = false;
    try
    {
        singleInstanceMutex = new Mutex(true, "Baihua_Server_Mutex", out createdNew);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[FATAL] Mutex creation failed: {ex.Message}");
    }

    if (!createdNew)
    {
        Console.WriteLine("Another Baihua.Server instance is already running. Exiting to avoid port conflicts.");
        return;
    }
}

// 监听地址：命令行 --urls > ASPNETCORE_URLS > appsettings Kestrel > 默认 8788
var urls = builder.Configuration["urls"]
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
    ?? builder.Configuration["Kestrel:Endpoints:Http:Url"]
    ?? "http://0.0.0.0:8788";
builder.WebHost.UseUrls(urls);

// 模块清单（新增业务模块只需在此追加一行）
IBaihuaModule[] modules = [new FamilyModule(), new AiModule(), new VaultModule()];

// 需要"已配对设备"才能访问的知识库路径（移动端同步/下载/推送）。
// 合并前这些路径由 family 转发到 vault 时附加 Bearer Token，现改为进程内注入。
string[] vaultMobilePaths =
[
    "/mg/manifest", "/mg/file", "/mg/file_chunk", "/mg/cards",
    "/mg/vaults", "/mg/verify-token", "/mg/note-count",
    "/api/sync/",
    "/vault/manifest", "/vault/file", "/vault/file_chunk",
    "/mobile-vaults/push"
];

// ---------------- 控制器 / JSON / 全局异常 ----------------
builder.Services.AddLocalization();
builder.Services.AddControllers(options =>
    {
        options.Filters.Add<Baihua.Server.Filters.GlobalExceptionFilter>();
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    })
    .ConfigureApiBehaviorOptions(options =>
    {
        // 统一使用 { error: "中文错误消息" }，禁用 ProblemDetails 包装
        options.SuppressMapClientErrors = true;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Baihua API",
        Version = "v1",
        Description = "百花服务器（家庭 / AI / 知识库 三模块合一）"
    });
});

// SignalR：保持 PascalCase 载荷（WebUI 侧大小写不敏感消费）
builder.Services.AddSignalR()
    .AddJsonProtocol(options => options.PayloadSerializerOptions.PropertyNamingPolicy = null);

// ---------------- 单一数据库：三个模块各自的 DbContext ----------------
// 表结构由 Baihua.Data.DatabaseInitializer 统一初始化（一个库，多上下文）
builder.Services.AddDbContext<FamilyDbContext>(options =>
{
    options.UseNpgsql(DbConnections.Baihua)
           .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
}, ServiceLifetime.Scoped, ServiceLifetime.Singleton);
builder.Services.AddDbContextFactory<FamilyDbContext>(options =>
{
    options.UseNpgsql(DbConnections.Baihua)
           .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
}, ServiceLifetime.Singleton);

builder.Services.AddDbContext<VaultDbContext>(options =>
{
    options.UseNpgsql(DbConnections.Baihua)
           .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
}, ServiceLifetime.Scoped, ServiceLifetime.Singleton);
builder.Services.AddDbContextFactory<VaultDbContext>(options =>
{
    options.UseNpgsql(DbConnections.Baihua)
           .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
}, ServiceLifetime.Singleton);

builder.Services.AddDbContext<AIDbContext>(options =>
{
    options.UseNpgsql(DbConnections.Baihua)
           .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
}, ServiceLifetime.Scoped, ServiceLifetime.Singleton);
builder.Services.AddDbContextFactory<AIDbContext>(options =>
{
    options.UseNpgsql(DbConnections.Baihua)
           .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
}, ServiceLifetime.Singleton);

// ---------------- 共享服务（Core，各模块均可注入） ----------------
builder.Services.AddMemoryCache();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("WebUI", c => c.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient("OllamaLibrary", c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient("ComputePool");
builder.Services.AddHttpClient("SystemHealth", c =>
{
    c.Timeout = TimeSpan.FromSeconds(1);
    c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Baihua/1.0");
});

// AI 配置与密钥（API Key 只在本进程内加解密）
builder.Services.AddDataProtection();
builder.Services.AddSingleton<ApiKeyProtectionService>();
builder.Services.AddSingleton<DataEncryptionService>();
builder.Services.AddSingleton<AiSettingsService>();
builder.Services.AddSingleton<AiCategorySettingsService>();
builder.Services.AddSingleton<AiConfigService>();
builder.Services.AddSingleton<IAiConfigService>(sp => sp.GetRequiredService<AiConfigService>());
builder.Services.AddSingleton<MigrationService>();
builder.Services.AddAiClientServices();

// 家庭/知识库共享域服务
builder.Services.AddSingleton<TaskManager>();
builder.Services.AddSingleton<DeviceService>();
builder.Services.AddSingleton<ServerAddressService>();
builder.Services.AddSingleton<RequestSignatureService>();
builder.Services.AddSingleton<WebUINotificationService>();
builder.Services.AddSingleton<IVaultNameResolver, VaultNameResolver>();
builder.Services.AddSingleton<VaultSettingsService>();
builder.Services.AddSingleton<VaultNoteIndexer>();
builder.Services.AddSingleton<EmbeddingService>();
builder.Services.AddSingleton<SystemHealthService>();
builder.Services.AddSingleton<HardwareInfoService>();
builder.Services.AddSingleton<CapabilityService>();
builder.Services.AddSingleton<ServiceMetrics>();
builder.Services.AddSingleton<ISyncAuthorizationStrategy, FamilySyncAuthorizationStrategy>();

// 本地推理运行时（OVMS / OpenVINO）
builder.Services.Configure<Baihua.AI.Provider.OpenVino.OmsOptions>(
    builder.Configuration.GetSection("OpenVinoOms"));
builder.Services.AddSingleton<Baihua.AI.Provider.ILocalRuntimeManager, Baihua.AI.Provider.OpenVino.OpenVinoRuntimeManager>();

// ---------------- 模块装配 ----------------
foreach (var module in modules)
{
    module.RegisterServices(builder.Services, builder.Configuration, builder.Environment);
}

// ---------------- MCP（供 DSH / Claude Desktop / Cursor 等客户端） ----------------
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<BaihuaVaultTools>()
    .WithTools<BaihuaFamilyTools>()
    .WithTools<BaihuaLocalModelTools>();

// ---------------- 限流 / 健康检查 / CORS ----------------
// 配对码防暴力破解
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("pair", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromHours(1)
            }));
});

builder.Services.AddHealthChecks()
    .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy());

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.SetIsOriginAllowed(origin =>
            {
                if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                {
                    if (uri.Host is "localhost" or "127.0.0.1" or "::1")
                        return true;
                    // 局域网 IP（192.168.x.x / 10.x.x.x / 172.16-31.x.x）：移动端原生 WebSocket 会带 Origin
                    if (IsPrivateIpAddress(uri.Host))
                        return true;
                }
                // 无 Origin 头的原生请求（如 ArkTS webSocket）放行
                return string.IsNullOrEmpty(origin);
            })
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });
});

// ---------------- 日志 ----------------
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// 结构化 JSON Lines 文件日志（所有类别共享 Writer）
var logsDir = Path.Combine(builder.Environment.ContentRootPath ?? AppContext.BaseDirectory, "logs");
var fileLogMinLevel = builder.Environment.IsDevelopment() ? LogLevel.Debug : LogLevel.Information;
builder.Logging.AddProvider(new JsonLineLoggerProvider(
    logsDir, "baihua-server", retentionDays: 7,
    globalMinimumLevel: fileLogMinLevel,
    categoryFilters: new Dictionary<string, LogLevel>
    {
        { "Microsoft.AspNetCore", LogLevel.Warning },
        { "System.Net.Http", LogLevel.Warning },
        { "Microsoft.EntityFrameworkCore", LogLevel.Warning },
        { "Microsoft.Extensions.Http", LogLevel.Warning },
        { "Baihua", LogLevel.Information },
    }));

builder.Logging.SetMinimumLevel(builder.Environment.IsDevelopment() ? LogLevel.Debug : LogLevel.Information);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

var serilogConfig = new Serilog.LoggerConfiguration()
    .MinimumLevel.Is(Serilog.Events.LogEventLevel.Information)
    .Enrich.WithProperty("Service", "Baihua.Server")
    .Filter.ByExcluding(e => e.Properties.ContainsKey("SourceContext") &&
        ((Serilog.Events.LogEventPropertyValue)e.Properties["SourceContext"]).ToString()
            .StartsWith("\"Microsoft.AspNetCore") ||
        ((Serilog.Events.LogEventPropertyValue)e.Properties["SourceContext"]).ToString()
            .StartsWith("\"System.Net.Http") ||
        ((Serilog.Events.LogEventPropertyValue)e.Properties["SourceContext"]).ToString()
            .StartsWith("\"Microsoft.EntityFrameworkCore"))
    .WriteTo.Console();
builder.Logging.AddSerilog(serilogConfig.CreateLogger(), dispose: true);

// OpenObserve 配置（结构化日志 + 指标 + 链路）
var logSinkConfig = new LogSinkConfigService(
    Microsoft.Extensions.Logging.LoggerFactory.Create(b => { }).CreateLogger<LogSinkConfigService>());
var ooConfig = logSinkConfig.GetConfig();
if (!string.IsNullOrEmpty(builder.Configuration["OpenObserve:WebUrl"])) ooConfig.WebUrl = builder.Configuration["OpenObserve:WebUrl"]!;
if (!string.IsNullOrEmpty(builder.Configuration["OpenObserve:User"])) ooConfig.User = builder.Configuration["OpenObserve:User"]!;
if (!string.IsNullOrEmpty(builder.Configuration["OpenObserve:Password"])) ooConfig.Password = builder.Configuration["OpenObserve:Password"]!;
builder.Services.AddSingleton(logSinkConfig);

var openobserveEnabled = builder.Configuration.GetValue<bool?>("OpenObserve:Enabled") ?? true;
var ooBaseUrl = string.IsNullOrWhiteSpace(ooConfig.WebUrl) ? "http://localhost:5082" : ooConfig.WebUrl.TrimEnd('/');
builder.Services.AddOpenObserveTelemetry(
    serviceName: "Baihua.Server",
    meterNames: new[] { AiMetricsService.MeterName, ServiceMetrics.MeterName },
    webUrl: ooBaseUrl,
    user: ooConfig.User,
    password: ooConfig.Password,
    enabled: openobserveEnabled,
    environmentName: builder.Environment.EnvironmentName);

// ---------------- 反向代理 / 管理网段 ----------------
builder.Services.Configure<ForwardedHeadersOptions>(options =>
    AdminNetworkPolicy.ConfigureForwardedHeaders(options, builder.Configuration));

var adminAllowedNets = AdminNetworkPolicy.ParseNets(
    builder.Configuration[AdminNetworkPolicy.AdminAllowedNetsEnv]);

var app = builder.Build();

// ---------------- 中间件管线 ----------------
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Baihua API V1");
        c.RoutePrefix = "swagger";
    });
}

app.UseForwardedHeaders();
app.UseCorrelationId();
app.UseServiceMetrics();
app.UseHealthChecks("/health");
app.UseRateLimiter();
app.UseCors("AllowAll");

// 本地化：仅中文（zh-CN）。固定 SupportedCultures 可确保浏览器发送 Accept-Language: en 也不切走中文
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture("zh-CN"),
    SupportedCultures = new[] { new CultureInfo("zh-CN") },
    SupportedUICultures = new[] { new CultureInfo("zh-CN") }
});

// 移动端请求签名验证（HMAC）
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    var signatureService = context.RequestServices.GetService<RequestSignatureService>();

    var mobileApiPaths = new[]
    {
        "/vault/manifest", "/vault/file", "/vault/file_chunk",
        "/api/vaults", "/vault/pair", "/pair",
        "/api/sync/notes", "/api/sync/system", "/api/sync",
        "/mobile-vaults/push",
        "/mg/manifest", "/mg/file", "/mg/cards",
        "/mg/vaults", "/mg/pair",
        "/mg/devices/revoke",
        "/mg/device-backup",
        "/api/ai/chat"
    };

    // 初始化流程（设备注册、密钥获取、算力广播等）无需 HMAC 签名
    var publicApiPaths = new[]
    {
        "/mg/register-device",
        "/mg/auth/config",
        "/mg/capabilities",
        "/mg/ai/",
        "/mg/pool/",
        "/mg/benchmark/run",
        "/mg/model-store"
    };

    var isWebUiBrowse = path.Contains("/browse");
    var isPublicPath = publicApiPaths.Any(p => path.StartsWith(p));

    if (signatureService != null &&
        mobileApiPaths.Any(p => path.StartsWith(p)) &&
        !isWebUiBrowse &&
        !isPublicPath)
    {
        string? body = null;
        if (context.Request.ContentLength > 0 &&
            (context.Request.Method == "POST" || context.Request.Method == "PUT" || context.Request.Method == "PATCH"))
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
            body = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;
        }

        if (signatureService.IsConfigured)
        {
            var signatureHeader = context.Request.Headers["X-Mobile-Signature"].FirstOrDefault();
            if (!signatureService.VerifySignature(context.Request.Method, context.Request.Path + context.Request.QueryString, body, signatureHeader))
            {
                logger.LogWarning("[Signature] 签名校验失败: {Path}", path);
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "Invalid request signature" });
                return;
            }
        }
    }

    await next();
});

// 已配对移动端的设备授权（原 family→vault / family→ai 两个转发中间件的职责，合并后进程内完成）：
// - /api/ai/chat/*：必须来自已授权设备（HMAC 只证明"持有共享密钥"，不代表"已配对"）
// - 知识库同步/下载路径：把设备的 AccessToken 以 Bearer 形式注入请求，供知识库模块的
//   ISyncAuthorizationStrategy 校验（原实现靠转发时附加 Authorization 头）
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    var needsDeviceAuth = path.StartsWith("/api/ai/chat", StringComparison.OrdinalIgnoreCase);
    var isVaultSyncPath = vaultMobilePaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    if (!needsDeviceAuth && !isVaultSyncPath)
    {
        await next();
        return;
    }

    var deviceService = context.RequestServices.GetService<DeviceService>();
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    var deviceId = context.Request.Headers["X-Device-Id"].FirstOrDefault();

    if (string.IsNullOrEmpty(deviceId))
    {
        logger.LogWarning("[AUTH-DIAG] 拒绝未携带设备标识的请求: {Path}", path);
        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new { error = "Device identity missing. Please re-pair the device." });
        return;
    }

    var authorizedDevice = deviceService?.GetAuthorizedDeviceById(deviceId);
    if (authorizedDevice == null || string.IsNullOrEmpty(authorizedDevice.AccessToken))
    {
        logger.LogWarning("[AUTH-DIAG] 拒绝未授权设备 {DeviceId}: {Path}", deviceId, path);
        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new { error = "Device not authorized. Please complete pairing first." });
        return;
    }

    // 知识库路径：为下游授权策略注入 Bearer Token（进程内等价于原转发行为）
    if (isVaultSyncPath && !context.Request.Headers.ContainsKey("Authorization"))
    {
        context.Request.Headers.Authorization = "Bearer " + authorizedDevice.AccessToken;
    }

    await next();
});

// 访问控制：非公开路径仅允许本机 / BAIHUA_ADMIN_ALLOWED_NETS 放行网段
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();

    // 测试环境跳过 loopback 限制（TestServer 无真实 socket）
    if (builder.Configuration.GetValue<bool>("BAIHUA_SKIP_ACCESS_CONTROL", false))
    {
        await next();
        return;
    }

    var remoteIp = context.Connection.RemoteIpAddress;

    var publicPaths = new[]
    {
        "/health", "/api/health", "/swagger",
        "/ws/devices",
        "/vault/manifest", "/vault/file", "/vault/file_chunk",
        "/api/vaults", "/vault/pair", "/pair",
        "/api/sync/notes", "/api/sync/system", "/api/sync",
        "/api/discovery", "/mg/discovery",
        "/mobile-vaults/push",
        "/mg/vaults", "/mg/manifest", "/mg/file", "/mg/cards",
        "/mg/pair", "/mg/pair/check",
        "/mg/register-device",
        "/mg/auth/config", "/mg/verify-token",
        "/mg/devices/revoke",
        "/mg/device-backup",
        "/mg/server-msg/inbox",

        // 算力池：对端访问，各端点自校验（X-Server-Token / Bearer）
        "/mg/capabilities",
        "/mg/ai/",
        "/mg/pool/",
        "/mg/benchmark/run",
        "/mg/model-store",

        // AI 对话代理：已配对移动端经 HMAC 鉴权后访问
        "/api/ai/chat",
    };

    if (publicPaths.Any(p => path.StartsWith(p)))
    {
        await next();
        return;
    }

    if (AdminNetworkPolicy.IsAllowed(remoteIp, adminAllowedNets))
    {
        await next();
        return;
    }

    logger.LogWarning("[AccessControl] Blocked non-local request from {RemoteIP}: {Path}", remoteIp?.ToString(), path);
    context.Response.StatusCode = 403;
    await context.Response.WriteAsJsonAsync(new
    {
        error = "Admin API is restricted to local access only. Please use the WebUI."
    });
});

app.UseAuthorization();

// MCP /mcp 鉴权：回环 + 管理网段免鉴权，否则要求 BAIHUA_AI_EXTERNAL_TOKEN（Bearer / X-Server-Token / ?token=）
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/mcp"))
    {
        var remoteIp = context.Connection.RemoteIpAddress;
        var allowed = AdminNetworkPolicy.ParseNets(Environment.GetEnvironmentVariable(AdminNetworkPolicy.AdminAllowedNetsEnv));
        if (remoteIp is null || !AdminNetworkPolicy.IsAllowed(remoteIp, allowed))
        {
            var expected = context.RequestServices.GetRequiredService<IConfiguration>()["BAIHUA_AI_EXTERNAL_TOKEN"] ?? "";
            if (!string.IsNullOrEmpty(expected))
            {
                var ok = false;
                var auth = context.Request.Headers.Authorization.FirstOrDefault();
                if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    ok = string.Equals(auth["Bearer ".Length..].Trim(), expected, StringComparison.Ordinal);
                if (!ok)
                    ok = string.Equals(context.Request.Headers["X-Server-Token"].FirstOrDefault(), expected, StringComparison.Ordinal);
                if (!ok)
                {
                    var q = context.Request.Query["token"].FirstOrDefault() ?? context.Request.Query["x-server-token"].FirstOrDefault();
                    ok = string.Equals(q, expected, StringComparison.Ordinal);
                }
                if (!ok)
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
                    return;
                }
            }
        }
    }
    await next();
});

app.MapControllers();

// 百花 MCP streamable-http 端点（鉴权见上方中间件）
app.MapMcp("/mcp");

// 根路径健康检查（快速探活）
app.MapGet("/health", (ServerAddressService sas) =>
{
    var settings = sas.GetSettings();
    return Results.Ok(new
    {
        status = "healthy",
        timestamp = DateTime.UtcNow.ToString("o"),
        message = "Baihua Server is running",
        serverId = settings.ServerInstanceId,
        serverName = settings.DisplayName
    });
});

// WebSocket（SignalR 与移动端 /ws/devices 均需要），随后映射各模块端点
app.UseWebSockets();
foreach (var module in modules)
{
    module.MapEndpoints(app);
}

// ---------------- 数据库与模块初始化 ----------------
// 带重试：k8s/compose 下后端与 PostgreSQL 是并行启动的（没有启动顺序保证），
// 数据库晚就绪几秒就永久留下"没建表"的半残状态，因此启动期必须重试而不是一次性尝试。
var logger = app.Services.GetRequiredService<ILogger<Program>>();
{
    const int maxAttempts = 20;
    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var report = await DatabaseInitializer.InitializeAsync(scope.ServiceProvider, logger);
            logger.LogInformation("数据库 {Database} 初始化（第 {Attempt} 次尝试）：{Results}",
                DbConnections.DatabaseName, attempt, string.Join("；", report.Messages));

            // 三个上下文全军覆没 = 数据库还没就绪（连接被拒），重试；部分成功则记警告继续（避免死循环）
            if (report.AllFailed && attempt < maxAttempts)
            {
                var retryDelay = TimeSpan.FromSeconds(Math.Min(2 * attempt, 5));
                logger.LogWarning("数据库 {Database} 尚未就绪（第 {Attempt}/{Max} 次），{Delay}s 后重试",
                    DbConnections.DatabaseName, attempt, maxAttempts, retryDelay.TotalSeconds);
                await Task.Delay(retryDelay);
                continue;
            }

            // API Key 历史数据迁移（幂等）
            try
            {
                var aiDb = scope.ServiceProvider.GetRequiredService<AIDbContext>();
                scope.ServiceProvider.GetRequiredService<MigrationService>().MigrateApiKeysIfNeeded(aiDb);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "API Key 迁移失败（不影响启动）");
            }
            break;
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            var delay = TimeSpan.FromSeconds(Math.Min(2 * attempt, 5));
            logger.LogWarning("数据库初始化异常（第 {Attempt}/{Max} 次）：{Message}；{Delay}s 后重试",
                attempt, maxAttempts, ex.Message, delay.TotalSeconds);
            await Task.Delay(delay);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "数据库初始化失败（已重试 {Max} 次）", maxAttempts);
        }
    }
}

foreach (var module in modules)
{
    try
    {
        await module.InitializeAsync(app.Services);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "模块 {Module} 初始化失败", module.Name);
    }
}

// ---------------- 启动信息与生命周期 ----------------
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    try { logger.LogCritical(e.ExceptionObject as Exception, "Unhandled domain exception occurred"); } catch { }
};
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    try { logger.LogError(e.Exception, "Unobserved task exception"); e.SetObserved(); } catch { }
};

var startupMonitor = Baihua.Modules.Family.Services.StartupMonitor.Instance;
startupMonitor.RecordStartup();

logger.LogInformation("===========================================");
logger.LogInformation("Baihua Server Starting...（模块：{Modules}）", string.Join(", ", modules.Select(m => m.Name)));
logger.LogInformation("Start time: {StartTime}", startupMonitor.StartTime.ToString("yyyy-MM-dd HH:mm:ss"));
logger.LogInformation("PID: {ProcessId}", Environment.ProcessId);
logger.LogInformation("Content Root: {ContentRoot}", app.Services.GetRequiredService<IHostEnvironment>().ContentRootPath);
logger.LogInformation("Environment: {Environment}", app.Environment.EnvironmentName);
logger.LogInformation("Database: {Database}", DbConnections.DatabaseName);
logger.LogInformation("Health: /health    Swagger: /swagger    MCP: /mcp");
logger.LogInformation("===========================================");

app.Lifetime.ApplicationStarted.Register(() =>
{
    _ = Task.Run(async () =>
    {
        try
        {
            var healthService = app.Services.GetRequiredService<SystemHealthService>();
            var report = await healthService.GetHealthReportAsync();
            var healthMessage = report.Status == "healthy"
                ? $"Health: {report.HealthScore}%"
                : $"Health: {report.HealthScore}% (Issues: {string.Join(", ", report.Components.Where(c => c.Status != "healthy").Select(c => c.Name))})";
            logger.LogInformation("System Status: {Status} - {HealthMessage}", report.Status, healthMessage);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "后台启动自检未完成");
        }
    });
});

app.Lifetime.ApplicationStopping.Register(() => logger.LogInformation("Baihua Server Stopping..."));
app.Lifetime.ApplicationStopped.Register(() => logger.LogInformation("Baihua Server Stopped"));

try
{
    app.Run();
}
catch (Exception ex)
{
    try { app.Services.GetService<ILogger<Program>>()?.LogCritical(ex, "Host terminated unexpectedly"); } catch { }
    throw;
}
finally
{
    try
    {
        singleInstanceMutex?.ReleaseMutex();
        singleInstanceMutex?.Dispose();
    }
    catch { }
}

static bool IsPrivateIpAddress(string host)
{
    if (System.Net.IPAddress.TryParse(host, out var ip))
    {
        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 4)
        {
            if (bytes[0] == 10) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            if (bytes[0] == 127) return true;
        }
    }
    return false;
}
