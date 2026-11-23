using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Baihua.Core.Services;

/// <summary>
/// Vault FTS 索引文件快照持久化（JSON 文件）。
/// 快照仅是性能缓存：文件缺失/损坏时安全退化为整库重建，绝不抛异常给调用方。
/// </summary>
public static class VaultIndexSnapshotStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>默认快照文件路径（数据目录下）</summary>
    public static string DefaultFilePath =>
        Path.Combine(Baihua.Contracts.BaihuaPaths.Db, "vault-index-snapshots.json");

    /// <summary>
    /// 加载快照字典。文件缺失/损坏返回空字典（触发整库重建，安全）。
    /// </summary>
    public static Dictionary<string, Dictionary<string, NoteFileStamp>> Load(
        ILogger logger, string? filePath = null)
    {
        var path = filePath ?? DefaultFilePath;
        try
        {
            if (!File.Exists(path))
                return new Dictionary<string, Dictionary<string, NoteFileStamp>>(StringComparer.OrdinalIgnoreCase);

            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, NoteFileStamp>>>(json);
            return data ?? new Dictionary<string, Dictionary<string, NoteFileStamp>>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[IndexSnapshot] 快照文件加载失败（{Path}），将从整库重建开始", path);
            return new Dictionary<string, Dictionary<string, NoteFileStamp>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 保存快照字典。先写临时文件再原子替换，防止写一半损坏；失败仅告警（下次整库重建兜底）。
    /// </summary>
    public static void Save(
        Dictionary<string, Dictionary<string, NoteFileStamp>> snapshots,
        ILogger logger, string? filePath = null)
    {
        var path = filePath ?? DefaultFilePath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(snapshots, Options);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[IndexSnapshot] 快照文件保存失败（{Path}）", path);
        }
    }
}
