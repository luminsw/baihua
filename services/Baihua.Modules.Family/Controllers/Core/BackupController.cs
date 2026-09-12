using Microsoft.AspNetCore.Mvc;
using Baihua.Core.Localization;
using Baihua.Modules.Family.Services;
using Microsoft.Extensions.Localization;

namespace Baihua.Modules.Family.Controllers;

[ApiController]
[Route("api/[controller]")]
public partial class BackupController : ControllerBase
{
    private readonly BackupService _backupService;
    private readonly ILogger<BackupController> _logger;
    private readonly IStringLocalizer<SharedResources> _loc;

    public BackupController(BackupService backupService, ILogger<BackupController> logger, IStringLocalizer<SharedResources> loc)
    {
        _backupService = backupService;
        _logger = logger;
        _loc = loc;
    }

    [HttpPost("full")]
    public async Task<ActionResult<FullBackupResponse>> CreateFullBackup([FromBody] FullBackupRequest request)
    {
        try
        {
            var result = await _backupService.CreateFullBackupAsync(request.BackupDir, request.Password, HttpContext.RequestAborted);

            return Ok(new FullBackupResponse
            {
                Success = result.Success,
                Message = result.Success ? _loc["Backup_FullBackupCreated"] : string.Format(_loc["Backup_Failed"], result.Error),
                BackupPath = result.BackupPath,
                BackupTime = result.BackupTime,
                FileSize = result.FileSize
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建全量备份失败");
            return Ok(new FullBackupResponse { Success = false, Message = string.Format(_loc["Backup_Failed"], ex.Message) });
        }
    }

    [HttpPost("restore")]
    public async Task<ActionResult<FullRestoreResponse>> RestoreFullBackup([FromBody] FullRestoreRequest request)
    {
        try
        {
            var result = await _backupService.RestoreFullBackupAsync(
                request.BackupPath, request.Password, request.VaultRootPathOverride, request.Overwrite, HttpContext.RequestAborted);

            return Ok(new FullRestoreResponse
            {
                Success = result.Success,
                Message = result.Success ? _loc["Backup_RestoreSuccess"] : string.Format(_loc["Backup_RestoreFailed"], result.Error),
                SourcePlatform = result.SourcePlatform,
                SourceOS = result.SourceOS,
                RestoredAt = result.RestoredAt
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "恢复全量备份失败");
            return Ok(new FullRestoreResponse { Success = false, Message = string.Format(_loc["Backup_RestoreFailed"], ex.Message) });
        }
    }

    [HttpPost("validate")]
    public async Task<ActionResult<ValidateBackupResponse>> ValidateBackup([FromBody] ValidateBackupRequest request)
    {
        try
        {
            var result = await _backupService.ValidateFullBackupAsync(request.BackupPath, request.Password);

            return Ok(new ValidateBackupResponse
            {
                Success = true,
                IsValid = result.IsValid,
                Version = result.Version,
                CreatedAt = result.CreatedAt,
                SourcePlatform = result.SourcePlatform,
                SourceOS = result.SourceOS,
                HasPassword = result.HasPassword,
                HasDatabase = result.HasDatabase,
                HasConfig = result.HasConfig,
                HasVaults = result.HasVaults,
                VaultCount = result.VaultCount,
                Message = result.IsValid ? _loc["Backup_FileValid"] : string.Format(_loc["Backup_Invalid"], result.Error)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "验证备份文件失败");
            return Ok(new ValidateBackupResponse { Success = false, IsValid = false, Message = string.Format(_loc["Backup_ValidateFailed"], ex.Message) });
        }
    }

    [HttpGet("list")]
    public ActionResult<BackupListResponse> GetBackupList([FromQuery] string? backupPath = null)
    {
        try
        {
            var backups = _backupService.GetBackupList(backupPath);
            return Ok(new BackupListResponse { Success = true, Backups = backups });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取备份列表失败");
            return Ok(new BackupListResponse { Success = false, Message = string.Format(_loc["Backup_ListFailedDetail"], ex.Message) });
        }
    }
}
