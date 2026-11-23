namespace MobileContract.VaultSync;

/// <summary>
/// 百花服务器上的知识库信息。
/// 来自 /mg/vaults 端点。
/// </summary>
public record VaultInfo(
    string Id,
    string Name,
    string Industry,
    string Source = "server");
