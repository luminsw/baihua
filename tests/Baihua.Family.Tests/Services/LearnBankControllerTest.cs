using System.Text;
using System.Text.Json;
using Baihua.Contracts.Learn;
using Baihua.Data;
using Baihua.Modules.Family.Controllers;
using Baihua.Modules.Family.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// LearnBankController 测试：api/learn/* 四端点的参数校验、错误映射（InvalidOperationException → 4xx）
/// 与导出文件契约（camelCase JSON 附件）。判定逻辑的深度覆盖见 <see cref="QuizBankServiceTest"/>。
/// </summary>
public class LearnBankControllerTest
{
    private static (LearnBankController Controller, DbContextOptions<FamilyDbContext> Options) CreateController()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        var controller = new LearnBankController(service, Mock.Of<ILogger<LearnBankController>>());
        return (controller, options);
    }

    private static async Task<(LearnBankController Controller, DbContextOptions<FamilyDbContext> Options)> CreateControllerWithSampleBankAsync()
    {
        var (controller, options) = CreateController();
        await QuizBankTestSupport.CreateService(options).ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());
        await QuizBankTestSupport.SeedLearnerAsync(options);
        return (controller, options);
    }

    /// <summary>读取匿名对象 { error = ... } 的 error 值。</summary>
    private static string? GetError(object? value)
        => value?.GetType().GetProperty("error")?.GetValue(value)?.ToString();

    // ============ GET api/learn/banks ============

    [Fact]
    public async Task GetBanks_AfterImport_ReturnsOverviewsWithCounts()
    {
        var (controller, _) = await CreateControllerWithSampleBankAsync();

        var action = await controller.GetBanks();

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var overviews = Assert.IsType<List<QuizBankOverviewDto>>(ok.Value!);
        var ov = Assert.Single(overviews);
        Assert.Equal(QuizBankTestSupport.BankCode, ov.BankCode);
        Assert.Equal(QuizBankTestSupport.IndustryCode, ov.IndustryCode);
        Assert.Equal("v1", ov.Version);
        Assert.Equal(2, ov.ItemCount);
        Assert.Equal(3, ov.QuestionCount);
    }

    // ============ GET api/learn/banks/{bankCode}/questions ============

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task DrawQuestions_CountOutOfRange_ReturnsBadRequest(int count)
    {
        var (controller, _) = await CreateControllerWithSampleBankAsync();

        var action = await controller.DrawQuestions(QuizBankTestSupport.BankCode, count);

        var bad = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("1-50", GetError(bad.Value));
    }

    [Fact]
    public async Task DrawQuestions_UnknownBank_ReturnsNotFound()
    {
        var (controller, _) = CreateController();

        var action = await controller.DrawQuestions("nope", 10);

        var notFound = Assert.IsType<NotFoundObjectResult>(action.Result);
        Assert.Contains("题库不存在", GetError(notFound.Value));
    }

    [Fact]
    public async Task DrawQuestions_Valid_ReturnsViewsWithoutCorrectFlag()
    {
        var (controller, _) = await CreateControllerWithSampleBankAsync();

        var action = await controller.DrawQuestions(QuizBankTestSupport.BankCode, 3);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var views = Assert.IsType<List<QuizQuestionViewDto>>(ok.Value!);
        Assert.Equal(3, views.Count);

        // 端点输出不泄题：序列化后不含任何 correct 相关字段
        var json = JsonSerializer.Serialize(views);
        Assert.False(json.Contains("correct", StringComparison.OrdinalIgnoreCase), $"端点视图泄露正确项信息：{json}");
    }

    // ============ POST api/learn/banks/{bankCode}/answer ============

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SubmitAnswer_MissingQuestionCode_ReturnsBadRequest(string questionCode)
    {
        var (controller, _) = await CreateControllerWithSampleBankAsync();

        var action = await controller.SubmitAnswer(QuizBankTestSupport.BankCode, new QuizAnswerSubmitDto
        {
            QuestionCode = questionCode,
            SelectedKey = "B",
        });

        var bad = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("题目代码", GetError(bad.Value));
    }

    [Fact]
    public async Task SubmitAnswer_MissingSelectedKey_ReturnsBadRequest()
    {
        var (controller, _) = await CreateControllerWithSampleBankAsync();

        var action = await controller.SubmitAnswer(QuizBankTestSupport.BankCode, new QuizAnswerSubmitDto
        {
            QuestionCode = "q_001",
            SelectedKey = "",
        });

        var bad = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("所选选项", GetError(bad.Value));
    }

    [Fact]
    public async Task SubmitAnswer_UnknownLearner_ReturnsBadRequest()
    {
        var (controller, _) = await CreateControllerWithSampleBankAsync();

        var action = await controller.SubmitAnswer(QuizBankTestSupport.BankCode, new QuizAnswerSubmitDto
        {
            QuestionCode = "q_001",
            SelectedKey = "B",
            LearnerId = 999,
        });

        var bad = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("家庭成员不存在", GetError(bad.Value));
    }

    [Fact]
    public async Task SubmitAnswer_UnknownQuestion_ReturnsBadRequest()
    {
        var (controller, _) = await CreateControllerWithSampleBankAsync();

        var action = await controller.SubmitAnswer(QuizBankTestSupport.BankCode, new QuizAnswerSubmitDto
        {
            QuestionCode = "q_ghost",
            SelectedKey = "B",
        });

        var bad = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains("题目不存在", GetError(bad.Value));
    }

    [Fact]
    public async Task SubmitAnswer_Valid_ReturnsJudgedResult()
    {
        var (controller, _) = await CreateControllerWithSampleBankAsync();

        var action = await controller.SubmitAnswer(QuizBankTestSupport.BankCode, new QuizAnswerSubmitDto
        {
            QuestionCode = "q_001",
            SelectedKey = "B",
            LearnerId = 1,
        });

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<QuizAnswerResultDto>(ok.Value!);
        Assert.True(result.IsCorrect);
        Assert.Equal("B", result.CorrectKey);
        Assert.Equal("方甲", result.CorrectContent);
    }

    // ============ GET api/learn/banks/{bankCode}/export ============

    [Fact]
    public async Task ExportPackage_UnknownBank_ReturnsNotFound()
    {
        var (controller, _) = CreateController();

        var action = await controller.ExportPackage("nope");

        var notFound = Assert.IsType<NotFoundObjectResult>(action);
        Assert.Contains("题库不存在", GetError(notFound.Value));
    }

    [Fact]
    public async Task ExportPackage_Valid_ReturnsCamelCaseJsonFile()
    {
        var (controller, _) = await CreateControllerWithSampleBankAsync();

        var action = await controller.ExportPackage(QuizBankTestSupport.BankCode);

        var file = Assert.IsType<FileContentResult>(action);
        Assert.Equal("application/json", file.ContentType);
        Assert.Equal("quizbank-jf_test-v1.json", file.FileDownloadName);

        var json = Encoding.UTF8.GetString(file.FileContents);
        // camelCase 序列化：正确项标记保留（导出交换文件允许含答案）
        Assert.Contains("isCorrect", json);

        var package = JsonSerializer.Deserialize<QuizBankPackage>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        });
        Assert.NotNull(package);
        Assert.Equal(QuizBankTestSupport.IndustryCode, package!.Industry.Code);
        Assert.Equal(QuizBankTestSupport.BankCode, package.Bank.Code);
        Assert.Equal("v1", package.Bank.Version);
        Assert.Equal(2, package.Items.Count);
        Assert.Equal(3, package.Questions.Count);
        Assert.Equal("B", package.Questions.Single(q => q.QuestionCode == "q_001").Options.Single(o => o.IsCorrect).Key);
        Assert.Equal("f_alpha", package.Questions.Single(q => q.QuestionCode == "q_001").ItemCode);
    }
}