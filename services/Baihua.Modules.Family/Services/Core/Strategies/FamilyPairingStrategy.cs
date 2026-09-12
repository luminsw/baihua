using Baihua.Core;
using Baihua.Core.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Baihua.Contracts.Pairing;

namespace Baihua.Modules.Family.Services.Strategies;

/// <summary>
/// 家庭版配对策略：提交配对请求，等待 WebUI 审批
/// </summary>
public class FamilyPairingStrategy : IPairingStrategy
{
    private readonly DeviceService _deviceService;
    private readonly IStringLocalizer<SharedResources> _loc;

    public FamilyPairingStrategy(DeviceService deviceService, IStringLocalizer<SharedResources> loc)
    {
        _deviceService = deviceService;
        _loc = loc;
    }

    public ActionResult<PairResponse> Pair(string deviceName, string? ipAddress, string? pairCode)
    {
        if (string.IsNullOrEmpty(pairCode))
        {
            return new BadRequestObjectResult(new { error = _loc["Pair_CodeRequired"] });
        }

        var pairRequest = _deviceService.SubmitPairRequest(deviceName, pairCode, ipAddress);
        return new OkObjectResult(new PairResponse
        {
            RequestId = pairRequest.RequestId,
            Status = "pending",
            Message = _loc["Pair_RequestSentToWebUI"]
        });
    }
}
