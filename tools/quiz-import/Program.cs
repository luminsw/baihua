using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Baihua.Contracts.Learn;
using Baihua.Data;
using Baihua.Modules.Family.Services;

// 用法：dotnet run -- [package.json | --check]
// 建表走 DatabaseInitializer（与 Baihua.Server 启动路径一致：缺哪张补哪张，幂等），
// 随后 QuizBankService.ImportPackageAsync 幂等 upsert（重复导入同一包＝内容刷新）。
// --check：只读统计（各题库的条目与题数、目标分布、正确项唯一性体检）。

var isCheck = args.Length > 0 && args[0] == "--check";
if (!isCheck && (args.Length < 1 || !File.Exists(args[0])))
{
    Console.Error.WriteLine("用法：dotnet run -- [package.json | --check]");
    return 1;
}

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
};

// ── 最小 DI：与 Server 相同的 DbContext 注册 + DatabaseInitializer 建表 ──
var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
services.AddDbContext<FamilyDbContext>(o => o.UseNpgsql(DbConnections.Baihua));
services.AddDbContext<VaultDbContext>(o => o.UseNpgsql(DbConnections.Baihua));
services.AddDbContext<AIDbContext>(o => o.UseNpgsql(DbConnections.Baihua));

using var provider = services.BuildServiceProvider();
var logger = provider.GetRequiredService<ILogger<Program>>();

var init = await DatabaseInitializer.InitializeAsync(provider, logger);
if (!isCheck)
{
    foreach (var msg in init.Messages) Console.WriteLine($"  [init] {msg}");
}
if (!init.AllSucceeded)
{
    Console.Error.WriteLine("✗ 数据库初始化未全部成功，中止");
    return 1;
}

using var scope = provider.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<FamilyDbContext>();

if (isCheck)
{
    // ── 只读统计 ──
    var banks = await (from bank in db.QuizBanks
                       join industry in db.QuizIndustries on bank.IndustryId equals industry.Id
                       select new
                       {
                           industry.Code,
                           industry.Name,
                           BankCode = bank.Code,
                           BankName = bank.Name,
                           bank.Version,
                           ItemCount = db.QuizItems.Count(i => i.BankId == bank.Id),
                           QuestionCount = db.QuizQuestions.Count(q => q.BankId == bank.Id),
                       }).ToListAsync();

    if (banks.Count == 0)
    {
        Console.WriteLine("（库中尚无题库）");
        return 0;
    }

    foreach (var b in banks)
    {
        Console.WriteLine($"{b.Name}（{b.Code}）→ {b.BankName}（{b.BankCode}）v{b.Version}：条目 {b.ItemCount}，题目 {b.QuestionCount}");
        var targets = await db.QuizQuestions
            .Where(q => q.BankId == db.QuizBanks.Single(x => x.Code == b.BankCode).Id)
            .GroupBy(q => q.Target)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync();
        Console.WriteLine("  目标分布：" + string.Join("，", targets.Select(t => $"{t.Key}={t.Count}")));
    }

    // 正确项唯一性体检（数据红线：判定零 AI 的前提）

    var allQuestions = await db.QuizQuestions.Select(q => new { q.QuestionCode, q.OptionsJson }).ToListAsync();
    var bad = allQuestions
        .Where(q =>
        {
            var opts = JsonSerializer.Deserialize<List<QuizOptionDto>>(q.OptionsJson, jsonOptions) ?? [];
            return opts.Count(o => o.IsCorrect) != 1 || opts.Count < 2;
        })
        .Select(q => q.QuestionCode)
        .ToList();
    Console.WriteLine(bad.Count == 0
        ? "  正确项唯一性体检：全部通过"
        : $"  ✗ 正确项≠1 的题：{bad.Count} 个（{string.Join(", ", bad.Take(5))}…）");
    return bad.Count == 0 ? 0 : 2;
}

// ── 导入 ──
var package = JsonSerializer.Deserialize<QuizBankPackage>(await File.ReadAllTextAsync(args[0]), jsonOptions!);
if (package is null || string.IsNullOrWhiteSpace(package.Bank.Code))
{
    Console.Error.WriteLine("✗ 题库包解析失败或缺少 bank.code");
    return 1;
}

Console.WriteLine($"题库包：{package.Industry.Name}（{package.Industry.Code}）→ {package.Bank.Name}（{package.Bank.Code}）v{package.Bank.Version}");
Console.WriteLine($"  items={package.Items.Count}  questions={package.Questions.Count}");

var result = await new QuizBankService(new SingleFactory(db)).ImportPackageAsync(package);

Console.WriteLine($"✓ 导入完成：题库 {result.BankCode}");
Console.WriteLine($"  items 导入 {result.ItemsImported}（跳过 {result.ItemsSkipped}）");
Console.WriteLine($"  questions 导入 {result.QuestionsImported}（跳过 {result.QuestionsSkipped}）");
foreach (var w in result.Warnings) Console.WriteLine($"  ⚠ {w}");
return result.Warnings.Count == 0 ? 0 : 2;

sealed class SingleFactory(FamilyDbContext ctx) : IDbContextFactory<FamilyDbContext>
{
    public FamilyDbContext CreateDbContext() => ctx;
}