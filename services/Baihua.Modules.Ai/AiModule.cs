using Baihua.Core.Modules;
using Baihua.Modules.Ai.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        services.AddScoped<AiProviderModelHealthService>();
        services.AddHttpClient("AiProviderModelHealth", client =>
            client.Timeout = TimeSpan.FromSeconds(3))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseProxy = false });
    }

    /// <inheritdoc />
    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
