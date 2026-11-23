using Markdig;

namespace MobileApp.Maui.Utils;

/// <summary>
/// 安全的 Markdown 渲染帮助类。
/// 统一使用禁用了原始 HTML 的 Markdig pipeline（DisableHtml），
/// 防止笔记/消息内容中的 HTML 以 MarkupString 注入页面造成 XSS。
/// </summary>
public static class SafeMarkdown
{
    private static readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    /// <summary>将 Markdown 渲染为禁用了原始 HTML 的 HTML 片段。</summary>
    public static string ToHtml(string? markdown) =>
        string.IsNullOrEmpty(markdown) ? "" : Markdown.ToHtml(markdown, _pipeline);
}
