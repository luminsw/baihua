using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;
using Baihua.AI.Provider;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 原子笔记解析器：从 AI 返回中提取并解析 JSON 笔记数组
/// </summary>
public class NoteParser
{
    private readonly ILogger<NoteParser> _logger;
    private readonly IStringLocalizer<SharedResources> _loc;

    public NoteParser(ILogger<NoteParser> logger, IStringLocalizer<SharedResources> loc)
    {
        _logger = logger;
        _loc = loc;
    }

    public List<Note> ParseResult(string aiContent)
    {
        try
        {
            var normalized = ExtractJsonPayload(aiContent);
            var options = JsonHelper.CaseInsensitive;
            _logger.LogDebug("准备解析 JSON 片段，长度：{Len}", normalized.Length);
            var notes = JsonSerializer.Deserialize<List<Note>>(normalized, options) ?? new List<Note>();
            _logger.LogInformation("解析到 {Count} 条笔记", notes.Count);
            foreach (var note in notes)
            {
                _logger.LogDebug("解析笔记：Path={Path}, Title={Title}", note.Path, note.Title);
            }
            return notes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "解析 AI 返回失败：{Content}", aiContent.Substring(0, Math.Min(500, aiContent.Length)));
            throw new Exception(string.Format(_loc["NoteParser_JsonParseFailed"], ex.Message));
        }
    }

    private string ExtractJsonPayload(string text)
    {
        var raw = (text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(raw))
        {
            throw new Exception(_loc["NoteParser_EmptyResponse"]);
        }

        if (raw.StartsWith("```", StringComparison.Ordinal))
        {
            var lines = raw.Split('\n').ToList();
            if (lines.Count > 0 && lines[0].StartsWith("```", StringComparison.Ordinal))
            {
                lines.RemoveAt(0);
            }
            if (lines.Count > 0 && lines[^1].Trim().StartsWith("```", StringComparison.Ordinal))
            {
                lines.RemoveAt(lines.Count - 1);
            }
            raw = string.Join('\n', lines).Trim();
        }

        var start = raw.IndexOf('[');
        if (start < 0)
        {
            throw new Exception(_loc["NoteParser_NoArrayStart"]);
        }

        int depth = 0;
        int end = -1;
        for (int i = start; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (ch == '[') depth++;
            else if (ch == ']')
            {
                depth--;
                if (depth == 0)
                {
                    end = i;
                    break;
                }
            }
        }

        if (end < 0)
        {
            throw new Exception(_loc["NoteParser_NoArrayEnd"]);
        }

        var jsonPayload = raw.Substring(start, end - start + 1).Trim();
        jsonPayload = FixUnescapedCharsInJson(jsonPayload);
        return jsonPayload;
    }

    /// <summary>
    /// 修复 JSON 字符串中未正确转义的控制字符
    /// </summary>
    private static string FixUnescapedCharsInJson(string json)
    {
        if (string.IsNullOrEmpty(json)) return json;

        var result = new StringBuilder();
        bool inString = false;
        bool escapeNext = false;

        foreach (char c in json)
        {
            if (escapeNext)
            {
                result.Append(c);
                escapeNext = false;
                continue;
            }

            if (c == '\\')
            {
                result.Append(c);
                escapeNext = true;
                continue;
            }

            if (c == '"' && !escapeNext)
            {
                inString = !inString;
                result.Append(c);
                continue;
            }

            if (inString)
            {
                switch (c)
                {
                    case '\n': result.Append("\\n"); break;
                    case '\r': result.Append("\\r"); break;
                    case '\t': result.Append("\\t"); break;
                    case '\b': result.Append("\\b"); break;
                    case '\f': result.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                            result.Append($"\\u{(int)c:X4}");
                        else
                            result.Append(c);
                        break;
                }
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }
}
