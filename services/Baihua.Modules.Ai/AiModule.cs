using Baihua.Core.Modules;
using Baihua.Modules.Ai.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Baihua.Modules.Ai;

/// <summary>AI 模块装配（原先由独立的 Baihua.AI 服务在自己的 Program.cs 中注册）。</summary>
public sealed class AiModule : IBaihuaModule
{
    /// <inheritdoc />
    public string Name => "Ai";

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        // 绘图产物流水：实现归 AI 模块，接口暴露给宿主与其他模块
        services.AddSingleton<IComfyArtworkStore, ComfyArtworkStore>();

        // Embedding 配置读取：知识库模块的语义检索经接口在进程内读取（不再 HTTP）
        services.AddSingleton<IEmbeddingConfigProvider, EmbeddingConfigProvider>();
    }

    /// <inheritdoc />
    public async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Baihua.Data.AIDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<AiModule>>();

        // 恢复备份带来的编程 Agent / 绘图表可能不存在于已有数据库，幂等建表。
        // 注意：EF 的 ExecuteSqlRaw 会把 {} 当作格式占位符，字面量必须写成 {{}}
        await db.Database.ExecuteSqlRawAsync("""
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
                "ParamsJson" TEXT NOT NULL DEFAULT '{{}}',
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
            """, cancellationToken);

        logger.LogInformation("AI 模块附加表结构就绪（CodeAgentSessions / ComfyArtworks）");
    }
}
