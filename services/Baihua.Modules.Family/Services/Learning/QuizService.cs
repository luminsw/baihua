using Microsoft.EntityFrameworkCore;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Core.Time;
using Baihua.Contracts.Achievements;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// FAM-30 亲子共学（互考模式）：2 人对战，回合制出题作答，知识库卡片当题库，简答模式 MVP。
/// 计分：答对 +1；N 轮后结算（胜者 + 得分 + 正确率）；对战记录写入 StudyActivity（类型=quiz），计入打卡。
/// </summary>
public class QuizService
{
    private readonly IDbContextFactory<FamilyDbContext> _dbFactory;
    private readonly CardRepository _cardRepository;
    private readonly ITimeProvider _timeProvider;

    public QuizService(
        IDbContextFactory<FamilyDbContext> dbFactory,
        CardRepository cardRepository,
        ITimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _cardRepository = cardRepository;
        _timeProvider = timeProvider;
    }

    /// <summary>默认每局轮数</summary>
    private const int DefaultRounds = 5;

    /// <summary>默认每题限时（秒）</summary>
    private const int DefaultTimeLimitSeconds = 30;

    /// <summary>
    /// 创建对战（AC1）：选择两位 Learner，返回第一轮题目（A 出题 → B 作答）。
    /// 题目从知识库卡片随机抽取（简答模式：卡片正面为题目）。
    /// </summary>
    public async Task<QuizSessionDto> CreateQuizAsync(int playerAId, int playerBId, string? vaultId = null, int rounds = DefaultRounds)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var learners = await db.LearnerProfiles.Where(l => l.Id == playerAId || l.Id == playerBId).ToListAsync();
        var a = learners.FirstOrDefault(l => l.Id == playerAId);
        var b = learners.FirstOrDefault(l => l.Id == playerBId);
        if (a == null || b == null)
            throw new InvalidOperationException("对战双方学习者必须存在");

        // 随机抽一张卡片作为第一题（A 出题，B 作答）
        var card = await DrawRandomCardAsync(vaultId);
        if (card == null)
            throw new InvalidOperationException("知识库暂无卡片，请先创建卡片");

        return new QuizSessionDto
        {
            SessionId = Guid.NewGuid().ToString("N"),
            PlayerA = new QuizPlayerDto { LearnerId = a.Id, Name = a.Name, AvatarEmoji = a.AvatarEmoji },
            PlayerB = new QuizPlayerDto { LearnerId = b.Id, Name = b.Name, AvatarEmoji = b.AvatarEmoji },
            TotalRounds = rounds,
            CurrentRound = 1,
            CurrentAskerId = a.Id,    // A 出题
            CurrentAnswererId = b.Id, // B 作答
            TimeLimitSeconds = DefaultTimeLimitSeconds,
            CurrentQuestion = card,
            ScoreA = 0,
            ScoreB = 0,
            Status = "playing",
            VaultId = vaultId
        };
    }

    /// <summary>
    /// 判定作答（AC2）：提交答案 → 返回对错判定 + 正确答案 + 双方分数 + 下一轮。
    /// 答对 +1；最后一轮后进入结算（AC3）。
    /// </summary>
    public async Task<QuizResultDto> SubmitAnswerAsync(
        QuizSessionDto session,
        string answer,
        string? vaultId = null)
    {
        // 简答模式判定：去空白后不区分大小写对比卡片背面
        var expected = session.CurrentQuestion?.Back?.Trim() ?? "";
        var isCorrect = !string.IsNullOrWhiteSpace(expected)
                        && string.Equals(expected.Trim(), answer?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);

        // 计分（答对者 = 当前作答者）
        var answererIsA = session.CurrentAnswererId == session.PlayerA.LearnerId;
        if (isCorrect)
        {
            if (answererIsA) session.ScoreA++;
            else session.ScoreB++;
        }

        var lastRound = session.CurrentRound >= session.TotalRounds;
        if (lastRound)
        {
            // AC3：结算（公开 FinishQuiz 供契约锁定）
            return await FinishQuizAsync(session);
        }

        // 进入下一轮：交换出题/作答方
        session.CurrentRound++;
        session.CurrentAskerId = answererIsA ? session.PlayerB.LearnerId : session.PlayerA.LearnerId;
        session.CurrentAnswererId = answererIsA ? session.PlayerA.LearnerId : session.PlayerB.LearnerId;
        session.CurrentQuestion = await DrawRandomCardAsync(vaultId);

        return new QuizResultDto
        {
            SessionId = session.SessionId,
            IsCorrect = isCorrect,
            CorrectAnswer = expected,
            ScoreA = session.ScoreA,
            ScoreB = session.ScoreB,
            CurrentRound = session.CurrentRound,
            Status = "playing",
            NextQuestion = session.CurrentQuestion,
            NextAskerId = session.CurrentAskerId,
            NextAnswererId = session.CurrentAnswererId
        };
    }

    /// <summary>随机抽取一张知识库卡片（简答模式：正面为题）</summary>
    private async Task<QuizCardDto?> DrawRandomCardAsync(string? vaultId)
    {
        if (string.IsNullOrEmpty(vaultId)) return null;
        var cardsPath = _cardRepository.ResolveCardsPath(vaultId);
        if (string.IsNullOrEmpty(cardsPath) || !Directory.Exists(cardsPath))
            return null;

        var allCards = _cardRepository.LoadAllCards(cardsPath);
        if (allCards.Count == 0) return null;

        var pick = allCards[Random.Shared.Next(allCards.Count)];
        return new QuizCardDto
        {
            CardId = pick.Id,
            Front = pick.Front,
            Back = pick.Back
        };
    }

    /// <summary>
    /// AC3 结算：计算胜者 + 双方得分 + 正确率，并将互考记录写入 StudyActivity（AC4，计入打卡）。
    /// </summary>
    public async Task<QuizResultDto> FinishQuizAsync(QuizSessionDto session)
    {
        var (winnerId, winnerName) = ResolveWinner(session);
        await RecordQuizResultAsync(session);

        return new QuizResultDto
        {
            SessionId = session.SessionId,
            ScoreA = session.ScoreA,
            ScoreB = session.ScoreB,
            CurrentRound = session.CurrentRound,
            Status = "finished",
            WinnerId = winnerId,
            WinnerName = winnerName,
            AccuracyA = session.TotalRounds > 0 ? (double)session.ScoreA / session.TotalRounds : 0,
            AccuracyB = session.TotalRounds > 0 ? (double)session.ScoreB / session.TotalRounds : 0
        };
    }

    /// <summary>AC4：互考记录写入 StudyActivity（ActivityType=quiz，计入打卡）</summary>
    public async Task RecordQuizResultAsync(QuizSessionDto session)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var winner = session.ScoreA >= session.ScoreB ? session.PlayerA : session.PlayerB;
        db.StudyActivities.Add(new StudyActivity
        {
            LearnerId = winner.LearnerId,
            VaultId = session.VaultId ?? "",
            ActivityType = "quiz",
            CardId = session.CurrentQuestion?.CardId,
            Result = "remember",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    /// <summary>结算：胜者判定（平局时返回 null）</summary>
    private static (int? WinnerId, string? WinnerName) ResolveWinner(QuizSessionDto session)
    {
        if (session.ScoreA > session.ScoreB)
            return (session.PlayerA.LearnerId, session.PlayerA.Name);
        if (session.ScoreB > session.ScoreA)
            return (session.PlayerB.LearnerId, session.PlayerB.Name);
        return (null, "平局");
    }
}
