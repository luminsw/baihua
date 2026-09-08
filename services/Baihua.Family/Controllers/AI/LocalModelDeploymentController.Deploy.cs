using Microsoft.AspNetCore.Mvc;
using Baihua.Contracts.LocalModels;
using Baihua.Contracts.OpenClaw;
using Baihua.Family.Services;

namespace Baihua.Family.Controllers;

public partial class LocalModelDeploymentController
{

        /// <summary>
        /// 查询部署任务状态
        /// </summary>
        [HttpGet("deploy/{taskId}")]
        public ActionResult<DeployTaskStatusDto> GetDeployStatus(string taskId)
        {
            var status = _deploymentService.GetRunnerTaskStatus(taskId);
            if (status == null)
                return NotFound(new { error = _loc["LocalModel_TaskNotFound"], taskId });

            return Ok(status);
        }

        /// <summary>
        /// 取消部署任务
        /// </summary>
        [HttpPost("deploy/{taskId}/cancel")]
        public ActionResult CancelDeploy(string taskId)
        {
            var cancelled = _deploymentService.CancelTask(taskId);
            if (!cancelled)
                return NotFound(new { error = _loc["LocalModel_TaskNotFoundOrDone"], taskId });

            return Ok(new { success = true, message = _loc["LocalModel_TaskCancelled"] });
        }

        /// <summary>
        /// 获取已安装的本地 AI 工具
        /// </summary>
        [HttpGet("tools")]
        public async Task<ActionResult<List<LocalToolInfoDto>>> GetTools([FromQuery] bool forceRefresh = false)
        {
            try
            {
                var tools = await _deploymentService.GetLocalToolsAsync(forceRefresh);
                return Ok(tools);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取本地工具信息失败");
                return StatusCode(500, new { error = _loc["LocalModel_GetToolsFailed"], message = ex.Message });
            }
        }

        /// <summary>
        /// 获取可用下载源
        /// </summary>
        [HttpGet("sources")]
        public ActionResult<List<DownloadSourceDto>> GetSources()
        {
            return Ok(_deploymentService.GetDownloadSources());
        }

        /// <summary>
        /// 获取下载目录配置
        /// </summary>
        [HttpGet("config")]
        public ActionResult<DownloadDirectoryConfigDto> GetConfig()
        {
            var dto = new DownloadDirectoryConfigDto
            {
                DownloadDirectory = _localModelSettings.LocalModelDownloadDirectory,
                PreferredSource = _localModelSettings.PreferredDownloadSource,
                UseChinaMirror = _localModelSettings.UseChinaMirror,
                PlatformDefaultDirectory = GetPlatformDefaultDirectory(),
            };
            return Ok(dto);
        }

        /// <summary>
        /// 保存下载目录配置
        /// </summary>
        [HttpPost("config")]
        public ActionResult SaveConfig([FromBody] DownloadDirectoryConfigDto config)
        {
            try
            {
                if (!string.IsNullOrEmpty(config.DownloadDirectory))
                {
                    _localModelSettings.LocalModelDownloadDirectory = config.DownloadDirectory;
                }

                if (!string.IsNullOrEmpty(config.PreferredSource))
                {
                    _localModelSettings.PreferredDownloadSource = config.PreferredSource;
                }

                _localModelSettings.UseChinaMirror = config.UseChinaMirror;

                return Ok(new { success = true, message = _loc["LocalModel_ConfigSaved"] });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存下载目录配置失败");
                return StatusCode(500, new { error = _loc["LocalModel_SaveFailed"], message = ex.Message });
            }
        }

}
