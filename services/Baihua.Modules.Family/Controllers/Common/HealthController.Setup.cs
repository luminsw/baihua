using Baihua.Core.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Baihua.Contracts.Health;
using Baihua.Modules.Family.Services;

namespace Baihua.Modules.Family.Controllers;

public partial class HealthController
{
        [HttpPost("setup/openclaw")]
        public async Task<ActionResult<dynamic>> SetupOpenClaw(CancellationToken cancellationToken)
        {
            try
            {
                // 1. 检查 openclaw 是否已安装
                var checkPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "openclaw",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var checkProcess = System.Diagnostics.Process.Start(checkPsi);
                if (checkProcess == null)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = _loc["Health_Setup_OpenClawNotInstalled"]
                    });
                }

                await checkProcess.WaitForExitAsync(cancellationToken);
                if (checkProcess.ExitCode != 0)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = _loc["Health_Setup_OpenClawBroken"]
                    });
                }

                var version = await checkProcess.StandardOutput.ReadToEndAsync(cancellationToken);

                // 2. 后台运行 openclaw doctor --fix（不阻塞等待完成）
                var doctorPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "openclaw",
                    Arguments = "doctor --fix",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var doctorProcess = System.Diagnostics.Process.Start(doctorPsi);
                if (doctorProcess == null)
                {
                    return StatusCode(500, new { success = false, message = _loc["Health_Setup_DoctorStartFailed"] });
                }

                // 异步读取输出，不等待进程退出（doctor 可能需要较长时间）
                var doctorTask = Task.Run(async () =>
                {
                    var stdout = await doctorProcess.StandardOutput.ReadToEndAsync();
                    var stderr = await doctorProcess.StandardError.ReadToEndAsync();
                    await doctorProcess.WaitForExitAsync();
                    return (stdout, stderr, doctorProcess.ExitCode);
                }, cancellationToken);

                // 3. 获取模型列表（快速操作）
                var modelsPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "openclaw",
                    Arguments = "models list --json",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var modelsJson = "";
                using var modelsProcess = System.Diagnostics.Process.Start(modelsPsi);
                if (modelsProcess != null)
                {
                    using var modelsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    modelsCts.CancelAfter(TimeSpan.FromSeconds(10));
                    try
                    {
                        await modelsProcess.WaitForExitAsync(modelsCts.Token);
                        modelsJson = await modelsProcess.StandardOutput.ReadToEndAsync(cancellationToken);
                    }
                    catch { /* ignore timeout */ }
                }

                // 尝试等待 doctor 最多 15 秒获取即时结果
                var doctorCompleted = doctorTask.Wait(TimeSpan.FromSeconds(15));
                var (doctorStdout, doctorStderr, exitCode) = doctorCompleted
                    ? doctorTask.Result
                    : ("", "", -1);

                return Ok(new
                {
                    success = exitCode == 0 || !doctorCompleted,
                    version = version.Trim(),
                    doctorCompleted,
                    doctorExitCode = exitCode,
                    doctorOutput = doctorStdout.Trim(),
                    doctorErrors = doctorStderr.Trim(),
                    modelsJson = modelsJson.Trim(),
                    message = doctorCompleted
                        ? (exitCode == 0 ? _loc["Health_Setup_FixCompleted"] : _loc["Health_Setup_DoctorNonZeroExit"])
                        : _loc["Health_Setup_DoctorBackground"]
                });
            }
            catch (OperationCanceledException)
            {
                return StatusCode(504, new { success = false, message = _loc["Health_Setup_Timeout"] });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OpenClaw 配置失败");
                return StatusCode(500, new { success = false, message = _loc["Health_Setup_Failed", ex.Message] });
            }
        }
}
