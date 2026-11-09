using Baihua.Contracts.Learn;
using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// 题库包校验器测试：落库前的整体体检（AI 生成 / Agent 写入共用）。
///
/// 业务红线：
///   1. Errors（结构缺陷）→ IsValid=false，严格模式下拒绝落库；
///   2. Warnings（质量提示）→ 不阻断落库；
///   3. 跨库引用、逐条 skip 仍由 QuizBankService 负责，本校验器只判包内自洽性。
/// </summary>
public class QuizBankPackageValidatorTests
{
    private static QuizBankPackage ValidPackage() => QuizBankTestSupport.CreateSamplePackage();

    // ============ 基线 ============

    [Fact]
    public void Validate_SamplePackage_IsValid()
    {
        var result = QuizBankPackageValidator.Validate(ValidPackage());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Equal(2, result.ItemCount);
        Assert.Equal(3, result.QuestionCount);
        Assert.Equal(1, result.FormulaQuestionCount);
        Assert.Equal(1, result.PatternQuestionCount);
        Assert.Equal(new[] { "f_alpha", "f_beta" }, result.ItemCodes);
        Assert.Equal(new[] { "q_001", "q_002", "q_003" }, result.QuestionCodes);
    }

    [Fact]
    public void Validate_NullPackage_ReturnsError()
    {
        var result = QuizBankPackageValidator.Validate(null);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("题库包为空"));
    }

    [Fact]
    public void Validate_EmptyPackage_ReturnsError()
    {
        var package = ValidPackage();
        package.Items = [];
        package.Questions = [];

        var result = QuizBankPackageValidator.Validate(package);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("items 与 questions 均为空"));
    }

    // ============ 行业 / 题库元信息 ============

    [Fact]
    public void Validate_MissingIndustryCode_ReturnsError()
    {
        var package = ValidPackage();
        package.Industry.Code = "  ";

        var result = QuizBankPackageValidator.Validate(package);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("industry.code"));
    }

    [Fact]
    public void Validate_MissingBankName_ReturnsError()
    {
        var package = ValidPackage();
        package.Bank.Name = "";

        var result = QuizBankPackageValidator.Validate(package);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("bank.name"));
    }

    // ============ 条目 ============

    [Fact]
    public void Validate_DuplicateItemCode_ReturnsError()
    {
        var package = ValidPackage();
        package.Items.Add(new QuizItemDto { ItemCode = "f_alpha", Name = "重复条目" });

        var result = QuizBankPackageValidator.Validate(package);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("itemCode 重复"));
    }

    [Fact]
    public void Validate_ItemMissingName_ReturnsError()
    {
        var package = ValidPackage();
        package.Items[0].Name = "";

        var result = QuizBankPackageValidator.Validate(package);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("缺少 name"));
    }

    [Fact]
    public void Validate_ItemMissingContent_IsOnlyWarning()
    {
        var package = ValidPackage();
        package.Items[1].Content = null;

        var result = QuizBankPackageValidator.Validate(package);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("缺少 content"));
    }

    // ============ 题目：结构与唯一性 ============

    [Fact]
    public void Validate_DuplicateQuestionCode_ReturnsError()
    {
        var package = ValidPackage();
        package.Questions.Add(QuizBankTestSupport.NewQuestion("q_001"));

        var result = QuizBankPackageValidator.Validate(package);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("questionCode 重复"));
    }

    [Fact]
    public void Validate_MissingStem_ReturnsError()
    {
        var package = ValidPackage();
        package.Questions[0].Stem = "";

        var result = QuizBankPackageValidator.Validate(package);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("缺少 stem"));
    }

    [Fact]
    public void Validate_MissingAnalysis_ReturnsError()
    {
        var package = ValidPackage();
        package.Questions[0].Analysis = "  ";

        var result = QuizBankPackageValidator.Validate(package);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("缺少 analysis"));
    }

    [Fact]
    public void Validate_MissingTarget_ReturnsError()
    {
        var package = ValidPackage();
        package.Questions[0].Target = "";

        var result = QuizBankPackageValidator.Validate(package);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("缺少 target"));
    }

    // ============ 选项 ============

    [Fact]
    public void Validate_TwoCorrectOptions_ReturnsError()
    {
        var package = ValidPackage();
        package.Questions[0].Options[0].IsCorrect = true;

        var result = QuizBankPackageValidator.Validate(package);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("正确项数量为 2"));
    }

    [Fact]
    public void Validate_ZeroCorrectOption_ReturnsError()
    {
        var package = ValidPackage();
        package.Questions[0].Options.ForEach(o => o.IsCorrect = false);

        var result = QuizBankPackageValidator.Validate(package);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("正确项数量为 0"));
    }

    [Fact]
    public void Validate_LessThanTwoOptions_ReturnsError()
    {
        var package = ValidPackage();
        package.Questions[0].Options = [new QuizOptionDto { Key = "A", Content = "唯一选项", IsCorrect = true }];

        var result = QuizBankPackageValidator.Validate(package);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("选项不足 2 个"));
    }

    [Fact]
    public void Validate_DuplicateOptionKey_ReturnsError()
    {
        var package = ValidPackage();
        package.Questions[0].Options[1].Key = "A";

        var result = QuizBankPackageValidator.Validate(package);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("选项 key 重复"));
    }

    [Fact]
    public void Validate_DuplicateOptionContent_ReturnsError()
    {
        var package = ValidPackage();
        package.Questions[0].Options[1].Content = package.Questions[0].Options[0].Content;

        var result = QuizBankPackageValidator.Validate(package);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("选项内容重复"));
    }

    [Fact]
    public void Validate_OptionMissingExplanation_IsOnlyWarning()
    {
        var package = ValidPackage();
        package.Questions[0].Options[0].Explanation = null;

        var result = QuizBankPackageValidator.Validate(package);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("缺少 explanation"));
    }

    // ============ 非阻断的质量提示 ============

    [Fact]
    public void Validate_UnknownTarget_IsOnlyWarning()
    {
        var package = ValidPackage();
        package.Questions[0].Target = "custom_target";

        var result = QuizBankPackageValidator.Validate(package);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("target 非标准值"));
    }

    [Fact]
    public void Validate_DifficultyOutOfRange_IsOnlyWarning()
    {
        var package = ValidPackage();
        package.Questions[0].Difficulty = 9;

        var result = QuizBankPackageValidator.Validate(package);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("difficulty 超出 1-5"));
    }

    [Fact]
    public void Validate_EmptyItemsWithQuestions_IsOnlyWarning()
    {
        var package = ValidPackage();
        package.Items = [];

        var result = QuizBankPackageValidator.Validate(package);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("items 为空"));
    }
}