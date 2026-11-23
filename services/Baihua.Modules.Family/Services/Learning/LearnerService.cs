using Microsoft.EntityFrameworkCore;
using Baihua.Data;
using Baihua.Data.Entities;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 学习者档案管理服务
/// </summary>
public class LearnerService
{
    private readonly IDbContextFactory<FamilyDbContext> _dbFactory;
    private readonly ILogger<LearnerService> _logger;

    public LearnerService(IDbContextFactory<FamilyDbContext> dbFactory, ILogger<LearnerService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<List<LearnerProfile>> GetAllAsync()
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        return await db.LearnerProfiles.OrderBy(l => l.Id).ToListAsync();
    }

    public async Task<LearnerProfile?> GetDefaultAsync()
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        return await db.LearnerProfiles.FirstOrDefaultAsync(l => l.IsDefault)
               ?? await db.LearnerProfiles.FirstOrDefaultAsync();
    }

    public async Task<LearnerProfile?> GetByIdAsync(int id)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        return await db.LearnerProfiles.FindAsync(id);
    }

    public async Task<LearnerProfile> CreateAsync(string name, string avatarEmoji = "👤", string color = "#007bff")
    {
        // FAM-12：输入校验（空名/全空格/超长/非法 emoji → 阻止创建 + 抛异常）
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("学习者名称不能为空", nameof(name));
        name = name.Trim();
        if (name.Length > 20)
            throw new ArgumentException("学习者名称不能超过 20 个字符", nameof(name));

        if (string.IsNullOrWhiteSpace(avatarEmoji))
            throw new ArgumentException("头像不能为空", nameof(avatarEmoji));
        if (!IsSingleEmoji(avatarEmoji))
            throw new ArgumentException("头像必须是单个 emoji 字符", nameof(avatarEmoji));

        using var db = await _dbFactory.CreateDbContextAsync();

        // 防重名（审计发现：重复「小明」污染家庭首页/看板/互考/排行）
        var exists = await db.LearnerProfiles.AnyAsync(l => l.Name == name);
        if (exists)
            throw new ArgumentException($"已存在同名成员「{name}」，请换一个名字", nameof(name));

        var learner = new LearnerProfile
        {
            Name = name,
            AvatarEmoji = avatarEmoji,
            Color = color,
            IsDefault = !await db.LearnerProfiles.AnyAsync()
        };
        db.LearnerProfiles.Add(learner);
        await db.SaveChangesAsync();
        _logger.LogInformation("创建学习者: {Name}", name);
        return learner;
    }

    /// <summary>
    /// 判断字符串是否为单个 emoji（FAM-12）：
    /// 必须仅由 emoji 区段字符组成且至多 2 个 Rune（单 emoji + 可选修饰符如肤色/ZWJ）；
    /// 普通文本、多个独立 emoji 拼接均视为非法。
    /// </summary>
    private static bool IsSingleEmoji(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        // 逐 Rune 遍历：单 emoji 通常占 1-2 个 Rune（如 👍+肤色、👨👩👧 ZWJ 序列）；
        // 但多个独立 emoji（如 "👍👍"）每个都是独立图形簇，Rune 数会 > 2 或每个单独算 emoji。
        // 判定：所有 Rune 都是 emoji 区段字符，且 Rune 数 ≤ 2（覆盖单 emoji + 修饰符），
        // 或者整个字符串是单个 ZWJ 序列（Rune 数可能 > 2 但由 ZWJ 连接）。
        var runes = s.EnumerateRunes().ToArray();
        if (runes.Length == 0) return false;

        // 统计：是否有 ZWJ 连接序列
        var hasZwj = runes.Any(r => r.Value == 0x200D);

        // 无 ZWJ：要求 1-2 个 Rune 且全部是 emoji
        // （2 Rune 时第二个必须是修饰符——变体选择符 FE0F 或肤色修饰符 1F3FB-1F3FF，
        //   否则如 "👍👍" 两个独立 emoji 视为非法）
        if (!hasZwj)
        {
            if (runes.Length == 1)
            {
                return IsEmojiCodepoint(runes[0].Value);
            }
            if (runes.Length == 2)
            {
                var first = runes[0].Value;
                var second = runes[1].Value;
                var isModifier = (second >= 0xFE00 && second <= 0xFE0F)  // 变体选择符
                    || (second >= 0x1F3FB && second <= 0x1F3FF);         // 肤色修饰符
                return IsEmojiCodepoint(first) && isModifier;
            }
            return false;
        }

        // 有 ZWJ：要求全部 Rune 是 emoji 或 ZWJ，且至少一个 emoji
        var emojiCount = 0;
        foreach (var r in runes)
        {
            var cp = r.Value;
            if (cp == 0x200D) continue;
            if (IsEmojiCodepoint(cp)) { emojiCount++; continue; }
            return false;
        }
        return emojiCount >= 2; // ZWJ 序列至少连接两个 emoji
    }

    private static bool IsEmojiCodepoint(int cp)
    {
        return (cp >= 0x1F000 && cp <= 0x1FAFF)   // 杂项符号和 pictograph
            || (cp >= 0x2600 && cp <= 0x27BF)     // 杂项符号/装饰符号/箭头
            || (cp >= 0xFE00 && cp <= 0xFE0F)     // 变体选择符（肤色等）
            || (cp >= 0x1F1E6 && cp <= 0x1F1FF)   // 区域指示符（国旗）
            || cp == 0x20E3                       // 组合围键符（keycap）
            || (cp >= 0x2B00 && cp <= 0x2BFF)     // 杂项符号和箭头
            || (cp >= 0x1F900 && cp <= 0x1F9FF);  // 补充符号和 pictograph
    }

    public async Task<bool> SetDefaultAsync(int id)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var all = await db.LearnerProfiles.ToListAsync();
        foreach (var l in all) l.IsDefault = (l.Id == id);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var learner = await db.LearnerProfiles.FindAsync(id);
        if (learner == null) return false;
        db.LearnerProfiles.Remove(learner);
        await db.SaveChangesAsync();
        return true;
    }
}
