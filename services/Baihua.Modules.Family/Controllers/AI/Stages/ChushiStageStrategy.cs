using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;

namespace Baihua.Modules.Family.Controllers.AI.Stages;

/// <summary>
/// 「出师」阶段策略 — 前辈：实战建议、考前冲刺。
/// </summary>
public class ChushiStageStrategy : StageStrategyBase
{
    private readonly IStringLocalizer<SharedResources> _loc;

    public ChushiStageStrategy(IStringLocalizer<SharedResources> loc)
    {
        _loc = loc;
    }

    public override string StageName => "出师";
    public override int Order => 5;
    public override string RoleName => "前辈";

    public override string[] BlessingTemplates =>
    [
        _loc["AiStage_Blessing_Chushi_1"],
        _loc["AiStage_Blessing_Chushi_2"],
        _loc["AiStage_Blessing_Chushi_3"]
    ];
}
