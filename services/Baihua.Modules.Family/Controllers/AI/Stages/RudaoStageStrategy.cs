using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;

namespace Baihua.Modules.Family.Controllers.AI.Stages;

/// <summary>
/// 「入道」阶段策略 — 引路人：温和引导，了解基础，建立学习方向。
/// </summary>
public class RudaoStageStrategy : StageStrategyBase
{
    private readonly IStringLocalizer<SharedResources> _loc;

    public RudaoStageStrategy(IStringLocalizer<SharedResources> loc)
    {
        _loc = loc;
    }

    public override string StageName => "入道";
    public override int Order => 1;
    public override string RoleName => "引路人";

    public override string[] BlessingTemplates =>
    [
        _loc["AiStage_Blessing_Rudao_1"],
        _loc["AiStage_Blessing_Rudao_2"],
        _loc["AiStage_Blessing_Rudao_3"]
    ];
}
