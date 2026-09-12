using Baihua.Modules.Vault.Services;
using Microsoft.AspNetCore.Mvc;
using Baihua.Contracts.Vaults;

namespace Baihua.Modules.Vault.Controllers;

public partial class VaultController
{
    [HttpGet("vaults/{vaultId}/notes-batch")]
    public ActionResult<VaultNotesBatchResponse> GetAllNotesBatch(string vaultId)
    {
        var baseVaultPath = ResolveVaultPath(vaultId);
        if (string.IsNullOrEmpty(baseVaultPath))
            return NotFound(new { error = _loc["Vault_NotFound"] });

        var notesPath = System.IO.Path.Combine(baseVaultPath, "notes");
        var effectiveRoot = System.IO.Directory.Exists(notesPath) ? notesPath : baseVaultPath;

        if (!System.IO.Directory.Exists(effectiveRoot))
            return Ok(new VaultNotesBatchResponse());

        var notes = new List<VaultNoteResponse>();
        CollectNotes(effectiveRoot, effectiveRoot, notes);
        return Ok(new VaultNotesBatchResponse { Notes = notes });
    }

    private void CollectNotes(string currentDir, string rootDir, List<VaultNoteResponse> notes)
    {
        foreach (var dir in System.IO.Directory.GetDirectories(currentDir))
        {
            var dirName = System.IO.Path.GetFileName(dir);
            if (ExcludedDirs.Contains(dirName)) continue;
            CollectNotes(dir, rootDir, notes);
        }

        foreach (var file in System.IO.Directory.GetFiles(currentDir, "*.md"))
        {
            try
            {
                var relativePath = file.Substring(rootDir.Length).TrimStart('/', '\\').Replace('\\', '/');
                if (relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    relativePath = relativePath[..^3];

                var content = System.IO.File.ReadAllText(file);
                var title = System.IO.Path.GetFileNameWithoutExtension(file);
                var (tags, aiGenerated, aiProvider, aiModel, generatedAt) = VaultNoteFrontmatter.Parse(content);

                notes.Add(new VaultNoteResponse
                {
                    Path = relativePath,
                    Title = title,
                    Content = content,
                    Modified = System.IO.File.GetLastWriteTime(file),
                    Tags = tags,
                    AiGenerated = aiGenerated,
                    AiProvider = aiProvider,
                    AiModel = aiModel,
                    GeneratedAt = generatedAt
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "批量读取笔记跳过: {File}", file);
            }
        }
    }
}