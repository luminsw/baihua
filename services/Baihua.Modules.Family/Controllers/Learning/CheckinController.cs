using Microsoft.AspNetCore.Mvc;
using Baihua.Contracts.Achievements;
using Baihua.Modules.Family.Services;

namespace Baihua.Modules.Family.Controllers;

/// <summary>
/// FAM-21/33 学习打卡 API
/// </summary>
[ApiController]
[Route("api/checkin")]
public class CheckinController : ControllerBase
{
    private readonly CheckinService _checkinService;

    public CheckinController(CheckinService checkinService)
    {
        _checkinService = checkinService;
    }

    /// <summary>
    /// 学习打卡数据：今日清单 + 连续打卡 + 连击保护 + 最近 7 天日历 + 补签剩余次数
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<CheckinDataDto>> GetCheckinData([FromQuery] string? vaultId = null)
    {
        var data = await _checkinService.GetCheckinDataAsync(vaultId);
        return Ok(new CheckinDataDto
        {
            FamilyStreak = data.FamilyStreak,
            StreakStatus = data.StreakStatus,
            MakeupRemaining = data.MakeupRemaining,
            TodayRecords = data.TodayRecords.Select(r => new CheckinRecordDto
            {
                LearnerName = r.LearnerName,
                Content = r.Content,
                Time = r.Time,
                IsCompleted = r.IsCompleted,
                Source = r.Source,
                StartTime = r.StartTime,
                EndTime = r.EndTime,
                CardCount = r.CardCount,
                Accuracy = Math.Round(r.Accuracy, 1)
            }).ToList(),
            Last7Days = data.Last7Days.Select(d => new CheckinCalendarDayDto
            {
                Date = d.Date,
                IsChecked = d.IsChecked,
                IsToday = d.IsToday,
                IsMakeupable = d.IsMakeupable
            }).ToList()
        });
    }

    /// <summary>
    /// FAM-33 补签：3 天窗口内、有学习记录的日期补签，月限 3 次
    /// </summary>
    [HttpPost("makeup")]
    public async Task<ActionResult<CheckinMakeupResultDto>> MakeupCheckin([FromBody] CheckinMakeupRequest request)
    {
        var result = await _checkinService.MakeupCheckinAsync(request.Date, request.VaultId);
        return Ok(new CheckinMakeupResultDto
        {
            Success = result.Success,
            Message = result.Message,
            Remaining = result.Remaining
        });
    }
}
