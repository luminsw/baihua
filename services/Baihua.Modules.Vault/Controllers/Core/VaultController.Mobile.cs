using Baihua.Core;
using Baihua.Core.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baihua.Data;
using Baihua.Core.Services;
using Baihua.Core.Services.Strategies;
using Baihua.Contracts.Vaults;

namespace Baihua.Modules.Vault.Controllers;

public partial class VaultController
{
    [HttpGet("cards")]
    public ActionResult<object> GetCards([FromQuery] string vaultId)
    {
        // 移动端 API 统一使用 HMAC 签名验证（在 Program.cs 中间件中完成）
        // 不再额外要求 Bearer Token，与 GetManifest/GetFile 保持一致
        var targetVault = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == vaultId);
        if (targetVault == null || string.IsNullOrEmpty(targetVault.Path))
        {
            return NotFound(new { error = _loc["Vault_NotFound"] });
        }

        var cardsPath = System.IO.Path.Combine(targetVault.Path, "cards");
        if (!System.IO.Directory.Exists(cardsPath))
        {
            return Ok(new { vaultId, count = 0, cards = new List<object>() });
        }

        var cards = new List<object>();
        var files = System.IO.Directory.GetFiles(cardsPath, "*.json");

        foreach (var file in files)
        {
            try
            {
                var json = System.IO.File.ReadAllText(file);
                var cardItems = ParseCardFile(json);
                foreach (var card in cardItems)
                {
                    var frontText = NormalizeCardField(card.Front, "front", file);
                    var backText = NormalizeCardField(card.Back, "back", file);
                    if (string.IsNullOrWhiteSpace(frontText) || string.IsNullOrWhiteSpace(backText))
                    {
                        continue;
                    }

                    cards.Add(new
                    {
                        front = frontText,
                        back = backText,
                        deck = card.Deck,
                        tags = string.Join(",", card.Tags ?? new List<string>()),
                        source = card.Source,
                        notePath = System.IO.Path.GetFileName(file)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "解析卡片文件失败：{File}", file);
            }
        }

        return Ok(new { vaultId, count = cards.Count, cards });
    }

    /// <summary>
    /// 解析卡片 JSON 文件，支持两种格式：
    /// 1. 旧格式：文件本身是 MobileCardItem[] 数组
    /// 2. 标准格式：{ "Name": "...", "Cards": [ ... ] }
    /// </summary>
    private static List<MobileCardItem> ParseCardFile(string json)
    {
        var result = new List<MobileCardItem>();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in root.EnumerateArray())
            {
                var card = JsonSerializer.Deserialize<MobileCardItem>(element.GetRawText());
                if (card != null) result.Add(card);
            }
            return result;
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("Cards", out var cardsElement) &&
            cardsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in cardsElement.EnumerateArray())
            {
                var card = JsonSerializer.Deserialize<MobileCardItem>(element.GetRawText());
                if (card != null) result.Add(card);
            }
        }

        return result;
    }

    /// <summary>
    /// 把卡片字段统一转成字符串。兼容字段为 JSON 数组（空数组则跳过）或字符串的情况。
    /// </summary>
    private static string NormalizeCardField(JsonElement field, string fieldName, string filePath)
    {
        switch (field.ValueKind)
        {
            case JsonValueKind.String:
                return field.GetString() ?? "";
            case JsonValueKind.Array:
                // 空数组视为无效；非空数组按行拼接
                var items = field.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();
                return items.Count > 0 ? string.Join("\n", items!) : "";
            default:
                return "";
        }
    }

    /// <summary>
    /// 标准卡片文件包装格式
    /// </summary>
    private class CardFileWrapper
    {
        public string Name { get; set; } = "";
        public List<MobileCardItem> Cards { get; set; } = new();
    }

    /// <summary>
    /// 获取知识库笔记数量
    /// </summary>
    [HttpGet("note-count")]
    public ActionResult<int> GetNoteCount([FromQuery] string vaultId)
    {
        var vault = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == vaultId);
        if (vault == null)
        {
            return NotFound(new { error = _loc["Vault_NotFound"], vaultId });
        }
        if (string.IsNullOrEmpty(vault.Path))
        {
            return StatusCode(500, new { error = _loc["Vault_PathEmpty"], vaultId });
        }

        var notesPath = System.IO.Path.Combine(vault.Path, "notes");
        if (!System.IO.Directory.Exists(notesPath))
        {
            _logger.LogWarning("知识库 notes 目录不存在：{Path}", notesPath);
            return Ok(0);
        }

        var files = System.IO.Directory.GetFiles(notesPath, "*.md", System.IO.SearchOption.AllDirectories);
        return Ok(files.Length);
    }
}
