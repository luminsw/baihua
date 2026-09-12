namespace Baihua.Modules.Vault.Services;

/// <summary>笔记相对路径归一化（Windows/Linux 一致的正斜杠 + 去 notes/ 前缀）。</summary>
public static class VaultNotePath
{
    /// <summary>
    /// 统一分隔符为正斜杠，去掉 .md 后缀，并在需要时剔除 "notes/" 前缀。
    /// 这样前端分组规则（按 "症状/" 等字符串匹配）在 Windows/Linux 都一致。
    /// </summary>
    public static string NormalizeRelative(string raw, bool removeNotesPrefix)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        // raw 可能以 \ 或 / 开头（来自 file.Substring(vaultPath.Length)）
        var p = raw.Replace('\\', '/').Trim().TrimStart('/');

        if (p.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            p = p[..^3];
        }

        if (removeNotesPrefix && p.StartsWith("notes/", StringComparison.OrdinalIgnoreCase))
        {
            p = p.Substring("notes/".Length);
        }

        return p;
    }
}
