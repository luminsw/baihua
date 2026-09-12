namespace Baihua.Modules.Family.Controllers.AI.Stages;

/// <summary>
/// 师父阶段策略接口。
/// 每个阶段可以独立定制角色名、祝福语、阶段序号等行为，
/// 方便新增/修改阶段而不影响其他阶段的逻辑。
/// </summary>
public interface IStageStrategy
{
    /// <summary>阶段名称（如"入道"、"筑基"）</summary>
    string StageName { get; }

    /// <summary>阶段序号（用于排序和计算下一阶段）</summary>
    int Order { get; }

    /// <summary>该阶段师父的角色名（如"引路人"、"严师"）</summary>
    string RoleName { get; }

    /// <summary>该阶段的祝福语模板数组</summary>
    string[] BlessingTemplates { get; }

    /// <summary>
    /// 获取一条随机祝福语
    /// </summary>
    string GetBlessing(string masterName);
}
