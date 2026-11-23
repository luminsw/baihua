using Baihua.Core;
using Microsoft.AspNetCore.Mvc;
using Baihua.Core.Services;

namespace Baihua.Modules.Vault.Controllers
{
    public partial class SyncController : ControllerBase
    {
        /// <summary>
        /// 内部方法：获取笔记列表
        /// </summary>
        private List<NoteMetadata> GetNotesInternal(string vaultPath, long? since = null)
        {
            if (string.IsNullOrEmpty(vaultPath) || !System.IO.Directory.Exists(vaultPath))
            {
                _logger.LogWarning("知识库路径无效：{Path}", vaultPath);
                return new List<NoteMetadata>();
            }

            try
            {
                var notes = new List<NoteMetadata>();
                var files = System.IO.Directory.GetFiles(vaultPath, "*.md", System.IO.SearchOption.AllDirectories);
                
                foreach (var file in files)
                {
                    try
                    {
                        var fileName = System.IO.Path.GetFileName(file);
                        if (fileName.Equals("README.md", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var relativePath = file.Substring(vaultPath.Length).TrimStart('/').Replace(".md", "");
                        var title = System.IO.Path.GetFileNameWithoutExtension(file);
                        var modified = System.IO.File.GetLastWriteTime(file);
                        var size = new System.IO.FileInfo(file).Length;
                        var hash = CalculateFileHash(file);

                        notes.Add(new NoteMetadata
                        {
                            Path = relativePath,
                            Title = title,
                            Modified = modified,
                            Size = size,
                            Hash = hash
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "读取文件元数据失败：{File}", file);
                    }
                }

                _logger.LogInformation("返回笔记列表：{Count} 条", notes.Count);
                return notes;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取笔记列表失败");
                throw;
            }
        }

        /// <summary>
        /// 内部方法：获取任务列表
        /// </summary>
        private List<TaskInfo> GetTasksInternal(long? since = null)
        {
            try
            {
                var tasks = _taskManager.GetAllTasks();
                
                if (since.HasValue)
                {
                    var sinceTime = DateTimeOffset.FromUnixTimeSeconds(since.Value).UtcDateTime;
                    tasks = tasks.Where(t => t.CreatedAt.ToUniversalTime() > sinceTime).ToList();
                }

                return tasks;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取任务列表失败");
                throw;
            }
        }
        
        private TimeSpan GetUptime()
        {
            return TimeSpan.FromHours(1);
        }

        /// <summary>知识库 id → 路径（统一走 VaultQueryService，避免多处重复实现）</summary>
        private string? ResolveVaultPath(string vaultId) => _vaultQuery.ResolveVaultPath(vaultId);

        private ConflictResolution UpdateNoteInternal(string vaultPath, NoteUpdateRequest request)
        {
            try
            {
                var fullPath = System.IO.Path.Combine(vaultPath, request.Path + ".md");
                var directory = System.IO.Path.GetDirectoryName(fullPath);
                
                if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                }

                if (System.IO.File.Exists(fullPath))
                {
                    var serverHash = CalculateFileHash(fullPath);
                    var serverModified = System.IO.File.GetLastWriteTime(fullPath);

                    if (!string.IsNullOrEmpty(request.Hash))
                    {
                        if (serverHash != request.Hash)
                        {
                            return new ConflictResolution
                            {
                                Path = request.Path,
                                Status = "conflict",
                                Message = _loc["Sync_FileModifiedByOther"],
                                ServerModified = serverModified,
                                ClientModified = request.Modified
                            };
                        }
                    }
                }

                System.IO.File.WriteAllText(fullPath, request.Content);
                System.IO.File.SetLastWriteTime(fullPath, request.Modified);

                return new ConflictResolution
                {
                    Path = request.Path,
                    Status = "success",
                    Message = _loc["Sync_NoteUpdateSuccess"]
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新笔记失败：{Path}", request.Path);
                return new ConflictResolution
                {
                    Path = request.Path,
                    Status = "error",
                    Message = _loc["Sync_UpdateFailed", ex.Message]
                };
            }
        }

        /// <summary>
        /// 计算文件哈希值（用于冲突检测）
        /// </summary>
        private string CalculateFileHash(string filePath)
        {
            using var stream = System.IO.File.OpenRead(filePath);
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hashBytes = sha256.ComputeHash(stream);
            return Convert.ToBase64String(hashBytes);
        }
    }
}
