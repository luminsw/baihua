using System.Text.RegularExpressions;
using Xunit;

namespace Baihua.Family.Tests.Learning;

/// <summary>
/// FAM-03 红测试：FamilyHome API Key 警报误报修复（源码级契约测试）。
///
/// 验收标准：
///   - 已开启百花服务器代理模式 → 首页不显示"API Key 未配置"红色警告
///   - aiStatus 查询失败（null）时不渲染为"未配置"（当前 `(!aiStatus?.IsConfigured ?? true)` 把故障伪装成配置问题）
///
/// 红测试方式：Blazor 页面逻辑无组件测试框架，用源码契约断言锁定验收行为。
/// 当前 FamilyHome.razor 含 `?? true` 的 null→未配置 兜底且不感知代理模式 → 红。
/// dev 修复后（引入代理感知判定 + 区分"未配置/查询失败"）→ 绿。
/// </summary>
public class Fam03ApiKeyAlertTests
{
    private static readonly string FamilyHomePath = RepoPath.FindUp(Path.Combine(
        "services", "Baihua.Web", "Pages", "FamilyHome.razor"));

    private static string ReadFamilyHomeSource()
    {
        return File.ReadAllText(FamilyHomePath);
    }

    [Fact]
    public void FamilyHome_ApiKeyAlert_MustNotRenderNullAsNotConfigured()
    {
        var source = ReadFamilyHomeSource();

        // 契约：aiStatus 为 null（查询失败）时不得渲染为"未配置"。
        // 当前源码 `(!aiStatus?.IsConfigured ?? true)` 的 `?? true` 兜底即该 bug → 红。
        // 修复后应改为三态：已配置 / 未配置 / 查询失败（null 时显示"服务状态获取失败"）。
        var nullAsNotConfigured = Regex.IsMatch(source,
            @"aiStatus\??\.IsConfigured\s*\?\?\s*true",
            RegexOptions.IgnoreCase);
        Assert.False(nullAsNotConfigured,
            "FAM-03：FamilyHome.razor 仍存在 null→未配置 兜底（`IsConfigured ?? true`）——aiStatus 查询失败会被伪造成'API Key 未配置'（红）");
    }

    [Fact]
    public void FamilyHome_ApiKeyAlert_MustBeAwareOfServerProxyMode()
    {
        var source = ReadFamilyHomeSource();

        // 契约：API Key 未配置判定必须感知百花服务器代理模式（AI-01 上线后代理模式无需 API Key）。
        // 当前源码无任何代理模式引用 → 红。
        var proxyAware = source.Contains("ServerProxy", StringComparison.OrdinalIgnoreCase)
            || source.Contains("UseServerProxy", StringComparison.OrdinalIgnoreCase)
            || source.Contains("UseServerAi", StringComparison.OrdinalIgnoreCase)
            || source.Contains("ServerAiMode", StringComparison.OrdinalIgnoreCase);
        Assert.True(proxyAware,
            "FAM-03：FamilyHome.razor 的 API Key 判定未感知服务器代理模式——开启百花代理的用户仍看到'未配置'红色警报（红）");
    }
}
