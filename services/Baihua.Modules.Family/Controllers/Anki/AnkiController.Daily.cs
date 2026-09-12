using Baihua.Core;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Baihua.AI.Provider;
using Baihua.Contracts.Anki;

namespace Baihua.Modules.Family.Controllers;

public partial class AnkiController
{
        [HttpGet("daily")]
        public async Task<ActionResult<DailyCardResultDto>> GetDailyCard([FromQuery] string vaultId)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                return BadRequest(new DailyCardResultDto { HasCard = false, Message = _loc["Anki_VaultRequired"] });

            var result = await _dailyCardService.GetTodayCardAsync(vaultId);
            var dto = new DailyCardResultDto
            {
                HasCard = result.HasCard,
                Message = result.Message,
                Card = result.Card == null ? null : new CardItemDto
                {
                    Id = result.Card.Id,
                    Deck = result.Card.Deck,
                    Front = result.Card.Front,
                    Back = result.Card.Back,
                    Tags = result.Card.Tags,
                    Source = result.Card.Source
                },
                TodayProgress = result.TodayProgress == null ? null : new DailyProgressDto
                {
                    Completed = result.TodayProgress.Completed,
                    Target = result.TodayProgress.Target,
                    TotalCards = result.TodayProgress.TotalCards,
                    Streak = result.TodayProgress.Streak
                },
                Remaining = result.Remaining
            };
            return Ok(dto);
        }

        /// <summary>
        /// 提交今日卡片答案
        /// </summary>
        [HttpPost("daily/answer")]
        public async Task<ActionResult> SubmitDailyAnswer([FromQuery] string vaultId, [FromBody] DailyAnswerRequestDto request)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                return BadRequest(new { error = _loc["Anki_VaultRequired"] });

            var success = await _dailyCardService.RecordAnswerAsync(vaultId, request.CardId, request.Result);
            if (!success)
                return StatusCode(500, new { error = _loc["Anki_RecordFailed"] });

            // 异步检查成就（DailyCardService 已记录 StudyActivity）
            var defaultLearner = await _learnerService.GetDefaultAsync();
            if (defaultLearner != null)
            {
                _ = _achievementEngine.CheckAndUnlockAsync(defaultLearner.Id);
            }

            var progress = _dailyCardService.GetTodayProgress(vaultId);
            return Ok(new { success = true, progress });
        }

        /// <summary>
        /// 获取今日学习进度
        /// </summary>
        [HttpGet("daily/progress")]
        public ActionResult<DailyProgressDto> GetDailyProgress([FromQuery] string vaultId)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                return BadRequest(new { error = _loc["Anki_VaultRequired"] });

            var p = _dailyCardService.GetTodayProgress(vaultId);
            return Ok(new DailyProgressDto
            {
                Completed = p.Completed,
                Target = p.Target,
                TotalCards = p.TotalCards,
                Streak = p.Streak
            });
        }

        /// <summary>
        /// 家长出题：保存自定义卡片
        /// </summary>
        [HttpPost("custom-card")]
        public async Task<ActionResult> SaveCustomCard([FromQuery] string vaultId, [FromBody] CustomCardRequestDto request)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                return BadRequest(new { error = _loc["Anki_VaultRequired"] });

            var req = new Services.CustomCardRequest
            {
                Front = request.Front,
                Back = request.Back,
                Deck = request.Deck,
                Tags = request.Tags
            };
            var success = _dailyCardService.SaveCustomCard(vaultId, req);
            if (!success)
                return StatusCode(500, new { error = _loc["Anki_SaveFailed"] });

            // 记录出题活动并检查成就
            var defaultLearner = await _learnerService.GetDefaultAsync();
            if (defaultLearner != null)
            {
                await _achievementEngine.RecordActivityAsync(defaultLearner.Id, vaultId, "create_card");
            }

            return Ok(new { success = true, message = _loc["Anki_CardSaved"] });
        }

        /// <summary>
        /// 获取最近学习记录
        /// </summary>
        [HttpGet("daily/recent")]
        public ActionResult<List<StudiedCardDto>> GetRecentStudied([FromQuery] string vaultId, [FromQuery] int days = 7)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                return BadRequest(new List<StudiedCardDto>());

            var list = _dailyCardService.GetRecentStudied(vaultId, days).Select(s => new StudiedCardDto
            {
                Card = new CardItemDto
                {
                    Id = s.Card.Id,
                    Deck = s.Card.Deck,
                    Front = s.Card.Front,
                    Back = s.Card.Back,
                    Tags = s.Card.Tags,
                    Source = s.Card.Source
                },
                Result = s.Result,
                Date = s.Date
            }).ToList();

            return Ok(list);
        }

        /// <summary>
        /// 获取指定知识库的卡片总数
        /// </summary>
        [HttpGet("vault-card-count")]
        public ActionResult<int> GetVaultCardCount([FromQuery] string vaultId)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                return BadRequest(0);
            var count = _cardGenerator.GetTotalCardCount(vaultId);
            return Ok(count);
        }

}
