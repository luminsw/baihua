using Baihua.Core.Models;
using Baihua.Core.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ComponentStatus = Baihua.Contracts.Health.ComponentStatusDto;

namespace Baihua.Modules.Family.Services
{
    public partial class SystemHealthService
    {
        private async Task<ComponentStatus> CheckGitAsync(CancellationToken cancellationToken)
        {
            Process? process = null;
            try
            {
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process is null)
                    return new ComponentStatus { Name = "Git", Status = "critical", Message = _loc["Health_GitNotInstalled"] };

                var (ok, exitCode, output) = await WaitForProcessAsync(process, 4000, cancellationToken);
                if (!ok)
                    return new ComponentStatus { Name = "Git", Status = "critical", Message = _loc["Health_GitTimeout"] };
                if (exitCode != 0)
                    return new ComponentStatus { Name = "Git", Status = "critical", Message = _loc["Health_GitCheckFailed"] };

                return new ComponentStatus
                {
                    Name = "Git",
                    Status = "healthy",
                    Version = HealthCheckHelper.ExtractVersion(output),
                    Message = _loc["Health_GitInstalled"]
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Git 检测失败");
                return new ComponentStatus { Name = "Git", Status = "critical", Message = _loc["Health_GitCheckError"] };
            }
        }

        private Task<ComponentStatus> CheckObsidianAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var obsidianRunning = Process.GetProcessesByName("Obsidian").Length > 0;

                if (obsidianRunning)
                {
                    return Task.FromResult(new ComponentStatus
                    {
                        Name = "Obsidian",
                        Status = "healthy",
                        Message = _loc["Health_ObsidianRunning"]
                    });
                }

                // Linux 上 Obsidian 桌面客户端不是必须的，FTS5 全文搜索已替代 CLI 搜索功能
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    return Task.FromResult(new ComponentStatus
                    {
                        Name = "Obsidian",
                        Status = "healthy",
                        Message = _loc["Health_ObsidianNotRunningLinux"]
                    });
                }

                return Task.FromResult(new ComponentStatus
                {
                    Name = "Obsidian",
                    Status = "warning",
                    Message = _loc["Health_ObsidianNotRunningWindows"]
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Obsidian 检测失败");
                return Task.FromResult(new ComponentStatus
                {
                    Name = "Obsidian",
                    Status = "warning",
                    Message = _loc["Health_ObsidianCheckError"]
                });
            }
        }

        /// <summary>
        /// 启动时初始化 Obsidian（仅调用一次）
        /// 如果 CLI 已安装但 Obsidian 未运行，则启动 Obsidian
        /// </summary>
        public async Task InitializeObsidianAsync()
        {
            try
            {
                var obsidianRunning = Process.GetProcessesByName("Obsidian").Length > 0;
                if (obsidianRunning)
                {
                    _logger.LogInformation("Obsidian 已在运行，跳过启动");
                    return;
                }

                // 过去用 `obsidian help` 做“CLI 可用性探测”，在 Windows 上可能表现为“打开又关闭”，
                // 反而干扰用户观察。这里不再探测，直接尝试启动桌面端；失败则记录并降级为文件扫描。
                var obsidianExe = ObsidianExecutableResolver.Resolve();
                _logger.LogInformation("Obsidian 未运行，尝试启动：{Exe}", obsidianExe);

                var startInfo = new ProcessStartInfo
                {
                    FileName = obsidianExe,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.EnvironmentVariables["ELECTRON_DISABLE_AUTO_UPDATE"] = "1";
                startInfo.EnvironmentVariables["OBSIDIAN_DISABLE_AUTO_UPDATE"] = "1";

                Process.Start(startInfo);
                _logger.LogInformation("Obsidian 启动成功");

                await Task.Delay(3000);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "启动 Obsidian 失败");
            }
        }

    }
}
