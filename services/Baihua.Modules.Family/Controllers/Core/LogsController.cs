using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Baihua.Modules.Family.Controllers;

/// <summary>
/// 日志查询API。支持按级别、类别、关键词、时间范围过滤JSON Lines日志。
/// 用法: GET /api/logs?level=WARN&amp;category=MDns&amp;search=multicast&amp;lines=100
///       GET /api/logs?level=ERR&amp;since=2026-04-12T14:00:00
/// </summary>
[ApiController]
[Route("api/logs")]
public partial class LogsController : ControllerBase
{
    private readonly ILogger<LogsController> _logger;
    private readonly string _logsDir;

    public LogsController(ILogger<LogsController> logger, IHostEnvironment env)
    {
        _logger = logger;
        _logsDir = Path.Combine(env.ContentRootPath ?? AppContext.BaseDirectory, "logs");
    }

    /// <summary>
    /// 查询日志
    /// </summary>
    /// <param name="level">最低级别过滤: DBG/INFO/WARN/ERR/CRIT</param>
    /// <param name="category">类别名过滤（前缀匹配，不区分大小写）</param>
    /// <param name="search">关键词搜索（消息内容包含）</param>
    /// <param name="since">起始时间 ISO8601</param>
    /// <param name="until">结束时间 ISO8601</param>
    /// <param name="lines">最大返回行数，默认100，最大1000</param>
    /// <param name="file">指定日志文件名（默认今天）</param>
    [HttpGet]
    public IActionResult GetLogs(
        [FromQuery] string? level,
        [FromQuery] string? category,
        [FromQuery] string? search,
        [FromQuery] string? since,
        [FromQuery] string? until,
        [FromQuery] int lines = 100,
        [FromQuery] string? file = null)
    {
        try
        {
            return HandleGetLogs(level, category, search, since, until, lines, file);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying logs");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// 列出可用的日志文件
    /// </summary>
    [HttpGet("files")]
    public IActionResult ListLogFiles()
    {
        try
        {
            if (!Directory.Exists(_logsDir))
                return Ok(Array.Empty<object>());

            var files = Directory.GetFiles(_logsDir, "baihua-family-*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => new
                {
                    name = f.Name,
                    size = f.Length,
                    lastModified = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                })
                .ToList();

            return Ok(files);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
