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
}