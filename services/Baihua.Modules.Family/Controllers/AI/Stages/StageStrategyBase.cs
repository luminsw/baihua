namespace Baihua.Modules.Family.Controllers.AI.Stages;

/// <summary>
/// 阶段策略基类，提供祝福语生成的公共逻辑。
/// </summary>
public abstract class StageStrategyBase : IStageStrategy
{
    public abstract string StageName { get; }
    public abstract int Order { get; }
    public abstract string RoleName { get; }
    public abstract string[] BlessingTemplates { get; }

    public string GetBlessing(string masterName)
    {
        var templates = BlessingTemplates;
        if (templates.Length == 0) return "";
        var template = templates[Random.Shared.Next(templates.Length)];
        return template
            .Replace("{name}", masterName)
            .Replace("{stage}", StageName)
            .Replace("{role}", RoleName);
    }
}
