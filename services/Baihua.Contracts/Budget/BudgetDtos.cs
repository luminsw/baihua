namespace Baihua.Contracts.Budget;

/// <summary>家庭记账：单笔收支记录</summary>
public class BudgetTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>income 收入 / expense 支出</summary>
    public string Type { get; set; } = "expense";

    public decimal Amount { get; set; }

    /// <summary>分类（餐饮/工资/…）</summary>
    public string Category { get; set; } = "";

    public string? Note { get; set; }

    /// <summary>记账日期（按天）</summary>
    public DateTime Date { get; set; } = DateTime.Today;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>新增记录请求</summary>
public class BudgetCreateRequest
{
    public string Type { get; set; } = "expense";
    public decimal Amount { get; set; }
    public string Category { get; set; } = "";
    public string? Note { get; set; }
    public DateTime? Date { get; set; }
}

/// <summary>月度汇总</summary>
public class BudgetSummary
{
    public int Year { get; set; }
    public int Month { get; set; }

    public decimal MonthIncome { get; set; }
    public decimal MonthExpense { get; set; }
    public decimal MonthBalance { get; set; }

    public decimal TotalIncome { get; set; }
    public decimal TotalExpense { get; set; }

    /// <summary>当月分类汇总（支出为主，按金额降序）</summary>
    public List<BudgetCategoryStat> CategoryStats { get; set; } = new();
}

public class BudgetCategoryStat
{
    public string Category { get; set; } = "";
    public decimal Amount { get; set; }
    public int Count { get; set; }
}

/// <summary>预置分类（前端下拉用）</summary>
public static class BudgetCategories
{
    public static readonly string[] Expense =
        ["餐饮", "交通", "购物", "居住", "水电", "教育", "医疗", "娱乐", "人情", "宠物", "其他"];

    public static readonly string[] Income =
        ["工资", "奖金", "理财", "兼职", "红包", "其他"];
}
