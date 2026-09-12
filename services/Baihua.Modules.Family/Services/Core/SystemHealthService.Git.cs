using Baihua.Core.Models;
using Baihua.Core.Services;
using System.Diagnostics;
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
    }
}
