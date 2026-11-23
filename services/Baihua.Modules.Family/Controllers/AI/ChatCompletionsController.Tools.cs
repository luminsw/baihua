using Baihua.Core.Models;
using Baihua.Core.Services;
using Microsoft.Extensions.AI;
using Baihua.Modules.Family.Models;

namespace Baihua.Modules.Family.Controllers;

public partial class ChatCompletionsController
{
        private static (string ProviderId, string ModelId) ParseModel(string? model)
        {
            if (string.IsNullOrWhiteSpace(model)) return ("", "");

            // 支持格式: "provider/model" 或 "model"
            if (model.Contains('/'))
            {
                var parts = model.Split('/', 2);
                return (parts[0], parts[1]);
            }

            // 也支持 openclaw 的格式: "ollama/biancang:latest"
            return ("", model);
        }

        private static bool IsLocalProvider(AiProviderConfig provider)
        {
            if (string.IsNullOrEmpty(provider?.AiBaseUrl))
                return false;
            var url = provider.AiBaseUrl.ToLowerInvariant();
            return url.Contains("localhost") || url.Contains("127.0.0.1") || url.Contains("0.0.0.0");
        }

        private static ChatRole ParseRole(string? role)
        {
            return role?.ToLowerInvariant() switch
            {
                "system" => ChatRole.System,
                "assistant" => ChatRole.Assistant,
                "user" => ChatRole.User,
                "tool" => ChatRole.Tool,
                _ => ChatRole.User
            };
        }
}
