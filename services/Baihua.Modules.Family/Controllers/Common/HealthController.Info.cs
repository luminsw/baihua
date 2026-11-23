using Microsoft.AspNetCore.Mvc;
using Baihua.Contracts.Health;
using Baihua.Modules.Family.Services;

namespace Baihua.Modules.Family.Controllers;

public partial class HealthController
{
        [HttpGet("startup")]
        public ActionResult<dynamic> GetStartupInfo()
        {
            try
            {
                var monitor = StartupMonitor.Instance;
                return Ok(new
                {
                    startTime = monitor.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    uptime = (DateTime.Now - monitor.StartTime).ToString(@"hh\:mm\:ss"),
                    restartCount = monitor.RestartCount,
                    pid = Environment.ProcessId,
                    recentRestarts = monitor.RestartHistory
                        .Where(t => (DateTime.Now - t).TotalMinutes < 10)
                        .Select(t => t.ToString("yyyy-MM-dd HH:mm:ss"))
                        .ToList()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取启动信息失败");
                return StatusCode(500, new { error = _loc["Health_StartupInfoFailed"], message = ex.Message });
            }
        }

        /// <summary>
        /// 获取服务器操作系统信息
        /// </summary>
        [HttpGet("os")]
        public ActionResult<dynamic> GetOperatingSystem()
        {
            try
            {
                var os = Environment.OSVersion;
                var platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
                var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows);
                var isLinux = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Linux);
                var isMac = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.OSX);

                return Ok(new
                {
                    OsName = isWindows ? "Windows" : isLinux ? "Linux" : isMac ? "macOS" : "Unknown",
                    IsWindows = isWindows,
                    IsLinux = isLinux,
                    IsMacOS = isMac,
                    UserName = Environment.UserName,
                    HomeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    AiRequestTimeoutMinutes = _aiSettings.AiRequestTimeoutMinutes
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取操作系统信息失败");
                return StatusCode(500, new { error = _loc["Health_OsInfoFailed"], message = ex.Message });
            }
        }
}
