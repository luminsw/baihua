namespace Baihua.Modules.Family.Services;

/// <summary>
/// 备份路径处理辅助方法
/// </summary>
public static class BackupPathHelper
{
    /// <summary>
    /// 将绝对路径转为相对于 basePath 的相对路径
    /// </summary>
    public static string? MakeRelativePath(string path, string? basePath)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(basePath))
            return null;

        try
        {
            var fullBase = Path.GetFullPath(basePath);
            var fullPath = Path.GetFullPath(path);

            if (fullPath.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
            {
                var relative = fullPath[fullBase.Length..].TrimStart('\\', '/');
                // 统一使用正斜杠，跨平台兼容
                return relative.Replace('\\', '/');
            }
        }
        catch { /* 路径解析失败，回退返回 null */ }

        return null;
    }

    /// <summary>
    /// 跨平台路径重映射
    /// Windows D:\Vaults\头疼 → Linux /home/user/vaults/头疼
    /// </summary>
    public static string RemapPath(string originalPath)
    {
        if (string.IsNullOrEmpty(originalPath))
            return originalPath;

        // 如果路径在当前系统有效，直接返回
        if (Directory.Exists(originalPath) || File.Exists(originalPath))
            return originalPath;

        // 尝试常见路径映射
        // Windows → Linux: D:\path → /mnt/d/path (WSL) 或保持原样
        // Linux → Windows: /home/user/path → C:\home\user\path

        // 无法自动映射，返回原路径让用户手动调整
        return originalPath;
    }

    /// <summary>
    /// 递归复制目录
    /// </summary>
    public static void CopyDirectory(string sourceDir, string targetDir, bool overwrite = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(file);
            var targetFile = Path.Combine(targetDir, fileName);
            File.Copy(file, targetFile, overwrite);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dirName = Path.GetFileName(dir);
            var targetSubDir = Path.Combine(targetDir, dirName);
            CopyDirectory(dir, targetSubDir, overwrite, cancellationToken);
        }
    }
}
