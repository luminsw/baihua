using Baihua.Core.Models;
using Baihua.Core.Services;
using System.Diagnostics;
using System.Text.RegularExpressions;
using ComponentStatus = Baihua.Contracts.Health.ComponentStatusDto;
using Baihua.Modules.Family.Models;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 健康检查辅助方法
/// </summary>
public static class HealthCheckHelper
{
    public static async Task<ComponentStatus> WithCheckDurationAsync(Func<Task<ComponentStatus>> check)
    {
        var sw = Stopwatch.StartNew();
        var result = await check().ConfigureAwait(false);
        sw.Stop();
        result.CheckDurationMs = sw.ElapsedMilliseconds;
        return result;
    }

    public static void TryKill(Process? process)
    {
        if (process is null || process.HasExited) return;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            /* ignore */
        }
    }

    public static bool IsLocalAiProvider(AiProviderConfig provider)
    {
        var name = provider.Name?.ToLowerInvariant() ?? "";
        return name.Contains("ollama") || name.Contains("local") || name.Contains("lmstudio");
    }

    public static string? ExtractVersion(string output)
    {
        var match = Regex.Match(output, @"(\d+\.\d+(?:\.\d+)?)");
        return match.Success ? match.Groups[1].Value : null;
    }
}
