namespace Baihua.Contracts.Ai;

public class AskRequest
{
    public string Query { get; set; } = string.Empty;
    public bool SaveToVault { get; set; } = true;
    public string? VaultPath { get; set; }
    public string? VaultId { get; set; }
    public string? ProviderId { get; set; }
    public string? Model { get; set; }
    public bool? EnableTools { get; set; }
}

public class GenerateMissingNoteRequest
{
    public string LinkPath { get; set; } = string.Empty;
    public string? VaultId { get; set; }
    public string? ProviderId { get; set; }
    public string? Model { get; set; }
    public bool? EnableTools { get; set; }
}

public class OpenAiChatRequest
{
    public string? Model { get; set; }
    public List<ChatMessageRequest> Messages { get; set; } = new();
    public double? Temperature { get; set; }
    public int? MaxTokens { get; set; }
}

public class ChatMessageRequest
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
}

public class OpenAiChatResponse
{
    public bool Success { get; set; }
    public string? Reply { get; set; }
    public string? Message { get; set; }
}

public class PromptTemplateDto
{
    public string DisplayName { get; set; } = "";
    public string ChatSystemPrompt { get; set; } = "";
    public string SplitSystemPrompt { get; set; } = "";
    public string SplitUserPrompt { get; set; } = "";
    public string SupplementUserPrompt { get; set; } = "";
    public List<string> DefaultCategories { get; set; } = new();
    public string ConfigurationAdvice { get; set; } = "";
    public string KnowledgeBuildPlaceholder { get; set; } = "";
    public string KnowledgeBuildDescription { get; set; } = "";
}