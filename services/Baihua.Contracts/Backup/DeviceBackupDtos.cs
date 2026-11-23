using System.Text.Json.Serialization;

namespace Baihua.Contracts.Backup;

/// <summary>
/// 移动端（花记）设备备份上传请求。
/// 花记将本地全量数据（笔记库 + 知识库文件等）打包为 ZIP，
/// Base64 编码后通过 HTTP JSON 上传到百花，由百花按设备归档保存。
///
/// 传输约定（与移动端 HMAC 签名体系一致）：
/// - 请求体为 JSON，签名中间件按现有 /mg/* 规则自动验签
/// - 不加密传输（家庭内网场景，由调用方决定）
/// </summary>
public class UploadDeviceBackupRequest
{
    /// <summary>设备唯一标识（应与 X-Device-Id 头一致，服务端以头为准）</summary>
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";

    /// <summary>建议文件名（可选，服务端会重命名为 huaji_backup_时间戳.zip）</summary>
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    /// <summary>ZIP 文件的 Base64 编码内容</summary>
    [JsonPropertyName("base64Data")]
    public string Base64Data { get; set; } = "";

    /// <summary>原始 ZIP 字节数（用于校验与展示，服务端以实际解码长度为准）</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public class UploadDeviceBackupResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("backupId")]
    public string? BackupId { get; set; }

    [JsonPropertyName("backupTime")]
    public DateTime? BackupTime { get; set; }

    [JsonPropertyName("fileSize")]
    public long FileSize { get; set; }
}

public class DeviceBackupInfo
{
    /// <summary>备份 ID（即服务器上的文件名）</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public class DeviceBackupListResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("backups")]
    public List<DeviceBackupInfo> Backups { get; set; } = new();
}

public class DeleteDeviceBackupResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}
