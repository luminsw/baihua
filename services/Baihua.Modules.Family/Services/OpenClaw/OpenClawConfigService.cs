using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baihua.Contracts.OpenClaw;
using Baihua.AI.Provider;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// OpenClaw 配置服务：读写 openclaw.json 和 llamacpp-config.json
/// </summary>
public class OpenClawConfigService(ILogger<OpenClawConfigService> logger)
{
    private static string GetOpenClawConfigPath()
    {
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetEnvironmentVariable("USERPROFILE")
            ?? AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(home, ".openclaw", "openclaw.json");
    }

    private static string GetLlamaCppConfigPath()
    {
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetEnvironmentVariable("USERPROFILE")
            ?? AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(home, ".openclaw", "llamacpp-config.json");
    }

    private static string GetOpenVinoConfigPath()
    {
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetEnvironmentVariable("USERPROFILE")
            ?? AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(home, ".openclaw", "openvino-config.json");
    }

    public async Task<OpenClawLocalAiConfigDto> GetLocalAiConfigAsync()
    {
        var path = GetOpenClawConfigPath();
        var result = new OpenClawLocalAiConfigDto();

        if (File.Exists(path))
        {
            var json = await File.ReadAllTextAsync(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("models", out var models) &&
                models.TryGetProperty("providers", out var providers))
            {
                if (providers.TryGetProperty("ollama", out var ollama))
                    result.Ollama = ParseProviderConfig(ollama);
                if (providers.TryGetProperty("lmstudio", out var lmstudio))
                    result.LmStudio = ParseProviderConfig(lmstudio);
                if (providers.TryGetProperty("llamacpp", out var llamacpp))
                    result.LlamaCpp = ParseLlamaCppConfig(llamacpp);
                if (providers.TryGetProperty("openvino", out var openvino))
                    result.OpenVino = ParseOpenVinoConfig(openvino);
            }
        }

        var llamaCppPath = GetLlamaCppConfigPath();
        if (File.Exists(llamaCppPath))
        {
            var json = await File.ReadAllTextAsync(llamaCppPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var cfg = result.LlamaCpp ?? new OpenClawLlamaCppConfigDto();
            if (root.TryGetProperty("enabled", out var enabled))
                cfg.Enabled = enabled.GetBoolean();
            if (root.TryGetProperty("binaryPath", out var binaryPath))
                cfg.BinaryPath = binaryPath.GetString() ?? "";
            if (root.TryGetProperty("modelPath", out var modelPath))
                cfg.ModelPath = modelPath.GetString() ?? "";
            if (root.TryGetProperty("baseUrl", out var baseUrl))
                cfg.BaseUrl = baseUrl.GetString() ?? "http://localhost:8080";
            if (root.TryGetProperty("port", out var port) && port.ValueKind == JsonValueKind.Number)
                cfg.Port = port.GetInt32();
            if (root.TryGetProperty("nGpuLayers", out var ngl) && ngl.ValueKind == JsonValueKind.Number)
                cfg.NGpuLayers = ngl.GetInt32();
            if (root.TryGetProperty("contextSize", out var ctx) && ctx.ValueKind == JsonValueKind.Number)
                cfg.ContextSize = ctx.GetInt32();
            if (root.TryGetProperty("extraArgs", out var extraArgs))
                cfg.ExtraArgs = extraArgs.GetString() ?? "";
            if (root.TryGetProperty("threads", out var threads) && threads.ValueKind == JsonValueKind.Number)
                cfg.Threads = threads.GetInt32();
            if (root.TryGetProperty("batchSize", out var batchSize) && batchSize.ValueKind == JsonValueKind.Number)
                cfg.BatchSize = batchSize.GetInt32();
            if (root.TryGetProperty("cacheTypeK", out var cacheTypeK))
                cfg.CacheTypeK = cacheTypeK.GetString() ?? "";
            if (root.TryGetProperty("cacheTypeV", out var cacheTypeV))
                cfg.CacheTypeV = cacheTypeV.GetString() ?? "";
            if (root.TryGetProperty("useContBatching", out var useContBatching) && useContBatching.ValueKind == JsonValueKind.True)
                cfg.UseContBatching = useContBatching.GetBoolean();
            result.LlamaCpp = cfg;
        }

        var openVinoPath = GetOpenVinoConfigPath();
        if (File.Exists(openVinoPath))
        {
            var json = await File.ReadAllTextAsync(openVinoPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var cfg = result.OpenVino ?? new OpenClawOpenVinoConfigDto();
            if (root.TryGetProperty("enabled", out var enabled))
                cfg.Enabled = enabled.GetBoolean();
            if (root.TryGetProperty("binaryPath", out var binaryPath))
                cfg.BinaryPath = binaryPath.GetString() ?? "";
            if (root.TryGetProperty("modelPath", out var modelPath))
                cfg.ModelPath = modelPath.GetString() ?? "";
            if (root.TryGetProperty("baseUrl", out var baseUrl))
                cfg.BaseUrl = baseUrl.GetString() ?? "http://localhost:8000";
            if (root.TryGetProperty("port", out var port) && port.ValueKind == JsonValueKind.Number)
                cfg.Port = port.GetInt32();
            if (root.TryGetProperty("device", out var device))
                cfg.Device = device.GetString() ?? "CPU";
            if (root.TryGetProperty("contextSize", out var ctx) && ctx.ValueKind == JsonValueKind.Number)
                cfg.ContextSize = ctx.GetInt32();
            if (root.TryGetProperty("extraArgs", out var extraArgs))
                cfg.ExtraArgs = extraArgs.GetString() ?? "";
            result.OpenVino = cfg;
        }

        return result;
    }

    public async Task<bool> SaveLocalAiConfigAsync(SaveOpenClawLocalAiConfigRequest request)
    {
        if (request.Ollama != null)
        {
            if (request.Ollama.Enabled && !string.IsNullOrWhiteSpace(request.Ollama.BaseUrl))
            {
                var providerJson = BuildProviderJson(request.Ollama).ToJsonString(JsonHelper.Compact);
                if (!await RunOpenClawConfigSetAsync("models.providers.ollama", providerJson))
                    return false;
            }
            else
            {
                await RunOpenClawConfigUnsetAsync("models.providers.ollama");
            }
        }

        if (request.LmStudio != null)
        {
            if (request.LmStudio.Enabled && !string.IsNullOrWhiteSpace(request.LmStudio.BaseUrl))
            {
                var providerJson = BuildProviderJson(request.LmStudio).ToJsonString(JsonHelper.Compact);
                if (!await RunOpenClawConfigSetAsync("models.providers.lmstudio", providerJson))
                    return false;
            }
            else
            {
                await RunOpenClawConfigUnsetAsync("models.providers.lmstudio");
            }
        }

        if (request.LlamaCpp != null)
        {
            var llamaCppPath = GetLlamaCppConfigPath();
            var llamaCppConfig = new JsonObject
            {
                ["enabled"] = request.LlamaCpp.Enabled,
                ["binaryPath"] = request.LlamaCpp.BinaryPath,
                ["modelPath"] = request.LlamaCpp.ModelPath,
                ["baseUrl"] = request.LlamaCpp.BaseUrl,
                ["port"] = request.LlamaCpp.Port,
                ["nGpuLayers"] = request.LlamaCpp.NGpuLayers,
                ["contextSize"] = request.LlamaCpp.ContextSize,
                ["apiType"] = request.LlamaCpp.ApiType,
                ["extraArgs"] = request.LlamaCpp.ExtraArgs,
                ["threads"] = request.LlamaCpp.Threads,
                ["batchSize"] = request.LlamaCpp.BatchSize,
                ["cacheTypeK"] = request.LlamaCpp.CacheTypeK,
                ["cacheTypeV"] = request.LlamaCpp.CacheTypeV,
                ["useContBatching"] = request.LlamaCpp.UseContBatching,
            };
            await File.WriteAllTextAsync(llamaCppPath, llamaCppConfig.ToJsonString(JsonHelper.Indented));

            if (request.LlamaCpp.Enabled && !string.IsNullOrWhiteSpace(request.LlamaCpp.ModelPath))
            {
                var providerJson = BuildLlamaCppProviderJson(request.LlamaCpp).ToJsonString(JsonHelper.Compact);
                // CLI 仅作为同步到 OpenClaw 的"尽力而为"；因为有独立 llamacpp-config.json 作为事实来源，
                // 就算当前 Node 版本不对导致 CLI 失败，只要文件落盘就认为保存成功。
                await RunOpenClawConfigSetAsync("models.providers.llamacpp", providerJson);
            }
            else
            {
                // 禁用也不把失败当致命问题
                await RunOpenClawConfigUnsetAsync("models.providers.llamacpp");
            }
        }

        if (request.OpenVino != null)
        {
            var openVinoPath = GetOpenVinoConfigPath();
            var openVinoConfig = new JsonObject
            {
                ["enabled"] = request.OpenVino.Enabled,
                ["binaryPath"] = request.OpenVino.BinaryPath,
                ["modelPath"] = request.OpenVino.ModelPath,
                ["baseUrl"] = request.OpenVino.BaseUrl,
                ["port"] = request.OpenVino.Port,
                ["device"] = request.OpenVino.Device,
                ["contextSize"] = request.OpenVino.ContextSize,
                ["apiType"] = request.OpenVino.ApiType,
                ["extraArgs"] = request.OpenVino.ExtraArgs,
            };
            await File.WriteAllTextAsync(openVinoPath, openVinoConfig.ToJsonString(JsonHelper.Indented));

            if (request.OpenVino.Enabled && !string.IsNullOrWhiteSpace(request.OpenVino.ModelPath))
            {
                var providerJson = BuildOpenVinoProviderJson(request.OpenVino).ToJsonString(JsonHelper.Compact);
                // 同理：独立 openvino-config.json 已落盘即算保存成功；CLI 只是尽力同步。
                await RunOpenClawConfigSetAsync("models.providers.openvino", providerJson);
            }
            else
            {
                await RunOpenClawConfigUnsetAsync("models.providers.openvino");
            }
        }

        return true;
    }

    public async Task<bool> RunOpenClawConfigSetAsync(string path, string jsonValue)
    {
        try
        {
            // 两个坑（均已实测验证）：
            // 1. openclaw 是 npm 安装的 .cmd shim，.NET Process.Start(UseShellExecute=false)
            //    无法直接启动 batch 文件（CreateProcess 不解析 .cmd/.bat），必须用 cmd.exe /c 包装。
            // 2. 内联 JSON 经 cmd 传递时引号转义不可靠（" → \" 或 ^" 都会丢失/破坏 JSON），
            //    因此 Windows 上用 --batch-file 临时文件传递，完全避开引号转义问题。
            // 之前文档记录的“Node 版本不足导致 CLI 失败”实际根因是启动方式错误。
            var isWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            if (isWindows)
            {
                var tempFile = Path.Combine(Path.GetTempPath(), $"openclaw-set-{Guid.NewGuid():N}.json");
                try
                {
                    var batch = new JsonArray
                    {
                        new JsonObject
                        {
                            ["path"] = path,
                            ["value"] = JsonNode.Parse(jsonValue) ?? new JsonObject(),
                        }
                    }.ToJsonString(JsonHelper.Compact);
                    await File.WriteAllTextAsync(tempFile, batch);

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c openclaw config set --batch-file \"{tempFile}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var process = Process.Start(startInfo);
                    if (process == null) return false;
                    var stderr = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    if (process.ExitCode != 0)
                    {
                        logger.LogWarning("openclaw config set 失败 ({Path}): {Stderr}", path, stderr);
                        return false;
                    }
                    logger.LogInformation("openclaw config set 成功: {Path}", path);
                    return true;
                }
                finally
                {
                    try { File.Delete(tempFile); } catch { /* 清理失败忽略 */ }
                }
            }

            // 非 Windows（Linux/macOS）：openclaw 是可执行脚本，直接启动
            var startInfo2 = new ProcessStartInfo
            {
                FileName = "openclaw",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo2.ArgumentList.Add("config");
            startInfo2.ArgumentList.Add("set");
            startInfo2.ArgumentList.Add(path);
            startInfo2.ArgumentList.Add(jsonValue);
            startInfo2.ArgumentList.Add("--strict-json");
            startInfo2.ArgumentList.Add("--merge");
            using var process2 = Process.Start(startInfo2);
            if (process2 == null) return false;
            var stderr2 = await process2.StandardError.ReadToEndAsync();
            await process2.WaitForExitAsync();
            if (process2.ExitCode != 0)
            {
                logger.LogWarning("openclaw config set 失败 ({Path}): {Stderr}", path, stderr2);
                return false;
            }
            logger.LogInformation("openclaw config set 成功: {Path}", path);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "openclaw config set 异常 ({Path})", path);
            return false;
        }
    }

    public async Task<bool> RunOpenClawConfigUnsetAsync(string path)
    {
        try
        {
            var isWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            ProcessStartInfo startInfo;
            if (isWindows)
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c openclaw config unset {path}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
            }
            else
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = "openclaw",
                    Arguments = $"config unset {path}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
            }
            using var process = Process.Start(startInfo);
            if (process == null) return false;
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                logger.LogWarning("openclaw config unset 失败 ({Path}): {Stderr}", path, stderr);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "openclaw config unset 异常 ({Path})", path);
            return false;
        }
    }

    private static OpenClawLlamaCppConfigDto ParseLlamaCppConfig(JsonElement element)
    {
        var config = new OpenClawLlamaCppConfigDto();
        if (element.TryGetProperty("baseUrl", out var baseUrl))
            config.BaseUrl = baseUrl.GetString() ?? "";
        if (element.TryGetProperty("modelPath", out var modelPath))
            config.ModelPath = modelPath.GetString() ?? "";
        if (element.TryGetProperty("binaryPath", out var binaryPath))
            config.BinaryPath = binaryPath.GetString() ?? "";
        if (element.TryGetProperty("enabled", out var enabled))
            config.Enabled = enabled.GetBoolean();
        if (element.TryGetProperty("nGpuLayers", out var ngl) && ngl.ValueKind == JsonValueKind.Number)
            config.NGpuLayers = ngl.GetInt32();
        if (element.TryGetProperty("contextSize", out var ctx) && ctx.ValueKind == JsonValueKind.Number)
            config.ContextSize = ctx.GetInt32();
        if (element.TryGetProperty("port", out var port) && port.ValueKind == JsonValueKind.Number)
            config.Port = port.GetInt32();
        if (element.TryGetProperty("apiType", out var apiType))
            config.ApiType = apiType.GetString() ?? "";
        if (element.TryGetProperty("extraArgs", out var extraArgs))
            config.ExtraArgs = extraArgs.GetString() ?? "";
        if (element.TryGetProperty("threads", out var threads) && threads.ValueKind == JsonValueKind.Number)
            config.Threads = threads.GetInt32();
        if (element.TryGetProperty("batchSize", out var batchSize) && batchSize.ValueKind == JsonValueKind.Number)
            config.BatchSize = batchSize.GetInt32();
        if (element.TryGetProperty("cacheTypeK", out var cacheTypeK))
            config.CacheTypeK = cacheTypeK.GetString() ?? "";
        if (element.TryGetProperty("cacheTypeV", out var cacheTypeV))
            config.CacheTypeV = cacheTypeV.GetString() ?? "";
        if (element.TryGetProperty("useContBatching", out var useContBatching))
            config.UseContBatching = useContBatching.GetBoolean();
        return config;
    }

    private static OpenClawLocalProviderConfigDto ParseProviderConfig(JsonElement element)
    {
        var config = new OpenClawLocalProviderConfigDto();
        if (element.TryGetProperty("baseUrl", out var baseUrl))
            config.BaseUrl = baseUrl.GetString() ?? "";
        if (element.TryGetProperty("enabled", out var enabled))
            config.Enabled = enabled.GetBoolean();
        if (element.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in models.EnumerateArray())
            {
                var model = new OpenClawLocalModelDto();
                if (m.TryGetProperty("id", out var id))
                    model.Id = id.GetString() ?? "";
                if (m.TryGetProperty("name", out var name))
                    model.Name = name.GetString() ?? "";

                config.Models.Add(model);
            }
        }
        return config;
    }

    public static JsonObject BuildLlamaCppProviderJson(OpenClawLlamaCppConfigDto config)
    {
        // OpenClaw 的 models.providers.<id> schema 只接受标准键：baseUrl/api/models[]，
        // 自定义字段（modelPath/ngpuLayers 等）留在 llamacpp-config.json 独立文件，
        // 否则 openclaw config set --strict-json 会校验失败。
        return new JsonObject
        {
            ["baseUrl"] = config.BaseUrl,
            ["api"] = string.IsNullOrWhiteSpace(config.ApiType) ? "openai-completions" : config.ApiType,
            ["models"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = Path.GetFileNameWithoutExtension(config.ModelPath).Replace(".", "-").ToLowerInvariant(),
                    ["name"] = Path.GetFileNameWithoutExtension(config.ModelPath),
                    ["input"] = new JsonArray("text"),
                    ["contextWindow"] = config.ContextSize,
                }
            },
        };
    }

    private static OpenClawOpenVinoConfigDto ParseOpenVinoConfig(JsonElement element)
    {
        var config = new OpenClawOpenVinoConfigDto();
        if (element.TryGetProperty("baseUrl", out var baseUrl))
            config.BaseUrl = baseUrl.GetString() ?? "";
        if (element.TryGetProperty("modelPath", out var modelPath))
            config.ModelPath = modelPath.GetString() ?? "";
        if (element.TryGetProperty("binaryPath", out var binaryPath))
            config.BinaryPath = binaryPath.GetString() ?? "";
        if (element.TryGetProperty("enabled", out var enabled))
            config.Enabled = enabled.GetBoolean();
        if (element.TryGetProperty("port", out var port) && port.ValueKind == JsonValueKind.Number)
            config.Port = port.GetInt32();
        if (element.TryGetProperty("device", out var device))
            config.Device = device.GetString() ?? "CPU";
        if (element.TryGetProperty("contextSize", out var ctx) && ctx.ValueKind == JsonValueKind.Number)
            config.ContextSize = ctx.GetInt32();
        if (element.TryGetProperty("apiType", out var apiType))
            config.ApiType = apiType.GetString() ?? "";
        if (element.TryGetProperty("extraArgs", out var extraArgs))
            config.ExtraArgs = extraArgs.GetString() ?? "";
        return config;
    }

    public static JsonObject BuildOpenVinoProviderJson(OpenClawOpenVinoConfigDto config)
    {
        // 同上：只写 OpenClaw schema 认可的标准键；OpenVINO 自定义字段
        // （modelPath/device/port/extraArgs 等）留在 openvino-config.json 独立文件。
        // 模型列表尽量从已扫描结果带过来，否则至少带上配置目录名推导的模型。
        var modelId = Path.GetFileName(config.ModelPath.TrimEnd('/', '\\')).Replace(".", "-").ToLowerInvariant();
        return new JsonObject
        {
            ["baseUrl"] = config.BaseUrl,
            ["api"] = string.IsNullOrWhiteSpace(config.ApiType) ? "openai-completions" : config.ApiType,
            ["models"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = modelId,
                    ["name"] = Path.GetFileName(config.ModelPath.TrimEnd('/', '\\')),
                    ["input"] = new JsonArray("text", "image"),
                    ["contextWindow"] = config.ContextSize,
                }
            },
        };
    }

    public static JsonObject BuildProviderJson(OpenClawLocalProviderConfigDto config)
    {
        var models = new JsonArray();
        foreach (var m in config.Models)
        {
            models.Add(new JsonObject
            {
                ["id"] = m.Id,
                ["name"] = m.Name,

            });
        }
        return new JsonObject
        {
            ["baseUrl"] = config.BaseUrl,
            ["enabled"] = config.Enabled,
            ["models"] = models,
        };
    }
}
