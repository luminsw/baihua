using System.Text.Json;
using Baihua.Contracts.Learn;
using Baihua.Data;
using Baihua.Family.Tests.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Baihua.Family.Tests.Learning;

/// <summary>
/// 题库源数据回归测试：data/tcm-quiz/*.json（B0 经方包 + B1~B10 增量包）是学习模块的唯一内容来源，
/// 也是导出给花阁/花记的交换格式。数据坏了不会编译失败、也不会被单测发现，只会在导入时静默跳过，
/// 因此这里直接拿仓库里的真实题库包做导入回归。
///
/// 锁定的不变量（内容增长不需要改测试——断言不变量而非固定题数）：
///   1. 每个包都能按 QuizBankPackage 契约解析、行业/题库 code 一致；
///   2. 跨文件 questionCode / itemCode 唯一（重复会在 upsert 时被静默覆盖，题就丢了）；
///   3. 导入零跳过、零告警（正确项≠1 / 缺题干 / 选项不足 2 都会被服务拒收）；
///   4. 落库行数 == 去重后的 code 数，题目与条目的关联不因 code 拼写错误而静默丢失；
///   5. 逐题：正确项恰好 1 个、选项 ≥2、key 唯一非空、题干与解析非空；
///   6. 导出往返：导出的包能还原全部条目与题目。
/// </summary>
public class QuizBankSourceDataTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>仓库内题库源文件：B0 基线包在前，B1~B10 增量包按序在后（导入顺序＝版本推进顺序）。</summary>
    private static List<string> SourceFiles()
    {
        var baseFile = RepoPath.FindUp(Path.Combine("data", "tcm-quiz", "jingfang-package.json"));
        var dir = Path.GetDirectoryName(baseFile)!;
        var batches = Directory.GetFiles(dir, "*.json")
            .Where(f => !string.Equals(Path.GetFileName(f), "jingfang-package.json", StringComparison.Ordinal))
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToList();
        return [baseFile, .. batches];
    }

    private static QuizBankPackage Load(string path)
        => JsonSerializer.Deserialize<QuizBankPackage>(File.ReadAllText(path), JsonOptions)
           ?? throw new InvalidOperationException($"题库包解析失败：{Path.GetFileName(path)}");

    [Fact]
    public void SourcePackages_ParseAndHaveNoDuplicateCodes()
    {
        var files = SourceFiles();
        Assert.True(files.Count >= 2, "题库源文件不足：至少应包含基线包与增量包");

        var questionOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        var itemOwner = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var package = Load(file);

            // 同属一个行业/题库：分散到别的 code 会让 /learn 列表里多出一堆空题库
            Assert.False(string.IsNullOrWhiteSpace(package.Industry.Code), $"{name}: industry.code 为空");
            Assert.False(string.IsNullOrWhiteSpace(package.Bank.Code), $"{name}: bank.code 为空");
            Assert.False(string.IsNullOrWhiteSpace(package.Bank.Name), $"{name}: bank.name 为空");
            Assert.Equal("tcm", package.Industry.Code);
            Assert.Equal("jingfang", package.Bank.Code);
            Assert.False(string.IsNullOrWhiteSpace(package.Bank.Version), $"{name}: bank.version 为空");

            foreach (var q in package.Questions)
            {
                Assert.False(string.IsNullOrWhiteSpace(q.QuestionCode), $"{name}: 存在空 questionCode");
                Assert.False(questionOwner.TryGetValue(q.QuestionCode, out var owner),
                    $"questionCode 跨文件重复：{q.QuestionCode}（{name} 与 {owner}）");
                questionOwner[q.QuestionCode] = name;
            }

            foreach (var i in package.Items)
            {
                Assert.False(string.IsNullOrWhiteSpace(i.ItemCode), $"{name}: 存在空 itemCode");
                Assert.False(itemOwner.TryGetValue(i.ItemCode, out var owner),
                    $"itemCode 跨文件重复：{i.ItemCode}（{name} 与 {owner}）");
                itemOwner[i.ItemCode] = name;
            }
        }
    }

    [Fact]
    public async Task SourcePackages_ImportCleanly_AndPassDataRedLines()
    {
        var files = SourceFiles();
        var packages = files.Select(Load).ToList();

        var expectedItems = packages.SelectMany(p => p.Items).Select(i => i.ItemCode).ToHashSet(StringComparer.Ordinal);
        var expectedQuestions = packages.SelectMany(p => p.Questions)
            .Select(q => new { q.QuestionCode, q.ItemCode })
            .GroupBy(x => x.QuestionCode, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToDictionary(q => q.QuestionCode, q => q.ItemCode, StringComparer.Ordinal);

        var options = QuizBankTestSupport.CreateInMemoryOptions();
        var service = QuizBankTestSupport.CreateService(options);

        foreach (var package in packages)
        {
            var result = await service.ImportPackageAsync(package);
            Assert.Equal("jingfang", result.BankCode);
            Assert.Empty(result.Warnings);
            Assert.Equal(package.Items.Count, result.ItemsImported);
            Assert.Equal(0, result.ItemsSkipped);
            Assert.Equal(package.Questions.Count, result.QuestionsImported);
            Assert.Equal(0, result.QuestionsSkipped);
        }

        await using var db = new FamilyDbContext(options);

        // 行业/题库唯一（并非按文件重复建库）
        var industry = await db.QuizIndustries.SingleAsync();
        Assert.Equal("tcm", industry.Code);
        var bank = await db.QuizBanks.SingleAsync();
        Assert.Equal("jingfang", bank.Code);
        Assert.Equal(industry.Id, bank.IndustryId);

        // 行数 == 去重 code 数（若有重复 code，行数会少于期望，即题目被静默覆盖）
        var items = await db.QuizItems.ToListAsync();
        var questions = await db.QuizQuestions.ToListAsync();
        Assert.Equal(expectedItems.Count, items.Count);
        Assert.Equal(expectedQuestions.Count, questions.Count);

        // 内容规模基线（今日 219 方 / 259 题；新增内容只会更大，不会更小）
        Assert.True(items.Count >= 200, $"条目数异常偏少：{items.Count}（基线 219）");
        Assert.True(questions.Count >= 250, $"题目数异常偏少：{questions.Count}（基线 259）");

        // 已入库 code 集合与源文件一致（无漏导/错码）
        Assert.Equal(expectedItems, items.Select(i => i.ItemCode).ToHashSet(StringComparer.Ordinal));
        Assert.Equal(expectedQuestions.Keys.ToHashSet(StringComparer.Ordinal),
            questions.Select(q => q.QuestionCode).ToHashSet(StringComparer.Ordinal));

        // 题目→条目关联：源文件里 itemCode 命中条目的题必须真的关联上（拼错会被服务静默置空）
        var expectedLinked = expectedQuestions
            .Where(kv => kv.Value != null && expectedItems.Contains(kv.Value))
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);
        var actualLinked = questions.Where(q => q.ItemId != null)
            .Select(q => q.QuestionCode)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(expectedLinked.SetEquals(actualLinked),
            $"题目与条目的关联不一致：期望 {expectedLinked.Count} 条，实际 {actualLinked.Count} 条");

        // 逐题数据红线（判定零 AI 的前提）
        foreach (var q in questions)
        {
            var opts = JsonSerializer.Deserialize<List<QuizOptionDto>>(q.OptionsJson, JsonOptions) ?? [];
            Assert.True(opts.Count >= 2, $"{q.QuestionCode}: 选项不足 2 个");
            Assert.True(opts.Count(o => o.IsCorrect) == 1, $"{q.QuestionCode}: 正确项不是恰好 1 个");
            Assert.Equal(opts.Count, opts.Select(o => o.Key).Distinct(StringComparer.Ordinal).Count());
            Assert.All(opts, o =>
            {
                Assert.False(string.IsNullOrWhiteSpace(o.Key), $"{q.QuestionCode}: 选项 key 为空");
                Assert.False(string.IsNullOrWhiteSpace(o.Content), $"{q.QuestionCode}: 选项内容为空");
            });
            Assert.False(string.IsNullOrWhiteSpace(q.Stem), $"{q.QuestionCode}: 题干为空");
            Assert.False(string.IsNullOrWhiteSpace(q.Analysis), $"{q.QuestionCode}: 解析为空");
            Assert.False(string.IsNullOrWhiteSpace(q.Target), $"{q.QuestionCode}: target 为空");
        }

        // 导出往返：导出给花阁/花记的包必须能还原全部条目与题目
        var exported = await service.ExportPackageAsync("tcm", "jingfang");
        Assert.Equal("tcm", exported.Industry.Code);
        Assert.Equal("jingfang", exported.Bank.Code);
        Assert.Equal(items.Count, exported.Items.Count);
        Assert.Equal(questions.Count, exported.Questions.Count);
        Assert.All(exported.Questions, q => Assert.Equal(1, q.Options.Count(o => o.IsCorrect)));
    }
}
