using Microsoft.AspNetCore.Mvc;
using Baihua.Contracts.Achievements;
using Baihua.Modules.Family.Services;

namespace Baihua.Modules.Family.Controllers;

public partial class AchievementsController : ControllerBase
{
    private async Task<DashboardDataDto> HandleGetDashboardAsync(string? vaultId, int? learnerId)
    {
        var data = await _leaderboardService.GetDashboardAsync(vaultId, learnerId);
        return new DashboardDataDto
        {
            FamilyStats = data.FamilyStats.Select(s => new FamilyMemberStatDto
            {
                LearnerId = s.LearnerId,
                Name = s.Name,
                AvatarEmoji = s.AvatarEmoji,
                Color = s.Color,
                WeekTotal = s.WeekTotal,
                Accuracy = Math.Round(s.Accuracy, 0),
                Streak = s.Streak,
                TotalCards = s.TotalCards
            }).ToList(),
            WeeklyTrend = data.WeeklyTrend.Select(t => new DailyTrendDto { Date = t.Date, Count = t.Count }).ToList(),
            RecentAchievements = data.RecentAchievements.Select(a => new RecentAchievementDto
            {
                LearnerName = a.LearnerName,
                AvatarEmoji = a.AvatarEmoji,
                Title = a.Title,
                Icon = a.Icon,
                Tier = a.Tier,
                UnlockedAt = a.UnlockedAt
            }).ToList(),
            ResultDistribution = new ResultDistributionDto
            {
                Remember = data.ResultDistribution.Remember,
                Hard = data.ResultDistribution.Hard,
                Forgot = data.ResultDistribution.Forgot
            },
            // FAM-20 家长看板 v2 字段
            FamilyStreak = data.FamilyStreak,
            TodayCompleted = data.TodayCompleted,
            YesterdayCompleted = data.YesterdayCompleted,
            TrendArrow = data.TrendArrow,
            TodayActivities = data.TodayActivities.Select(a => new TodayActivityItemDto
            {
                LearnerName = a.LearnerName,
                Description = a.Description
            }).ToList(),
            LatestAchievements = data.LatestAchievements.Select(a => new RecentAchievementDto
            {
                LearnerName = a.LearnerName,
                AvatarEmoji = a.AvatarEmoji,
                Title = a.Title,
                Icon = a.Icon,
                Tier = a.Tier,
                UnlockedAt = a.UnlockedAt
            }).ToList(),
            GrowthTimeline = data.GrowthTimeline.Select(t => new GrowthTimelineItemDto
            {
                Date = t.Date,
                LearnerName = t.LearnerName,
                Description = t.Description
            }).ToList(),
            PageSize = data.PageSize
        };
    }

    private static List<LeaderboardEntryDto> ToDtos(List<LeaderboardEntry> entries)
    {
        // FAM-13：标准竞赛排名——相同分数同排名，排名有跳跃（1,1,3,3,5）
        // 输入按 Score 降序（LeaderboardService 已 OrderByDescending）
        int rank = 0;
        int prevScore = int.MinValue;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Score != prevScore)
            {
                rank = i + 1;
                prevScore = entries[i].Score;
            }
            entries[i].Rank = rank;
        }
        return entries.Select(e => new LeaderboardEntryDto
        {
            LearnerId = e.LearnerId,
            LearnerName = e.LearnerName,
            AvatarEmoji = e.AvatarEmoji,
            Color = e.Color,
            CardsStudied = e.CardsStudied,
            Accuracy = Math.Round(e.Accuracy, 1),
            Score = e.Score,
            Streak = e.Streak,
            Rank = e.Rank
        }).ToList();
    }
}
