using Baihua.Core;
using Baihua.Modules.Family.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Baihua.Modules.Family.Models;
using Baihua.Contracts.Ai;
using Path = System.IO.Path;
using Directory = System.IO.Directory;
using File = System.IO.File;

namespace Baihua.Modules.Family.Controllers
{
    public partial class AIController
    {
        private Note ParseNote(string aiContent, string query)
        {
            var lines = aiContent.Split('\n');
            var title = lines.FirstOrDefault(l => l.StartsWith("# "))?.TrimStart('#').Trim() ?? $"关于：{query}";
            
            return new Note
            {
                Title = title,
                FilePath = $"{_loc["AiGeneratedDir"]}/{GenerateSafeFileName(title)}",
                Content = aiContent,
                Summary = aiContent.Length > 100 ? aiContent.Substring(0, 100) + "..." : aiContent
            };
        }

        private async Task SaveNote(Note note, string vaultPath, string? providerName = null, string? modelName = null)
        {
            if (string.IsNullOrEmpty(vaultPath)) return;

            var notesRoot = System.IO.Path.Combine(vaultPath, "notes");
            var fullPath = System.IO.Path.Combine(notesRoot, note.FilePath + ".md");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            var frontmatter = $"---\nai_generated: true\nai_provider: {providerName ?? ""}\nai_model: {modelName ?? ""}\ngenerated_at: {DateTimeOffset.UtcNow:O}\n---\n";
            await System.IO.File.WriteAllTextAsync(fullPath, frontmatter + note.Content);
            _logger.LogDebug("笔记已保存：{Path}", fullPath);
        }

        private string GenerateNoteId(string title)
        {
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(title))[..8];
        }

        private string GenerateSafeFileName(string title)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var invalidSet = new HashSet<char>(invalid);
            return string.Concat(title.Where(c => !invalidSet.Contains(c)).Take(50));
        }

        private string GetSystemPrompt(string providerId)
        {
            var activeVault = _vaultSettings.GetActiveVault();
            var industry = activeVault?.Industry ?? "";
            var template = _scenePromptService.GetTemplateByName(industry);
            var prompt = template.ChatSystemPrompt;

            // Qwen (阿里云百炼) 在启用 function calling 时容易返回空内容
            // 通过 system prompt 明确指示模型直接回答，仅在明确要求时才调用工具
            if (string.Equals(providerId, "aliyun", StringComparison.OrdinalIgnoreCase))
            {
                prompt += "\n\n【重要指令】请直接回答用户的问题，给出完整、详细的回复。" +
                          "只有在用户明确要求搜索知识库、获取时间、查看系统状态或创建笔记时，才调用相应工具。" +
                          "如果用户只是询问一般性知识问题，请正常回答，不要返回空内容，也不要调用任何工具。";
            }

            return prompt;
        }

        /// <summary>
        /// 异步版本：使用三层记忆系统构建消息列表
        /// </summary>
        private async Task<List<ChatMessage>> BuildMessagesWithMemoryAsync(
            List<ChatHistoryItem>? history, string providerId, string model,
            string currentMessage, string? sessionId = null, CancellationToken ct = default)
        {
            return await _chatMemoryService.BuildMessagesWithMemoryAsync(
                history, providerId, model, currentMessage, sessionId, ct);
        }

        private class Note
        {
            public string Title { get; set; } = string.Empty;
            public string FilePath { get; set; } = string.Empty;
            public string Summary { get; set; } = string.Empty;
            public string Content { get; set; } = string.Empty;
        }
    }
}
