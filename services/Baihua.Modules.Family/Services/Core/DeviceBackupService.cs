using Baihua.Contracts.Backup;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 移动端（花记）设备备份存储服务。
///
/// 花记 App 把本地全量数据（笔记库 SQLite + 知识库文件等）打包成 ZIP，
/// 通过 /mg/device-backup/* API 上传到百花存档；换机 / 重装 / 误删时下载恢复。
///
/// 存储布局（百花数据根 BAIHUA_HOME 下）：
///   $BAIHUA_HOME/device-backups/{deviceId}/
///     huaji_backup_{yyyyMMdd_HHmmss}.zip
///
/// 保留策略：每设备滚动保留最近 N 份（默认 7，配置 DeviceBackup:RetainCount），
/// 超出后自动删除最旧的备份，与百花自身备份策略一致。
/// </summary>
public class DeviceBackupService
{
    private readonly ILogger<DeviceBackupService> _logger;
    private readonly int _retainCount;
    private readonly long _maxBytes;

    public DeviceBackupService(IConfiguration configuration, ILogger<DeviceBackupService> logger)
    {
        _logger = logger;
        _retainCount = Math.Max(1, configuration.GetValue<int?>("DeviceBackup:RetainCount") ?? 7);
        _maxBytes = configuration.GetValue<long?>("DeviceBackup:MaxBytes") ?? 150L * 1024 * 1024;
    }

    /// <summary>
    /// 保存一份设备备份（Base64 解码后落盘），并清理过期备份。
    /// </summary>
    public async Task<UploadDeviceBackupResult> SaveAsync(string deviceId, string base64Data, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(base64Data))
        {
            return new UploadDeviceBackupResult { Success = false, Error = "备份内容为空" };
        }

        try
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64Data);
            }
            catch (FormatException)
            {
                return new UploadDeviceBackupResult { Success = false, Error = "备份内容不是合法的 Base64 数据" };
            }

            if (bytes.Length == 0)
            {
                return new UploadDeviceBackupResult { Success = false, Error = "备份内容为空" };
            }

            if (bytes.Length > _maxBytes)
            {
                return new UploadDeviceBackupResult
                {
                    Success = false,
                    Error = $"备份文件过大（{bytes.Length} 字节，上限 {_maxBytes} 字节）"
                };
            }

            var deviceDir = GetDeviceDir(deviceId);
            Directory.CreateDirectory(deviceDir);

            var timestamp = DateTime.UtcNow;
            // 毫秒精度避免同一秒内多次上传互相覆盖
            var fileName = $"huaji_backup_{timestamp:yyyyMMdd_HHmmss_fff}.zip";
            var fullPath = Path.Combine(deviceDir, fileName);

            await File.WriteAllBytesAsync(fullPath, bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            await CleanupOldAsync(deviceDir, cancellationToken);

            _logger.LogInformation("设备备份已保存: device={DeviceId} file={File} size={Size}",
                deviceId, fileName, bytes.Length);

            return new UploadDeviceBackupResult
            {
                Success = true,
                BackupPath = fullPath,
                BackupTime = timestamp,
                FileSize = bytes.Length
            };
        }
        catch (OperationCanceledException)
        {
            return new UploadDeviceBackupResult { Success = false, Error = "备份已取消" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存设备备份失败: device={DeviceId}", deviceId);
            return new UploadDeviceBackupResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>列出某设备的全部备份（新 → 旧）</summary>
    public List<DeviceBackupInfo> List(string deviceId)
    {
        var deviceDir = GetDeviceDir(deviceId);
        if (!Directory.Exists(deviceDir))
        {
            return new List<DeviceBackupInfo>();
        }

        return Directory.GetFiles(deviceDir, "huaji_backup_*.zip")
            .OrderByDescending(f => f)
            .Select(f => new DeviceBackupInfo
            {
                Id = Path.GetFileName(f),
                FileName = Path.GetFileName(f),
                CreatedAt = File.GetLastWriteTimeUtc(f),
                Size = new FileInfo(f).Length
            })
            .ToList();
    }

    /// <summary>
    /// 解析备份文件完整路径；ID 非法（含路径分隔符等）时返回 null。
    /// </summary>
    public string? ResolveFilePath(string deviceId, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var safeId = Path.GetFileName(id);
        if (safeId != id || !safeId.StartsWith("huaji_backup_", StringComparison.Ordinal) || !safeId.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var fullPath = Path.Combine(GetDeviceDir(deviceId), safeId);
        return File.Exists(fullPath) ? fullPath : null;
    }

    /// <summary>删除某设备的指定备份</summary>
    public bool Delete(string deviceId, string id)
    {
        var fullPath = ResolveFilePath(deviceId, id);
        if (fullPath == null)
        {
            return false;
        }

        try
        {
            File.Delete(fullPath);
            _logger.LogInformation("设备备份已删除: device={DeviceId} file={File}", deviceId, id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除设备备份失败: device={DeviceId} file={File}", deviceId, id);
            return false;
        }
    }

    /// <summary>清理过期备份：保留最近 <see cref="_retainCount"/> 份</summary>
    private Task CleanupOldAsync(string deviceDir, CancellationToken cancellationToken)
    {
        var files = Directory.GetFiles(deviceDir, "huaji_backup_*.zip")
            .OrderByDescending(f => f)
            .ToList();

        if (files.Count <= _retainCount)
        {
            return Task.CompletedTask;
        }

        foreach (var file in files.Skip(_retainCount))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Delete(file);
                _logger.LogInformation("过期设备备份已清理: {Path}", file);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "清理过期设备备份失败: {Path}", file);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>设备备份目录（deviceId 已做路径安全清洗）</summary>
    public static string GetDeviceDir(string deviceId)
    {
        var safeId = SanitizeDeviceId(deviceId);
        return Path.Combine(Baihua.Contracts.BaihuaPaths.Home, "device-backups", safeId);
    }

    private static string SanitizeDeviceId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return "unknown";
        }

        var sb = new System.Text.StringBuilder(deviceId.Length);
        foreach (var ch in deviceId)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_');
        }

        var result = sb.ToString();
        return string.IsNullOrWhiteSpace(result) ? "unknown" : result;
    }
}

public class UploadDeviceBackupResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? BackupPath { get; set; }
    public DateTime? BackupTime { get; set; }
    public long FileSize { get; set; }
}
