using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>题库生成服务的分批逻辑测试（30 题上限 → 每批 10 题）</summary>
public class QuizBankGeneratorServiceTests
{
    [Fact]
    public void SplitBatches_WithinOneBatch_ReturnsSingle()
    {
        Assert.Equal(new[] { 10 }, QuizBankGeneratorService.SplitBatches(10, 10));
        Assert.Equal(new[] { 5 }, QuizBankGeneratorService.SplitBatches(5, 10));
        Assert.Equal(new[] { 1 }, QuizBankGeneratorService.SplitBatches(1, 10));
    }

    [Fact]
    public void SplitBatches_MultipleBatches_ReturnsEvenSplitWithRemainder()
    {
        Assert.Equal(new[] { 10, 10, 10 }, QuizBankGeneratorService.SplitBatches(30, 10));
        Assert.Equal(new[] { 10, 10, 5 }, QuizBankGeneratorService.SplitBatches(25, 10));
        Assert.Equal(new[] { 10, 1 }, QuizBankGeneratorService.SplitBatches(11, 10));
    }

    [Fact]
    public void SplitBatches_ZeroOrNegative_ReturnsEmpty()
    {
        Assert.Empty(QuizBankGeneratorService.SplitBatches(0, 10));
        Assert.Empty(QuizBankGeneratorService.SplitBatches(-5, 10));
    }

    [Fact]
    public void SplitBatches_SumEqualsTotal()
    {
        var batches = QuizBankGeneratorService.SplitBatches(30, 10);
        Assert.Equal(30, batches.Sum());
    }

    // ============ 代码自动生成（普通用户不填 code） ============

    [Fact]
    public void EnsureCode_Blank_GeneratesUniquePrefixedCode()
    {
        var a = QuizBankGeneratorService.EnsureCode("", "ai");
        var b = QuizBankGeneratorService.EnsureCode(null, "bank");

        Assert.StartsWith("ai_", a);
        Assert.StartsWith("bank_", b);
        Assert.Equal(9, a.Length); // ai_ + 6 位随机
        Assert.NotEqual(a, QuizBankGeneratorService.EnsureCode("   ", "ai"));
    }

    [Fact]
    public void EnsureCode_Provided_TrimsAndKeeps()
    {
        Assert.Equal("jingfang_ai", QuizBankGeneratorService.EnsureCode("  jingfang_ai  ", "bank"));
    }

    // ============ 跨批合并去重（AI 忽略题号前缀时不得产生重复题号） ============

    private static Baihua.Contracts.Learn.QuizBankPackage Batch(
        string prefix, int itemCount, int questionCount, int startAt = 1)
        => new()
        {
            Items = Enumerable.Range(startAt, itemCount)
                .Select(i => new Baihua.Contracts.Learn.QuizItemDto
                {
                    ItemCode = $"{prefix}_item_{i}",
                    Name = $"条目{i}",
                })
                .ToList(),
            Questions = Enumerable.Range(startAt, questionCount)
                .Select(i => QuizBankTestSupport.NewQuestion($"{prefix}_q_{i}"))
                .ToList(),
        };

    [Fact]
    public void MergeBatch_DistinctCodes_AppendsEverything()
    {
        var merged = new Baihua.Contracts.Learn.QuizBankPackage();
        var skippedA = QuizBankGeneratorService.MergeBatch(merged, Batch("ai_b1", 2, 2));
        var skippedB = QuizBankGeneratorService.MergeBatch(merged, Batch("ai_b2", 1, 3));

        Assert.Equal(0, skippedA);
        Assert.Equal(0, skippedB);
        Assert.Equal(3, merged.Items.Count);
        Assert.Equal(5, merged.Questions.Count);
    }

    [Fact]
    public void MergeBatch_RepeatedCodesAcrossBatches_SkipsDuplicatesAndReports()
    {
        var merged = new Baihua.Contracts.Learn.QuizBankPackage();
        QuizBankGeneratorService.MergeBatch(merged, Batch("ai", 2, 3));

        // 第二批忽略了前缀 → 题号/条目号全撞：必须全部丢弃（保留首批内容）
        var skipped = QuizBankGeneratorService.MergeBatch(merged, Batch("ai", 2, 3, startAt: 1));

        Assert.Equal(5, skipped);
        Assert.Equal(2, merged.Items.Count);
        Assert.Equal(3, merged.Questions.Count);
        Assert.All(merged.Questions, q => Assert.Equal("ai_q_", q.QuestionCode[..5]));
        Assert.Equal(3, merged.Questions.Select(q => q.QuestionCode).Distinct().Count());
    }

    [Fact]
    public void MergeBatch_DuplicatesInsideOneBatch_KeepsFirst()
    {
        var merged = new Baihua.Contracts.Learn.QuizBankPackage();
        var batch = Batch("ai", 1, 2);
        // 同批内重复同一题号（AI 偶发）
        batch.Questions.Add(QuizBankTestSupport.NewQuestion("ai_q_1", stem: "重复的题干"));

        var skipped = QuizBankGeneratorService.MergeBatch(merged, batch);

        Assert.Equal(1, skipped);
        Assert.Equal(2, merged.Questions.Count);
        Assert.Equal("测试题干：最适宜的选择是？", merged.Questions.Single(q => q.QuestionCode == "ai_q_1").Stem);
    }

    [Fact]
    public void MergeBatch_BlankCodes_AreKeptForValidatorToReject()
    {
        var merged = new Baihua.Contracts.Learn.QuizBankPackage();
        var batch = new Baihua.Contracts.Learn.QuizBankPackage
        {
            Questions = [QuizBankTestSupport.NewQuestion("  "), QuizBankTestSupport.NewQuestion("  ")],
        };

        var skipped = QuizBankGeneratorService.MergeBatch(merged, batch);

        // 空代码交给校验器统一报错（错误信息比这里更完整），合并阶段不静默丢弃
        Assert.Equal(0, skipped);
        Assert.Equal(2, merged.Questions.Count);
    }
}