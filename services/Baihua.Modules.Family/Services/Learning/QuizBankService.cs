using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Baihua.Contracts.Learn;
using Baihua.Data;
using Baihua.Data.Entities;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 多行业选择题题库服务：导入（题库包文件，幂等 upsert）/ 导出 / 抽题 / 判定 / 答题记录。
///
/// 设计要点：
/// - <b>判定零 AI</b>：正确项由 OptionsJson 数据静态声明，判定＝比对所选 key，纯确定性。
/// - <b>行业无关</b>：本服务不包含任何中医语义；中医（经方）只是数据实例。
/// - <b>数据驱动</b>：内容随题库包 JSON 文件整体进出，代码不随内容变化。
/// </summary>
public class QuizBankService
{
    private readonly IDbContextFactory<FamilyDbContext> _dbFactory;

    /// <summary>题库 JSON 序列化选项（仓库规范：camelCase + 大小写不敏感）</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public QuizBankService(IDbContextFactory<FamilyDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    // ─────────────────────────────────────────
    // 导入 / 导出（题库包文件）
    // ─────────────────────────────────────────

    /// <summary>
    /// 导入题库包（幂等 upsert）：行业/题库按 code 定位，条目/题目按 code upsert。
    /// 重复导入同一包＝内容刷新（内容以包为准），不产生重复行。
    /// </summary>
    public async Task<QuizBankImportResultDto> ImportPackageAsync(QuizBankPackage package, CancellationToken ct = default)
    {
        var result = new QuizBankImportResultDto
        {
            BankCode = package.Bank.Code,
        };

        using var db = await _dbFactory.CreateDbContextAsync(ct);

        // 1. 行业 upsert
        var industry = await db.QuizIndustries.SingleOrDefaultAsync(x => x.Code == package.Industry.Code, ct);
        if (industry == null)
        {
            industry = new QuizIndustry { Code = package.Industry.Code, Name = package.Industry.Name, Description = package.Industry.Name + "题库行业" };
            db.QuizIndustries.Add(industry);
        }
        else
        {
            industry.Name = package.Industry.Name;
            industry.UpdatedAt = DateTime.UtcNow;
        }

        // 2. 题库 upsert
        var bank = await db.QuizBanks.SingleOrDefaultAsync(
            x => x.IndustryId == industry.Id && x.Code == package.Bank.Code, ct);
        if (bank == null)
        {
            // 新建题库时 industry.Id 尚未生成：先保存拿到 Id
            await db.SaveChangesAsync(ct);
            bank = new QuizBank
            {
                IndustryId = industry.Id,
                Code = package.Bank.Code,
                Name = package.Bank.Name,
                Version = package.Bank.Version,
                Description = package.Bank.Description,
            };
            db.QuizBanks.Add(bank);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            bank.Name = package.Bank.Name;
            bank.Version = package.Bank.Version;
            bank.Description = package.Bank.Description;
            bank.UpdatedAt = DateTime.UtcNow;
        }

        // 3. 知识条目 upsert（按 ItemCode）
        // itemIdMap 必须预先装入库内既有条目：增量包常常只带题目（items 为空），条目定义在早先的批次里，
        // 只看本包会把题目引用的 itemCode 静默置空（导出/按方剂关联就断了，且不报错）。
        var itemCodes = package.Items.Select(x => x.ItemCode).ToList();
        var referencedItemCodes = package.Questions
            .Select(x => x.ItemCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!)
            .Distinct()
            .ToList();
        var lookupItemCodes = itemCodes.Concat(referencedItemCodes).Distinct().ToList();
        var existingItems = await db.QuizItems
            .Where(x => x.BankId == bank.Id && lookupItemCodes.Contains(x.ItemCode))
            .ToDictionaryAsync(x => x.ItemCode, ct);

        var itemIdMap = existingItems.ToDictionary(x => x.Key, x => x.Value.Id);
        foreach (var dto in package.Items)
        {
            if (string.IsNullOrWhiteSpace(dto.ItemCode) || string.IsNullOrWhiteSpace(dto.Name))
            {
                result.Warnings.Add($"条目缺少 ItemCode/Name，已跳过：{dto.Name ?? dto.ItemCode ?? "?"}");
                result.ItemsSkipped++;
                continue;
            }

            var contentJson = JsonSerializer.Serialize(dto.Content ?? new QuizItemContentDto(), JsonOptions);
            if (existingItems.TryGetValue(dto.ItemCode, out var entity))
            {
                entity.ItemType = dto.ItemType;
                entity.Name = dto.Name;
                entity.Summary = dto.Summary;
                entity.ContentJson = contentJson;
                entity.UpdatedAt = DateTime.UtcNow;
                itemIdMap[dto.ItemCode] = entity.Id;
            }
            else
            {
                var neu = new QuizItem
                {
                    BankId = bank.Id,
                    ItemCode = dto.ItemCode,
                    ItemType = dto.ItemType,
                    Name = dto.Name,
                    Summary = dto.Summary,
                    ContentJson = contentJson,
                };
                db.QuizItems.Add(neu);
                itemIdMap[dto.ItemCode] = 0; // 占位，SaveChanges 后回填
            }
            result.ItemsImported++;
        }
        await db.SaveChangesAsync(ct);

        // 回填新增条目的 Id
        foreach (var neu in db.QuizItems.Local.Where(it => it.BankId == bank.Id))
        {
            if (itemIdMap.TryGetValue(neu.ItemCode, out var id) && id == 0) itemIdMap[neu.ItemCode] = neu.Id;
        }

        // 4. 题目 upsert（按 QuestionCode）
        var questionCodes = package.Questions.Select(x => x.QuestionCode).ToList();
        var existingQuestions = await db.QuizQuestions
            .Where(x => x.BankId == bank.Id && questionCodes.Contains(x.QuestionCode))
            .ToDictionaryAsync(x => x.QuestionCode, ct);

        foreach (var dto in package.Questions)
        {
            if (string.IsNullOrWhiteSpace(dto.QuestionCode) || string.IsNullOrWhiteSpace(dto.Stem))
            {
                result.Warnings.Add($"题目缺少 QuestionCode/Stem，已跳过：{dto.QuestionCode ?? "?"}");
                result.QuestionsSkipped++;
                continue;
            }
            if (dto.Options.Count(o => o.IsCorrect) != 1)
            {
                result.Warnings.Add($"题目正确项数≠1，已跳过：{dto.QuestionCode}");
                result.QuestionsSkipped++;
                continue;
            }
            if (dto.Options.Count < 2)
            {
                result.Warnings.Add($"题目选项不足 2 个，已跳过：{dto.QuestionCode}");
                result.QuestionsSkipped++;
                continue;
            }

            var optionsJson = JsonSerializer.Serialize(dto.Options, JsonOptions);
            var stemJson = dto.StemDetail == null ? null : JsonSerializer.Serialize(dto.StemDetail, JsonOptions);
            var discriminationJson = dto.Discrimination == null || dto.Discrimination.Count == 0
                ? null
                : JsonSerializer.Serialize(dto.Discrimination, JsonOptions);
            int? itemId = dto.ItemCode != null && itemIdMap.TryGetValue(dto.ItemCode, out var iid) && iid > 0
                ? iid
                : null;

            if (existingQuestions.TryGetValue(dto.QuestionCode, out var entity))
            {
                entity.ItemId = itemId;
                entity.QuestionType = dto.QuestionType;
                entity.Target = dto.Target;
                entity.Stem = dto.Stem;
                entity.StemJson = stemJson;
                entity.OptionsJson = optionsJson;
                entity.Analysis = dto.Analysis;
                entity.DiscriminationJson = discriminationJson;
                entity.Difficulty = dto.Difficulty;
                entity.SourceRef = dto.SourceRef;
                entity.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                db.QuizQuestions.Add(new QuizQuestion
                {
                    BankId = bank.Id,
                    ItemId = itemId,
                    QuestionCode = dto.QuestionCode,
                    QuestionType = dto.QuestionType,
                    Target = dto.Target,
                    Stem = dto.Stem,
                    StemJson = stemJson,
                    OptionsJson = optionsJson,
                    Analysis = dto.Analysis,
                    DiscriminationJson = discriminationJson,
                    Difficulty = dto.Difficulty,
                    SourceRef = dto.SourceRef,
                });
            }
            result.QuestionsImported++;
        }

        await db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>导出题库包（百花→花阁/花记 的交换文件）</summary>
    public async Task<QuizBankPackage> ExportPackageAsync(string industryCode, string bankCode, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);

        var industry = await db.QuizIndustries.SingleOrDefaultAsync(x => x.Code == industryCode, ct)
            ?? throw new InvalidOperationException($"行业不存在：{industryCode}");
        var bank = await db.QuizBanks.SingleOrDefaultAsync(x => x.IndustryId == industry.Id && x.Code == bankCode, ct)
            ?? throw new InvalidOperationException($"题库不存在：{industryCode}/{bankCode}");

        var items = await db.QuizItems.Where(x => x.BankId == bank.Id).ToListAsync(ct);
        var questions = await db.QuizQuestions.Where(x => x.BankId == bank.Id).ToListAsync(ct);

        var package = new QuizBankPackage
        {
            FormatVersion = 1,
            Industry = new QuizIndustryInfo { Code = industry.Code, Name = industry.Name },
            Bank = new QuizBankInfo { Code = bank.Code, Name = bank.Name, Version = bank.Version, Description = bank.Description },
        };

        foreach (var it in items)
        {
            package.Items.Add(new QuizItemDto
            {
                ItemCode = it.ItemCode,
                ItemType = it.ItemType,
                Name = it.Name,
                Summary = it.Summary,
                Content = JsonSerializer.Deserialize<QuizItemContentDto>(it.ContentJson, JsonOptions),
            });
        }

        var itemIdToCode = items.ToDictionary(x => x.Id, x => x.ItemCode);
        foreach (var q in questions)
        {
            package.Questions.Add(new QuizQuestionDto
            {
                QuestionCode = q.QuestionCode,
                ItemCode = q.ItemId.HasValue && itemIdToCode.TryGetValue(q.ItemId.Value, out var code) ? code : null,
                QuestionType = q.QuestionType,
                Target = q.Target,
                Stem = q.Stem,
                StemDetail = q.StemJson == null ? null : JsonSerializer.Deserialize<QuizStemDetailDto>(q.StemJson, JsonOptions),
                Options = JsonSerializer.Deserialize<List<QuizOptionDto>>(q.OptionsJson, JsonOptions) ?? [],
                Analysis = q.Analysis,
                Discrimination = q.DiscriminationJson == null
                    ? null
                    : JsonSerializer.Deserialize<List<QuizDiscriminationDto>>(q.DiscriminationJson, JsonOptions),
                Difficulty = q.Difficulty,
                SourceRef = q.SourceRef,
            });
        }

        return package;
    }

    // ─────────────────────────────────────────
    // 刷题（抽题 / 作答判定 / 记录）
    // ─────────────────────────────────────────

    /// <summary>题库概览列表（行业→题库）</summary>
    public async Task<List<QuizBankOverviewDto>> GetOverviewsAsync(CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = from bank in db.QuizBanks
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
                    };
        return (await query.ToListAsync(ct))
            .Select(x => new QuizBankOverviewDto
            {
                IndustryCode = x.Code,
                IndustryName = x.Name,
                BankCode = x.BankCode,
                BankName = x.BankName,
                Version = x.Version,
                ItemCount = x.ItemCount,
                QuestionCount = x.QuestionCount,
            })
            .ToList();
    }

    /// <summary>随机抽题（作答前不泄露正确项）</summary>
    public async Task<List<QuizQuestionViewDto>> DrawQuestionsAsync(
        string bankCode, int count, IReadOnlyCollection<string>? excludeQuestionCodes = null, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var bank = await db.QuizBanks.SingleOrDefaultAsync(x => x.Code == bankCode, ct)
            ?? throw new InvalidOperationException($"题库不存在：{bankCode}");

        var query = db.QuizQuestions.Where(q => q.BankId == bank.Id);
        if (excludeQuestionCodes is { Count: > 0 })
        {
            query = query.Where(q => !excludeQuestionCodes.Contains(q.QuestionCode));
        }

        // 随机抽 count 题（题库规模千级，全量取回后洗牌可接受）
        var all = await query.Select(q => new
        {
            q.QuestionCode,
            q.ItemId,
            q.Target,
            q.Stem,
            q.StemJson,
            q.Difficulty,
            q.SourceRef,
            q.OptionsJson,
        }).ToListAsync(ct);

        return all
            .OrderBy(_ => Guid.NewGuid())
            .Take(count)
            .Select(q => new QuizQuestionViewDto
            {
                QuestionCode = q.QuestionCode,
                ItemCode = null, // 视图层不需要条目代码（避免从题目反查答案线索）
                Target = q.Target,
                Stem = q.Stem,
                StemDetail = q.StemJson == null ? null : JsonSerializer.Deserialize<QuizStemDetailDto>(q.StemJson, JsonOptions),
                Difficulty = q.Difficulty,
                SourceRef = q.SourceRef,
                Options = (JsonSerializer.Deserialize<List<QuizOptionDto>>(q.OptionsJson, JsonOptions) ?? [])
                    .Select(o => new QuizOptionViewDto { Key = o.Key, Content = o.Content })
                    .OrderBy(o => o.Key)
                    .ToList(),
            })
            .ToList();
    }

    /// <summary>
    /// 作答判定（零 AI：比对所选 key）＋ 写答题记录（仅家庭成员）。
    /// </summary>
    public async Task<QuizAnswerResultDto> SubmitAnswerAsync(string bankCode, QuizAnswerSubmitDto submit, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);

        var question = await db.QuizQuestions.SingleOrDefaultAsync(
            q => q.Bank!.Code == bankCode && q.QuestionCode == submit.QuestionCode, ct)
            ?? throw new InvalidOperationException($"题目不存在：{bankCode}/{submit.QuestionCode}");

        var options = JsonSerializer.Deserialize<List<QuizOptionDto>>(question.OptionsJson, JsonOptions) ?? [];
        var correct = options.SingleOrDefault(o => o.IsCorrect);
        var selected = options.SingleOrDefault(o => o.Key == submit.SelectedKey);
        var isCorrect = correct != null && selected != null && selected.Key == correct.Key;

        // 答题记录：仅家庭成员（无匿名模式——用户口径）
        if (submit.LearnerId is int learnerId)
        {
            var learnerExists = await db.LearnerProfiles.AnyAsync(l => l.Id == learnerId, ct);
            if (!learnerExists)
                throw new InvalidOperationException($"家庭成员不存在：{learnerId}");

            db.QuizAttempts.Add(new QuizAttempt
            {
                LearnerId = learnerId,
                QuestionId = question.Id,
                BankId = question.BankId,
                SelectedKey = submit.SelectedKey,
                IsCorrect = isCorrect,
                TimeCostMs = submit.TimeCostMs,
                SessionId = submit.SessionId,
            });
            await db.SaveChangesAsync(ct);
        }

        return new QuizAnswerResultDto
        {
            IsCorrect = isCorrect,
            CorrectKey = correct?.Key ?? "",
            CorrectContent = correct?.Content ?? "",
            Options = options.OrderBy(o => o.Key).ToList(),
            Analysis = question.Analysis,
            Discrimination = question.DiscriminationJson == null
                ? null
                : JsonSerializer.Deserialize<List<QuizDiscriminationDto>>(question.DiscriminationJson, JsonOptions),
        };
    }
}