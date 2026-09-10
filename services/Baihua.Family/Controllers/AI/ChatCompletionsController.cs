using Baihua.Core.Models;
using Baihua.Core.Services;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;
using Baihua.Family.Models;
using Baihua.Family.Services;

namespace Baihua.Family.Controllers;
    /// <summary>
    /// OpenAI 兼容 Chat Completions API Facade
    /// 让 Cursor/Claude Code/Windsurf 等工具可以直接使用 百花 AI 服务
    /// </summary>
    [ApiController]
    [Route("api/chat/completions")]
    public partial class ChatCompletionsController : ControllerBase
    {
        private readonly AiSettingsService _aiSettings;
        private readonly VaultSettingsService _vaultSettings;
        private readonly AiClientService _aiClientService;
        private readonly RagService _ragService;

        private readonly DefaultPromptProvider _scenePromptService;
        private readonly ILogger<ChatCompletionsController> _logger;
        private readonly IStringLocalizer<SharedResources> _loc;

        public ChatCompletionsController(
            AiSettingsService aiSettings,
            VaultSettingsService vaultSettings,
            AiClientService aiClientService,
            RagService ragService,

            DefaultPromptProvider scenePromptService,
            ILogger<ChatCompletionsController> logger,
            IStringLocalizer<SharedResources> loc)
        {
            _aiSettings = aiSettings;
            _vaultSettings = vaultSettings;
            _aiClientService = aiClientService;
            _ragService = ragService;

            _scenePromptService = scenePromptService;
            _logger = logger;
            _loc = loc;
        }

        /// <summary>
        /// OpenAI 兼容 Chat Completions 端点
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> CreateChatCompletion([FromBody] OpenAiChatRequest request)
        {
            try
            {
                if (request.Messages == null || request.Messages.Count == 0)
                    return BadRequest(new { error = _loc["ChatCompletions_MessagesEmpty"] });

                // 解析 model 参数，格式: "provider/model" 或直接使用默认 provider
                var (providerId, modelId) = ParseModel(request.Model);
                var provider = _aiSettings.GetAiProvider(providerId)
                    ?? _aiSettings.GetMainAiProvider()
                    ?? throw new Exception(_loc["ChatCompletions_ProviderNotConfigured"]);

                var model = string.IsNullOrWhiteSpace(modelId) ? GetDefaultModel(provider) : modelId;

                // 转换消息格式
                var messages = request.Messages.Select(m => new ChatMessage(
                    ParseRole(m.Role),
                    m.Content ?? ""
                )).ToList();

                // 注入系统提示（知识库助手上下文）
                InjectSystemPrompt(messages);

                // RAG 增强：检索知识库上下文
                messages = await _ragService.EnrichMessagesWithVaultContextAsync(messages, HttpContext.RequestAborted);

                // 调用 AI
                var options = new ChatOptions();
                if (request.Temperature.HasValue)
                    options.Temperature = (float)request.Temperature.Value;
                if (request.MaxTokens.HasValue)
                    options.MaxOutputTokens = request.MaxTokens.Value;

                var sw = Stopwatch.StartNew();
                ChatResponse response;

                if (request.Stream == true)
                {
                    return await HandleStreamingAsync(provider, model, messages, options, HttpContext.RequestAborted);
                }

                response = await _aiClientService.GetChatResponseWithAutoStartAsync(
                    provider, model, messages, options, HttpContext.RequestAborted);
                sw.Stop();

                var result = new OpenAiChatResponse
                {
                    Id = $"chatcmpl-{Guid.NewGuid().ToString("N")[..12]}",
                    Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Model = $"{provider.Id}/{model}",
                    Choices =
                    [
                        new OpenAiChoice
                        {
                            Index = 0,
                            Message = new OpenAiMessage
                            {
                                Role = "assistant",
                                Content = response.Text ?? ""
                            },
                            FinishReason = "stop"
                        }
                    ],
                    Usage = new OpenAiUsage
                    {
                        PromptTokens = (int)(response.Usage?.InputTokenCount ?? 0),
                        CompletionTokens = (int)(response.Usage?.OutputTokenCount ?? 0),
                        TotalTokens = (int)((response.Usage?.InputTokenCount ?? 0) + (response.Usage?.OutputTokenCount ?? 0))
                    }
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OpenAI 兼容 Chat Completion 失败");
                return StatusCode(500, new { error = new { message = ex.Message, type = "internal_error" } });
            }
        }

        /// <summary>
        /// 列出可用模型（OpenAI 兼容格式）
        /// </summary>
        [HttpGet("models")]
        public IActionResult ListModels()
        {
            var models = _aiSettings.GetAiProviders()
                .SelectMany(p => p.GetModelOptions().Select(m => new OpenAiModel
                {
                    Id = $"{p.Id}/{m.Name}",
                    Object = "model",
                    Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    OwnedBy = p.Id
                }))
                .ToList();

            // 添加简化格式的模型 ID
            var allModels = models.ToList();
            foreach (var m in models)
            {
                if (m.Id.Contains('/'))
                {
                    var simpleName = m.Id.Split('/')[1];
                    if (!allModels.Any(x => x.Id == simpleName))
                    {
                        allModels.Add(new OpenAiModel
                        {
                            Id = simpleName,
                            Object = "model",
                            Created = m.Created,
                            OwnedBy = m.OwnedBy
                        });
                    }
                }
            }

            return Ok(new { data = allModels, @object = "list" });
        }

}
