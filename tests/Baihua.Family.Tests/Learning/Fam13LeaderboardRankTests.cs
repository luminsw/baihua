using System.Reflection;
using Baihua.Contracts.Achievements;
using Baihua.Modules.Family.Controllers;
using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Learning;

/// <summary>
/// FAM-13 红测试：Leaderboard 排名算法（标准竞赛排名）。
///
/// 验收标准：
///   - 相同分数的 Learner 获得相同排名（1,1,3 而非 1,2,3）
///   - 排名有跳跃；无并列时行为不变（回归锚）
///
/// 红测试方式：Rank 赋值点在 AchievementsController.ToDtos（私有静态）的
/// `entries[i].Rank = i + 1`。反射调用它，构造并列分数输入断言排名语义 → 当前红。
/// 若 dev 重构 ToDtos 位置，请同步本测试的反射入口。
/// </summary>
public class Fam13LeaderboardRankTests
{
    private static LeaderboardEntry Entry(int id, string name, int score) => new()
    {
        LearnerId = id,
        LearnerName = name,
        Score = score,
        Accuracy = 100,
        CardsStudied = 1,
        Streak = 1
    };

    private static List<LeaderboardEntryDto> InvokeToDtos(List<LeaderboardEntry> entries)
    {
        var method = typeof(AchievementsController).GetMethod(
            "ToDtos", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (List<LeaderboardEntryDto>)method.Invoke(null, new object[] { entries })!;
    }

    // ============ 红测试：并列分数必须同排名 ============

    [Fact]
    public void EqualScores_GetSameRank()
    {
        // A/B 同分（22），C 低分（21）
        var dtos = InvokeToDtos(new List<LeaderboardEntry>
        {
            Entry(1, "A", 22), Entry(2, "B", 22), Entry(3, "C", 21)
        });

        // 验收：相同分数 → 相同排名（1,1,3）
        Assert.Equal(dtos[0].Rank, dtos[1].Rank);
    }

    [Fact]
    public void Ranking_HasGaps_WhenTiesExist()
    {
        // 标准竞赛排名：[100,100,80,80,50] → [1,1,3,3,5]
        var dtos = InvokeToDtos(new List<LeaderboardEntry>
        {
            Entry(1, "A", 100), Entry(2, "B", 100),
            Entry(3, "C", 80), Entry(4, "D", 80),
            Entry(5, "E", 50)
        });

        var expected = new[] { 1, 1, 3, 3, 5 };
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], dtos[i].Rank);
    }

    [Fact]
    public void AllTiedScores_AllRankFirst()
    {
        // 三人全同分 → 全为第 1 名（当前 index+1 会给 1,2,3）
        var dtos = InvokeToDtos(new List<LeaderboardEntry>
        {
            Entry(1, "A", 10), Entry(2, "B", 10), Entry(3, "C", 10)
        });

        Assert.Equal(1, dtos[0].Rank);
        Assert.Equal(1, dtos[1].Rank);
        Assert.Equal(1, dtos[2].Rank);
    }

    // ============ 回归锚：无并列时行为不变 ============

    [Fact]
    public void UniqueScores_SequentialRanks()
    {
        var dtos = InvokeToDtos(new List<LeaderboardEntry>
        {
            Entry(1, "A", 10), Entry(2, "B", 8), Entry(3, "C", 5)
        });

        Assert.Equal(1, dtos[0].Rank);
        Assert.Equal(2, dtos[1].Rank);
        Assert.Equal(3, dtos[2].Rank);
    }
}
