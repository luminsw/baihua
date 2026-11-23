using Baihua.Core.Models;
using Baihua.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using System.Diagnostics;
using System.Text;
using Baihua.Core.Localization;
using Baihua.Modules.Family.Services;
using Baihua.Contracts.Git;

namespace Baihua.Modules.Family.Controllers;
    /// <summary>
    /// Git 管理控制器：提供知识库的 Git 操作
    /// </summary>
    [ApiController]
    [Route("api/git")]
    public partial class GitController : ControllerBase
    {
        private readonly ILogger<GitController> _logger;
        private readonly VaultSettingsService _vaultSettings;
        private readonly IStringLocalizer<SharedResources> _loc;

        public GitController(ILogger<GitController> logger, VaultSettingsService vaultSettings, IStringLocalizer<SharedResources> loc)
        {
            _logger = logger;
            _vaultSettings = vaultSettings;
            _loc = loc;
        }

        /// <summary>
        /// 检查知识库是否配置了 Git
        /// </summary>
        [HttpGet("status")]
        public async Task<ActionResult<GitStatusResponse>> GetStatus([FromQuery] string vaultId)
        {
            try
            {
                var vaultPath = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == vaultId)?.Path;
                if (string.IsNullOrEmpty(vaultPath))
                {
                    return Ok(new GitStatusResponse
                    {
                        IsGitRepo = false,
                        Message = _loc["Vault_Required"]
                    });
                }

                var gitDir = Path.Combine(vaultPath, ".git");
                if (!Directory.Exists(gitDir))
                {
                    return Ok(new GitStatusResponse
                    {
                        IsGitRepo = false,
                        Message = _loc["Git_VaultNotConfigured"]
                    });
                }

                // 获取当前分支
                var branch = await RunGitCommand(vaultPath, "rev-parse --abbrev-ref HEAD");
                
                // 获取状态（-uall 显示所有未跟踪文件，不只是目录）
                var status = await RunGitCommand(vaultPath, "status --porcelain -uall");
                var remote = await RunGitCommand(vaultPath, "remote -v");
                
                // 解析变更文件
                var changes = new List<ChangeResponse>();
                if (!string.IsNullOrWhiteSpace(status))
                {
                    foreach (var line in status.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        try
                        {
                            if (line.Length >= 3)
                            {
                                var changeType = line.Substring(0, Math.Min(2, line.Length)).Trim();
                                var filePath = line.Length > 3 ? line.Substring(3) : line;
                                
                                // 解码 Git 返回的八进制编码（如 \347\224\237 -> 生）
                                filePath = DecodeGitOctalPath(filePath);
                                
                                // 移除引号
                                filePath = filePath.Trim('"');
                                
                                changes.Add(new ChangeResponse
                                {
                                    Status = MapGitStatus(changeType),
                                    File = filePath
                                });
                            }
                            else if (line.Length > 0)
                            {
                                var decodedLine = DecodeGitOctalPath(line);
                                changes.Add(new ChangeResponse
                                {
                                    Status = "Unknown",
                                    File = decodedLine
                                });
                            }
                        }
                        catch
                        {
                            // 忽略解析错误的行
                        }
                    }
                }

                // 获取 ahead/behind 信息
                var ahead = 0;
                var behind = 0;
                try
                {
                    var branchInfo = await RunGitCommand(vaultPath, $"rev-list --left-right --count origin/{branch}...HEAD");
                    if (!string.IsNullOrWhiteSpace(branchInfo))
                    {
                        var parts = branchInfo.Split('\t');
                        if (parts.Length == 2)
                        {
                            behind = int.TryParse(parts[0].Trim(), out var b) ? b : 0;
                            ahead = int.TryParse(parts[1].Trim(), out var a) ? a : 0;
                        }
                    }
                }
                catch
                {
                    // 可能没有远程分支
                }

                return Ok(new GitStatusResponse
                {
                    IsGitRepo = true,
                    Branch = branch.Trim(),
                    HasRemote = !string.IsNullOrWhiteSpace(remote),
                    Changes = changes,
                    Ahead = ahead,
                    Behind = behind,
                    Message = changes.Count == 0 ? _loc["Git_WorkingDirClean"] : _loc["Git_ChangesCount", changes.Count]
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取 Git 状态失败");
                return Ok(new GitStatusResponse
                {
                    IsGitRepo = false,
                    Message = _loc["Git_StatusFailed", ex.Message]
                });
            }
        }

}
