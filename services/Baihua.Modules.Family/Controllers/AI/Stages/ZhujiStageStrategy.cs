using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;

namespace Baihua.Modules.Family.Controllers.AI.Stages;

/// <summary>
/// 「筑基」阶段策略 — 严师：严格要求，打牢基础。
/// </summary>
public class ZhujiStageStrategy : StageStrategyBase
{
    private readonly IStringLocalizer<SharedResources> _loc;

    public ZhujiStageStrategy(IStringLocalizer<SharedResources> loc)
    {
        _loc = loc;
    }

    public override string StageName => "筑基";
    public override int Order => 2;
    public override string RoleName => "严师";

    public override string[] BlessingTemplates =>
    [
        _loc["AiStage_Blessing_Zhuji_1"],
        _loc["AiStage_Blessing_Zhuji_2"],
        _loc["AiStage_Blessing_Zhuji_3"]
    ];
}
