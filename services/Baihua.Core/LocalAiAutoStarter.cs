using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Baihua.Core.Services
{
    /// <summary>
    /// 自动检测并启动本地 AI 服务（LM Studio / Ollama）。
    /// 当 provider 的 BaseUrl 指向 localhost 且端口不可连时，尝试用 CLI 命令启动。
    /// </summary>
    public class LocalAiAutoStarter
    {
        private readonly ILogger<LocalAiAutoStarter> _logger;
        private static readonly Dictionary<string, DateTime> _lastStartAttempts = new();
        private static readonly TimeSpan StartCooldown = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan MaxWaitTime = TimeSpan.FromSeconds(15);

        public LocalAiAutoStarter(ILogger<LocalAiAutoStarter> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 如果 provider 是本地服务且端口未监听，尝试自动启动。
        /// 返回 true 表示服务已可用（原本就运行或成功启动）。
        /// </summary>
        public async Task<bool> TryEnsureRunningAsync(string providerId, string baseUrl)
        {
            if (!IsLocalUrl(baseUrl))
                return true;

            Uri uri;
            try
            {
                uri = new Uri(baseUrl);
            }
            catch
            {
                return true;
            }

            // 先快速探测端口
            if (await IsPortOpenAsync(uri.Host, uri.Port))
                return true;

            var key = $"{providerId}:{uri.Host}:{uri.Port}";
            lock (_lastStartAttempts)
            {
                if (_lastStartAttempts.TryGetValue(key, out var lastAttempt)
                    && DateTime.Now - lastAttempt < StartCooldown)
                {
                    _logger.LogDebug("本地 AI 服务 {ProviderId} 自动启动冷却中，跳过", providerId);
                    return false;
                }
                _lastStartAttempts[key] = DateTime.Now;
            }

            return await TryStartAsync(providerId, uri);
        }

        private static bool IsLocalUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            return url.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                || url.Contains("127.0.0.1")
                || url.Contains("0.0.0.0");
        }

        private static async Task<bool> IsPortOpenAsync(string host, int port)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> TryStartAsync(string providerId, Uri uri)
        {
            var (command, args) = GetStartCommand(providerId, uri);
            if (string.IsNullOrEmpty(command))
            {
                _logger.LogWarning("未找到本地 AI 服务 {ProviderId} 的自动启动命令", providerId);
                return false;
            }

            _logger.LogInformation("正在尝试自动启动本地 AI 服务 {ProviderId}: {Command} {Args}", providerId, command, args);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    _logger.LogWarning("无法启动进程 {Command}", command);
                    return false;
                }

                // 读取并记录启动输出（短时间内）
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                // 最多等待服务就绪
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < MaxWaitTime)
                {
                    await Task.Delay(500);

                    if (await IsPortOpenAsync(uri.Host, uri.Port))
                    {
                        _logger.LogInformation("本地 AI 服务 {ProviderId} 启动成功，端口 {Port} 已监听", providerId, uri.Port);
                        return true;
                    }

                    if (process.HasExited)
                    {
                        var stdout = await stdoutTask;
                        var stderr = await stderrTask;
                        _logger.LogWarning("本地 AI 启动进程已退出，Code={ExitCode}。stdout={Stdout} stderr={Stderr}",
                            process.ExitCode, stdout, stderr);
                        return false;
                    }
                }

                _logger.LogWarning("本地 AI 服务 {ProviderId} 在 {MaxWait}s 内未就绪，端口 {Port} 仍未监听",
                    providerId, MaxWaitTime.TotalSeconds, uri.Port);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "自动启动本地 AI 服务 {ProviderId} 失败", providerId);
                return false;
            }
        }

        private static (string? Command, string Args) GetStartCommand(string providerId, Uri uri)
        {
            var idLower = providerId.ToLowerInvariant();

            // LM Studio
            if (idLower.Contains("lmstudio") || uri.Port == 1234)
                return ("lms", "server start");

            // Ollama
            if (idLower.Contains("ollama") || uri.Port == 11434)
                return ("ollama", "serve");

            return (null, "");
        }
    }
}
