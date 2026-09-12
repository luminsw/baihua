using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using Baihua.AI.Provider;
using ComponentStatus = Baihua.Contracts.Health.ComponentStatusDto;

namespace Baihua.Modules.Family.Services
{
    public partial class SystemHealthService
    {
        private async Task<ComponentStatus> CheckOllamaAsync(CancellationToken cancellationToken)
        {
            try
            {
                // 只检测 Ollama 服务是否在运行，不尝试启动进程
                var ollamaRunning = await IsOllamaServiceUpAsync(cancellationToken);

                if (!ollamaRunning)
                {
                    return new ComponentStatus
                    {
                        Name = "Ollama",
                        Status = "warning",
                        Message = _loc["Health_OllamaNotRunning"]
                    };
                }

                return new ComponentStatus
                {
                    Name = "Ollama",
                    Status = "healthy",
                    Message = _loc["Health_OllamaRunning"]
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ollama 检测失败");
                return new ComponentStatus
                {
                    Name = "Ollama",
                    Status = "warning",
                    Message = _loc["Health_OllamaCheckError"]
                };
            }
        }

        private async Task<(bool ExitedOk, int ExitCode, string Stdout)> RunSimpleCommandAsync(
            string fileName,
            string arguments,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            Process? process = null;
            try
            {
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "{File} 启动失败", fileName);
                return (false, -1, "");
            }

            if (process is null)
                return (false, -1, "");

            var (ok, exitCode, stdout) = await WaitForProcessAsync(process, timeoutMs, cancellationToken);
            if (!ok)
                return (false, exitCode, stdout);
            return (true, exitCode, stdout);
        }

        private async Task<(bool ExitedOk, int ExitCode, string Stdout)> RunWslAsync(
            IReadOnlyList<string> argumentList,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            Process? process = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "wsl.exe",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var a in argumentList)
                    psi.ArgumentList.Add(a);

                process = Process.Start(psi);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "wsl.exe 启动失败（可能未安装 WSL）");
                return (false, -1, "");
            }

            if (process is null)
                return (false, -1, "");

            return await WaitForProcessAsync(process, timeoutMs, cancellationToken);
        }

        /// <summary>
        /// Windows 上对本机环回与 WSL 内 11434 并行探测，避免串行 HTTP+curl 多等几秒。
        /// </summary>
        private async Task<bool> IsOllamaServiceUpAsync(CancellationToken cancellationToken)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return await ProbeOllamaHttpFromWindowsAsync(cancellationToken).ConfigureAwait(false);

            var winTask = ProbeOllamaHttpFromWindowsAsync(cancellationToken);
            var wslTask = ProbeOllamaHttpViaWslAsync(cancellationToken);
            var first = await Task.WhenAny(winTask, wslTask).ConfigureAwait(false);
            if (first == winTask)
            {
                if (await winTask.ConfigureAwait(false))
                    return true;
                return await wslTask.ConfigureAwait(false);
            }

            if (await wslTask.ConfigureAwait(false))
                return true;
            return await winTask.ConfigureAwait(false);
        }

        private async Task<bool> ProbeOllamaHttpFromWindowsAsync(CancellationToken cancellationToken)
        {
            foreach (var baseUrl in new[] { "http://127.0.0.1:11434/", "http://localhost:11434/" })
            {
                try
                {
                    using var client = _httpClientFactory.CreateClient("SystemHealth");
                    using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl);
                    using var response = await client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false);
                    if ((int)response.StatusCode < 500)
                        return true;
                }
                catch
                {
                    /* try next */
                }
            }

            return false;
        }

        private async Task<bool> ProbeOllamaHttpViaWslAsync(CancellationToken cancellationToken)
        {
            var curl = await RunWslAsync(
                new[] { "-e", "curl", "-fsS", "-m", "2", "-o", "/dev/null", "http://127.0.0.1:11434/" },
                3500,
                cancellationToken);
            if (curl.ExitedOk && curl.ExitCode == 0)
                return true;

            var wget = await RunWslAsync(
                new[] { "-e", "wget", "-q", "-T", "2", "-O", "/dev/null", "http://127.0.0.1:11434/" },
                3500,
                cancellationToken);
            return wget.ExitedOk && wget.ExitCode == 0;
        }

    }
}
