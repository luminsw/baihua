namespace MobileContract.VaultSync;

/// <summary>
/// 服务端知识库文件清单响应。
/// 对应 /mg/manifest 端点返回结构。
/// </summary>
public record VaultManifestResponse(
    string? VaultId,
    string? VaultName,
    int? Cursor,
    IReadOnlyList<ManifestFile>? Files);

/// <summary>
/// 清单中的单个文件条目。
/// Op = "upsert" | "delete"
/// </summary>
public record ManifestFile(
    string Op,
    string RelPath,
    long? Mtime,
    long? Size,
    string? Sha256);
