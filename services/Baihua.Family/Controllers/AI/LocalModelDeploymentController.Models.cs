using Microsoft.AspNetCore.Mvc;
using Baihua.Contracts.LocalModels;
using Baihua.Contracts.OpenClaw;
using Baihua.Family.Services;

namespace Baihua.Family.Controllers;

public partial class LocalModelDeploymentController
{
    #region Running Model Management

    /// <summary>
    /// 获取运行中的模型列表
    /// </summary>
    [HttpGet("running")]
    public async Task<ActionResult<List<RunningModelDto>>> GetRunningModels([FromQuery] bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var models = await _deploymentService.GetRunningModelsAsync(forceRefresh, cancellationToken);
            return Ok(models);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取运行中模型失败");
            return StatusCode(500, new { error = _loc["LocalModel_GetRunningFailed"], message = ex.Message });
        }
    }

    /// <summary>
    /// 获取指定工具中可用的模型列表（已下载）
    /// </summary>
    [HttpGet("available")]
    public async Task<ActionResult<List<string>>> GetAvailableModels([FromQuery] string toolId, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(toolId))
                return BadRequest(new { error = _loc["LocalModel_ToolIdRequired"] });

            var models = await _deploymentService.GetAvailableModelsAsync(toolId, cancellationToken);
            return Ok(models);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取可用模型列表失败");
            return StatusCode(500, new { error = _loc["LocalModel_GetAvailableFailed"], message = ex.Message });
        }
    }

    /// <summary>
    /// 加载模型到内存
    /// </summary>
    [HttpPost("running/load")]
    public async Task<ActionResult<dynamic>> LoadModel([FromBody] LoadModelRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.ToolId) || string.IsNullOrWhiteSpace(request.ModelName))
                return BadRequest(new { error = _loc["LocalModel_ToolIdModelNameRequired"] });

            var success = await _deploymentService.LoadModelAsync(request.ToolId, request.ModelName, request.KeepAliveMinutes, cancellationToken);
            if (success)
                return Ok(new { success = true, message = string.Format(_loc["LocalModel_ModelLoaded"], request.ModelName) });

            return StatusCode(500, new { error = _loc["LocalModel_LoadFailed"], message = _loc["LocalModel_CheckToolRunning"] });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载模型失败");
            return StatusCode(500, new { error = _loc["LocalModel_LoadFailed"], message = ex.Message });
        }
    }

    /// <summary>
    /// 卸载模型释放内存
    /// </summary>
    [HttpPost("running/unload")]
    public async Task<ActionResult<dynamic>> UnloadModel([FromBody] UnloadModelRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.ToolId) || string.IsNullOrWhiteSpace(request.ModelName))
                return BadRequest(new { error = _loc["LocalModel_ToolIdModelNameRequired"] });

            var success = await _deploymentService.UnloadModelAsync(request.ToolId, request.ModelName, cancellationToken);
            if (success)
                return Ok(new { success = true, message = string.Format(_loc["LocalModel_ModelUnloaded"], request.ModelName) });

            return StatusCode(500, new { error = _loc["LocalModel_UnloadFailed"], message = _loc["LocalModel_CheckToolRunning"] });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "卸载模型失败");
            return StatusCode(500, new { error = _loc["LocalModel_UnloadFailed"], message = ex.Message });
        }
    }

    /// <summary>
    /// 获取所有已下载模型（聚合所有工具）
    /// </summary>
    [HttpGet("downloaded")]
    public async Task<ActionResult<List<DownloadedModelDto>>> GetDownloadedModels(CancellationToken cancellationToken)
    {
        try
        {
            var models = await _deploymentService.GetDownloadedModelsAsync(cancellationToken);
            return Ok(models);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取已下载模型列表失败");
            return StatusCode(500, new { error = _loc["LocalModel_GetDownloadedFailed"], message = ex.Message });
        }
    }

    /// <summary>
    /// 删除本地模型
    /// </summary>
    [HttpPost("delete")]
    public async Task<ActionResult> DeleteModel([FromBody] DeleteModelRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.ToolId) || string.IsNullOrWhiteSpace(request.ModelName))
                return BadRequest(new { error = _loc["LocalModel_ToolIdModelNameRequired"] });

            var success = await _deploymentService.DeleteModelAsync(request.ToolId, request.ModelName, cancellationToken);
            if (success)
                return Ok(new { success = true, message = string.Format(_loc["LocalModel_ModelDeleted"], request.ModelName) });

            return StatusCode(500, new { error = _loc["LocalModel_DeleteFailed"], message = _loc["LocalModel_CheckToolOrModel"] });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除模型失败");
            return StatusCode(500, new { error = _loc["LocalModel_DeleteFailed"], message = ex.Message });
        }
    }

    /// <summary>
    /// 获取模型详情
    /// </summary>
    [HttpPost("details")]
    public async Task<ActionResult<ModelDetailsDto?>> GetModelDetails([FromBody] ModelDetailsRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.ToolId) || string.IsNullOrWhiteSpace(request.ModelName))
                return BadRequest(new { error = _loc["LocalModel_ToolIdModelNameRequired"] });

            var details = await _deploymentService.GetModelDetailsAsync(request.ToolId, request.ModelName, cancellationToken);
            if (details != null)
                return Ok(details);

            return NotFound(new { error = _loc["LocalModel_DetailsNotFound"], message = _loc["LocalModel_DetailsNotSupported"] });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取模型详情失败");
            return StatusCode(500, new { error = _loc["LocalModel_GetDetailsFailed"], message = ex.Message });
        }
    }

    #endregion
}
