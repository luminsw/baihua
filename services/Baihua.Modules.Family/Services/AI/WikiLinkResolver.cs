using System.Text.RegularExpressions;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// WikiLink 解析器：从笔记内容中提取 wikilink 并解析为目标路径
/// </summary>
public static class WikiLinkResolver
{
    private static readonly Regex WikiLinkPattern = new(@"\[\[([^\]|#]+)", RegexOptions.Compiled);

    public static Dictionary<string, string> BuildTitleToPathMap(List<Note> notes, Dictionary<Note, string> notePathMap)
    {
        return notes
            .GroupBy(n => n.Title, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => notePathMap[g.Single()], StringComparer.OrdinalIgnoreCase);
    }

    public static List<string> ExtractWikiLinkTargets(List<Note> notes)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in notes)
        {
            if (string.IsNullOrEmpty(n.Content)) continue;
            foreach (Match m in WikiLinkPattern.Matches(n.Content))
            {
                var v = m.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(v)) set.Add(v);
            }
        }
        return set.ToList();
    }

    public static string? ResolveLinkToPath(string rawLink, Dictionary<string, string> titleToPath)
    {
        if (string.IsNullOrWhiteSpace(rawLink)) return null;
        var s = rawLink.Trim();
        if (s.Contains('/')) return s;
        if (titleToPath != null && titleToPath.TryGetValue(s, out var p)) return p;
        return null;
    }
}
