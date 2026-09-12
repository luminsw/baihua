using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Baihua.Modules.Family.Services
{
    /// <summary>
    /// 修复 wikilink 在 AI 拆分后丢失分类目录的问题：
    /// 例如从 [[桂枝汤]] 补全为 [[方剂/桂枝汤]]（保留 #header 与 |alias）。
    /// </summary>
    public static class WikiLinkRewriter
    {
        // 匹配 [[target]] 或 [[target|alias]]，其中 target 不包含 ']' 和 '|'。
        private static readonly Regex WikiLinkRegex = new(@"\[\[([^\]|]+?)(\|[^\]]+)?\]\]",
            RegexOptions.Compiled);

        public static string RewriteMissingCategoryLinks(
            string content,
            IReadOnlyDictionary<string, string> titleToPath)
        {
            if (string.IsNullOrWhiteSpace(content) || titleToPath.Count == 0)
                return content;

            return WikiLinkRegex.Replace(content, match =>
            {
                var rawTarget = match.Groups[1].Value?.Trim() ?? string.Empty;
                var aliasSuffix = match.Groups[2].Success ? match.Groups[2].Value : string.Empty; // includes leading '|'
                if (string.IsNullOrWhiteSpace(rawTarget))
                    return match.Value;

                // 已包含分类/目录路径的，跳过（例如：[[方剂/桂枝汤]]）
                var hashSplit = rawTarget.Split('#', 2);
                var pagePart = hashSplit[0].Trim();
                var headerPart = hashSplit.Length == 2 ? "#" + hashSplit[1] : string.Empty;

                // 规范化 pagePart 与 titleToPath 的 key 一致性
                pagePart = pagePart.Replace('\\', '/').Trim('/').Trim();

                if (pagePart.Contains('/'))
                    return match.Value;

                // only map when target page name equals a generated note title
                if (!titleToPath.TryGetValue(pagePart, out var targetPath) || string.IsNullOrWhiteSpace(targetPath))
                    return match.Value;

                // 目标路径已由 AtomNoteSplitter 规范化，确保返回的 wikilink 使用一致的格式
                return $"[[{targetPath}{headerPart}{aliasSuffix}]]";
            });
        }

        // Overload with logger to emit replacement events for diagnostics
        public static string RewriteMissingCategoryLinks(
            string content,
            IReadOnlyDictionary<string, string> titleToPath,
            ILogger? logger)
        {
            if (string.IsNullOrWhiteSpace(content) || titleToPath.Count == 0)
                return content;

            return WikiLinkRegex.Replace(content, match =>
            {
                var rawTarget = match.Groups[1].Value?.Trim() ?? string.Empty;
                var aliasSuffix = match.Groups[2].Success ? match.Groups[2].Value : string.Empty; // includes leading '|'
                if (string.IsNullOrWhiteSpace(rawTarget))
                    return match.Value;

                var hashSplit = rawTarget.Split('#', 2);
                var pagePart = hashSplit[0].Trim();
                var headerPart = hashSplit.Length == 2 ? "#" + hashSplit[1] : string.Empty;

                // normalize
                var normPage = pagePart.Replace('\\', '/').Trim('/').Trim();
                if (normPage.Contains('/'))
                    return match.Value;

                if (!titleToPath.TryGetValue(normPage, out var targetPath) || string.IsNullOrWhiteSpace(targetPath))
                    return match.Value;

                var replaced = $"[[{targetPath}{headerPart}{aliasSuffix}]]";
                logger?.LogDebug("Rewriting wikilink: '{Raw}' -> '{Replaced}'", rawTarget, replaced);

                return replaced;
            });
        }
    }
}

