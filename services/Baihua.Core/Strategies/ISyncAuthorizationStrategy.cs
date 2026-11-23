using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Baihua.Core.Services.Strategies;

/// <summary>
/// 同步授权策略接口
/// 区分官网版和家庭版对同步请求（manifest/file）的验证方式
/// </summary>
public interface ISyncAuthorizationStrategy
{
    /// <summary>
    /// 验证知识库清单请求
    /// </summary>
    /// <param name="httpContext">当前 HTTP 上下文</param>
    /// <param name="vaultId">知识库 ID</param>
    /// <param name="deviceId">设备 ID（官网版需要）</param>
    /// <returns>null 表示验证通过；否则返回对应的 ActionResult</returns>
    ActionResult? ValidateManifest(HttpContext httpContext, string vaultId, string? deviceId);

    /// <summary>
    /// 验证文件下载请求
    /// </summary>
    /// <param name="httpContext">当前 HTTP 上下文</param>
    /// <param name="vaultId">知识库 ID</param>
    /// <param name="deviceId">设备 ID（官网版需要）</param>
    /// <returns>null 表示验证通过；否则返回对应的 ActionResult</returns>
    ActionResult? ValidateFile(HttpContext httpContext, string vaultId, string? deviceId);
}
