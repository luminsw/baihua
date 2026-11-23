using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Baihua.Modules.Family.Controllers;

public partial class LogsController : ControllerBase
{
    private IActionResult HandleGetLogs(
        string? level,
        string? category,
        string? search,
        string? since,
        string? until,
        int lines,
        string? file)
    {
        lines = Math.Clamp(lines, 1, 1000);

        var logFile = !string.IsNullOrEmpty(file)
            ? Path.Combine(_logsDir, file)
            : Path.Combine(_logsDir, $"baihua-family-{DateTime.Now:yyyyMMdd}.log");

        if (!System.IO.File.Exists(logFile))
        {
            return Ok(new { total = 0, logs = Array.Empty<object>(), file = logFile });
        }

        var minLevel = ParseLevel(level);

        DateTime? sinceTime = null, untilTime = null;
        if (!string.IsNullOrEmpty(since) && DateTime.TryParse(since, out var st)) sinceTime = st;
        if (!string.IsNullOrEmpty(until) && DateTime.TryParse(until, out var ut)) untilTime = ut;

        var results = new List<Dictionary<string, JsonElement>>();

        var allLines = System.IO.File.ReadAllLines(logFile);
        for (var i = allLines.Length - 1; i >= 0 && results.Count < lines; i--)
        {
            var line = allLines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            Dictionary<string, JsonElement>? entry;
            try
            {
                entry = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line);
                if (entry == null) continue;
            }
            catch { continue; }

            if (minLevel != null && entry.TryGetValue("Level", out var entryLevel))
            {
                var entryLevelInt = LevelToInt(entryLevel.ValueKind == JsonValueKind.String ? entryLevel.GetString() ?? "" : "");
                if (entryLevelInt < minLevel.Value) continue;
            }

            if (!string.IsNullOrEmpty(category) && entry.TryGetValue("Cat", out var entryCat))
            {
                var catStr = entryCat.ValueKind == JsonValueKind.String ? entryCat.GetString() ?? "" : "";
                if (!catStr.StartsWith(category, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            if (!string.IsNullOrEmpty(search) && entry.TryGetValue("Msg", out var entryMsg))
            {
                var msgStr = entryMsg.ValueKind == JsonValueKind.String ? entryMsg.GetString() ?? "" : "";
                if (!msgStr.Contains(search, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            if (sinceTime != null && entry.TryGetValue("Ts", out var entryTs))
            {
                var tsStr = entryTs.ValueKind == JsonValueKind.String ? entryTs.GetString() ?? "" : "";
                if (DateTime.TryParse(tsStr, out var ts) && ts < sinceTime.Value)
                    continue;
            }
            if (untilTime != null && entry.TryGetValue("Ts", out var entryTs2))
            {
                var tsStr2 = entryTs2.ValueKind == JsonValueKind.String ? entryTs2.GetString() ?? "" : "";
                if (DateTime.TryParse(tsStr2, out var ts2) && ts2 > untilTime.Value)
                    continue;
            }

            results.Add(entry);
        }

        results.Reverse();

        return Ok(new { total = results.Count, logs = results, file = Path.GetFileName(logFile) });
    }

    private static int? ParseLevel(string? level) => level?.ToUpperInvariant() switch
    {
        "TRCE" => 0,
        "DBG" or "DEBUG" => 1,
        "INFO" or "INFORMATION" => 2,
        "WARN" or "WARNING" => 3,
        "ERR" or "ERROR" => 4,
        "CRIT" or "CRITICAL" or "FATAL" => 5,
        _ => null
    };

    private static int LevelToInt(string level) => level.ToUpperInvariant() switch
    {
        "TRCE" => 0,
        "DBG" => 1,
        "INFO" => 2,
        "WARN" => 3,
        "ERR" => 4,
        "CRIT" => 5,
        _ => 2
    };
}
