using BaihuaSdk.Storage;

namespace MobileApp.Maui.Services;

/// <summary>
/// IVaultStorageAdapter 的 MAUI 实现。
/// 基于 BaihuaSdk FileSystemVaultStorage，根目录使用应用私有目录。
/// </summary>
public class VaultStorageAdapter : FileSystemVaultStorage
{
    public VaultStorageAdapter(string vaultRoot) : base(vaultRoot)
    {
    }
}