using MobileContract.Pairing;

namespace MobileContract.Services;

/// <summary>
/// 设备配对接口 — 管理移动端与后端的配对、认证流程
/// </summary>
public interface IPairingService
{
    /// <summary>
    /// 设置后续配对操作使用的服务器地址。
    /// </summary>
    void Initialize(string serverUrl);

    /// <summary>
    /// 获取当前配对码和设备ID
    /// </summary>
    Task<PairCodeResponse> GetPairCodeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 刷新配对码
    /// </summary>
    Task<PairCodeResponse> RefreshPairCodeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 使用配对码配对设备
    /// </summary>
    Task<PairResponse> PairDeviceAsync(PairRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 检查配对/授权状态
    /// </summary>
    /// <remarks>
    /// [后端未实现] 对应端点 /mg/pair/status 暂未向移动端暴露。
    /// 在服务端实现该端点前，实现类应抛出 <see cref="NotSupportedException"/>。
    /// </remarks>
    Task<PairResponse> CheckPairStatusAsync(string requestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 验证访问令牌是否有效
    /// </summary>
    /// <remarks>
    /// [后端未实现] 对应端点 /mg/verify-token 暂未实现。
    /// 在服务端实现该端点前，实现类应抛出 <see cref="NotSupportedException"/>。
    /// </remarks>
    Task<bool> VerifyTokenAsync(VerifyTokenRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取认证配置（共享密钥等）
    /// </summary>
    /// <remarks>
    /// [后端未实现] 对应端点 /mg/auth/config 暂未实现。
    /// 在服务端实现该端点前，实现类应抛出 <see cref="NotSupportedException"/>。
    /// </remarks>
    Task<AuthConfigResponse> GetAuthConfigAsync(AuthConfigRequest request, CancellationToken cancellationToken = default);
}
