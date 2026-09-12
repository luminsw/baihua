using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Localization;
using Baihua.Contracts.OpenClaw;
using Baihua.Core.Localization;
using Baihua.AI.Provider;

namespace Baihua.Modules.Family.Services;

public partial class LocalAiConfigService
{
    #region Detect and Start Local AI

    public async Task<LocalAiServiceStatusDto> DetectAndStartLocalAiAsync(string provider)
    {
        var result = new LocalAiServiceStatusDto { Provider = provider };

        // llama.cpp 特殊处理
        if (provider.ToLowerInvariant() == "llamacpp")
        {
            return await DetectAndStartLlamaCppAsync();
        }

        // OpenVINO 特殊处理
        if (provider.ToLowerInvariant() == "openvino")
        {
            return await DetectAndStartOpenVinoAsync();
        }

        var (checkUrl, startCmd, startArgs, displayName) = provider.ToLowerInvariant() switch
        {
            "ollama" => ("http://localhost:11434/api/tags", "ollama", "serve", "Ollama"),
            "lmstudio" => ("http://localhost:1234/v1/models", "lms", "server start", "LM Studio"),
            _ => (null, null, null, provider)
        };

        if (checkUrl == null)
        {
            result.Message = string.Format(_loc["LocalAi_UnsupportedProvider"], provider);
            return result;
        }

        // 1. 检测服务是否已运行
        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var response = await client.GetAsync(checkUrl);
            if (response.IsSuccessStatusCode)
            {
                result.IsRunning = true;
                result.Message = string.Format(_loc["LocalAi_ServiceRunning"], displayName);
                return result;
            }
        }
        catch
        {
            // 未运行，继续尝试启动
        }

        // 2. 尝试启动服务
        result.AttemptedStart = true;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = startCmd,
                Arguments = startArgs,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var process = Process.Start(startInfo);
            if (process == null)
            {
                result.Message = string.Format(_loc["LocalAi_StartFailedCannotCreateProcess"], displayName);
                return result;
            }

            // 等待服务启动
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                try
                {
                    using var client = httpClientFactory.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(3);
                    var response = await client.GetAsync(checkUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        result.IsRunning = true;
                        result.StartSuccess = true;
                        result.Message = string.Format(_loc["LocalAi_StartSuccess"], displayName);
                        return result;
                    }
                }
                catch (Exception ex) { logger.LogDebug(ex, "探测 {DisplayName} 启动状态失败", displayName); }
            }

            result.Message = string.Format(_loc["LocalAi_StartTimeout"], displayName);
            logger.LogWarning("{DisplayName} 启动后未就绪", displayName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动 {DisplayName} 失败", displayName);
            result.Message = string.Format(_loc["LocalAi_StartFailedDetail"], displayName, ex.Message);
        }

        return result;
    }

    private async Task<LocalAiServiceStatusDto> DetectAndStartLlamaCppAsync()
    {
        var result = new LocalAiServiceStatusDto { Provider = "llamacpp" };
        var config = await GetLocalAiConfigAsync();
        var llamaCpp = config.LlamaCpp;

        if (llamaCpp == null || !llamaCpp.Enabled)
        {
            result.Message = _loc["LocalAi_LlamaCppNotEnabled"];
            return result;
        }

        if (string.IsNullOrWhiteSpace(llamaCpp.BinaryPath) || !File.Exists(llamaCpp.BinaryPath))
        {
            result.Message = string.Format(_loc["LocalAi_BinaryPathInvalid"], llamaCpp.BinaryPath);
            return result;
        }

        if (string.IsNullOrWhiteSpace(llamaCpp.ModelPath) || !File.Exists(llamaCpp.ModelPath))
        {
            result.Message = string.Format(_loc["LocalAi_ModelFileNotFound"], llamaCpp.ModelPath);
            return result;
        }

        var checkUrl = $"{llamaCpp.BaseUrl.TrimEnd('/')}/v1/models";

        // 1. 检测服务是否已运行
        try
        {
            using var httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(5);
            var response = await httpClient.GetAsync(checkUrl);
            if (response.IsSuccessStatusCode)
            {
                result.IsRunning = true;
                result.Message = _loc["LocalAi_LlamaCppRunning"];
                return result;
            }
        }
        catch
        {
            // 未运行，继续尝试启动
        }

        // 2. 尝试启动服务
        result.AttemptedStart = true;
        try
        {
            var oneapiScript = "/opt/intel/oneapi/setvars.sh";
            var hasOneapi = File.Exists(oneapiScript);
            var args = $"-m \"{llamaCpp.ModelPath}\" -ngl {llamaCpp.NGpuLayers} --port {llamaCpp.Port} --host 127.0.0.1 -c {llamaCpp.ContextSize}";

            // 组合预定义参数
            if (llamaCpp.UseFlashAttn) args += " --flash-attn";
            if (llamaCpp.UseMlock) args += " --mlock";
            if (llamaCpp.UseNoMmap) args += " --no-mmap";
            if (llamaCpp.Threads > 0) args += $" -t {llamaCpp.Threads}";
            if (llamaCpp.BatchSize > 0) args += $" -b {llamaCpp.BatchSize}";
            if (!string.IsNullOrWhiteSpace(llamaCpp.CacheTypeK)) args += $" --cache-type-k {llamaCpp.CacheTypeK}";
            if (!string.IsNullOrWhiteSpace(llamaCpp.CacheTypeV)) args += $" --cache-type-v {llamaCpp.CacheTypeV}";
            if (llamaCpp.UseContBatching) args += " --cont-batching";

            if (!string.IsNullOrWhiteSpace(llamaCpp.ExtraArgs))
                args += " " + llamaCpp.ExtraArgs.Trim();
            var shellCmd = hasOneapi
                ? $"source {oneapiScript} > /dev/null 2>&1 && {llamaCpp.BinaryPath} {args}"
                : $"{llamaCpp.BinaryPath} {args}";

            logger.LogInformation("正在启动 llama.cpp: {Cmd}", shellCmd);
            var startInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{shellCmd}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var process = Process.Start(startInfo);
            if (process == null)
            {
                result.Message = _loc["LocalAi_StartLlamaCppProcessFailed"];
                return result;
            }

            // llama.cpp 加载模型需要较长时间
            await Task.Delay(TimeSpan.FromSeconds(5));

            // 轮询检测服务是否就绪（最多 30 秒）
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                try
                {
                    using var httpClient = httpClientFactory.CreateClient();
                    httpClient.Timeout = TimeSpan.FromSeconds(3);
                    var response = await httpClient.GetAsync(checkUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        result.IsRunning = true;
                        result.StartSuccess = true;
                        result.Message = _loc["LocalAi_LlamaCppStartSuccessGpu"];
                        return result;
                    }
                }
                catch (Exception ex) { logger.LogDebug(ex, "探测 llama.cpp 启动状态失败"); }
            }

            result.Message = _loc["LocalAi_LlamaCppStartTimeout"];
            logger.LogWarning("llama.cpp 启动后未就绪");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动 llama.cpp 失败");
            result.Message = string.Format(_loc["LocalAi_StartLlamaCppFailedDetail"], ex.Message);
        }

        return result;
    }

    private async Task<LocalAiServiceStatusDto> DetectAndStartOpenVinoAsync()
    {
        var result = new LocalAiServiceStatusDto { Provider = "openvino" };
        var config = await GetLocalAiConfigAsync();
        var openvino = config.OpenVino;

        if (openvino == null || !openvino.Enabled)
        {
            result.Message = "OpenVINO 未启用";
            return result;
        }

        // ── 远程模式（K8s 独立 OpenVINO 容器）──
        // 当 OPENVINO_LLM_URL 环境变量指向非 localhost 时，跳过本地进程启动
        var remoteLlmUrl = Environment.GetEnvironmentVariable("OPENVINO_LLM_URL");
        if (!string.IsNullOrWhiteSpace(remoteLlmUrl) &&
            !remoteLlmUrl.Contains("localhost") &&
            !remoteLlmUrl.Contains("127.0.0.1"))
        {
            openvino.BaseUrl = remoteLlmUrl.TrimEnd('/');
            logger.LogInformation("OpenVINO 远程模式: {Url}", openvino.BaseUrl);

            result.Devices = await ProbeOpenVinoDevicesAsync();
            result.CommandLine = $"(remote) {openvino.BaseUrl}";

            try
            {
                using var httpClient = httpClientFactory.CreateClient();
                httpClient.Timeout = TimeSpan.FromSeconds(5);
                var response = await httpClient.GetAsync($"{openvino.BaseUrl}/v1/models");
                if (response.IsSuccessStatusCode)
                {
                    result.IsRunning = true;
                    result.Message = "OpenVINO 远程服务正在运行";
                }
                else
                {
                    result.Message = $"OpenVINO 远程服务返回 {response.StatusCode}";
                }
            }
            catch (Exception ex)
            {
                result.Message = $"OpenVINO 远程服务不可达: {openvino.BaseUrl} ({ex.Message})";
                logger.LogWarning(ex, "OpenVINO 远程服务不可达");
            }
            return result;
        }

        // ── 本地模式（Docker Compose / 开发环境）──
        if (string.IsNullOrWhiteSpace(openvino.ModelPath) || !Directory.Exists(openvino.ModelPath))
        {
            result.Message = $"模型目录不存在: {openvino.ModelPath}";
            return result;
        }

        // 探测可用推理设备（CPU/GPU/NPU），供前端回填
        result.Devices = await ProbeOpenVinoDevicesAsync();

        var checkUrl = $"{openvino.BaseUrl.TrimEnd('/')}/v1/models";

        // 1. 检测服务是否已运行
        try
        {
            using var httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(5);
            var response = await httpClient.GetAsync(checkUrl);
            if (response.IsSuccessStatusCode)
            {
                result.IsRunning = true;
                result.Message = "OpenVINO 服务正在运行";
                return result;
            }
        }
        catch
        {
            // 未运行，继续尝试启动
        }

        // 2. 解析启动命令（优先用户配置；否则自动探测 python + openvino_genai + 随发布拷贝的脚本）
        var (commandLine, displayCmd) = await BuildOpenVinoStartCommandAsync(openvino);
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            result.Message = displayCmd;
            return result;
        }
        result.CommandLine = displayCmd;
        result.AttemptedStart = true;

        try
        {
            logger.LogInformation("正在启动 OpenVINO: {Cmd}", displayCmd);

            var isWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            ProcessStartInfo startInfo;
            if (isWindows)
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = "cmd",
                    Arguments = $"/c {commandLine}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
            }
            else
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"-c \"{commandLine}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
            }

            var process = Process.Start(startInfo);
            if (process == null)
            {
                result.Message = "无法创建 OpenVINO 服务进程";
                return result;
            }

            // OpenVINO 冷加载模型需要 10-30 秒，先等 8 秒再轮询
            await Task.Delay(TimeSpan.FromSeconds(8));

            // 轮询检测服务是否就绪（最多 60 秒）
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                try
                {
                    using var httpClient = httpClientFactory.CreateClient();
                    httpClient.Timeout = TimeSpan.FromSeconds(3);
                    var response = await httpClient.GetAsync(checkUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        result.IsRunning = true;
                        result.StartSuccess = true;
                        result.Message = "OpenVINO 服务启动成功";
                        return result;
                    }
                }
                catch (Exception ex) { logger.LogDebug(ex, "探测 OpenVINO 启动状态失败"); }
            }

            result.Message = "OpenVINO 启动超时（模型冷加载可能需要更长时间，请查看启动命令手动验证）";
            logger.LogWarning("OpenVINO 启动后未就绪: {Cmd}", displayCmd);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动 OpenVINO 失败");
            result.Message = $"启动 OpenVINO 失败: {ex.Message}";
        }

        return result;
    }

    /// <summary>
    /// 构造 OpenVINO 启动命令。返回 (可直接执行的命令行, 用于展示的脱敏版本)。
    /// 优先级：用户配置的 BinaryPath（.py 脚本 / 可执行文件）→ 自动探测 python+openvino_genai+随发布脚本。
    /// </summary>
    private async Task<(string? CommandLine, string Display)> BuildOpenVinoStartCommandAsync(OpenClawOpenVinoConfigDto openvino)
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "LocalVision", "openvino_llm_server.py");
        var baseArgs = $"--model \"{openvino.ModelPath}\" --device {openvino.Device} --port {openvino.Port} --max-context-size {openvino.ContextSize}";
        if (!string.IsNullOrWhiteSpace(openvino.ExtraArgs))
            baseArgs += " " + openvino.ExtraArgs.Trim();

        var userBinary = openvino.BinaryPath?.Trim();
        if (!string.IsNullOrWhiteSpace(userBinary))
        {
            // 用户显式配置：.py 脚本用 python 跑，其余按可执行文件/命令处理
            if (userBinary.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(userBinary))
                    return (null, $"服务脚本不存在: {userBinary}");
                var python = await FindPythonWithOpenVinoAsync();
                if (python == null)
                    return (null, "未找到可用的 Python 环境（需要安装 openvino-genai，见文档）");
                return ($"{python} \"{userBinary}\" {baseArgs}", $"{python} \"{userBinary}\" {baseArgs}");
            }
            return (userBinary.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                ? $"\"{userBinary}\" {baseArgs}"
                : $"{userBinary} {baseArgs}", $"{userBinary} {baseArgs}");
        }

        // 自动探测：随发布拷贝的 openvino_llm_server.py + python + openvino_genai
        if (!File.Exists(scriptPath))
        {
            // 开发环境兜底：从源码目录找（Baihua.AI.Provider 项目）
            var devPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "services", "Baihua.AI.Provider", "LocalVision", "openvino_llm_server.py");
            if (File.Exists(devPath)) scriptPath = Path.GetFullPath(devPath);
        }
        if (!File.Exists(scriptPath))
            return (null, "未找到 openvino_llm_server.py（随发布拷贝缺失），请手动填写服务脚本路径");

        var py = await FindPythonWithOpenVinoAsync();
        if (py == null)
            return (null, "未找到可用的 Python 环境（需要 pip install openvino-genai，见文档 §安装）");

        return ($"{py} \"{scriptPath}\" {baseArgs}", $"{py} \"{scriptPath}\" {baseArgs}");
    }

    /// <summary>探测 python 可执行文件，并确认能 import openvino_genai</summary>
    private async Task<string?> FindPythonWithOpenVinoAsync()
    {
        var candidates = new[] { "python", "py -3", "python3" };
        foreach (var candidate in candidates)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = candidate.Split(' ')[0],
                    Arguments = candidate.Contains(' ') ? candidate.Split(' ', 2)[1] + " -c \"import openvino_genai\"" : "-c \"import openvino_genai\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(startInfo);
                if (process == null) continue;
                var stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                if (process.ExitCode == 0)
                    return candidate.Split(' ')[0];
                logger.LogDebug("Python 候选 {Candidate} 无 openvino_genai: {Err}", candidate, stderr.Trim());
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "探测 Python {Candidate} 失败", candidate);
            }
        }
        return null;
    }

    /// <summary>探测可用推理设备（CPU/GPU/NPU）。
    /// 远程模式：调用 OpenVINO 服务的 /health 端点获取当前设备。
    /// 本地模式：通过 openvino.Core().available_devices 获取全部设备列表。
    /// </summary>
    private async Task<List<string>> ProbeOpenVinoDevicesAsync()
    {
        // 远程模式：查询 OpenVINO 服务的 /health 端点
        var remoteLlmUrl = Environment.GetEnvironmentVariable("OPENVINO_LLM_URL");
        if (!string.IsNullOrWhiteSpace(remoteLlmUrl) &&
            !remoteLlmUrl.Contains("localhost") &&
            !remoteLlmUrl.Contains("127.0.0.1"))
        {
            try
            {
                using var client = httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                var response = await client.GetAsync($"{remoteLlmUrl.TrimEnd('/')}/health");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("device", out var deviceProp))
                    {
                        var device = deviceProp.GetString() ?? "";
                        return string.IsNullOrEmpty(device)
                            ? new List<string>()
                            : new List<string> { device };
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "探测远程 OpenVINO 设备失败");
            }
            return new List<string>();
        }

        // 本地模式：通过 python + openvino 探测设备
        // 修复：使用 FindPythonWithOpenVinoAsync() 而非硬编码 "python"
        var python = await FindPythonWithOpenVinoAsync();
        if (python == null)
        {
            logger.LogDebug("未找到带 openvino 的 Python，跳过设备探测");
            return new List<string>();
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = python,
                Arguments = "-c \"import openvino as ov; print(','.join(ov.Core().available_devices))\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo);
            if (process == null) return new List<string>();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) return new List<string>();
            return stdout.Trim().Split(',')
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "探测 OpenVINO 设备失败");
            return new List<string>();
        }
    }

    #endregion

}
