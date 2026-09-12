using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;
using Baihua.Contracts.OpenClaw;
using Baihua.AI.Provider;

namespace Baihua.Modules.Family.Services;

public partial class LocalAiConfigService
{
    #region Scan Local Models

    public async Task<List<OpenClawLocalModelDto>> ScanLocalModelsAsync(string provider)
    {
        var config = await GetLocalAiConfigAsync();

        if (provider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            return await ScanOllamaModelsAsync(config.Ollama);
        }
        if (provider.Equals("lmstudio", StringComparison.OrdinalIgnoreCase))
        {
            return await ScanLmStudioModelsAsync(config.LmStudio);
        }
        if (provider.Equals("llamacpp", StringComparison.OrdinalIgnoreCase))
        {
            return await ScanLlamaCppModelsAsync(config.LlamaCpp);
        }
        if (provider.Equals("openvino", StringComparison.OrdinalIgnoreCase))
        {
            return await ScanOpenVinoModelsAsync(config.OpenVino);
        }

        return new List<OpenClawLocalModelDto>();
    }

    private async Task<List<OpenClawLocalModelDto>> ScanOllamaModelsAsync(OpenClawLocalProviderConfigDto? config)
    {
        if (config == null || !config.Enabled || string.IsNullOrWhiteSpace(config.BaseUrl))
            return new List<OpenClawLocalModelDto>();

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var response = await client.GetAsync($"{config.BaseUrl.TrimEnd('/')}/api/tags");
            if (!response.IsSuccessStatusCode) return new List<OpenClawLocalModelDto>();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var result = new List<OpenClawLocalModelDto>();
            if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in models.EnumerateArray())
                {
                    var id = item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(id)) continue;
                    result.Add(new OpenClawLocalModelDto
                    {
                        Id = id,
                        Name = id,
                        ApiType = config.ApiType,
                    });
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "扫描 Ollama 模型失败");
            return new List<OpenClawLocalModelDto>();
        }
    }

    private async Task<List<OpenClawLocalModelDto>> ScanLmStudioModelsAsync(OpenClawLocalProviderConfigDto? config)
    {
        if (config == null || !config.Enabled || string.IsNullOrWhiteSpace(config.BaseUrl))
            return new List<OpenClawLocalModelDto>();

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var response = await client.GetAsync($"{config.BaseUrl.TrimEnd('/')}/v1/models");
            if (!response.IsSuccessStatusCode) return new List<OpenClawLocalModelDto>();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var result = new List<OpenClawLocalModelDto>();
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(id)) continue;
                    result.Add(new OpenClawLocalModelDto
                    {
                        Id = id,
                        Name = id,
                        ApiType = config.ApiType,
                    });
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "扫描 LM Studio 模型失败");
            return new List<OpenClawLocalModelDto>();
        }
    }

    private async Task<List<OpenClawLocalModelDto>> ScanLlamaCppModelsAsync(OpenClawLlamaCppConfigDto? config)
    {
        if (config == null || !config.Enabled)
            return new List<OpenClawLocalModelDto>();

        // 先检测服务是否运行
        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var response = await client.GetAsync($"{config.BaseUrl.TrimEnd('/')}/v1/models");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var result = new List<OpenClawLocalModelDto>();
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(id)) continue;
                        result.Add(new OpenClawLocalModelDto
                        {
                            Id = id,
                            Name = id,
                            ApiType = config.ApiType,
                        });
                    }
                }
                return result;
            }
        }
        catch (Exception ex) { logger.LogDebug(ex, "探测 llama.cpp 运行模型失败"); }

        // 服务未运行，返回配置中的模型（前端会提示用户先启动）
        if (File.Exists(config.ModelPath))
        {
            var modelName = Path.GetFileNameWithoutExtension(config.ModelPath);
            var modelId = modelName.Replace(".", "-").ToLowerInvariant();
            return new List<OpenClawLocalModelDto>
            {
                new OpenClawLocalModelDto
                {
                    Id = modelId,
                    Name = string.Format(_loc["LocalModel_NeedsStart"], modelName),
                    ApiType = config.ApiType,
                    ContextWindow = config.ContextSize,
                }
            };
        }

        return new List<OpenClawLocalModelDto>();
    }

    private async Task<List<OpenClawLocalModelDto>> ScanOpenVinoModelsAsync(OpenClawOpenVinoConfigDto? config)
    {
        if (config == null || !config.Enabled)
            return new List<OpenClawLocalModelDto>();

        // 1. 服务运行中 → 从 /v1/models 拿实时列表（OpenAI 兼容 server）
        if (!string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            try
            {
                using var client = httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                var response = await client.GetAsync($"{config.BaseUrl.TrimEnd('/')}/v1/models");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var result = new List<OpenClawLocalModelDto>();
                    if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in data.EnumerateArray())
                        {
                            var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                            if (string.IsNullOrEmpty(id)) continue;
                            result.Add(new OpenClawLocalModelDto
                            {
                                Id = id,
                                Name = id,
                                ApiType = config.ApiType,
                                Input = new List<string> { "text", "image" },
                                ContextWindow = config.ContextSize,
                            });
                        }
                    }
                    return result;
                }
            }
            catch (Exception ex) { logger.LogDebug(ex, "探测 OpenVINO 运行模型失败"); }
        }

        // 2. 服务未运行 → 扫描模型目录（含子目录多模型识别）
        if (!string.IsNullOrWhiteSpace(config.ModelPath) && Directory.Exists(config.ModelPath))
            return ScanOpenVinoModelDirectory(config.ModelPath, config);

        return new List<OpenClawLocalModelDto>();
    }

    /// <summary>
    /// 扫描 OpenVINO 模型目录：目录本身 + 一级子目录中凡含 openvino_language_model.xml 的均为一个模型。
    /// 从 config.json / openvino_config.json 读取真实模型类型与精度，构建可读名称。
    /// </summary>
    private List<OpenClawLocalModelDto> ScanOpenVinoModelDirectory(string rootPath, OpenClawOpenVinoConfigDto config)
    {
        var candidates = new List<string>();
        if (IsOpenVinoModelDir(rootPath)) candidates.Add(rootPath);
        try
        {
            foreach (var sub in Directory.GetDirectories(rootPath))
                if (IsOpenVinoModelDir(sub)) candidates.Add(sub);
        }
        catch (Exception ex) { logger.LogDebug(ex, "扫描 OpenVINO 子目录失败: {Path}", rootPath); }

        var result = new List<OpenClawLocalModelDto>();
        foreach (var dir in candidates)
        {
            var modelName = new DirectoryInfo(dir).Name;
            var modelId = modelName.Replace(".", "-").ToLowerInvariant();
            var (displayName, isVl, precision) = ReadOpenVinoModelMeta(dir, modelName);
            // 注意：modelId 必须与 openvino_llm_server.py 的 model_id() 完全一致（目录名小写、点转横线），
            // 否则同步到 openclaw.json 后 OpenClaw 实际请求时找不到模型。
            result.Add(new OpenClawLocalModelDto
            {
                Id = modelId,
                Name = string.Format(_loc["LocalModel_NeedsStart"], displayName),
                ApiType = config.ApiType,
                Input = new List<string> { "text", "image" },
                ContextWindow = config.ContextSize,
            });
            _ = precision; // 精度信息暂不展示，保留供后续 UI 徽标使用
        }
        return result;
    }

    /// <summary>判断目录是否为 OpenVINO 模型目录（含语言模型主文件）</summary>
    private static bool IsOpenVinoModelDir(string path)
        => File.Exists(Path.Combine(path, "openvino_language_model.xml"))
           || File.Exists(Path.Combine(path, "openvino_language_model.bin"));

    /// <summary>从 config.json 读取模型元信息：(显示名, 是否 VL, 精度)</summary>
    private static (string DisplayName, bool IsVl, string Precision) ReadOpenVinoModelMeta(string dir, string fallbackName)
    {
        var isVl = File.Exists(Path.Combine(dir, "openvino_vision_embeddings_model.xml"));
        var precision = "";
        var displayName = fallbackName;
        try
        {
            var ovConfig = Path.Combine(dir, "openvino_config.json");
            if (File.Exists(ovConfig))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ovConfig));
                if (doc.RootElement.TryGetProperty("dtype", out var dt))
                    precision = dt.GetString() ?? "";
            }
        }
        catch (Exception) { /* 元信息读取失败不阻塞扫描 */ }
        try
        {
            var config = Path.Combine(dir, "config.json");
            if (File.Exists(config))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(config));
                var root = doc.RootElement;
                if (root.TryGetProperty("architectures", out var archs) && archs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in archs.EnumerateArray())
                    {
                        var arch = a.GetString() ?? "";
                        if (arch.Contains("VL", StringComparison.OrdinalIgnoreCase) || arch.Contains("Vision", StringComparison.OrdinalIgnoreCase))
                        {
                            isVl = true;
                            break;
                        }
                    }
                }
                if (root.TryGetProperty("model_type", out var mt))
                {
                    var modelType = mt.GetString() ?? "";
                    if (!string.IsNullOrEmpty(modelType))
                        displayName = $"{fallbackName} ({modelType}{(isVl ? ", VL" : "")}{(string.IsNullOrEmpty(precision) ? "" : $", {precision}")})";
                }
            }
        }
        catch (Exception) { /* 同上 */ }
        return (displayName, isVl, precision);
    }

    #endregion

}
