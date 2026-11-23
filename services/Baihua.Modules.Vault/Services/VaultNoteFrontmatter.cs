namespace Baihua.Modules.Vault.Services;

/// <summary>笔记 YAML frontmatter 解析（标签 / AI 生成元信息）。</summary>
public static class VaultNoteFrontmatter
{
    /// <summary>解析结果</summary>
    public readonly record struct Result(
        List<string> Tags,
        bool AiGenerated,
        string? AiProvider,
        string? AiModel,
        DateTime? GeneratedAt);

    /// <summary>从 markdown 全文解析 frontmatter；无 frontmatter 时返回空结果</summary>
    public static Result Parse(string content)
    {
        var tags = new List<string>();
        var aiGenerated = false;
        string? aiProvider = null;
        string? aiModel = null;
        DateTime? generatedAt = null;

        if (string.IsNullOrEmpty(content) || !content.StartsWith("---"))
            return new Result(tags, aiGenerated, aiProvider, aiModel, generatedAt);

        var endIndex = content.IndexOf("---", 3);
        if (endIndex <= 0)
            return new Result(tags, aiGenerated, aiProvider, aiModel, generatedAt);

        var frontmatter = content.Substring(0, endIndex);
        var lines = frontmatter.Split('\n');

        foreach (var line in lines)
        {
            if (line.StartsWith("tags:"))
            {
                var tagPart = line.Substring(5).Trim();
                if (tagPart.StartsWith("["))
                {
                    var tagStr = tagPart.Trim('[', ']', ' ');
                    if (!string.IsNullOrWhiteSpace(tagStr))
                    {
                        tags.AddRange(tagStr.Split(',').Select(t => t.Trim().Trim('"', '\'')));
                    }
                }
            }
            else if (line.StartsWith("ai_generated:"))
            {
                var val = line.Substring("ai_generated:".Length).Trim();
                aiGenerated = val.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            else if (line.StartsWith("ai_provider:"))
            {
                aiProvider = line.Substring("ai_provider:".Length).Trim().Trim('"', '\'');
            }
            else if (line.StartsWith("ai_model:"))
            {
                aiModel = line.Substring("ai_model:".Length).Trim().Trim('"', '\'');
            }
            else if (line.StartsWith("generated_at:"))
            {
                var val = line.Substring("generated_at:".Length).Trim().Trim('"', '\'');
                if (DateTimeOffset.TryParse(val, out var dto))
                    generatedAt = dto.DateTime;
            }
        }

        return new Result(tags.Take(10).ToList(), aiGenerated, aiProvider, aiModel, generatedAt);
    }
}
