namespace MobileContract.VaultSync;

/// <summary>
/// 移动端向百花推送 AI 生成知识库的请求体。
/// 对应 POST /mobile-vaults/push 端点。
/// </summary>
public record MobileVaultPushRequest(
    string VaultName,
    string Industry,
    MobileVaultNoteDto[] Notes);

public record MobileVaultNoteDto(
    string RelPath,
    string Title,
    string Content,
    string Category);
