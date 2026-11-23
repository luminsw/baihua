using BaihuaSdk.Storage;

namespace MobileApp.Maui.Services;

/// <summary>
/// ISecureStore 的 MAUI 实现。
/// 底层使用平台原生安全存储（iOS Keychain / Android EncryptedSharedPreferences）。
/// </summary>
public class MauiSecureStore : ISecureStore
{
    public async Task<string?> GetAsync(string key)
        => await SecureStorage.Default.GetAsync(key);

    public async Task SetAsync(string key, string value)
        => await SecureStorage.Default.SetAsync(key, value);

    // 注：MAUI SecureStorage.Remove 是同步操作，无需 await
    public Task RemoveAsync(string key)
    {
        SecureStorage.Default.Remove(key);
        return Task.CompletedTask;
    }
}
