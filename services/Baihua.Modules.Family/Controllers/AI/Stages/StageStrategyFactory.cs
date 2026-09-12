using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;

namespace Baihua.Modules.Family.Controllers.AI.Stages;

/// <summary>
/// 阶段策略工厂 — 根据阶段名称获取对应的策略实例。
/// 新增阶段只需添加新的策略类和此处的映射即可。
/// </summary>
public class StageStrategyFactory
{
    private readonly IReadOnlyDictionary<string, IStageStrategy> _strategies;
    private readonly IReadOnlyList<IStageStrategy> _orderedStrategies;

    public StageStrategyFactory(IStringLocalizer<SharedResources> loc)
    {
        var list = new List<IStageStrategy>
        {
            new RudaoStageStrategy(loc),
            new ZhujiStageStrategy(loc),
            new JingjinStageStrategy(loc),
            new MoliStageStrategy(loc),
            new ChushiStageStrategy(loc)
        };
        _orderedStrategies = list.OrderBy(s => s.Order).ToList().AsReadOnly();
        _strategies = list.ToDictionary(s => s.StageName);
    }

    /// <summary>
    /// 获取指定阶段的策略。如果未找到，返回 null。
    /// </summary>
    public IStageStrategy? GetStrategy(string stageName)
    {
        return _strategies.GetValueOrDefault(stageName);
    }

    /// <summary>
    /// 获取指定阶段之后的下一阶段策略。如果已是最后阶段，返回 null。
    /// </summary>
    public IStageStrategy? GetNextStrategy(string stageName)
    {
        if (!_strategies.TryGetValue(stageName, out var current))
            return null;

        var nextOrder = current.Order + 1;
        return _orderedStrategies.FirstOrDefault(s => s.Order == nextOrder);
    }

    /// <summary>
    /// 获取所有已注册的阶段策略，按顺序排列。
    /// </summary>
    public IReadOnlyList<IStageStrategy> GetAllStrategies() => _orderedStrategies;
}
