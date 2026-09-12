using Baihua.Contracts.Backup;
using Baihua.Modules.Family.Services;
using Microsoft.AspNetCore.Mvc;

namespace Baihua.Modules.Family.Controllers;

/// <summary>
/// 移动端（花记）设备备份 API。
///
/// 花记 App 通过本组接口把本地全量数据备份上传到百花存档，
/// 或从百花下载备份用于恢复。接口均位于 /mg/device-backup 前缀下：
/// - 自动纳入移动端 HMAC 签名校验域（mobileApiPaths）
/// - 允许局域网访问（publicPaths，与 /mg/vaults 等移动端接口一致）
///
/// 设备隔离：以 X-Device-Id 请求头（由移动端签名器自动携带）区分设备，
/// 每台设备只能管理自己的备份。
/// </summary>
[ApiController]
[Route("mg/device-backup")]
public class DeviceBackupController : ControllerBase
{
    private readonly DeviceBackupService _backupService;
    private readonly ILogger<DeviceBackupController> _logger;

    public DeviceBackupController(DeviceBackupService backupService, ILogger<DeviceBackupController> logger)
    {
        _backupService = backupService;
        _logger = logger;
    }

    /// <summary>
    /// 上传设备备份（JSON，Base64 编码的 ZIP）。
    /// 服务器按设备归档保存，并滚动清理过期备份（默认保留 7 份）。
    /// </summary>
    [HttpPost("upload")]
    [RequestSizeLimit(300_000_000)]
    public async Task<ActionResult<UploadDeviceBackupResponse>> Upload([FromBody] UploadDeviceBackupRequest request)
    {
        try
        {
            if (request == null)
            {
                return BadRequest(new UploadDeviceBackupResponse { Success = false, Message = "请求体不能为空" });
            }

            var deviceId = ResolveDeviceId(request.DeviceId);
            var result = await _backupService.SaveAsync(deviceId, request.Base64Data ?? "", HttpContext.RequestAborted);

            return Ok(new UploadDeviceBackupResponse
            {
                Success = result.Success,
                Message = result.Success
                    ? $"备份上传成功：{Path.GetFileName(result.BackupPath)}（{result.FileSize} 字节）"
                    : $"备份上传失败：{result.Error}",
                BackupId = result.Success ? Path.GetFileName(result.BackupPath) : null,
                BackupTime = result.BackupTime,
                FileSize = result.FileSize
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "设备备份上传失败");
            return Ok(new UploadDeviceBackupResponse { Success = false, Message = $"备份上传失败：{ex.Message}" });
        }
    }

    /// <summary>列出本设备的备份（新 → 旧）</summary>
    [HttpGet("list")]
    public ActionResult<DeviceBackupListResponse> List()
    {
        try
        {
            var deviceId = ResolveDeviceId();
            var backups = _backupService.List(deviceId);
            return Ok(new DeviceBackupListResponse { Success = true, Backups = backups });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取设备备份列表失败");
            return Ok(new DeviceBackupListResponse { Success = false, Message = ex.Message });
        }
    }

    /// <summary>下载指定备份（原始 ZIP 字节流）</summary>
    [HttpGet("{id}/download")]
    public IActionResult Download(string id)
    {
        var deviceId = ResolveDeviceId();
        var fullPath = _backupService.ResolveFilePath(deviceId, id);
        if (fullPath == null)
        {
            return NotFound(new { error = "备份不存在或 ID 非法" });
        }

        var bytes = System.IO.File.ReadAllBytes(fullPath);
        return File(bytes, "application/octet-stream", Path.GetFileName(fullPath));
    }

    /// <summary>删除指定备份</summary>
    [HttpDelete("{id}")]
    public ActionResult<DeleteDeviceBackupResponse> Delete(string id)
    {
        var deviceId = ResolveDeviceId();
        var deleted = _backupService.Delete(deviceId, id);
        return Ok(new DeleteDeviceBackupResponse
        {
            Success = deleted,
            Message = deleted ? "备份已删除" : "备份不存在或 ID 非法"
        });
    }

    /// <summary>
    /// 解析设备 ID：优先取签名器携带的 X-Device-Id 请求头（服务端可信来源），
    /// 头部缺失时回退到请求体字段。
    /// </summary>
    private string ResolveDeviceId(string? bodyDeviceId = null)
    {
        var headerId = Request.Headers["X-Device-Id"].FirstOrDefault();
        return !string.IsNullOrWhiteSpace(headerId) ? headerId : (bodyDeviceId ?? "");
    }
}
