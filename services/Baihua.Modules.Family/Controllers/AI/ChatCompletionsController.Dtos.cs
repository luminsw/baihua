using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Baihua.Modules.Family.Models;
using Baihua.Modules.Family.Services;

namespace Baihua.Modules.Family.Controllers;

    #region OpenAI 兼容 DTO

    public class OpenAiChatRequest
    {
        public string? Model { get; set; }
        public List<OpenAiMessage> Messages { get; set; } = new();
        public bool? Stream { get; set; }
        public double? Temperature { get; set; }
        public int? MaxTokens { get; set; }
        public List<OpenAiTool>? Tools { get; set; }
    }

    public class OpenAiMessage
    {
        public string Role { get; set; } = "user";
        public string? Content { get; set; }
        public List<OpenAiToolCall>? ToolCalls { get; set; }
        public string? ToolCallId { get; set; }
    }

    public class OpenAiTool
    {
        public string Type { get; set; } = "function";
        public OpenAiFunction Function { get; set; } = new();
    }

    public class OpenAiFunction
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public Dictionary<string, object>? Parameters { get; set; }
    }

    public class OpenAiToolCall
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "function";
        public OpenAiToolCallFunction Function { get; set; } = new();
    }

    public class OpenAiToolCallFunction
    {
        public string Name { get; set; } = "";
        public string Arguments { get; set; } = "";
    }

    public class OpenAiChatResponse
    {
        public string Id { get; set; } = "";
        public string Object { get; set; } = "chat.completion";
        public long Created { get; set; }
        public string Model { get; set; } = "";
        public List<OpenAiChoice> Choices { get; set; } = new();
        public OpenAiUsage Usage { get; set; } = new();
    }

    public class OpenAiChoice
    {
        public int Index { get; set; }
        public OpenAiMessage Message { get; set; } = new();
        public string? FinishReason { get; set; }
    }

    public class OpenAiUsage
    {
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
    }

    public class OpenAiChatStreamChunk
    {
        public string Id { get; set; } = "";
        public string Object { get; set; } = "chat.completion.chunk";
        public long Created { get; set; }
        public string Model { get; set; } = "";
        public List<OpenAiStreamChoice> Choices { get; set; } = new();
    }

    public class OpenAiStreamChoice
    {
        public int Index { get; set; }
        public OpenAiDelta Delta { get; set; } = new();
        public string? FinishReason { get; set; }
    }

    public class OpenAiDelta
    {
        public string? Role { get; set; }
        public string? Content { get; set; }
    }

    public class OpenAiModel
    {
        public string Id { get; set; } = "";
        public string Object { get; set; } = "model";
        public long Created { get; set; }
        public string OwnedBy { get; set; } = "";
    }

    #endregion
