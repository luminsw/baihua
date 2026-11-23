using MobileContract.Services;

namespace MobileApp.Maui.Services;

/// <summary>
/// 按 vaultId 创建 <see cref="IVaultStorageAdapter"/> 实例的工厂。
/// </summary>
public interface IVaultStorageFactory
{
    IVaultStorageAdapter CreateForVault(string vaultId);
}
