using Baihua.Family.Tests.TestDoubles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Baihua.Family.Tests.Routing;

/// <summary>
/// 路由冲突回归测试。
///
/// 背景：合并前 ai / vault / family 是三个进程，同名路径（ASP.NET Core 路由**大小写不敏感**）
/// 天然互不干扰；合并为单进程后，例如 family 的 <c>api/AI/chat/stream</c> 与 AI 模块的
/// <c>api/ai/chat/stream</c> 会同时命中 → 运行时 AmbiguousMatchException（500），
/// 而这类冲突只在真实请求时才暴露。
///
/// 本测试在启动后枚举全部端点，锁定"同一 HTTP 方法 + 同一路由模板"只能出现一次。
/// 新增模块/控制器后若撞路由，这里会立刻变红。
/// </summary>
[CollectionDefinition("Routing", DisableParallelization = true)]
public class RoutingCollection { }

[Collection("Routing")]
public class NoAmbiguousRoutesTests
{
    [Fact]
    public void AllEndpoints_HaveUniqueMethodAndRoutePattern()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), "baihua-routing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempHome);
        var oldHome = Environment.GetEnvironmentVariable("BAIHUA_HOME");
        Environment.SetEnvironmentVariable("BAIHUA_HOME", tempHome);
        Baihua.Contracts.BaihuaPaths.Reset();

        try
        {
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseSetting("BAIHUA_SKIP_MUTEX", "true");
                    builder.UseSetting("BAIHUA_SKIP_ACCESS_CONTROL", "true");
                    builder.ConfigureServices(services =>
                    {
                        var dbPath = Path.Combine(tempHome, "baihua.db");
                        TestSqliteDb.ConfigureSqlite(services, dbPath, dbPath);
                    });
                });

            // 触发宿主构建（端点在此注册）
            using var _ = factory.CreateClient();

            var endpoints = factory.Services.GetServices<EndpointDataSource>()
                .SelectMany(ds => ds.Endpoints)
                .OfType<RouteEndpoint>()
                .ToList();

            Assert.NotEmpty(endpoints);

            var duplicates = endpoints
                .Select(e => new
                {
                    // 复数方法（如 GET|POST）也要逐个展开比较
                    Methods = e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods
                              ?? new[] { "*" },
                    Pattern = (e.RoutePattern.RawText ?? string.Empty).Trim('/'),
                    Endpoint = e.DisplayName ?? e.RoutePattern.RawText ?? "?"
                })
                .SelectMany(x => x.Methods.Select(m => new { Method = m.ToUpperInvariant(), x.Pattern, x.Endpoint }))
                .GroupBy(x => $"{x.Method} {x.Pattern}", StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => $"{g.Key}  ←  {string.Join(" | ", g.Select(x => x.Endpoint).Distinct())}")
                .ToList();

            Assert.True(duplicates.Count == 0,
                "存在路由冲突（单进程下会 AmbiguousMatchException）：\n  " + string.Join("\n  ", duplicates));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BAIHUA_HOME", oldHome);
            Baihua.Contracts.BaihuaPaths.Reset();
            try { Directory.Delete(tempHome, recursive: true); } catch { }
        }
    }
}
