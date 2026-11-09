using Baihua.Contracts.Learn;
using Baihua.Data;
using Baihua.Modules.Family.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// 生成页控制器测试：只覆盖不依赖 AI 的 validate / import 两个端点
/// （AI 生成预览由 QuizBankGeneratorService 的解析/分批测试与人工验证覆盖）。
/// </summary>
public class LearnGenerateControllerTests
{
    private static (LearnGenerateController Controller, DbContextOptions<FamilyDbContext> Options) Create()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        // validate / import 不使用 generator，测试时传 null 以隔离 AI 依赖
        return (new LearnGenerateController(service, null!), options);
    }

    [Fact]
    public void Validate_ValidPackage_ReturnsOkWithNoErrors()
    {
        var (controller, _) = Create();

        var action = controller.Validate(QuizBankTestSupport.CreateSamplePackage());

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var validation = Assert.IsType<QuizBankValidationResultDto>(ok.Value);
        Assert.True(validation.IsValid);
        Assert.Empty(validation.Errors);
    }

    [Fact]
    public async Task Import_InvalidPackage_ReturnsBadRequestAndWritesNothing()
    {
        var (controller, options) = Create();
        var package = QuizBankTestSupport.CreateSamplePackage();
        package.Questions[0].Analysis = "";

        var action = await controller.Import(new QuizBankImportRequestDto { Package = package });

        Assert.IsType<BadRequestObjectResult>(action.Result);
        await using var db = new FamilyDbContext(options);
        Assert.Equal(0, await db.QuizBanks.CountAsync());
    }

    [Fact]
    public async Task Import_ValidPackage_AppendsAndReturnsCounts()
    {
        var (controller, options) = Create();

        var action = await controller.Import(new QuizBankImportRequestDto
        {
            Package = QuizBankTestSupport.CreateSamplePackage(),
        });

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<QuizBankImportResultDto>(ok.Value);
        Assert.Equal(2, result.ItemsImported);
        Assert.Equal(3, result.QuestionsImported);

        await using var db = new FamilyDbContext(options);
        Assert.Equal(3, await db.QuizQuestions.CountAsync());
    }

    [Fact]
    public async Task Import_DefaultIsAppend_DoesNotOverwriteExisting()
    {
        var (controller, options) = Create();
        await controller.Import(new QuizBankImportRequestDto
        {
            Package = QuizBankTestSupport.CreateSamplePackage(),
        });

        var changed = QuizBankTestSupport.CreateSamplePackage();
        changed.Questions[0].Stem = "被改的题干";
        var action = await controller.Import(new QuizBankImportRequestDto { Package = changed });

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<QuizBankImportResultDto>(ok.Value);
        Assert.Equal(0, result.QuestionsImported);
        Assert.Equal(3, result.QuestionsSkipped);

        await using var db = new FamilyDbContext(options);
        var q1 = await db.QuizQuestions.SingleAsync(q => q.QuestionCode == "q_001");
        Assert.Equal("患者甲，证见测试主证一。最适宜的方剂是？", q1.Stem);
    }

    [Fact]
    public async Task Import_OverwriteFlag_UpdatesExisting()
    {
        var (controller, options) = Create();
        await controller.Import(new QuizBankImportRequestDto
        {
            Package = QuizBankTestSupport.CreateSamplePackage(),
        });

        var changed = QuizBankTestSupport.CreateSamplePackage();
        changed.Questions[0].Stem = "改后的题干";
        var action = await controller.Import(new QuizBankImportRequestDto
        {
            Package = changed,
            OverwriteExisting = true,
        });

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        Assert.Equal(3, Assert.IsType<QuizBankImportResultDto>(ok.Value).QuestionsImported);

        await using var db = new FamilyDbContext(options);
        var q1 = await db.QuizQuestions.SingleAsync(q => q.QuestionCode == "q_001");
        Assert.Equal("改后的题干", q1.Stem);
    }
}