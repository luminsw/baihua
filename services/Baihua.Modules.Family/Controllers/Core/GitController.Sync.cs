using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using System.Diagnostics;
using System.Text;
using Baihua.Core.Localization;
using Baihua.Modules.Family.Services;
using Baihua.Contracts.Git;

namespace Baihua.Modules.Family.Controllers;

public partial class GitController
{
        /// <summary>提交当前变更（WebUI 知识库 Git 面板用：POST api/git/commit?vaultId=…）</summary>
        [HttpPost("commit")]
        public async Task<ActionResult<GitResultResponse>> Commit([FromQuery] string vaultId, [FromBody] CommitRequest request)
        {
            try
            {
                var vaultPath = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == vaultId)?.Path;
                if (string.IsNullOrEmpty(vaultPath))
                {
                    return BadRequest(new GitResultResponse { Success = false, Message = _loc["Vault_Required"] });
                }

                if (string.IsNullOrWhiteSpace(request.Message))
                {
                    return BadRequest(new GitResultResponse { Success = false, Message = _loc["Git_CommitMessageRequired"] });
                }

                // 先检查是否有变更
                var status = await RunGitCommand(vaultPath, "status --porcelain");
                if (string.IsNullOrWhiteSpace(status))
                {
                    return Ok(new GitResultResponse { Success = true, Message = _loc["Git_NoChanges"] });
                }

                // 添加所有变更
                await RunGitCommand(vaultPath, "add -A");
                
                // 提交
                var escapedMessage = request.Message.Replace("\"", "\\\"");
                var commitResult = await RunGitCommand(vaultPath, $"commit -m \"{escapedMessage}\"");
                
                return Ok(new GitResultResponse
                {
                    Success = true,
                    Message = _loc["Git_CommitSuccess"],
                    Output = commitResult
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Git 提交失败");
                return Ok(new GitResultResponse
                {
                    Success = false,
                    Message = string.Format(_loc["Git_CommitFailed"], ex.Message)
                });
            }
        }

        /// <summary>
        /// 推送到远程
        /// </summary>
        [HttpPost("push")]
        public async Task<ActionResult<GitResultResponse>> Push([FromQuery] string vaultId)
        {
            try
            {
                var vaultPath = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == vaultId)?.Path;
                if (string.IsNullOrEmpty(vaultPath))
                {
                    return BadRequest(new GitResultResponse { Success = false, Message = _loc["Vault_Required"] });
                }

                // 检查远程配置
                var remote = await RunGitCommand(vaultPath, "remote -v");
                if (string.IsNullOrWhiteSpace(remote))
                {
                    return Ok(new GitResultResponse { Success = false, Message = _loc["Git_NoRemote"] });
                }

                // 获取当前分支
                var branch = await RunGitCommand(vaultPath, "rev-parse --abbrev-ref HEAD");
                
                // 推送
                var pushResult = await RunGitCommand(vaultPath, $"push origin {branch.Trim()}", timeoutMs: 60000);
                
                return Ok(new GitResultResponse
                {
                    Success = true,
                    Message = _loc["Git_PushSuccess"],
                    Output = pushResult
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Git 推送失败");
                return Ok(new GitResultResponse
                {
                    Success = false,
                    Message = string.Format(_loc["Git_PushFailed"], ex.Message)
                });
            }
        }

        /// <summary>
        /// 拉取远程变更
        /// </summary>
        [HttpPost("pull")]
        public async Task<ActionResult<GitResultResponse>> Pull([FromQuery] string vaultId)
        {
            try
            {
                var vaultPath = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == vaultId)?.Path;
                if (string.IsNullOrEmpty(vaultPath))
                {
                    return BadRequest(new GitResultResponse { Success = false, Message = _loc["Vault_Required"] });
                }

                var pullResult = await RunGitCommand(vaultPath, "pull", timeoutMs: 60000);
                
                return Ok(new GitResultResponse
                {
                    Success = true,
                    Message = _loc["Git_PullSuccess"],
                    Output = pullResult
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Git 拉取失败");
                return Ok(new GitResultResponse
                {
                    Success = false,
                    Message = string.Format(_loc["Git_PullFailed"], ex.Message)
                });
            }
        }

}
