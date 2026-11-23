using MobileApp.Maui.Utils;

namespace MobileApp.Maui.Tests;

/// <summary>
/// SafeMarkdown 渲染安全性测试。
/// MarkdownView.razor 与 SearchPage/VaultsPage 的预览均通过 SafeMarkdown.ToHtml 渲染，
/// 断言原始 HTML（如 &lt;script&gt;）被转义，杜绝 MarkupString 注入 XSS。
/// </summary>
public class MarkdownRenderingTests
{
    [Fact]
    public void ToHtml_EscapesRawScriptTag()
    {
        var html = SafeMarkdown.ToHtml("<script>alert('xss')</script>");

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void ToHtml_EscapesInlineHtml()
    {
        var html = SafeMarkdown.ToHtml("hello <img src=x onerror=alert(1)> world");

        // 整段被当作纯文本转义，未解析为 HTML 标签
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;img", html);
    }

    [Fact]
    public void ToHtml_StillRendersMarkdown()
    {
        var html = SafeMarkdown.ToHtml("# 标题\n\n**加粗**");

        Assert.Contains("<h1", html);
        Assert.Contains("<strong>加粗</strong>", html);
    }

    [Fact]
    public void ToHtml_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal("", SafeMarkdown.ToHtml(null));
        Assert.Equal("", SafeMarkdown.ToHtml(""));
        Assert.Equal("", SafeMarkdown.ToHtml("   "));
    }
}
