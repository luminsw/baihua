using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Baihua.Core.Modules;

/// <summary>
/// 百花模块契约（合并为单进程后的模块边界）。
///
/// 约定：
/// - 宿主 <c>Baihua.Server</c> 只依赖本接口按顺序装配模块，不直接 new 模块内部类型；
/// - 模块之间<b>禁止</b>互相引用实现（不得 using 另一个模块的命名空间），
///   跨模块能力一律经本命名空间下的接口（如 <see cref="IAiConfigService"/>、<see cref="IVaultQueryService"/>）
///   由宿主注入，实现类归各自模块所有——即接口隔离 + 依赖倒置；
/// - 模块自带自己的 DbContext/表结构，数据库只有一个（见 <c>Baihua.Data.DbConnections</c>）。
/// </summary>
public interface IBaihuaModule
{
    /// <summary>模块名（启动日志与诊断用）</summary>
    string Name { get; }

    /// <summary>注册模块自身的服务（DbContext、业务服务、后台任务、授权策略等）</summary>
    void RegisterServices(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment);

    /// <summary>映射模块自有的非控制器端点（SignalR Hub、WebSocket 等）；默认什么都不做</summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }

    /// <summary>模块自有的一次性启动工作（建表补丁、数据迁移等）；默认什么都不做</summary>
    Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
