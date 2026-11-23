using System.Formats.Tar;

namespace Baihua.Modules.Family.Services.ComputePool;

/// <summary>
/// 模型 tar 传输助手（算力池 / 模型商店共用）：
/// 从对端 /mg/model-store/download/{name} 流式下载 tar 并解压到本地模型根。
/// 下载端点写入的 tar 条目带 "{modelName}/" 前缀，解压时自动剥掉一层，
/// 避免出现 root/{modelName}/{modelName}/... 的双重嵌套（导致运行时扫描不到模型）。
/// </summary>
public static class ModelTarTransfer
{
    /// <summary>
    /// 流式下载 tar 并解压到 destDir（先写 .pulling 临时目录，成功后再原子改名）。
    /// </summary>
    /// <param name="client">HttpClient（调用方负责超时配置）</param>
    /// <param name="url">tar 下载地址</param>
    /// <param name="token">X-Server-Token（可为空）</param>
    /// <param name="destDir">解压目标目录（不存在则创建；已存在视为冲突）</param>
    /// <param name="stripPrefix">需要剥掉的前缀（如 "qwen3.8-27b/"），null 表示不剥</param>
    /// <param name="ct">取消令牌</param>
    public static async Task<(bool ok, string? error)> DownloadAndExtractAsync(
        HttpClient client, string url, string? token, string destDir, string? stripPrefix, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(token))
                req.Headers.TryAddWithoutValidation("X-Server-Token", token);

            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return (false, $"来源返回 {(int)resp.StatusCode}");

            var tmp = destDir + ".pulling";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
            Directory.CreateDirectory(tmp);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            await using var tar = new TarReader(stream);
            TarEntry? entry;
            while ((entry = await tar.GetNextEntryAsync(copyData: false, ct)) != null)
            {
                if (entry.EntryType is not TarEntryType.RegularFile && entry.EntryType is not TarEntryType.V7RegularFile)
                    continue;

                var name = entry.Name.Replace('/', Path.DirectorySeparatorChar);
                // 剥掉下载端点写入的 "{modelName}/" 前缀，避免双重嵌套
                if (!string.IsNullOrEmpty(stripPrefix))
                {
                    var prefix = stripPrefix.TrimEnd('/') + Path.DirectorySeparatorChar;
                    if (name.StartsWith(prefix, StringComparison.Ordinal))
                        name = name[prefix.Length..];
                }
                if (name.Contains(".." + Path.DirectorySeparatorChar))
                    continue;

                var dest = Path.Combine(tmp, name);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                await using var outStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await using var inStream = entry.DataStream;
                if (inStream != null)
                    await inStream.CopyToAsync(outStream, ct);
            }

            Directory.Move(tmp, destDir);
            return (true, null);
        }
        catch (Exception ex)
        {
            try { if (Directory.Exists(destDir + ".pulling")) Directory.Delete(destDir + ".pulling", recursive: true); } catch { }
            return (false, ex.Message);
        }
    }
}
