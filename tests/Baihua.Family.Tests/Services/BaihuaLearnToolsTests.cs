using System.Text.Json;
using Baihua.Contracts.Learn;
using Baihua.Data;
using Baihua.Modules.Family.Services.Mcp;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// MCP 题库工具测试（薄封装层）：
///   - 返回必须是 camelCase JSON 文本，且失败时 ok=false 而非抛异常（MCP 客户端靠文本判错）；
///   - import 默认 append 模式且校验错误整体拒绝（不写半截数据）；
///   - dry_run 绝不落库。
/// </summary>
public class BaihuaLearnToolsTests
{
    private static readonly JsonSerializerOptions JsonOpts = JsonSerializerOptions.Web;

    private static (BaihuaLearnTools Tools, DbContextOptions<FamilyDbContext> Options) CreateTools()
    {
        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);
        return (new BaihuaLearnTools(service), options);
    }

    private static string Serialize(QuizBankPackage package) => JsonSerializer.Serialize(package, JsonOpts);

    [Fact]
    public async Task List_EmptyDatabase_ReturnsOk()
    {
        var (tools, _) = CreateTools();

        var json = await tools.List();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(0, root.GetProperty("banks").GetArrayLength());
        Assert.Equal(0, root.GetProperty("industries").GetArrayLength());
    }

    [Fact]
    public async Task Industries_ReturnsBuiltInProfiles()
    {
        var (tools, _) = CreateTools();

        var json = await tools.Industries();

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var industries = doc.RootElement.GetProperty("industries");
        var codes = Enumerable.Range(0, industries.GetArrayLength())
            .Select(i => industries[i].GetProperty("code").GetString())
            .ToList();
        Assert.Contains("tcm", codes);
        Assert.Contains("programming", codes);
        Assert.Contains("general", codes);

        var tcm = Enumerable.Range(0, industries.GetArrayLength())
            .Select(i => industries[i])
            .First(p => p.GetProperty("code").GetString() == "tcm");
        var targetCodes = Enumerable.Range(0, tcm.GetProperty("targets").GetArrayLength())
            .Select(i => tcm.GetProperty("targets")[i].GetProperty("code").GetString())
            .ToList();
        Assert.Contains("formula", targetCodes);
        Assert.Contains("pattern", targetCodes);
    }

    [Fact]
    public void PromptTemplate_ProgrammingIndustry_UsesNeutralTerms()
    {
        var (tools, _) = CreateTools();

        var json = tools.PromptTemplate(
            "programming", "编程", "cs_ai", "C# 题库", "异步与并发", target: "debug");

        using var doc = JsonDocument.Parse(json);
        var prompt = doc.RootElement.GetProperty("prompt").GetString();
        Assert.Contains("选原因 / 修复", prompt);
        Assert.DoesNotContain("方剂", prompt);
    }

    [Fact]
    public async Task List_AfterImport_ReportsCounts()
    {
        var (tools, _) = CreateTools();
        await tools.ImportPackage(Serialize(QuizBankTestSupport.CreateSamplePackage()));

        var json = await tools.List();

        using var doc = JsonDocument.Parse(json);
        var bank = doc.RootElement.GetProperty("banks")[0];
        Assert.Equal("jf_test", bank.GetProperty("bankCode").GetString());
        Assert.Equal(2, bank.GetProperty("itemCount").GetInt32());
        Assert.Equal(3, bank.GetProperty("questionCount").GetInt32());
    }

    [Fact]
    public void PromptTemplate_ReturnsPrompt()
    {
        var (tools, _) = CreateTools();

        var json = tools.PromptTemplate(
            "tcm", "中医", "jingfang", "经方", "桂枝汤类方",
            target: "formula", question_count: 10, difficulty: 3,
            reference_scope: "伤寒论·太阳篇", distractor_strategy: "same_scope", code_prefix: "aix");

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var prompt = doc.RootElement.GetProperty("prompt").GetString();
        Assert.Contains("桂枝汤类方", prompt);
        Assert.Contains("aix_q_1", prompt);
    }

    [Fact]
    public void PromptTemplate_EmptyTopic_ReturnsError()
    {
        var (tools, _) = CreateTools();

        var json = tools.PromptTemplate("tcm", "中医", "b", "题库", "  ");

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("topic", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ImportPackage_InvalidJson_ReturnsError()
    {
        var (tools, _) = CreateTools();

        var json = await tools.ImportPackage("{ not json");

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("JSON 解析失败", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ImportPackage_MissingAnalysis_RejectedAndNothingWritten()
    {
        var (tools, options) = CreateTools();
        var package = QuizBankTestSupport.CreateSamplePackage();
        package.Questions[0].Analysis = "";

        var json = await tools.ImportPackage(Serialize(package));

        using (var doc = JsonDocument.Parse(json))
        {
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("isValid").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("validation").GetProperty("errors").GetArrayLength() > 0);
        }

        await using var db = new FamilyDbContext(options);
        Assert.Equal(0, await db.QuizBanks.CountAsync());
    }

    [Fact]
    public async Task ImportPackage_ValidPackage_WritesAndReturnsCounts()
    {
        var (tools, options) = CreateTools();

        var json = await tools.ImportPackage(Serialize(QuizBankTestSupport.CreateSamplePackage()));

        using (var doc = JsonDocument.Parse(json))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            var result = doc.RootElement.GetProperty("result");
            Assert.Equal(2, result.GetProperty("itemsImported").GetInt32());
            Assert.Equal(3, result.GetProperty("questionsImported").GetInt32());
        }

        await using var db = new FamilyDbContext(options);
        Assert.Equal(3, await db.QuizQuestions.CountAsync());
    }

    [Fact]
    public async Task ImportPackage_DryRun_DoesNotWrite()
    {
        var (tools, options) = CreateTools();

        var json = await tools.ImportPackage(Serialize(QuizBankTestSupport.CreateSamplePackage()), dry_run: true);

        using (var doc = JsonDocument.Parse(json))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("dryRun").GetBoolean());
        }

        await using var db = new FamilyDbContext(options);
        Assert.Equal(0, await db.QuizBanks.CountAsync());
        Assert.Equal(0, await db.QuizQuestions.CountAsync());
    }

    [Fact]
    public async Task ImportPackage_AppendMode_DoesNotOverwrite()
    {
        var (tools, options) = CreateTools();
        await tools.ImportPackage(Serialize(QuizBankTestSupport.CreateSamplePackage()));

        var changed = QuizBankTestSupport.CreateSamplePackage();
        changed.Questions[0].Stem = "被改的题干";
        var json = await tools.ImportPackage(Serialize(changed), append_only: true);

        using (var doc = JsonDocument.Parse(json))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(0, doc.RootElement.GetProperty("result").GetProperty("questionsImported").GetInt32());
            Assert.Equal(3, doc.RootElement.GetProperty("result").GetProperty("questionsSkipped").GetInt32());
        }

        await using var db = new FamilyDbContext(options);
        var q1 = await db.QuizQuestions.SingleAsync(q => q.QuestionCode == "q_001");
        Assert.Equal("患者甲，证见测试主证一。最适宜的方剂是？", q1.Stem);
    }

    [Fact]
    public async Task ExportPackage_UnknownBank_ReturnsError()
    {
        var (tools, _) = CreateTools();

        var json = await tools.ExportPackage("tcm", "missing");

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("行业不存在", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ExportPackage_AfterImport_Roundtrips()
    {
        var (tools, _) = CreateTools();
        await tools.ImportPackage(Serialize(QuizBankTestSupport.CreateSamplePackage()));

        var json = await tools.ExportPackage(QuizBankTestSupport.IndustryCode, QuizBankTestSupport.BankCode);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var package = doc.RootElement.GetProperty("package");
        Assert.Equal(QuizBankTestSupport.BankCode, package.GetProperty("bank").GetProperty("code").GetString());
        Assert.Equal(3, package.GetProperty("questions").GetArrayLength());
    }
}