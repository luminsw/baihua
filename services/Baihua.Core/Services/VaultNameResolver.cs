using System.Text.RegularExpressions;

namespace Baihua.Core.Services;

/// <summary>
/// 知识库名称解析服务
/// 统一管理知识库名称的安全化、目录推断和显示名称回退
/// </summary>
public class VaultNameResolver : IVaultNameResolver
{
    /// <summary>
    /// 将任意字符串转换为安全的目录名（去除文件系统非法字符）
    /// </summary>
    public string ToSafeDirectoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "未命名";

        // 去除 Windows/Linux 文件系统非法字符
        var safe = Regex.Replace(name.Trim(), @"[<>:""/\\|?*\x00-\x1f]", "_");
        // 去除首尾空格和点
        safe = safe.Trim('.', ' ');
        // 限制长度
        if (safe.Length > 100)
            safe = safe[..100];
        // 回退
        if (string.IsNullOrWhiteSpace(safe))
            safe = "未命名";

        return safe;
    }

    /// <summary>
    /// 获取唯一的目录路径。如果目标目录已存在，尝试追加序号
    /// </summary>
    public string GetUniqueDirectoryPath(string parentDir, string name)
    {
        var safeName = ToSafeDirectoryName(name);
        var targetPath = Path.Combine(parentDir, safeName);

        if (!Directory.Exists(targetPath))
            return targetPath;

        for (int i = 2; i <= 999; i++)
        {
            var candidate = Path.Combine(parentDir, $"{safeName}_{i}");
            if (!Directory.Exists(candidate))
                return candidate;
        }

        return Path.Combine(parentDir, $"{safeName}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
    }

    /// <summary>
    /// 从目录路径推断知识库名称
    /// 对于 mobile/{行业}/{名称}/ 结构，取最深层目录名
    /// 对于 local/{行业}/{名称}/ 结构，取最深层目录名
    /// </summary>
    public string InferNameFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "未命名";

        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? "未命名" : name;
    }

}
