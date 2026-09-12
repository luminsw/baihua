using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;

namespace Baihua.Modules.Family.Controllers.AI.Stages;

/// <summary>
/// 「磨砺」阶段策略 — 考官：模拟实战，查漏补缺。
/// </summary>
public class MoliStageStrategy : StageStrategyBase
{
    private readonly IStringLocalizer<SharedResources> _loc;

    public MoliStageStrategy(IStringLocalizer<SharedResources> loc)
    {
        _loc = loc;
    }

    public override string StageName => "磨砺";
    public override int Order => 4;
    public override string RoleName => "考官";

    public override string[] BlessingTemplates =>
    [
        _loc["AiStage_Blessing_Moli_1"],
        _loc["AiStage_Blessing_Moli_2"],
        _loc["AiStage_Blessing_Moli_3"]
    ];
}
