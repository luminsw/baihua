using System.Text.RegularExpressions;
using Xunit;

namespace Baihua.Family.Tests.Learning;

/// <summary>
/// FAM-32 静态契约红测试：手机响应式适配（CSS media queries）。
///
/// 验收标准覆盖（源码级契约，纯前端）：
///   - AC1  看板响应式：≤768px 第一屏 3 列 → 1 列堆叠
///   - AC2  打卡页响应式：7 天日历移动端不换行缩小
///   - AC3  导航折叠：移动端侧边栏折叠为汉堡菜单
///   - AC4  桌面不变：≥1025px 断点存在（锚）
///
/// 红测试方式（FAM-11 先例）：当前家庭页面无 media queries/汉堡菜单 → 红。
/// </summary>
public class Fam32ResponsiveContractTests
{
    private static readonly string WebRoot = Path.GetDirectoryName(
        Path.GetDirectoryName(RepoPath.FindUp(Path.Combine("services", "Baihua.Web", "Pages", "FamilyLanding.razor"))))!;

    private static string ReadPage(string relative)
    {
        var path = Path.Combine(WebRoot, relative);
        Assert.True(File.Exists(path), $"FAM-32 契约：{relative} 不存在（红）");
        return File.ReadAllText(path);
    }

    private static string ReadPages(params string[] relatives)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var r in relatives)
        {
            var path = Path.Combine(WebRoot, r);
            if (File.Exists(path)) sb.AppendLine(File.ReadAllText(path));
        }
        return sb.ToString();
    }

    private static readonly string NavSource = ReadPage(Path.Combine("Shared", "FamilyNavMenu.razor"));

    // ============ AC1：看板响应式（≤768px 1 列堆叠） ============

    [Fact]
    public void Dashboard_HasMobileBreakpoint_StackedColumns()
    {
        // ≤768px：第一屏"今日三件事"3 列 → 1 列堆叠（内容不溢出）
        var source = ReadPages("Pages/Dashboard.razor", "Components/DashboardFirstScreen.razor");
        var hasMobileQuery = source.Contains("768px", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(source, @"@media[^{]*max-width\s*:\s*768", RegexOptions.IgnoreCase);
        var hasStacked = source.Contains("grid-template-columns", StringComparison.OrdinalIgnoreCase) &&
            (source.Contains("1fr", StringComparison.OrdinalIgnoreCase) ||
             source.Contains("repeat(1", StringComparison.OrdinalIgnoreCase) ||
             Regex.IsMatch(source, @"grid-template-columns[^;]*1fr\s*;", RegexOptions.IgnoreCase));
        Assert.True(hasMobileQuery && hasStacked,
            "FAM-32-AC1：看板缺少移动端断点（≤768px 1 列堆叠）（红）");
    }

    // ============ AC2：打卡页响应式 ============

    [Fact]
    public void Checkin_Calendar_MobileLayout()
    {
        // ≤768px：7 天日历横向不换行、格缩小但可辨识
        var source = ReadPage("Pages/Checkin.razor");
        var hasMobileQuery = source.Contains("768px", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(source, @"@media[^{]*max-width\s*:\s*768", RegexOptions.IgnoreCase);
        Assert.True(hasMobileQuery,
            "FAM-32-AC2：打卡页缺少移动端断点（≤768px）（红）");
    }

    // ============ AC3：导航折叠（汉堡菜单） ============

    [Fact]
    public void Nav_HasMobileHamburger()
    {
        // ≤768px：侧边栏折叠为汉堡菜单，点击展开导航项
        var hasHamburger =
            NavSource.Contains("hamburger", StringComparison.OrdinalIgnoreCase) ||
            NavSource.Contains("汉堡", StringComparison.OrdinalIgnoreCase) ||
            NavSource.Contains("menu-btn", StringComparison.OrdinalIgnoreCase) ||
            NavSource.Contains("☰", StringComparison.OrdinalIgnoreCase) ||
            NavSource.Contains("\\u2630", StringComparison.OrdinalIgnoreCase);
        var hasMobileQuery = NavSource.Contains("768px", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(NavSource, @"@media[^{]*max-width\s*:\s*768", RegexOptions.IgnoreCase);
        Assert.True(hasHamburger && hasMobileQuery,
            "FAM-32-AC3：导航缺少移动端汉堡菜单（红）");
    }

    // ============ AC4：桌面不变（锚） ============

    [Fact]
    public void Desktop_Breakpoint_Exists()
    {
        // ≥1025px 桌面断点存在（保持现有布局，锚）
        var source = ReadPages("Pages/Dashboard.razor", "Pages/Checkin.razor", "Pages/FamilyLanding.razor");
        var hasDesktopQuery = source.Contains("1025px", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(source, @"@media[^{]*min-width\s*:\s*1025", RegexOptions.IgnoreCase);
        Assert.True(hasDesktopQuery,
            "FAM-32-AC4：缺少桌面断点（≥1025px）（红）");
    }

    // ============ AC5：平板适中（锚） ============

    [Fact]
    public void Tablet_Breakpoint_Exists()
    {
        // 平板 769-1024px 断点存在（介于手机和桌面）
        var source = ReadPages("Pages/Dashboard.razor", "Pages/Checkin.razor", "Pages/FamilyLanding.razor");
        var hasTabletQuery = source.Contains("769px", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("1024px", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(source, @"@media[^{]*min-width\s*:\s*769", RegexOptions.IgnoreCase);
        Assert.True(hasTabletQuery,
            "FAM-32-AC5：缺少平板断点（769-1024px）（红）");
    }
}
