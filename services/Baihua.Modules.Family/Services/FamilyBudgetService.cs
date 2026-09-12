using System.Text.Json;
using Baihua.Contracts;
using Baihua.Contracts.Budget;
using Microsoft.Extensions.Logging;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 家庭记账服务：收支记录 CRUD + 月度统计。
/// 持久化到 $BAIHUA_HOME/budget/transactions.json（单文件 JSON，家庭场景足够）。
/// </summary>
public class FamilyBudgetService
{
    private readonly ILogger<FamilyBudgetService> _logger;
    private readonly string _filePath;
    private readonly object _lock = new();
    private List<BudgetTransaction>? _cache;

    public FamilyBudgetService(ILogger<FamilyBudgetService> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(BaihuaPaths.Home, "budget", "transactions.json");
    }

    private List<BudgetTransaction> Load()
    {
        if (_cache != null) return _cache;
        lock (_lock)
        {
            if (_cache != null) return _cache;
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    _cache = JsonSerializer.Deserialize<List<BudgetTransaction>>(json) ?? new List<BudgetTransaction>();
                }
                else
                {
                    _cache = new List<BudgetTransaction>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "读取记账数据失败: {Path}", _filePath);
                _cache = new List<BudgetTransaction>();
            }
            return _cache;
        }
    }

    private void Save()
    {
        lock (_lock)
        {
            var dir = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(dir);
            // 原子写：先写临时文件再替换
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _filePath, overwrite: true);
        }
    }

    /// <summary>按月份筛选记录（不传则全部），日期倒序</summary>
    public Task<List<BudgetTransaction>> GetTransactionsAsync(int? year, int? month)
    {
        var list = Load();
        if (year.HasValue && month.HasValue)
        {
            list = list.Where(t => t.Date.Year == year.Value && t.Date.Month == month.Value).ToList();
        }
        return Task.FromResult(list.OrderByDescending(t => t.Date).ThenByDescending(t => t.CreatedAt).ToList());
    }

    public async Task<BudgetTransaction> AddAsync(BudgetCreateRequest req)
    {
        if (req.Amount <= 0) throw new ArgumentException("金额必须大于 0");
        var type = req.Type?.ToLowerInvariant() == "income" ? "income" : "expense";
        if (string.IsNullOrWhiteSpace(req.Category)) throw new ArgumentException("请选择分类");

        var tx = new BudgetTransaction
        {
            Type = type,
            Amount = req.Amount,
            Category = req.Category.Trim(),
            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
            Date = req.Date ?? DateTime.Today
        };

        var list = Load();
        lock (_lock)
        {
            list.Add(tx);
            Save();
        }
        await Task.CompletedTask;
        return tx;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var list = Load();
        lock (_lock)
        {
            var removed = list.RemoveAll(t => t.Id == id) > 0;
            if (removed) Save();
            return removed;
        }
    }

    /// <summary>月度汇总（默认本月）+ 累计收支</summary>
    public Task<BudgetSummary> GetSummaryAsync(int? year, int? month)
    {
        var now = DateTime.Now;
        var y = year ?? now.Year;
        var m = month ?? now.Month;

        var list = Load();
        var monthItems = list.Where(t => t.Date.Year == y && t.Date.Month == m).ToList();

        var summary = new BudgetSummary { Year = y, Month = m };
        summary.MonthIncome = monthItems.Where(t => t.Type == "income").Sum(t => t.Amount);
        summary.MonthExpense = monthItems.Where(t => t.Type == "expense").Sum(t => t.Amount);
        summary.MonthBalance = summary.MonthIncome - summary.MonthExpense;
        summary.TotalIncome = list.Where(t => t.Type == "income").Sum(t => t.Amount);
        summary.TotalExpense = list.Where(t => t.Type == "expense").Sum(t => t.Amount);

        summary.CategoryStats = monthItems
            .Where(t => t.Type == "expense")
            .GroupBy(t => t.Category)
            .Select(g => new BudgetCategoryStat
            {
                Category = g.Key,
                Amount = g.Sum(t => t.Amount),
                Count = g.Count()
            })
            .OrderByDescending(s => s.Amount)
            .ToList();

        return Task.FromResult(summary);
    }
}
