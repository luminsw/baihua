using Baihua.Core.Models;
using Baihua.Core.Services;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Baihua.Contracts.Stock;
using Baihua.Modules.Family.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 股票 AI 建议服务：拉取 A 股实时行情（东方财富免费接口），
/// 交给配置的 AI 模型分析，输出买入推荐 / 持仓卖出建议。
/// 分层缓存：行情快照 30s TTL；AI 分析结果按条件 10min / 按代码 5min TTL（refresh 可绕过）。
/// 仅供学习参考，不构成投资建议。
/// </summary>
public class StockAdvisorService
{
    private readonly AiClientService _aiClient;
    private readonly AiSettingsService _aiSettings;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IDistributedCache _cache;
    private readonly ILogger<StockAdvisorService> _logger;

    private static readonly TimeSpan QuoteCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RecCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan EvalCacheTtl = TimeSpan.FromMinutes(5);

    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36" } }
    };

    public StockAdvisorService(
        AiClientService aiClient,
        AiSettingsService aiSettings,
        IHttpClientFactory httpFactory,
        IDistributedCache cache,
        ILogger<StockAdvisorService> logger)
    {
        _aiClient = aiClient;
        _aiSettings = aiSettings;
        _httpFactory = httpFactory;
        _cache = cache;
        _logger = logger;
    }

    #region 候选池（A 股各行业龙头，供 AI 筛选）

    private sealed record StockMeta(string Code, string Name, string Industry);

    /// <summary>候选股票池：代码 + 名称 + 行业</summary>
    private static readonly StockMeta[] CandidatePool =
    [
        // 银行
        new("600036", "招商银行", "银行"), new("601398", "工商银行", "银行"),
        new("601288", "农业银行", "银行"), new("600000", "浦发银行", "银行"),
        new("601166", "兴业银行", "银行"), new("600016", "民生银行", "银行"),
        // 白酒
        new("600519", "贵州茅台", "白酒"), new("000858", "五粮液", "白酒"),
        new("000568", "泸州老窖", "白酒"), new("600809", "山西汾酒", "白酒"),
        new("002304", "洋河股份", "白酒"),
        // 新能源 / 电池 / 光伏
        new("300750", "宁德时代", "电池"), new("002594", "比亚迪", "新能源车"),
        new("601012", "隆基绿能", "光伏"), new("300274", "阳光电源", "光伏"),
        new("601865", "福莱特", "光伏"), new("688599", "天合光能", "光伏"),
        new("600438", "通威股份", "光伏"),
        // 医药
        new("600276", "恒瑞医药", "医药"), new("300760", "迈瑞医疗", "医疗器械"),
        new("000538", "云南白药", "中药"), new("600196", "复星医药", "医药"),
        new("603259", "药明康德", "医药外包"), new("300015", "爱尔眼科", "医疗服务"),
        new("600085", "同仁堂", "中药"), new("000999", "华润三九", "中药"),
        // 科技 / 半导体 / 通信
        new("688981", "中芯国际", "半导体"), new("002371", "北方华创", "半导体设备"),
        new("603501", "韦尔股份", "半导体"), new("688012", "中微公司", "半导体设备"),
        new("002415", "海康威视", "安防"), new("000063", "中兴通讯", "通信"),
        new("002230", "科大讯飞", "AI"), new("300308", "中际旭创", "光模块"),
        new("688111", "金山办公", "软件"), new("002475", "立讯精密", "消费电子"),
        new("002027", "分众传媒", "传媒"), new("300418", "昆仑万维", "AI应用"),
        // 消费 / 家电 / 食品
        new("000333", "美的集团", "家电"), new("000651", "格力电器", "家电"),
        new("600690", "海尔智家", "家电"), new("603288", "海天味业", "调味品"),
        new("600887", "伊利股份", "乳制品"), new("000895", "双汇发展", "食品"),
        new("600600", "青岛啤酒", "啤酒"), new("002714", "牧原股份", "养殖"),
        new("300498", "温氏股份", "养殖"),
        // 运营商 / 互联网
        new("600941", "中国移动", "运营商"), new("601728", "中国电信", "运营商"),
        new("600050", "中国联通", "运营商"),
        // 汽车
        new("601633", "长城汽车", "汽车"), new("600104", "上汽集团", "汽车"),
        new("002920", "德赛西威", "汽车电子"),
        // 化工 / 材料
        new("600309", "万华化学", "化工"), new("002812", "恩捷股份", "锂电材料"),
        // 军工
        new("600893", "航发动力", "军工"), new("002179", "中航光电", "军工"),
        new("600760", "中航沈飞", "军工"),
        // 有色 / 资源 / 能源
        new("601899", "紫金矿业", "有色"), new("600111", "北方稀土", "稀土"),
        new("603993", "洛阳钼业", "有色"), new("601088", "中国神华", "煤炭"),
        new("600028", "中国石化", "石油"), new("601857", "中国石油", "石油"),
        new("600900", "长江电力", "电力"),
        // 基建 / 建材
        new("601668", "中国建筑", "基建"), new("601390", "中国中铁", "基建"),
        new("600585", "海螺水泥", "建材"),
        // 券商 / 保险
        new("600030", "中信证券", "券商"), new("601688", "华泰证券", "券商"),
        new("300059", "东方财富", "券商"), new("601318", "中国平安", "保险"),
        new("601601", "中国太保", "保险"),
        // 地产 / 消费服务
        new("600048", "保利发展", "地产"), new("000002", "万科A", "地产"),
        new("601888", "中国中免", "免税"), new("600009", "上海机场", "机场"),
        // 航运 / 钢铁 / 铁路
        new("601919", "中远海控", "航运"), new("600018", "上港集团", "港口"),
        new("600019", "宝钢股份", "钢铁"), new("601006", "大秦铁路", "铁路"),
    ];

    private static string ToSecId(string code) =>
        code.StartsWith("6") ? "1." + code : "0." + code;

    private static string ToSymbol(string code) =>
        code.StartsWith("6") ? "sh" + code : "sz" + code;

    #endregion

    #region 行情拉取（东方财富免费接口）

    /// <summary>批量拉取实时行情（30s 缓存，key 按代码集合签名）</summary>
    public async Task<List<StockQuote>> FetchQuotesAsync(IEnumerable<string> codes, CancellationToken ct)
    {
        var key = "stock:quotes:" + string.Join(',', codes.Distinct().OrderBy(x => x, StringComparer.Ordinal));
        var cached = await _cache.GetStringAsync(key, ct);
        if (cached != null)
            return JsonSerializer.Deserialize<List<StockQuote>>(cached) ?? new List<StockQuote>();

        var result = await FetchQuotesCoreAsync(codes, ct);
        if (result.Count > 0)
        {
            await _cache.SetStringAsync(key, JsonSerializer.Serialize(result),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = QuoteCacheTtl }, ct);
        }
        return result;
    }

    private async Task<List<StockQuote>> FetchQuotesCoreAsync(IEnumerable<string> codes, CancellationToken ct)
    {
        var list = codes.ToList();
        var result = new List<StockQuote>();
        // 东财接口单次最多约 100 个 secid
        foreach (var chunk in list.Chunk(80))
        {
            var secids = string.Join(',', chunk.Select(ToSecId));
            var url = "https://push2.eastmoney.com/api/qt/ulist.np/get?fltt=2&invt=2&fields=f2,f3,f6,f8,f9,f12,f14,f20,f23&secids=" + secids;
            try
            {
                using var resp = await SharedHttp.GetAsync(url, ct);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("diff", out var diff) &&
                    diff.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in diff.EnumerateArray())
                    {
                        var meta = FindMeta(item);
                        var quote = ParseQuote(item, meta);
                        if (quote.HasData) result.Add(quote);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "拉取行情失败: {Url}", url);
            }
        }
        return result;
    }

    private StockMeta? FindMeta(JsonElement item)
    {
        var code = item.TryGetProperty("f12", out var c) ? c.GetString() : null;
        if (string.IsNullOrEmpty(code)) return null;
        return CandidatePool.FirstOrDefault(x => x.Code == code);
    }

    private static StockQuote ParseQuote(JsonElement item, StockMeta? meta)
    {
        var q = new StockQuote
        {
            Code = item.TryGetProperty("f12", out var c) ? c.GetString() ?? "" : "",
            Name = item.TryGetProperty("f14", out var n) ? n.GetString() ?? "" : "",
            Industry = meta?.Industry ?? ""
        };
        q.HasData = item.TryGetProperty("f2", out var price) && price.ValueKind == JsonValueKind.Number;
        if (!q.HasData) return q;

        q.Price = GetDecimal(item, "f2");
        q.ChangePercent = GetDecimal(item, "f3");
        q.TurnoverRate = GetDecimal(item, "f8");
        q.Pe = GetDecimal(item, "f9");
        q.Pb = GetDecimal(item, "f23");
        q.MarketCapYi = GetDecimal(item, "f20") / 1e8m;
        // f6 成交额（元）→ 亿
        q.AmountYi = GetDecimal(item, "f6") / 1e8m;
        return q;
    }

    private static decimal GetDecimal(JsonElement item, string prop)
    {
        if (item.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Number)
            return el.GetDecimal();
        return 0;
    }

    /// <summary>拉取单只股票近 N 日 K 线（前复权）</summary>
    public async Task<List<string>?> FetchKlineAsync(string code, int days, CancellationToken ct)
    {
        var url = $"https://push2his.eastmoney.com/api/qt/stock/kline/get?secid={ToSecId(code)}" +
                  $"&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56,f57,f58&klt=101&fqt=1&end=20500101&lmt={days}";
        try
        {
            using var resp = await SharedHttp.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("klines", out var klines) &&
                klines.ValueKind == JsonValueKind.Array)
            {
                return klines.EnumerateArray().Select(k => k.GetString() ?? "").ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "拉取 K 线失败: {Url}", url);
        }
        return null;
    }

    #endregion

    #region AI 分析

    private (AiProviderConfig provider, string model) ResolveProvider(string? providerId, string? model)
    {
        var provider = !string.IsNullOrWhiteSpace(providerId)
            ? _aiSettings.GetAiProvider(providerId)
            : _aiSettings.GetMainAiProvider();
        if (provider == null)
            throw new Exception("未配置 AI 提供商，请先在 AI 设置中配置");
        var resolvedModel = !string.IsNullOrWhiteSpace(model)
            ? model
            : _aiSettings.GetModelForProvider(provider.Id);
        return (provider, resolvedModel);
    }

    /// <summary>AI 推荐 10 只股票（按建议度排名，支持方向/策略/行业/周期/自定义提示词；结果 10min 缓存，refresh 绕过）</summary>
    public async Task<StockRecommendationResponse> GetRecommendationsAsync(
        string? providerId, string? model, string? strategy, string? industry, string? horizon,
        string? prompt, string? direction, bool refresh = false, CancellationToken ct = default)
    {
        var isSell = direction?.ToLowerInvariant() == "sell";
        // 缓存 key：方向 + 条件 + 提示词摘要（提示词变化 → 不同结果）
        var promptKey = string.IsNullOrWhiteSpace(prompt)
            ? ""
            : "|p:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)))[..16];
        var cacheKey = $"stock:rec:{(isSell ? "sell" : "buy")}|{strategy ?? ""}|{industry?.Trim() ?? ""}|{horizon ?? ""}{promptKey}";
        if (!refresh)
        {
            var cached = await _cache.GetStringAsync(cacheKey, ct);
            if (cached != null)
            {
                var hit = JsonSerializer.Deserialize<StockRecommendationResponse>(cached);
                if (hit != null) return hit;
            }
        }

        var (provider, resolvedModel) = ResolveProvider(providerId, model);

        // 候选池按行业过滤
        var pool = string.IsNullOrWhiteSpace(industry)
            ? CandidatePool
            : CandidatePool.Where(x => x.Industry == industry.Trim()).ToArray();
        if (pool.Length == 0)
            throw new Exception($"候选池中没有「{industry}」行业，可用行业: {string.Join("、", GetIndustries())}");

        var quotes = await FetchQuotesAsync(pool.Select(x => x.Code), ct);
        if (quotes.Count == 0)
            throw new Exception("行情数据拉取失败（网络或接口异常），请稍后重试");

        var strategyDesc = DescribeStrategy(strategy);
        var horizonDesc = DescribeHorizon(horizon);
        var industryText = string.IsNullOrWhiteSpace(industry) ? "全部行业" : industry.Trim();
        var userPrompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt.Trim();

        var table = string.Join('\n', quotes.Select((q, i) =>
            $"{i + 1}. {q.Code} {q.Name} [{q.Industry}] 现价{q.Price:N2} 涨跌{q.ChangePercent:+#.##;-#.##;0}% " +
            $"换手{q.TurnoverRate:F2}% PE{q.Pe:F1} PB{q.Pb:F2} 市值{q.MarketCapYi:F0}亿"));

        var taskInstruction = isSell
            ? "请从以上候选池中选出 10 只当前建议卖出或规避的股票（基本面转弱、估值过高、技术破位或量能异常），按卖出紧迫度从高到低排序。"
            : "请从以上候选池中选出 10 只当前最值得买入的股票，按建议度从高到低排序。";
        var jsonExample = isSell
            ? "[{\"code\":\"600519\",\"name\":\"贵州茅台\",\"score\":85,\"action\":\"卖出\",\"reason\":\"...\"}]"
            : "[{\"code\":\"600519\",\"name\":\"贵州茅台\",\"score\":85,\"action\":\"买入\",\"reason\":\"...\"}]";
        var scoreDesc = isSell ? "score 为 0-100 整数（卖出紧迫度）。" : "score 为 0-100 整数（建议度）。";

        var promptText = $$"""
            你是 A 股分析师。以下是候选股票的实时行情快照：
            {{table}}

            分析策略：{{strategyDesc}}
            持有周期：{{horizonDesc}}
            行业范围：{{industryText}}
            {{(userPrompt != null ? "用户附加要求：" + userPrompt : "")}}

            {{taskInstruction}}
            依据：遵循上述分析策略与持有周期，结合行情指标（估值、量能、趋势）与你的基本面知识综合判断。
            股票只能从上面的候选列表中选取，代码必须与列表严格一致，禁止编造候选列表之外的股票。
            只输出一个 JSON 数组（不要 markdown 代码块，不要任何其他文字）：
            {{jsonExample}}
            {{scoreDesc}} reason 控制在 25 字以内。
            """;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "你是严谨的 A 股分析师，只依据给定数据与知识做分析，输出严格 JSON。"),
            new(ChatRole.User, promptText)
        };
        var options = new ChatOptions { Temperature = 0.3f, MaxOutputTokens = 3000 };

        var raw = await _aiClient.GetChatResponseWithAutoStartAsync(
            provider, resolvedModel, messages, options, ct, operation: "stock");

        var text = raw.Text ?? "";
        var recommendations = ParseRecommendations(text);
        var response = new StockRecommendationResponse
        {
            Recommendations = recommendations,
            Model = $"{provider.Name}/{resolvedModel}",
            Strategy = strategy,
            Industry = string.IsNullOrWhiteSpace(industry) ? null : industry.Trim(),
            Horizon = horizon,
            Direction = isSell ? "sell" : "buy",
            Prompt = userPrompt,
            GeneratedAt = DateTime.Now,
            Raw = text
        };

        await _cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(response),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = RecCacheTtl }, ct);
        return response;
    }

    /// <summary>可用行业列表（候选池去重）</summary>
    public static List<string> GetIndustries() =>
        CandidatePool.Select(x => x.Industry).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

    private static string DescribeStrategy(string? strategy) => strategy?.ToLowerInvariant() switch
    {
        "value" => "价值投资：偏好低估值（低 PE/PB）、高 ROE、现金流稳健的标的",
        "growth" => "成长股：偏好高成长赛道、营收利润增长潜力大的标的",
        "technical" => "技术面：偏好近期趋势向上、量能配合、动量强的标的",
        "dividend" => "高股息：偏好分红率高、低波动、防御性强的标的",
        _ => "综合：结合估值、成长、技术面与基本面均衡判断"
    };

    private static string DescribeHorizon(string? horizon) => horizon?.ToLowerInvariant() switch
    {
        "short" => "短期持有（数日至数周）：侧重技术面、量能与近期动量",
        "long" => "长期持有（数月至数年）：侧重基本面、估值与行业景气度",
        _ => "中长期持有：均衡考虑基本面与趋势"
    };

    /// <summary>AI 评估已购股票是否卖出（结果 5min 缓存，refresh 绕过）</summary>
    public async Task<StockEvaluationResponse> EvaluateHoldingAsync(
        string code, string? providerId, string? model, bool refresh = false, CancellationToken ct = default)
    {
        var cacheKey = $"stock:eval:{code.Trim()}";
        if (!refresh)
        {
            var cached = await _cache.GetStringAsync(cacheKey, ct);
            if (cached != null)
            {
                var hit = JsonSerializer.Deserialize<StockEvaluationResponse>(cached);
                if (hit != null) return hit;
            }
        }

        var (provider, resolvedModel) = ResolveProvider(providerId, model);
        var quotes = await FetchQuotesAsync(new[] { code }, ct);
        var quote = quotes.FirstOrDefault(q => q.Code == code);
        var kline = await FetchKlineAsync(code, 30, ct);

        var klineText = kline == null || kline.Count == 0
            ? "（K 线数据不可用）"
            : string.Join(", ", kline.Select(k => k.Split(',') switch
            {
                [var date, var open, var close, var high, var low, ..] =>
                    $"{date}(收{close})",
                _ => k
            }));

        var quoteText = quote?.HasData == true
            ? $"现价 {quote.Price:N2} 元，涨跌 {quote.ChangePercent:+#.##;-#.##;0}%，换手 {quote.TurnoverRate:F2}%，PE {quote.Pe:F1}，PB {quote.Pb:F2}，总市值 {quote.MarketCapYi:F0} 亿"
            : "（行情数据不可用）";

        var prompt = $$"""
            你是 A 股分析师。用户持有以下股票，请判断现在是否应该卖出：
            股票：{{quote?.Name ?? code}}（{{code}}）
            当前行情：{{quoteText}}
            近 30 个交易日收盘：{{klineText}}

            请结合近期走势、估值与量能给出卖出建议。
            只输出一个 JSON 对象（不要 markdown 代码块，不要任何其他文字）：
            {"action":"sell"|"hold","confidence":80,"reason":"..."}
            action 只能是 sell 或 hold。confidence 为 0-100 整数。reason 控制在 40 字以内。
            """;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "你是严谨的 A 股分析师，只依据给定数据与知识做分析，输出严格 JSON。"),
            new(ChatRole.User, prompt)
        };
        var options = new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 1500 };

        var raw = await _aiClient.GetChatResponseWithAutoStartAsync(
            provider, resolvedModel, messages, options, ct, operation: "stock");

        var text = raw.Text ?? "";
        var parsed = ParseEvaluation(text);

        var response = new StockEvaluationResponse
        {
            Code = code,
            Name = quote?.Name ?? "",
            Action = parsed.action,
            Confidence = parsed.confidence,
            Reason = parsed.reason,
            Raw = text
        };

        await _cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(response),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = EvalCacheTtl }, ct);
        return response;
    }

    #endregion

    #region JSON 容错解析

    /// <summary>从模型输出中提取 JSON 文本（去 markdown 围栏、取最大括号块）</summary>
    private static string? TryExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        var fence = Regex.Match(t, "```(?:json)?\\s*([\\s\\S]*?)```");
        if (fence.Success) t = fence.Groups[1].Value.Trim();

        // 对象 vs 数组：取最外层的完整括号块
        var arrStart = t.IndexOf('[');
        var arrEnd = t.LastIndexOf(']');
        var objStart = t.IndexOf('{');
        var objEnd = t.LastIndexOf('}');

        if (arrStart >= 0 && arrEnd > arrStart && (objStart < 0 || arrStart < objStart))
            return t[arrStart..(arrEnd + 1)];
        if (objStart >= 0 && objEnd > objStart)
            return t[objStart..(objEnd + 1)];
        return null;
    }

    private static List<StockRecommendation> ParseRecommendations(string text)
    {
        var json = TryExtractJson(text);
        if (json == null) return new List<StockRecommendation>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var arr = root.ValueKind == JsonValueKind.Array
                ? root
                : root.TryGetProperty("recommendations", out var r) ? r : default;
            if (arr.ValueKind != JsonValueKind.Array) return new List<StockRecommendation>();

            // 只保留候选池内的代码（模型可能编造池外股票），并按代码去重
            var validCodes = new HashSet<string>(CandidatePool.Select(x => x.Code));
            var seen = new HashSet<string>();
            var list = new List<StockRecommendation>();
            foreach (var item in arr.EnumerateArray())
            {
                var code = GetStr(item, "code").Trim();
                if (!validCodes.Contains(code)) continue;
                if (!seen.Add(code)) continue;
                list.Add(new StockRecommendation
                {
                    Code = code,
                    Name = GetStr(item, "name"),
                    Score = item.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0,
                    Action = GetStr(item, "action"),
                    Reason = GetStr(item, "reason")
                });
            }
            // 按建议度降序
            return list.OrderByDescending(x => x.Score).ToList();
        }
        catch (Exception)
        {
            // 解析失败返回空，由调用方展示原始输出
            return new List<StockRecommendation>();
        }
    }

    private static (string action, int confidence, string reason) ParseEvaluation(string text)
    {
        var json = TryExtractJson(text);
        if (json == null) return ("", 0, "");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var action = GetStr(root, "action").ToLowerInvariant();
            if (action is not ("sell" or "hold")) action = "";
            var confidence = root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
            return (action, confidence, GetStr(root, "reason"));
        }
        catch
        {
            return ("", 0, "");
        }
    }

    private static string GetStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    #endregion
}
