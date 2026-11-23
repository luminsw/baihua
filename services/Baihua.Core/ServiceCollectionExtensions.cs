using Microsoft.Extensions.DependencyInjection;
using Baihua.Core.Services;

namespace Baihua.Core;

/// <summary>
/// Core.Shared 服务的 IServiceCollection 扩展方法
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 AI 客户端基础设施（AiClientService、AnthropicAiClient、LocalAiAutoStarter、AiMetricsService）
    /// </summary>
    public static IServiceCollection AddAiClientServices(this IServiceCollection services)
    {
        services.AddSingleton<AnthropicAiClient>();
        services.AddSingleton<LocalAiAutoStarter>();
        services.AddSingleton<AiMetricsService>();
        services.AddSingleton<AiClientService>();
        return services;
    }
}
