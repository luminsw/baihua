namespace BaihuaSdk.Storage;

/// <summary>
/// 安全存储抽象接口。
/// 平台层提供基于系统安全机制的实现（iOS Keychain / Android Keystore / MAUI SecureStorage）。
/// </summary>
public interface ISecureStore
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    Task RemoveAsync(string key);
}
