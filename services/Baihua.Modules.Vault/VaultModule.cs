using Baihua.Core.Modules;
using Baihua.Core.Services;
using Baihua.Modules.Vault.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Baihua.Modules.Vault;

/// <summary>知识库模块装配（原先由独立的 Baihua.Vault 服务在自己的 Program.cs 中注册）。</summary>
public sealed class VaultModule : IBaihuaModule
{
    /// <inheritdoc />
    public string Name => "Vault";

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        // 检索/笔记读写：控制器与跨模块调用（MCP 工具）共用同一实现
        services.AddSingleton<IVaultQueryService, VaultQueryService>();

        // 知识库索引调度：合并为单进程后只在这里注册一次（原先靠"只有 Vault 服务注册"来避免重复索引）
        services.AddHostedService<VaultIndexSchedulerService>();
    }
}
