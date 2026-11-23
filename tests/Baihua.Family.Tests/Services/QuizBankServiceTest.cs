using System.Text.Json;
using Baihua.Contracts.Learn;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Modules.Family.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// QuizBankService 测试支撑：InMemory 数据库 + IDbContextFactory（跟随 LearnerServiceTests 装配惯例）
/// 与样例题库包 / 单题构造工厂。
/// </summary>
internal static class QuizBankTestSupport
{
    public const string IndustryCode = "tcm";
    public const string BankCode = "jf_test";

    public static DbContextOptions<FamilyDbContext> CreateInMemoryOptions()
        => new DbContextOptionsBuilder<FamilyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    public sealed class InMemoryFactory(DbContextOptions<FamilyDbContext> options)
        : IDbContextFactory<FamilyDbContext>
    {
        public FamilyDbContext CreateDbContext() => new(options);
        public Task<FamilyDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    public static QuizBankService CreateService(DbContextOptions<FamilyDbContext> options)
        => new(new InMemoryFactory(options));

    public static async Task SeedLearnerAsync(DbContextOptions<FamilyDbContext> options, int learnerId = 1)
    {
        await using var db = new FamilyDbContext(options);
        db.LearnerProfiles.Add(new LearnerProfile
        {
            Id = learnerId,
            Name = "小明",
            AvatarEmoji = "🧑",
            Color = "#ff6b6b",
            IsDefault = true,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 样例题库包：1 行业 / 1 题库 / 2 条目 / 3 题。
    /// q_001 关联条目且带结构化题干与区分点；q_002 关联条目无结构化字段；q_003 无条目关联。
    /// </summary>
    public static QuizBankPackage CreateSamplePackage() => new()
    {
        FormatVersion = 1,
        Industry = new QuizIndustryInfo { Code = IndustryCode, Name = "中医" },
        Bank = new QuizBankInfo { Code = BankCode, Name = "经方测试库", Version = "v1", Description = "测试用题库" },
        Items =
        [
            new QuizItemDto
            {
                ItemCode = "f_alpha",
                ItemType = "formula",
                Name = "方甲",
                Summary = "条目甲摘要",
                Content = new QuizItemContentDto
                {
                    Source = "伤寒论·测试条",
                    Composition =
                    [
                        new QuizHerbDoseDto { Name = "药一", AncientDose = "三两", ModernDose = "9g", Role = "君" },
                        new QuizHerbDoseDto { Name = "药二", AncientDose = "一两", ModernDose = "3g" },
                    ],
                    Decoction = "水煎测试",
                    Pathogenesis = "测试病机",
                    Treatment = "测试治法",
                    Indications = ["证甲", "证乙"],
                    Contraindications = ["忌测试"],
                    Discriminations = [new QuizDiscriminationDto { Versus = "方乙", Point = "测试区分点" }],
                    Notes = "测试备注",
                },
            },
            new QuizItemDto { ItemCode = "f_beta", ItemType = "concept", Name = "概念乙", Summary = "条目乙摘要" },
        ],
        Questions =
        [
            new QuizQuestionDto
            {
                QuestionCode = "q_001",
                ItemCode = "f_alpha",
                QuestionType = "single_choice",
                Target = "formula",
                Stem = "患者甲，证见测试主证一。最适宜的方剂是？",
                StemDetail = new QuizStemDetailDto { Patient = "患者甲，男，38岁", Signs = ["主证一", "主证二"] },
                Options =
                [
                    new QuizOptionDto { Key = "A", Content = "干扰方一", IsCorrect = false, Explanation = "无此证" },
                    new QuizOptionDto { Key = "B", Content = "方甲", IsCorrect = true, Explanation = "对证" },
                    new QuizOptionDto { Key = "C", Content = "干扰方二", IsCorrect = false, Explanation = "药味不符" },
                    new QuizOptionDto { Key = "D", Content = "干扰方三", IsCorrect = false, Explanation = "病机不符" },
                ],
                Analysis = "测试解析：选方甲。",
                Discrimination = [new QuizDiscriminationDto { Versus = "干扰方一", Point = "鉴别点一" }],
                Difficulty = 3,
                SourceRef = "金匮要略·测试篇",
            },
            new QuizQuestionDto
            {
                QuestionCode = "q_002",
                ItemCode = "f_beta",
                Target = "pattern",
                Stem = "患者乙，证见测试主证二。对应的证型是？",
                Options =
                [
                    new QuizOptionDto { Key = "A", Content = "证丙", IsCorrect = false },
                    new QuizOptionDto { Key = "B", Content = "证丁", IsCorrect = false },
                    new QuizOptionDto { Key = "C", Content = "证乙", IsCorrect = true },
                ],
                Analysis = "测试解析：选证乙。",
                Difficulty = 4,
            },
            new QuizQuestionDto
            {
                QuestionCode = "q_003",
                ItemCode = null,
                Target = "concept",
                Stem = "测试概念题：下列正确的是？",
                Options =
                [
                    new QuizOptionDto { Key = "A", Content = "正确概念", IsCorrect = true },
                    new QuizOptionDto { Key = "B", Content = "错误概念", IsCorrect = false },
                ],
                Analysis = "测试解析：概念题。",
                Difficulty = 2,
            },
        ],
    };

    /// <summary>构造合法单选题（便捷工厂，供校验类测试在其基础上破坏数据）。</summary>
    public static QuizQuestionDto NewQuestion(
        string questionCode,
        string correctKey = "A",
        int optionCount = 4,
        string? itemCode = null,
        string stem = "测试题干：最适宜的选择是？") => new()
    {
        QuestionCode = questionCode,
        ItemCode = itemCode,
        Target = "formula",
        Stem = stem,
        Options = Enumerable.Range(0, optionCount)
            .Select(i =>
            {
                var key = ((char)('A' + i)).ToString();
                return new QuizOptionDto { Key = key, Content = $"选项{key}", IsCorrect = key == correctKey };
            })
            .ToList(),
    };
}

/// <summary>
/// QuizBankService 单元测试：导入（幂等 upsert）/ 导出往返 / 概览 / 抽题（不泄题）/ 判定（零 AI）。
///
/// 业务红线（按优先级）：
///   1. 判定零 AI：正确项由 OptionsJson 数据静态声明，判定＝比对所选 key，纯确定性；
///      抽题视图（QuizQuestionViewDto）不含正确项信息（作答前不泄题）。
///   2. 导入幂等：重复导入同一包＝内容刷新，不产生重复行。
///   3. 数据校验：正确项≠1 / 缺 QuestionCode/Stem / 选项不足 2 / 条目缺 ItemCode/Name 的数据被拒（Warnings+跳过）。
///   4. 作答记录仅写家庭成员（LearnerProfile 校验）。
/// </summary>
public class QuizBankServiceTest
{
    // ============ 导入：基础正确性与条目关联 ============

    [Fact]
    public async Task ImportPackageAsync_FirstImport_PersistsAllRows()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);

        var result = await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());

        Assert.Equal(QuizBankTestSupport.BankCode, result.BankCode);
        Assert.Equal(2, result.ItemsImported);
        Assert.Equal(3, result.QuestionsImported);
        Assert.Equal(0, result.ItemsSkipped);
        Assert.Equal(0, result.QuestionsSkipped);
        Assert.Empty(result.Warnings);

        await using var db = new FamilyDbContext(options);
        Assert.Equal(1, await db.QuizIndustries.CountAsync());
        Assert.Equal(1, await db.QuizBanks.CountAsync());
        Assert.Equal(2, await db.QuizItems.CountAsync());
        Assert.Equal(3, await db.QuizQuestions.CountAsync());

        var industry = await db.QuizIndustries.SingleAsync();
        Assert.Equal(QuizBankTestSupport.IndustryCode, industry.Code);
        Assert.Equal("中医", industry.Name);

        var bank = await db.QuizBanks.SingleAsync();
        Assert.Equal(QuizBankTestSupport.BankCode, bank.Code);
        Assert.Equal("经方测试库", bank.Name);
        Assert.Equal("v1", bank.Version);
        Assert.Equal("测试用题库", bank.Description);
        Assert.Equal(industry.Id, bank.IndustryId);
    }

    [Fact]
    public async Task ImportPackageAsync_QuestionWithItemCode_LinksItemId()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);

        await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());

        await using var db = new FamilyDbContext(options);
        var alpha = await db.QuizItems.SingleAsync(i => i.ItemCode == "f_alpha");
        var beta = await db.QuizItems.SingleAsync(i => i.ItemCode == "f_beta");
        var q1 = await db.QuizQuestions.SingleAsync(q => q.QuestionCode == "q_001");
        var q2 = await db.QuizQuestions.SingleAsync(q => q.QuestionCode == "q_002");
        var q3 = await db.QuizQuestions.SingleAsync(q => q.QuestionCode == "q_003");

        Assert.Equal(alpha.Id, q1.ItemId);
        Assert.Equal(beta.Id, q2.ItemId);
        Assert.Null(q3.ItemId);

        // 题目内容落库（OptionsJson 含正确项标记——判定的唯一事实来源）
        Assert.Contains("\"isCorrect\":true", q1.OptionsJson);
        var bank = await db.QuizBanks.SingleAsync();
        Assert.Equal(bank.Id, q1.BankId);
    }

    [Fact]
    public async Task ImportPackageAsync_QuestionWithUnknownItemCode_ImportsWithNullItemId()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        var package = QuizBankTestSupport.CreateSamplePackage();
        package.Questions.Add(QuizBankTestSupport.NewQuestion("q_ghost_item", itemCode: "f_not_exists"));

        var result = await service.ImportPackageAsync(package);

        Assert.Equal(4, result.QuestionsImported);
        Assert.Equal(0, result.QuestionsSkipped);

        await using var db = new FamilyDbContext(options);
        var ghost = await db.QuizQuestions.SingleAsync(q => q.QuestionCode == "q_ghost_item");
        Assert.Null(ghost.ItemId);
    }

    // ============ 导入：幂等性（业务红线 2） ============

    [Fact]
    public async Task ImportPackageAsync_ImportTwice_IsIdempotent_NoDuplicateRows()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        var package = QuizBankTestSupport.CreateSamplePackage();

        await service.ImportPackageAsync(package);

        // 第二次导入：内容有变化 → 应为 upsert 内容刷新，而非重复插入
        package.Bank.Name = "经方测试库（改）";
        package.Bank.Version = "v2";
        package.Items[0].Name = "方甲（改）";
        package.Questions[0].Stem = "改后的题干";
        var second = await service.ImportPackageAsync(package);

        Assert.Equal(2, second.ItemsImported);
        Assert.Equal(3, second.QuestionsImported);
        Assert.Equal(0, second.ItemsSkipped);
        Assert.Equal(0, second.QuestionsSkipped);

        // 不产生重复行（upsert 语义）
        await using var db = new FamilyDbContext(options);
        Assert.Equal(1, await db.QuizIndustries.CountAsync());
        Assert.Equal(1, await db.QuizBanks.CountAsync());
        Assert.Equal(2, await db.QuizItems.CountAsync());
        Assert.Equal(3, await db.QuizQuestions.CountAsync());

        // 内容以包为准（刷新生效）
        var bank = await db.QuizBanks.SingleAsync();
        Assert.Equal("经方测试库（改）", bank.Name);
        Assert.Equal("v2", bank.Version);
        var alpha = await db.QuizItems.SingleAsync(i => i.ItemCode == "f_alpha");
        Assert.Equal("方甲（改）", alpha.Name);
        var q1 = await db.QuizQuestions.SingleAsync(q => q.QuestionCode == "q_001");
        Assert.Equal("改后的题干", q1.Stem);
    }

    [Fact]
    public async Task ImportPackageAsync_SecondImportWithOnlyNewQuestions_AppendsWithoutSideEffects()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);

        await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());

        // 增量导入：items 空数组 + 仅 1 道新题
        var incremental = QuizBankTestSupport.CreateSamplePackage();
        incremental.Items = [];
        incremental.Questions = [QuizBankTestSupport.NewQuestion("q_004", correctKey: "B")];
        var result = await service.ImportPackageAsync(incremental);

        Assert.Equal(0, result.ItemsImported);
        Assert.Equal(1, result.QuestionsImported);

        await using var db = new FamilyDbContext(options);
        Assert.Equal(1, await db.QuizBanks.CountAsync());
        Assert.Equal(2, await db.QuizItems.CountAsync()); // 原条目保留
        Assert.Equal(4, await db.QuizQuestions.CountAsync()); // 3 旧 + 1 新

        // 原题未被改动
        var q1 = await db.QuizQuestions.SingleAsync(q => q.QuestionCode == "q_001");
        Assert.Equal("患者甲，证见测试主证一。最适宜的方剂是？", q1.Stem);
    }

    [Fact]
    public async Task ImportPackageAsync_EmptyItemsAndQuestions_CreatesIndustryAndBankOnly()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        var package = QuizBankTestSupport.CreateSamplePackage();
        package.Items = [];
        package.Questions = [];

        var result = await service.ImportPackageAsync(package);

        Assert.Equal(0, result.ItemsImported);
        Assert.Equal(0, result.QuestionsImported);
        Assert.Equal(0, result.ItemsSkipped);
        Assert.Equal(0, result.QuestionsSkipped);
        Assert.Empty(result.Warnings);

        await using var db = new FamilyDbContext(options);
        Assert.Equal(1, await db.QuizIndustries.CountAsync());
        Assert.Equal(1, await db.QuizBanks.CountAsync());
        Assert.Equal(0, await db.QuizItems.CountAsync());
        Assert.Equal(0, await db.QuizQuestions.CountAsync());
    }

    [Fact]
    public async Task ImportPackageAsync_IncrementalPackageWithOnlyQuestions_KeepsLinkToExistingItem()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());

        // 增量包：items 为空，新题引用早先批次里的条目（真实数据里 b* 批次就是这样增量导入的）
        var incremental = QuizBankTestSupport.CreateSamplePackage();
        incremental.Items = [];
        incremental.Questions = [QuizBankTestSupport.NewQuestion("q_005", correctKey: "A", itemCode: "f_alpha")];
        await service.ImportPackageAsync(incremental);

        await using var db = new FamilyDbContext(options);
        var alpha = await db.QuizItems.SingleAsync(i => i.ItemCode == "f_alpha");
        var q5 = await db.QuizQuestions.SingleAsync(q => q.QuestionCode == "q_005");
        Assert.Equal(alpha.Id, q5.ItemId);

        // 导出按 itemCode 回链，不是 null（否则花阁/花记拿到的包丢失方剂关联）
        var exported = await service.ExportPackageAsync(QuizBankTestSupport.IndustryCode, QuizBankTestSupport.BankCode);
        Assert.Equal("f_alpha", exported.Questions.Single(q => q.QuestionCode == "q_005").ItemCode);
    }

    // ============ 导入：数据校验红线（业务红线 3） ============

    [Fact]
    public async Task ImportPackageAsync_QuestionsWithInvalidCorrectness_AreRejected()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        var package = QuizBankTestSupport.CreateSamplePackage();

        // 两个正确项
        var multiCorrect = QuizBankTestSupport.NewQuestion("q_bad_multi", correctKey: "A");
        multiCorrect.Options[1].IsCorrect = true;
        package.Questions.Add(multiCorrect);
        // 零个正确项
        package.Questions.Add(QuizBankTestSupport.NewQuestion("q_bad_none", correctKey: "@"));
        // 仅 1 个选项（正确项=1 但选项不足 2）
        package.Questions.Add(QuizBankTestSupport.NewQuestion("q_bad_single_option", correctKey: "A", optionCount: 1));

        var result = await service.ImportPackageAsync(package);

        Assert.Equal(3, result.QuestionsImported);
        Assert.Equal(3, result.QuestionsSkipped);
        Assert.Equal(3, result.Warnings.Count);
        Assert.All(result.Warnings, w => Assert.Contains("已跳过", w));

        await using var db = new FamilyDbContext(options);
        Assert.Equal(3, await db.QuizQuestions.CountAsync());
        Assert.Null(await db.QuizQuestions.SingleOrDefaultAsync(q => q.QuestionCode == "q_bad_multi"));
        Assert.Null(await db.QuizQuestions.SingleOrDefaultAsync(q => q.QuestionCode == "q_bad_none"));
        Assert.Null(await db.QuizQuestions.SingleOrDefaultAsync(q => q.QuestionCode == "q_bad_single_option"));
    }

    [Fact]
    public async Task ImportPackageAsync_QuestionsMissingCodeOrStem_AreRejected()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        var package = QuizBankTestSupport.CreateSamplePackage();

        var noCode = QuizBankTestSupport.NewQuestion("q_no_code");
        noCode.QuestionCode = "  ";
        package.Questions.Add(noCode);
        var noStem = QuizBankTestSupport.NewQuestion("q_no_stem");
        noStem.Stem = "";
        package.Questions.Add(noStem);

        var result = await service.ImportPackageAsync(package);

        Assert.Equal(3, result.QuestionsImported);
        Assert.Equal(2, result.QuestionsSkipped);
        Assert.Equal(2, result.Warnings.Count);

        await using var db = new FamilyDbContext(options);
        Assert.Equal(3, await db.QuizQuestions.CountAsync());
        Assert.Null(await db.QuizQuestions.SingleOrDefaultAsync(q => q.QuestionCode == "q_no_stem"));
    }

    [Fact]
    public async Task ImportPackageAsync_ItemsMissingCodeOrName_AreSkipped()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        var package = QuizBankTestSupport.CreateSamplePackage();

        package.Items.Add(new QuizItemDto { ItemCode = "", Name = "无代码条目" });
        package.Items.Add(new QuizItemDto { ItemCode = "f_no_name", Name = "" });

        var result = await service.ImportPackageAsync(package);

        Assert.Equal(2, result.ItemsImported);
        Assert.Equal(2, result.ItemsSkipped);
        Assert.Equal(2, result.Warnings.Count);

        await using var db = new FamilyDbContext(options);
        Assert.Equal(2, await db.QuizItems.CountAsync());
        Assert.Null(await db.QuizItems.SingleOrDefaultAsync(i => i.ItemCode == "f_no_name"));
    }

    // ============ 导入：append 模式（防 AI 生成撞码覆盖） ============

    [Fact]
    public async Task ImportPackageAsync_AppendMode_DoesNotOverwriteExistingContent()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());

        // 同一 code 再次导入但内容被改动：append 模式必须原样保留
        var package = QuizBankTestSupport.CreateSamplePackage();
        package.Bank.Name = "被改的库名";
        package.Items[0].Name = "被改的条目";
        package.Questions[0].Stem = "被改的题干";

        var result = await service.ImportPackageAsync(
            package, new QuizBankImportOptions { OverwriteExisting = false });

        Assert.Equal(0, result.ItemsImported);
        Assert.Equal(2, result.ItemsSkipped);
        Assert.Equal(0, result.QuestionsImported);
        Assert.Equal(3, result.QuestionsSkipped);
        Assert.Contains(result.Warnings, w => w.Contains("append 模式保留原内容"));

        await using var db = new FamilyDbContext(options);
        var bank = await db.QuizBanks.SingleAsync();
        Assert.Equal("经方测试库", bank.Name);
        var alpha = await db.QuizItems.SingleAsync(i => i.ItemCode == "f_alpha");
        Assert.Equal("方甲", alpha.Name);
        var q1 = await db.QuizQuestions.SingleAsync(q => q.QuestionCode == "q_001");
        Assert.Equal("患者甲，证见测试主证一。最适宜的方剂是？", q1.Stem);
    }

    [Fact]
    public async Task ImportPackageAsync_AppendMode_AddsOnlyNewCodes_AndKeepsItemLink()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());

        // 只新增 1 道题，并引用已存在条目（append 跳过后仍须回填 Id）
        var incremental = QuizBankTestSupport.CreateSamplePackage();
        incremental.Bank.Name = "不应生效";
        incremental.Items = [];
        incremental.Questions = [QuizBankTestSupport.NewQuestion("q_new", itemCode: "f_alpha")];

        var result = await service.ImportPackageAsync(
            incremental, new QuizBankImportOptions { OverwriteExisting = false });

        Assert.Equal(0, result.ItemsImported);
        Assert.Equal(1, result.QuestionsImported);
        Assert.Equal(0, result.QuestionsSkipped);

        await using var db = new FamilyDbContext(options);
        Assert.Equal("经方测试库", (await db.QuizBanks.SingleAsync()).Name);
        var alpha = await db.QuizItems.SingleAsync(i => i.ItemCode == "f_alpha");
        var qNew = await db.QuizQuestions.SingleAsync(q => q.QuestionCode == "q_new");
        Assert.Equal(alpha.Id, qNew.ItemId);
        Assert.Equal(4, await db.QuizQuestions.CountAsync());
    }

    [Fact]
    public async Task ImportPackageAsync_AppendMode_CreatesNewBankWhenAbsent()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);

        var result = await service.ImportPackageAsync(
            QuizBankTestSupport.CreateSamplePackage(), new QuizBankImportOptions { OverwriteExisting = false });

        Assert.Equal(2, result.ItemsImported);
        Assert.Equal(3, result.QuestionsImported);

        await using var db = new FamilyDbContext(options);
        Assert.Equal(1, await db.QuizBanks.CountAsync());
        Assert.Equal(3, await db.QuizQuestions.CountAsync());
    }

    // ============ 导出往返（业务场景 5） ============

    [Fact]
    public async Task ExportPackageAsync_AfterImport_RoundtripsAllFields()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        var original = QuizBankTestSupport.CreateSamplePackage();
        await service.ImportPackageAsync(original);

        var exported = await service.ExportPackageAsync(QuizBankTestSupport.IndustryCode, QuizBankTestSupport.BankCode);

        Assert.Equal(1, exported.FormatVersion);
        Assert.Equal(QuizBankTestSupport.IndustryCode, exported.Industry.Code);
        Assert.Equal("中医", exported.Industry.Name);
        Assert.Equal(QuizBankTestSupport.BankCode, exported.Bank.Code);
        Assert.Equal("经方测试库", exported.Bank.Name);
        Assert.Equal("v1", exported.Bank.Version);
        Assert.Equal("测试用题库", exported.Bank.Description);

        // 条目往返（含 Content JSON 反序列化）
        Assert.Equal(2, exported.Items.Count);
        var alpha = exported.Items.Single(i => i.ItemCode == "f_alpha");
        Assert.Equal("formula", alpha.ItemType);
        Assert.Equal("方甲", alpha.Name);
        Assert.Equal("条目甲摘要", alpha.Summary);
        Assert.NotNull(alpha.Content);
        Assert.Equal("伤寒论·测试条", alpha.Content.Source);
        Assert.Equal("水煎测试", alpha.Content.Decoction);
        Assert.Equal("测试病机", alpha.Content.Pathogenesis);
        Assert.Equal("测试治法", alpha.Content.Treatment);
        Assert.Equal(new[] { "证甲", "证乙" }, alpha.Content.Indications);
        Assert.Equal(new[] { "忌测试" }, alpha.Content.Contraindications);
        Assert.Equal("测试备注", alpha.Content.Notes);
        Assert.Equal(2, alpha.Content.Composition!.Count);
        Assert.Equal("药一", alpha.Content.Composition[0].Name);
        Assert.Equal("三两", alpha.Content.Composition[0].AncientDose);
        Assert.Equal("9g", alpha.Content.Composition[0].ModernDose);
        Assert.Equal("君", alpha.Content.Composition[0].Role);
        Assert.Null(alpha.Content.Composition[1].Role);

        // Content == null 的条目导出为空对象（非 null）
        var beta = exported.Items.Single(i => i.ItemCode == "f_beta");
        Assert.NotNull(beta.Content);
        Assert.Equal("概念乙", beta.Name);

        // 题目往返（含 ItemCode 回链 / Options / 结构化题干 / 区分点）
        Assert.Equal(3, exported.Questions.Count);
        var q1 = exported.Questions.Single(q => q.QuestionCode == "q_001");
        Assert.Equal("f_alpha", q1.ItemCode);
        Assert.Equal("single_choice", q1.QuestionType);
        Assert.Equal("formula", q1.Target);
        Assert.Equal(original.Questions[0].Stem, q1.Stem);
        Assert.NotNull(q1.StemDetail);
        Assert.Equal("患者甲，男，38岁", q1.StemDetail.Patient);
        Assert.Equal(new[] { "主证一", "主证二" }, q1.StemDetail.Signs);
        Assert.Equal(4, q1.Options.Count);
        Assert.Equal("A", q1.Options[0].Key);
        Assert.Equal("干扰方一", q1.Options[0].Content);
        Assert.Equal("无此证", q1.Options[0].Explanation);
        Assert.Equal("B", q1.Options.Single(o => o.IsCorrect).Key);
        Assert.Equal("方甲", q1.Options.Single(o => o.IsCorrect).Content);
        Assert.Equal("测试解析：选方甲。", q1.Analysis);
        var disc = Assert.Single(q1.Discrimination!);
        Assert.Equal("干扰方一", disc.Versus);
        Assert.Equal("鉴别点一", disc.Point);
        Assert.Equal(3, q1.Difficulty);
        Assert.Equal("金匮要略·测试篇", q1.SourceRef);

        // 无结构化字段的题导出为 null
        var q2 = exported.Questions.Single(q => q.QuestionCode == "q_002");
        Assert.Equal("f_beta", q2.ItemCode);
        Assert.Null(q2.StemDetail);
        Assert.Null(q2.Discrimination);
        Assert.Equal("C", q2.Options.Single(o => o.IsCorrect).Key);
        Assert.Equal(4, q2.Difficulty);

        var q3 = exported.Questions.Single(q => q.QuestionCode == "q_003");
        Assert.Null(q3.ItemCode);
        Assert.Equal(2, q3.Options.Count);
    }

    [Fact]
    public async Task ExportPackageAsync_UnknownIndustry_Throws()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExportPackageAsync("nope", QuizBankTestSupport.BankCode));
        Assert.Contains("行业不存在", ex.Message);
    }

    [Fact]
    public async Task ExportPackageAsync_UnknownBank_Throws()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExportPackageAsync(QuizBankTestSupport.IndustryCode, "nope"));
        Assert.Contains("题库不存在", ex.Message);
    }

    // ============ 概览统计（业务场景 6） ============

    [Fact]
    public async Task GetOverviewsAsync_AfterImport_ReturnsCorrectCounts()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());

        var overviews = await service.GetOverviewsAsync();

        var ov = Assert.Single(overviews);
        Assert.Equal(QuizBankTestSupport.IndustryCode, ov.IndustryCode);
        Assert.Equal("中医", ov.IndustryName);
        Assert.Equal(QuizBankTestSupport.BankCode, ov.BankCode);
        Assert.Equal("经方测试库", ov.BankName);
        Assert.Equal("v1", ov.Version);
        Assert.Equal(2, ov.ItemCount);
        Assert.Equal(3, ov.QuestionCount);
    }

    [Fact]
    public async Task GetOverviewsAsync_EmptyDatabase_ReturnsEmpty()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);

        var overviews = await service.GetOverviewsAsync();

        Assert.Empty(overviews);
    }

    // ============ 抽题：不泄题红线（业务红线 1） ============

    [Fact]
    public async Task DrawQuestionsAsync_DoesNotLeakCorrectAnswer()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());

        var views = await service.DrawQuestionsAsync(QuizBankTestSupport.BankCode, 3);

        Assert.Equal(3, views.Count);

        // 契约层红线：视图选项 DTO 不存在任何「正确项/答案」成员（编译期就不可能泄题）
        var leakProps = typeof(QuizOptionViewDto).GetProperties()
            .Where(p => p.Name.Contains("correct", StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains("answer", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Empty(leakProps);

        // 序列化层红线：视图 JSON 输出不含任何 correct 相关字段（作答前不泄题）
        var json = JsonSerializer.Serialize(views);
        Assert.False(json.Contains("correct", StringComparison.OrdinalIgnoreCase), $"抽题视图泄露正确项信息：{json}");

        // ItemCode 不下发（避免从题目反查答案线索）
        Assert.All(views, v => Assert.Null(v.ItemCode));

        // 选项只含 Key/Content，数量完整
        var q1 = views.Single(v => v.QuestionCode == "q_001");
        Assert.Equal(4, q1.Options.Count);
        Assert.All(q1.Options, o =>
        {
            Assert.False(string.IsNullOrWhiteSpace(o.Key));
            Assert.False(string.IsNullOrWhiteSpace(o.Content));
        });
    }

    [Fact]
    public async Task DrawQuestionsAsync_ReturnsRequestedCount_WithOptionsSortedByKey()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());

        var views = await service.DrawQuestionsAsync(QuizBankTestSupport.BankCode, 2);

        Assert.Equal(2, views.Count);
        Assert.Equal(2, views.Select(v => v.QuestionCode).Distinct().Count());

        // 选项按 Key 升序（稳定渲染，避免顺序本身成为提示）
        Assert.All(views, v =>
            Assert.Equal(
                v.Options.Select(o => o.Key).OrderBy(k => k, StringComparer.Ordinal),
                v.Options.Select(o => o.Key)));
    }

    [Fact]
    public async Task DrawQuestionsAsync_CountExceedsTotal_ReturnsAll()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());

        var views = await service.DrawQuestionsAsync(QuizBankTestSupport.BankCode, 50);

        Assert.Equal(3, views.Count);
    }

    [Fact]
    public async Task DrawQuestionsAsync_ExcludesSpecifiedQuestionCodes()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());

        var views = await service.DrawQuestionsAsync(
            QuizBankTestSupport.BankCode, 3, new[] { "q_001", "q_002" });

        var view = Assert.Single(views);
        Assert.Equal("q_003", view.QuestionCode);
    }

    [Fact]
    public async Task DrawQuestionsAsync_UnknownBank_Throws()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DrawQuestionsAsync("nope", 10));
        Assert.Contains("题库不存在", ex.Message);
    }

    [Fact]
    public async Task DrawQuestionsAsync_DeserializesStemDetailAndMeta()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());

        var views = await service.DrawQuestionsAsync(QuizBankTestSupport.BankCode, 3);

        var q1 = views.Single(v => v.QuestionCode == "q_001");
        Assert.NotNull(q1.StemDetail);
        Assert.Equal("患者甲，男，38岁", q1.StemDetail.Patient);
        Assert.Equal(new[] { "主证一", "主证二" }, q1.StemDetail.Signs);
        Assert.Equal("患者甲，证见测试主证一。最适宜的方剂是？", q1.Stem);
        Assert.Equal("formula", q1.Target);
        Assert.Equal(3, q1.Difficulty);
        Assert.Equal("金匮要略·测试篇", q1.SourceRef);

        var q2 = views.Single(v => v.QuestionCode == "q_002");
        Assert.Null(q2.StemDetail);
    }

    // ============ 判定零 AI（业务红线 1）+ 作答记录（业务红线 4） ============

    [Fact]
    public async Task SubmitAnswerAsync_CorrectKey_JudgesCorrectAndWritesAttempt()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());
        await QuizBankTestSupport.SeedLearnerAsync(options, learnerId: 1);

        var result = await service.SubmitAnswerAsync(QuizBankTestSupport.BankCode, new QuizAnswerSubmitDto
        {
            QuestionCode = "q_001",
            SelectedKey = "B",
            LearnerId = 1,
            TimeCostMs = 4200,
            SessionId = "sess-1",
        });

        Assert.True(result.IsCorrect);
        Assert.Equal("B", result.CorrectKey);
        Assert.Equal("方甲", result.CorrectContent);
        Assert.Equal("测试解析：选方甲。", result.Analysis);
        Assert.NotNull(result.Discrimination);
        var disc = Assert.Single(result.Discrimination);
        Assert.Equal("干扰方一", disc.Versus);
        Assert.Equal("鉴别点一", disc.Point);

        // 作答后下发完整选项（按 Key 升序、含 IsCorrect 与解析）
        Assert.Equal(4, result.Options.Count);
        Assert.Equal("A", result.Options[0].Key);
        Assert.Equal("B", result.Options.Single(o => o.IsCorrect).Key);
        Assert.Equal("对证", result.Options.Single(o => o.IsCorrect).Explanation);

        // 答题记录写入（仅家庭成员）
        await using var db = new FamilyDbContext(options);
        var attempt = await db.QuizAttempts.SingleAsync();
        var q1 = await db.QuizQuestions.SingleAsync(q => q.QuestionCode == "q_001");
        Assert.Equal(1, attempt.LearnerId);
        Assert.Equal(q1.Id, attempt.QuestionId);
        Assert.Equal(q1.BankId, attempt.BankId);
        Assert.Equal("B", attempt.SelectedKey);
        Assert.True(attempt.IsCorrect);
        Assert.Equal(4200, attempt.TimeCostMs);
        Assert.Equal("sess-1", attempt.SessionId);
    }

    [Fact]
    public async Task SubmitAnswerAsync_WrongKey_JudgesIncorrectButRevealsAnswer()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());
        await QuizBankTestSupport.SeedLearnerAsync(options, learnerId: 1);

        var result = await service.SubmitAnswerAsync(QuizBankTestSupport.BankCode, new QuizAnswerSubmitDto
        {
            QuestionCode = "q_001",
            SelectedKey = "C",
            LearnerId = 1,
        });

        // 选错：判定不正确，但作答后允许揭示正确答案与解析
        Assert.False(result.IsCorrect);
        Assert.Equal("B", result.CorrectKey);
        Assert.Equal("方甲", result.CorrectContent);
        Assert.Equal("C", result.Options.Single(o => o.Key == "C").Key);

        await using var db = new FamilyDbContext(options);
        var attempt = await db.QuizAttempts.SingleAsync();
        Assert.False(attempt.IsCorrect);
        Assert.Equal("C", attempt.SelectedKey);
    }

    [Fact]
    public async Task SubmitAnswerAsync_InvalidKey_JudgesIncorrect()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());
        await QuizBankTestSupport.SeedLearnerAsync(options, learnerId: 1);

        // 无效 key（不在选项中）：纯确定性判定 → 不正确，不抛异常
        var result = await service.SubmitAnswerAsync(QuizBankTestSupport.BankCode, new QuizAnswerSubmitDto
        {
            QuestionCode = "q_001",
            SelectedKey = "Z",
            LearnerId = 1,
        });

        Assert.False(result.IsCorrect);
        Assert.Equal("B", result.CorrectKey);

        await using var db = new FamilyDbContext(options);
        var attempt = await db.QuizAttempts.SingleAsync();
        Assert.False(attempt.IsCorrect);
        Assert.Equal("Z", attempt.SelectedKey);
    }

    [Fact]
    public async Task SubmitAnswerAsync_UnknownLearner_Throws()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SubmitAnswerAsync(QuizBankTestSupport.BankCode, new QuizAnswerSubmitDto
            {
                QuestionCode = "q_001",
                SelectedKey = "B",
                LearnerId = 999,
            }));
        Assert.Contains("家庭成员不存在", ex.Message);

        // 未写任何答题记录
        await using var db = new FamilyDbContext(options);
        Assert.Equal(0, await db.QuizAttempts.CountAsync());
    }

    [Fact]
    public async Task SubmitAnswerAsync_NullLearner_JudgesWithoutWritingAttempt()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());

        var result = await service.SubmitAnswerAsync(QuizBankTestSupport.BankCode, new QuizAnswerSubmitDto
        {
            QuestionCode = "q_001",
            SelectedKey = "B",
            LearnerId = null,
        });

        // 判定照常返回，但不落答题记录
        Assert.True(result.IsCorrect);
        Assert.Equal("B", result.CorrectKey);
        await using var db = new FamilyDbContext(options);
        Assert.Equal(0, await db.QuizAttempts.CountAsync());
    }

    [Fact]
    public async Task SubmitAnswerAsync_UnknownQuestion_Throws()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());
        await QuizBankTestSupport.SeedLearnerAsync(options, learnerId: 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SubmitAnswerAsync(QuizBankTestSupport.BankCode, new QuizAnswerSubmitDto
            {
                QuestionCode = "q_ghost",
                SelectedKey = "B",
                LearnerId = 1,
            }));
        Assert.Contains("题目不存在", ex.Message);

        await using var db = new FamilyDbContext(options);
        Assert.Equal(0, await db.QuizAttempts.CountAsync());
    }

    // ============ 统计（总题数/已刷/掌握/待复习/正确率/覆盖率） ============

    [Fact]
    public async Task GetStatsAsync_NoAttempts_ReturnsZeroStats()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());

        var stats = await service.GetStatsAsync(QuizBankTestSupport.BankCode);

        Assert.Equal(QuizBankTestSupport.BankCode, stats.Overall.BankCode);
        Assert.Equal(3, stats.Overall.QuestionCount);
        Assert.Equal(0, stats.Overall.AnsweredCount);
        Assert.Equal(0, stats.Overall.MasteredCount);
        Assert.Equal(0, stats.Overall.WrongCount);
        Assert.Equal(0, stats.Overall.TotalAttempts);
        Assert.Equal(0, stats.Overall.CorrectAttempts);
        Assert.Equal(0, stats.Overall.Accuracy);
        Assert.Equal(0, stats.Overall.Coverage);
        Assert.Null(stats.Overall.LastAnsweredAt);
        Assert.Empty(stats.Learners);
    }

    [Fact]
    public async Task GetStatsAsync_LatestAttemptPerQuestion_DrivesMasteredAndWrong()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());
        await QuizBankTestSupport.SeedLearnerAsync(options, learnerId: 1);

        // 第一次答对
        await SubmitAsync(service, "q_001", "B", 1);
        var afterCorrect = (await service.GetStatsAsync(QuizBankTestSupport.BankCode)).Overall;
        Assert.Equal(3, afterCorrect.QuestionCount);
        Assert.Equal(1, afterCorrect.AnsweredCount);
        Assert.Equal(1, afterCorrect.MasteredCount);
        Assert.Equal(0, afterCorrect.WrongCount);
        Assert.Equal(1, afterCorrect.TotalAttempts);
        Assert.Equal(1, afterCorrect.CorrectAttempts);
        Assert.Equal(100, afterCorrect.Accuracy);
        Assert.Equal(33.3, afterCorrect.Coverage);
        Assert.NotNull(afterCorrect.LastAnsweredAt);

        // 同一题再答错：仍只算 1 道已刷题，最近一次为错 → 待复习
        await SubmitAsync(service, "q_001", "A", 1);
        var afterWrong = (await service.GetStatsAsync(QuizBankTestSupport.BankCode)).Overall;
        Assert.Equal(1, afterWrong.AnsweredCount);
        Assert.Equal(0, afterWrong.MasteredCount);
        Assert.Equal(1, afterWrong.WrongCount);
        Assert.Equal(2, afterWrong.TotalAttempts);
        Assert.Equal(50, afterWrong.Accuracy);

        // 再答对：回到已掌握
        await SubmitAsync(service, "q_001", "B", 1);
        var afterReCorrect = (await service.GetStatsAsync(QuizBankTestSupport.BankCode)).Overall;
        Assert.Equal(1, afterReCorrect.AnsweredCount);
        Assert.Equal(1, afterReCorrect.MasteredCount);
        Assert.Equal(0, afterReCorrect.WrongCount);
        Assert.Equal(3, afterReCorrect.TotalAttempts);
        Assert.Equal(66.7, afterReCorrect.Accuracy);
    }

    [Fact]
    public async Task GetStatsAsync_GroupsByLearner()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());
        await QuizBankTestSupport.SeedLearnerAsync(options, learnerId: 1);
        await QuizBankTestSupport.SeedLearnerAsync(options, learnerId: 2);

        await SubmitAsync(service, "q_001", "B", 1);
        await SubmitAsync(service, "q_001", "A", 2);

        var bundle = await service.GetStatsAsync(QuizBankTestSupport.BankCode);

        Assert.Equal(1, bundle.Overall.AnsweredCount);
        Assert.Equal(0, bundle.Overall.MasteredCount);
        Assert.Equal(1, bundle.Overall.WrongCount);
        Assert.Equal(2, bundle.Overall.TotalAttempts);
        Assert.Equal(50, bundle.Overall.Accuracy);

        Assert.Equal(2, bundle.Learners.Count);
        var learner1 = bundle.Learners.Single(s => s.LearnerId == 1);
        Assert.Equal("小明", learner1.LearnerName);
        Assert.Equal(1, learner1.MasteredCount);
        Assert.Equal(0, learner1.WrongCount);
        Assert.Equal(100, learner1.Accuracy);

        var learner2 = bundle.Learners.Single(s => s.LearnerId == 2);
        Assert.Equal(0, learner2.MasteredCount);
        Assert.Equal(1, learner2.WrongCount);
        Assert.Equal(0, learner2.Accuracy);
    }

    [Fact]
    public async Task GetStatsAsync_UnknownBank_Throws()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetStatsAsync("ghost_bank"));
        Assert.Contains("题库不存在", ex.Message);
    }

    private static Task<QuizAnswerResultDto> SubmitAsync(
        QuizBankService service, string questionCode, string key, int learnerId)
        => service.SubmitAnswerAsync(QuizBankTestSupport.BankCode, new QuizAnswerSubmitDto
        {
            QuestionCode = questionCode,
            SelectedKey = key,
            LearnerId = learnerId,
        });

    // ============ 删除题库（题库下线：条目/题目/答题记录一并清理） ============

    [Fact]
    public async Task DeleteBankAsync_RemovesBankWithItemsQuestionsAndAttempts()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());
        await QuizBankTestSupport.SeedLearnerAsync(options, learnerId: 1);
        await SubmitAsync(service, "q_001", "B", 1);
        await SubmitAsync(service, "q_002", "C", 1);

        var result = await service.DeleteBankAsync(QuizBankTestSupport.BankCode);

        Assert.Equal(QuizBankTestSupport.BankCode, result.BankCode);
        Assert.Equal("经方测试库", result.BankName);
        Assert.Equal(2, result.ItemsDeleted);
        Assert.Equal(3, result.QuestionsDeleted);
        Assert.Equal(2, result.AttemptsDeleted);

        await using var db = new FamilyDbContext(options);
        Assert.Equal(0, await db.QuizBanks.CountAsync());
        Assert.Equal(0, await db.QuizItems.CountAsync());
        Assert.Equal(0, await db.QuizQuestions.CountAsync());
        Assert.Equal(0, await db.QuizAttempts.CountAsync());

        // 该行业下已无题库 → 行业一并清理（生成页下拉不出现孤立行业）
        Assert.True(result.IndustryRemoved);
        Assert.Equal(0, await db.QuizIndustries.CountAsync());

        Assert.Empty(await service.GetOverviewsAsync());
    }

    [Fact]
    public async Task DeleteBankAsync_OtherBanksUnaffected_IndustryKept()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());

        // 同行业第二个题库
        var second = QuizBankTestSupport.CreateSamplePackage();
        second.Bank = new QuizBankInfo { Code = "jf_second", Name = "第二题库", Version = "v1" };
        second.Items = [new QuizItemDto { ItemCode = "f_gamma", ItemType = "formula", Name = "方丙" }];
        second.Questions = [QuizBankTestSupport.NewQuestion("s_q_001", itemCode: "f_gamma")];
        await service.ImportPackageAsync(second);

        var result = await service.DeleteBankAsync(QuizBankTestSupport.BankCode);

        Assert.Equal(2, result.ItemsDeleted);
        Assert.Equal(3, result.QuestionsDeleted);
        Assert.Equal(0, result.AttemptsDeleted);
        Assert.False(result.IndustryRemoved);

        await using var db = new FamilyDbContext(options);
        Assert.Equal(1, await db.QuizBanks.CountAsync());
        Assert.Equal("jf_second", (await db.QuizBanks.SingleAsync()).Code);
        Assert.Equal(1, await db.QuizItems.CountAsync());
        Assert.Equal(1, await db.QuizQuestions.CountAsync());
        Assert.Equal(1, await db.QuizIndustries.CountAsync());

        // 剩下的题库仍可正常刷题
        var views = await service.DrawQuestionsAsync("jf_second", 10);
        Assert.Equal("s_q_001", Assert.Single(views).QuestionCode);
    }

    [Fact]
    public async Task DeleteBankAsync_UnknownBank_Throws()
    {
        var service = QuizBankTestSupport.CreateService(QuizBankTestSupport.CreateInMemoryOptions());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteBankAsync("ghost"));
        Assert.Contains("题库不存在", ex.Message);
    }

    [Fact]
    public async Task DeleteBankAsync_BlankCode_Throws()
    {
        var service = QuizBankTestSupport.CreateService(QuizBankTestSupport.CreateInMemoryOptions());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteBankAsync("  "));
        Assert.Contains("题库代码不能为空", ex.Message);
    }

    [Fact]
    public async Task DeleteBankAsync_ThenReimport_SameBankRecreated()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());
        await service.DeleteBankAsync(QuizBankTestSupport.BankCode);

        // 删库后重新导入同一包：应重新建库建行（不是「已存在」误判）
        var result = await service.ImportPackageAsync(QuizBankTestSupport.CreateSamplePackage());

        Assert.Equal(2, result.ItemsImported);
        Assert.Equal(3, result.QuestionsImported);
        var overviews = await service.GetOverviewsAsync();
        Assert.Equal(3, Assert.Single(overviews).QuestionCount);
    }
}