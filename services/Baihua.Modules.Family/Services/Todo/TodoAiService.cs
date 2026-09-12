using System.Text.Json;
using Baihua.AI.Provider;
using Baihua.Contracts.Todo;
using Baihua.Core.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Baihua.Modules.Family.Services.Todo;

/// <summary>
/// AI 待办生成服务：用户输入一个目标，AI 拆解为一组具体、可执行、带实操指引的待办（预览）。
/// 生成结果不落库，由用户确认后再调用保存接口入库。
/// 本地小模型常忽略长提示词中的 JSON 要求，内置"简化提示词重试"兜底。
/// </summary>
public class TodoAiService
{
    private readonly AiClientService _aiClient;
    private readonly AiSettingsService _aiSettings;
    private readonly ILogger<TodoAiService> _logger;

    public TodoAiService(AiClientService aiClient, AiSettingsService aiSettings, ILogger<TodoAiService> logger)
    {
        _aiClient = aiClient;
        _aiSettings = aiSettings;
        _logger = logger;
    }

    /// <summary>
    /// 调用 AI 把目标拆解为具体待办，返回预览（不保存；保存见 TodoService.CreateGoalWithItemsAsync）。
    /// 失败时返回 Success=false 与面向用户的错误信息。
    /// </summary>
    public async Task<TodoAiResult> GeneratePreviewAsync(string goal, CancellationToken ct = default)
    {
        var trimmed = goal?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Length > 200)
            return TodoAiResult.Fail("目标描述不能为空或过长（最多 200 字）");

        var provider = _aiSettings.GetMainAiProvider();
        if (provider == null)
        {
            _logger.LogWarning("未找到 AI Provider 配置，无法生成待办");
            return TodoAiResult.Fail("未配置 AI 模型，请先在 AI 设置中配置主模型");
        }

        var model = provider.GetMainModel();
        if (string.IsNullOrWhiteSpace(model))
        {
            _logger.LogWarning("主 AI Provider 未配置模型");
            return TodoAiResult.Fail("主 AI Provider 未配置模型，请在 AI 设置中选择模型");
        }

        var chatClient = _aiClient.CreateChatClient(provider.Id, model);
        var options = new ChatOptions { MaxOutputTokens = 1500 };

        // 第一次：完整办事顾问提示词（指导性最强：机构/网站/证件/表单）
        var plan = await TryGeneratePlanAsync(chatClient, options,
            new List<ChatMessage>
            {
                new(ChatRole.System, TodoPlanSystemPrompt),
                new(ChatRole.User, $"我的目标：{trimmed}\n\n请把该目标拆解为可执行的具体待办。")
            }, ct);

        // 本地小模型常忽略长提示词中的 JSON 要求，改用简化提示词重试一次
        if (plan == null)
        {
            _logger.LogWarning("完整提示词未产出 JSON，改用简化提示词重试: {Goal}", trimmed);
            plan = await TryGeneratePlanAsync(chatClient, options,
                new List<ChatMessage>
                {
                    new(ChatRole.User, $"把目标拆解为 4-8 个具体可执行的待办，每个待办附一行执行指引（去哪里、带什么、做什么）。只输出 JSON，不要任何其他文字：{{\"title\":\"目标标题\",\"items\":[{{\"title\":\"具体动作\",\"note\":\"执行指引\"}}]}}。目标：{trimmed}")
                }, ct);
        }

        if (plan == null)
        {
            _logger.LogWarning("AI 未能生成有效的待办计划: {Goal}", trimmed);
            return TodoAiResult.Fail("AI 没有生成有效待办，请换个目标描述再试");
        }

        var goalTitle = plan.Title?.Trim();
        if (string.IsNullOrWhiteSpace(goalTitle))
            goalTitle = trimmed;
        if (goalTitle.Length > 200)
            goalTitle = goalTitle[..200];

        // 与保存规则（TodoService.CreateGoalWithItemsAsync）保持一致：空标题/超长标题/超长指引丢弃
        var items = new List<AiTodoPreviewItemDto>();
        foreach (var i in plan.Items)
        {
            var title = i.Title?.Trim() ?? "";
            if (title.Length == 0 || title.Length > 200)
                continue;
            var note = i.Note;
            string? noteTrimmed = null;
            if (!string.IsNullOrWhiteSpace(note))
            {
                noteTrimmed = note.Trim();
                if (noteTrimmed.Length > 1000)
                    continue; // 指引超长，与保存规则一致：丢弃该项
            }
            items.Add(new AiTodoPreviewItemDto { Title = title, Note = noteTrimmed });
        }

        if (items.Count == 0)
        {
            _logger.LogWarning("AI 生成的待办全部不合法: {Goal}", trimmed);
            return TodoAiResult.Fail("AI 生成的内容不合法，请重试");
        }

        return TodoAiResult.Ok(new AiTodoPreviewDto { Title = goalTitle, Items = items });
    }

    /// <summary>调用一次 AI 并尝试解析为待办计划；解析失败返回 null（不抛异常）</summary>
    private async Task<AiTodoPlan?> TryGeneratePlanAsync(
        IChatClient chatClient, ChatOptions options, List<ChatMessage> messages, CancellationToken ct)
    {
        try
        {
            var response = await chatClient.GetResponseAsync(messages, options, cancellationToken: ct);
            var rawText = response.Text?.Trim() ?? "";
            rawText = ExtractJsonObject(rawText);

            var plan = JsonSerializer.Deserialize<AiTodoPlan>(rawText, JsonHelper.CaseInsensitive);
            if (plan == null || plan.Items == null || plan.Items.Count == 0)
                return null;
            return plan;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户主动取消请求（如关闭页面），不吞掉
            throw;
        }
        catch (OperationCanceledException)
        {
            // 内部超时（如本地模型生成过久）视为本次尝试失败
            _logger.LogWarning("AI 生成待办请求超时");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI 生成待办请求失败");
            return null;
        }
    }

    /// <summary>从 AI 响应文本中提取 JSON 对象部分（兼容 markdown 代码块与前后杂文）</summary>
    internal static string ExtractJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        if (text.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            var end = text.LastIndexOf("```", StringComparison.Ordinal);
            if (end > 7)
                text = text[7..end].Trim();
        }
        else if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var end = text.LastIndexOf("```", StringComparison.Ordinal);
            if (end > 3)
                text = text[3..end].Trim();
        }

        var start = text.IndexOf('{');
        var endBrace = text.LastIndexOf('}');
        if (start >= 0 && endBrace > start)
            text = text[start..(endBrace + 1)];

        return text;
    }

    private const string TodoPlanSystemPrompt = """"
你是一位熟悉中国政务与生活办事流程的办事顾问，擅长把用户的目标拆解成可直接执行的具体待办，帮助用户少走弯路。

用户会给出一个生活或办事目标，你需要：
1. 把目标拆解为 4-8 个具体的待办事项（todo），按办事先后顺序排列；
2. 每个待办必须非常具体、可直接执行：title 用一句话说清动作（不超过 40 字），例如"去户籍所在地派出所申请身份证""在移民局小程序预约护照办理"；
3. 每个待办必须附上执行指引 note（不超过 300 字），写明用户最需要的实操信息，尽量包括：
   - 应该去哪个机构（例如"户籍所在地派出所户籍窗口""XX区政务服务中心"），或打哪个电话确认；
   - 登录哪个网站 / App / 小程序（例如"移民局微信小程序""国家政务服务平台官网"）；
   - 提前准备哪些证件和材料（例如"身份证原件及复印件、户口本、2 寸白底照片"）；
   - 需要填写哪些表单（例如"《中国公民出入境证件申请表》"）；
   - 注意事项：办理时间、费用、预约方式、常见坑点等；
4. 不确定的信息要诚实：写"建议先电话确认（如 12345）"而不是编造机构或网址；
5. 如果目标太大，先拆成阶段，给出第一阶段的具体待办即可。

输出严格为 JSON 对象，不要包含 markdown 代码块标记或其他说明文字：
{"title": "目标的简短标题", "items": [{"title": "具体动作", "note": "执行指引"}]}
"""";

    private sealed class AiTodoPlan
    {
        public string Title { get; set; } = "";
        public List<AiTodoItem> Items { get; set; } = new();
    }

    private sealed class AiTodoItem
    {
        public string Title { get; set; } = "";
        public string? Note { get; set; }
    }
}

/// <summary>AI 生成结果：成功时携带待办预览（未保存），失败时携带面向用户的错误信息</summary>
public sealed class TodoAiResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public AiTodoPreviewDto? Preview { get; init; }

    public static TodoAiResult Ok(AiTodoPreviewDto preview) => new() { Success = true, Preview = preview };
    public static TodoAiResult Fail(string error) => new() { Success = false, Error = error };
}
