using MobileContract.Services;

namespace MobileApp.Maui.Services;

/// <summary>
/// 基于应用私有目录下 vaults/&lt;vaultId&gt; 创建存储适配器。
/// </summary>
public class VaultStorageFactory : IVaultStorageFactory
{
    private readonly string _baseVaultsDirectory;

    public VaultStorageFactory(string baseVaultsDirectory)
    {
        _baseVaultsDirectory = baseVaultsDirectory;
    }

    public IVaultStorageAdapter CreateForVault(string vaultId)
    {
        if (string.IsNullOrWhiteSpace(vaultId))
            throw new ArgumentException("vaultId 不能为空", nameof(vaultId));

        var vaultRoot = Path.Combine(_baseVaultsDirectory, vaultId);
        return new VaultStorageAdapter(vaultRoot);
    }
}
