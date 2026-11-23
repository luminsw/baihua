namespace Baihua.Contracts.Stock;

/// <summary>单只股票的 AI 推荐项</summary>
public class StockRecommendation
{
    /// <summary>股票代码（如 600519）</summary>
    public string Code { get; set; } = "";

    /// <summary>股票名称</summary>
    public string Name { get; set; } = "";

    /// <summary>建议度 0-100（越高越建议买入）</summary>
    public int Score { get; set; }

    /// <summary>建议动作（买入/关注/观望…）</summary>
    public string Action { get; set; } = "";

    /// <summary>理由（简短）</summary>
    public string Reason { get; set; } = "";
}

/// <summary>AI 推荐列表响应</summary>
public class StockRecommendationResponse
{
    public List<StockRecommendation> Recommendations { get; set; } = new();

    /// <summary>使用的模型</summary>
    public string Model { get; set; } = "";

    /// <summary>分析策略：value/growth/technical/dividend/auto</summary>
    public string? Strategy { get; set; }

    /// <summary>行业过滤（null/空 = 全部）</summary>
    public string? Industry { get; set; }

    /// <summary>持有周期：short/long</summary>
    public string? Horizon { get; set; }

    /// <summary>分析方向：buy 建议买入 / sell 建议卖出（默认 buy）</summary>
    public string? Direction { get; set; }

    /// <summary>用户附加提示词（回显）</summary>
    public string? Prompt { get; set; }

    public DateTime GeneratedAt { get; set; } = DateTime.Now;

    /// <summary>AI 原始输出（调试/展示用）</summary>
    public string? Raw { get; set; }
}

/// <summary>持仓评估请求</summary>
public class StockEvaluationRequest
{
    /// <summary>已购股票代码（如 600519）</summary>
    public string Code { get; set; } = "";

    public string? ProviderId { get; set; }
    public string? Model { get; set; }
}

/// <summary>持仓评估响应：是否卖出</summary>
public class StockEvaluationResponse
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>建议：sell 卖出 / hold 持有</summary>
    public string Action { get; set; } = "";

    /// <summary>置信度 0-100</summary>
    public int Confidence { get; set; }

    /// <summary>理由</summary>
    public string Reason { get; set; } = "";

    public string? Raw { get; set; }
}

/// <summary>行情快照（东财接口解析结果）</summary>
public class StockQuote
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Industry { get; set; } = "";

    /// <summary>现价</summary>
    public decimal Price { get; set; }

    /// <summary>涨跌幅 %</summary>
    public decimal ChangePercent { get; set; }

    /// <summary>换手率 %</summary>
    public decimal TurnoverRate { get; set; }

    /// <summary>市盈率</summary>
    public decimal Pe { get; set; }

    /// <summary>市净率</summary>
    public decimal Pb { get; set; }

    /// <summary>总市值（亿元）</summary>
    public decimal MarketCapYi { get; set; }

    /// <summary>成交额（亿元）</summary>
    public decimal AmountYi { get; set; }

    public bool HasData { get; set; }
}
