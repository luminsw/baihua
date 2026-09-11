namespace Baihua.Contracts.Ai;

public class AiNoteResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string NoteId { get; set; } = "";
}

public class GenerateMissingNoteResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? NotePath { get; set; }
    public string? Title { get; set; }
    public string? Content { get; set; }
    public int FixedLinks { get; set; }
}

public class ChatResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string Reply { get; set; } = "";
    public string Content { get; set; } = "";
}

public enum AiModelTier
{
    Unset = 0,
    Tier1_Embedding = 1,
    Tier2_Local = 2,
    Tier3_Cloud = 3
}

public class AiConfigProvider
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string? AnthropicBaseUrl { get; set; }
    public bool IsMain { get; set; }
    public List<AiConfigModel> Models { get; set; } = new();
    public bool HasApiKey { get; set; }
    public string? KeyMask { get; set; }
    public AiModelTier Tier { get; set; }
}

public class AiConfigModel
{
    public string Name { get; set; } = "";
    public bool IsMain { get; set; }

    /// <summary>
    /// 任务分类（chat/reasoning/code/vision），用于按用途路由模型
    /// </summary>
    public string Category { get; set; } = AiTaskCategory.Chat;
}

public class SaveAiProviderRequest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string? AnthropicBaseUrl { get; set; }
    public bool IsMain { get; set; }
    public List<AiModelRequest> Models { get; set; } = new();
    public string? ApiKey { get; set; }
    public int SortOrder { get; set; }
    public AiModelTier Tier { get; set; }
}

public class AiModelRequest
{
    public string Name { get; set; } = "";
    public bool IsMain { get; set; }

    /// <summary>任务分类（chat/reasoning/code/vision）</summary>
    public string Category { get; set; } = AiTaskCategory.Chat;
}

public class SaveAiProviderResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

public class AiProviderPreset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string? AnthropicBaseUrl { get; set; }
    public List<AiProviderPresetModel> Models { get; set; } = new();
    public AiModelTier Tier { get; set; } = AiModelTier.Tier3_Cloud;
}

public class AiProviderPresetModel
{
    public string Name { get; set; } = "";
    public bool IsMain { get; set; }

    /// <summary>任务分类（chat/reasoning/code/vision）</summary>
    public string Category { get; set; } = AiTaskCategory.Chat;
}

public class LocalModelInfo
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Type { get; set; } = "";
    public long Size { get; set; }
}

public class EnvConfigHelp
{
    public string Description { get; set; } = "";
}

public class UpdateApiKeyRequest
{
    public string ApiKey { get; set; } = "";
}

public class EmbeddingConfigDto
{
    public int Id { get; set; }
    public string ProviderId { get; set; } = "";
    public string Model { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public int? Dimensions { get; set; }
    public string? KeyMask { get; set; }
    /// <summary>前端编辑用，不序列化到后端响应</summary>
    public string ApiKey { get; set; } = "";
}

public class SaveEmbeddingConfigRequest
{
    public string ProviderId { get; set; } = "";
    public string Model { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string? ApiKey { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int? Dimensions { get; set; }
}

public class ChatHistoryItem
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
}

public class ChatRequest
{
    public string Message { get; set; } = string.Empty;
    public string? ProviderId { get; set; }
    public string? Model { get; set; }

    /// <summary>任务分类（chat/reasoning/code/vision）；未指定提供方/模型时按分类路由</summary>
    public string? Category { get; set; }
    public List<ChatHistoryItem>? History { get; set; }
    public string? SessionId { get; set; }
    public bool? EnableTools { get; set; }
}

public class LocalChatRequest
{
    public string Message { get; set; } = string.Empty;
    public string ModelPath { get; set; } = string.Empty;
    public string ModelType { get; set; } = "gguf";
    public string? SystemPrompt { get; set; }
    public List<ChatHistoryItem>? History { get; set; }
}
