using Microsoft.AspNetCore.Mvc;
using Baihua.Contracts.Pairing;

namespace Baihua.Modules.Family.Services.Strategies;

/// <summary>
/// 设备配对策略接口
/// 官网版（cloud）自动授权；家庭版（family）提交审批请求
/// </summary>
public interface IPairingStrategy
{
    /// <summary>
    /// 执行配对逻辑
    /// </summary>
    /// <param name="deviceName">设备名称</param>
    /// <param name="ipAddress">设备 IP</param>
    /// <param name="pairCode">配对码（家庭版需要）</param>
    /// <returns>配对响应</returns>
    ActionResult<PairResponse> Pair(string deviceName, string? ipAddress, string? pairCode);
}
