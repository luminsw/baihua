using Baihua.Core;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Baihua.AI.Provider;
using Baihua.Contracts.Anki;

namespace Baihua.Modules.Family.Controllers;

public partial class AnkiController
{
        [HttpPost("study")]
        public ActionResult RecordStudy([FromQuery] string vaultId, [FromBody] StudyRequest request)
        {
            var statsPath = ResolveCardsPath(vaultId);
            if (string.IsNullOrEmpty(statsPath)) 
            {
                return BadRequest(new { error = _loc["Vault_Required"] });
            }

            var studyDir = System.IO.Path.Combine(statsPath, ".study");
            System.IO.Directory.CreateDirectory(studyDir);

            var today = DateTime.Today.ToString("yyyy-MM-dd");
            var statsFile = System.IO.Path.Combine(studyDir, $"{today}.json");

            // 读取今日统计
            var stats = ReadStudyStats(statsFile);
            
            // 更新统计
            stats.Total++;
            switch (request.Result?.ToLower())
            {
                case "remember":
                case "remembered":
                    stats.Remembered++;
                    break;
                case "hard":
                case "forgot":
                    stats.Forgot++;
                    break;
                default:
                    stats.Normal++;
                    break;
            }

            // 记录学习历史
            if (stats.History == null) stats.History = new List<StudyRecord>();
            stats.History.Add(new StudyRecord
            {
                CardFront = request.CardFront ?? "",
                CardBack = request.CardBack ?? "",
                Deck = request.Deck ?? "",
                Result = request.Result ?? "normal",
                Time = DateTime.Now
            });

            // 保存
            WriteStudyStats(statsFile, stats);

            return Ok(new { success = true, stats });
        }

        /// <summary>
        /// 获取学习统计
        /// </summary>
        [HttpGet("stats")]
        public ActionResult<StudyStatsResponse> GetStats([FromQuery] string vaultId, [FromQuery] int days = 7)
        {
            var statsPath = ResolveCardsPath(vaultId);
            if (string.IsNullOrEmpty(statsPath))
            {
                return Ok(new StudyStatsResponse());
            }

            var studyDir = System.IO.Path.Combine(statsPath, ".study");
            var response = new StudyStatsResponse
            {
                DailyStats = new List<DailyStats>()
            };

            // 统计最近 N 天
            var totalStudied = 0;
            var totalRemembered = 0;
            var totalForgot = 0;
            var streak = 0;
            var today = DateTime.Today;

            for (int i = 0; i < days; i++)
            {
                var date = today.AddDays(-i);
                var dateStr = date.ToString("yyyy-MM-dd");
                var statsFile = System.IO.Path.Combine(studyDir, $"{dateStr}.json");
                
                if (System.IO.File.Exists(statsFile))
                {
                    var stats = ReadStudyStats(statsFile);
                    
                    if (i == 0) response.Today = stats;
                    
                    response.DailyStats.Add(new DailyStats
                    {
                        Date = dateStr,
                        Total = stats.Total,
                        Remembered = stats.Remembered,
                        Forgot = stats.Forgot,
                        Normal = stats.Normal
                    });

                    totalStudied += stats.Total;
                    totalRemembered += stats.Remembered;
                    totalForgot += stats.Forgot;

                    // 连续学习天数
                    if (stats.Total > 0 && i == streak) streak++;
                }
                else if (i > 0) // 今天还没学习不算断
                {
                    break;
                }
            }

            response.Summary = new StudySummary
            {
                TotalCards = totalStudied,
                Remembered = totalRemembered,
                Forgot = totalForgot,
                Accuracy = totalStudied > 0 ? (double)totalRemembered / totalStudied * 100 : 0,
                Streak = streak
            };

            return Ok(response);
        }

        private StudyStats ReadStudyStats(string file)
        {
            if (!System.IO.File.Exists(file)) return new StudyStats();
            try
            {
                var json = System.IO.File.ReadAllText(file);
                return JsonSerializer.Deserialize<StudyStats>(json) ?? new StudyStats();
            }
            catch { return new StudyStats(); }
        }

        private void WriteStudyStats(string file, StudyStats stats)
        {
            var json = JsonSerializer.Serialize(stats, JsonHelper.IndentedUnicode);
            System.IO.File.WriteAllText(file, json);
        }

        /// <summary>
        /// 导出所有卡片为 CSV 格式（可用于 Anki 导入）
        /// </summary>
        [HttpGet("export")]
        public async Task<ActionResult> ExportToCsv([FromQuery] string vaultId)
        {
            var cardsPath = ResolveCardsPath(vaultId);
            if (string.IsNullOrEmpty(cardsPath))
            {
                return BadRequest(new { error = _loc["Vault_Required"] });
            }

            var csv = await _cardGenerator.ExportToCsv(cardsPath);
            
            if (string.IsNullOrEmpty(csv))
            {
                return NotFound(new { error = _loc["Anki_NoCardsToExport"] });
            }

            return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", "anki_cards.csv");
        }

}
