using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;

namespace Baihua.Modules.Family.Controllers.AI.Stages;

/// <summary>
/// 「精进」阶段策略 — 匠人：精益求精，不放过任何细节。
/// </summary>
public class JingjinStageStrategy : StageStrategyBase
{
    private readonly IStringLocalizer<SharedResources> _loc;

    public JingjinStageStrategy(IStringLocalizer<SharedResources> loc)
    {
        _loc = loc;
    }

    public override string StageName => "精进";
    public override int Order => 3;
    public override string RoleName => "匠人";

    public override string[] BlessingTemplates =>
    [
        _loc["AiStage_Blessing_Jingjin_1"],
        _loc["AiStage_Blessing_Jingjin_2"],
        _loc["AiStage_Blessing_Jingjin_3"]
    ];
}
