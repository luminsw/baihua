using Microsoft.AspNetCore.Mvc;
using Baihua.Modules.Family.Services;
using Baihua.Contracts.Achievements;

namespace Baihua.Modules.Family.Controllers;

/// <summary>
/// FAM-30 亲子共学（互考模式）API
/// </summary>
[ApiController]
[Route("api/quiz")]
public class QuizController : ControllerBase
{
    private readonly QuizService _quizService;

    public QuizController(QuizService quizService)
    {
        _quizService = quizService;
    }

    /// <summary>创建对战（AC1）：选两位 Learner + 知识库，返回第一轮</summary>
    [HttpPost("create")]
    public async Task<ActionResult<QuizSessionDto>> Create([FromBody] CreateQuizRequest request)
    {
        if (request.PlayerAId <= 0 || request.PlayerBId <= 0 || request.PlayerAId == request.PlayerBId)
            return BadRequest(new { error = "请选择两位不同的对战成员" });
        try
        {
            var session = await _quizService.CreateQuizAsync(
                request.PlayerAId, request.PlayerBId, request.VaultId, request.Rounds > 0 ? request.Rounds : 5);
            return Ok(session);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>提交答案（AC2）：判定对错 + 分数 + 下一轮/结算</summary>
    [HttpPost("answer")]
    public async Task<ActionResult<QuizResultDto>> Answer([FromBody] SubmitAnswerRequest request)
    {
        var session = new QuizSessionDto
        {
            SessionId = request.SessionId,
            PlayerA = request.PlayerA,
            PlayerB = request.PlayerB,
            TotalRounds = request.TotalRounds,
            CurrentRound = request.CurrentRound,
            CurrentAskerId = request.CurrentAskerId,
            CurrentAnswererId = request.CurrentAnswererId,
            TimeLimitSeconds = request.TimeLimitSeconds,
            CurrentQuestion = request.CurrentQuestion,
            ScoreA = request.ScoreA,
            ScoreB = request.ScoreB,
            Status = request.Status,
            VaultId = request.VaultId
        };
        var result = await _quizService.SubmitAnswerAsync(session, request.Answer, request.VaultId);
        return Ok(result);
    }
}
