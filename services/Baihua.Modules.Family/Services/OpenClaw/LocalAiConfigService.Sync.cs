using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baihua.Contracts.OpenClaw;
using Baihua.AI.Provider;

namespace Baihua.Modules.Family.Services;

public partial class LocalAiConfigService
{
    #region Sync Local Models to OpenClaw

    public async Task<bool> SyncLocalModelsToOpenClawAsync(string provider)
    {
        var config = await GetLocalAiConfigAsync();

        return provider.ToLowerInvariant() switch
        {
            "ollama" => await SyncOllamaModelsToOpenClawAsync(config.Ollama),
            "lmstudio" => await SyncLmStudioModelsToOpenClawAsync(config.LmStudio),
            "llamacpp" => await SyncLlamaCppModelsToOpenClawAsync(config.LlamaCpp),
            "openvino" => await SyncOpenVinoModelsToOpenClawAsync(config.OpenVino),
            _ => false,
        };
    }

    private async Task<bool> SyncOllamaModelsToOpenClawAsync(OpenClawLocalProviderConfigDto? config)
    {
        if (config == null || !config.Enabled || string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            logger.LogWarning("Ollama 未配置或未启用，无法同步模型");
            return false;
        }

        try
        {
            using var httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            var response = await httpClient.GetAsync($"{config.BaseUrl.TrimEnd('/')}/api/tags");
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Ollama /api/tags 请求失败: {StatusCode}", response.StatusCode);
                return false;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var modelsArray = new JsonArray();
            if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in models.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(name)) continue;

                    modelsArray.Add(new JsonObject
                    {
                        ["id"] = name,
                        ["name"] = name,
                        ["api"] = config.ApiType,
                        ["input"] = new JsonArray("text"),
                        ["contextWindow"] = 128000,
                        ["maxTokens"] = 4096,
                    });
                }
            }

            var providerJson = new JsonObject
            {
                ["baseUrl"] = config.BaseUrl.TrimEnd('/'),
                ["api"] = config.ApiType,
                ["models"] = modelsArray,
            }.ToJsonString(JsonHelper.Compact);

            return await openClawConfigService.RunOpenClawConfigSetAsync("models.providers.ollama", providerJson);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "同步 Ollama 模型到 OpenClaw 失败");
            return false;
        }
    }

    private async Task<bool> SyncLmStudioModelsToOpenClawAsync(OpenClawLocalProviderConfigDto? config)
    {
        if (config == null || !config.Enabled || string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            logger.LogWarning("LM Studio 未配置或未启用，无法同步模型");
            return false;
        }

        try
        {
            using var httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            var baseUrl = config.BaseUrl.TrimEnd('/');
            if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                baseUrl = baseUrl[..^3];
            }
            var response = await httpClient.GetAsync($"{baseUrl}/v1/models");
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("LM Studio /v1/models 请求失败: {StatusCode}", response.StatusCode);
                return false;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var modelsArray = new JsonArray();
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(id)) continue;

                    modelsArray.Add(new JsonObject
                    {
                        ["id"] = id,
                        ["name"] = id,
                        ["api"] = config.ApiType,
                        ["input"] = new JsonArray("text"),
                        ["contextWindow"] = 128000,
                        ["maxTokens"] = 4096,
                    });
                }
            }

            var providerJson = new JsonObject
            {
                ["baseUrl"] = config.BaseUrl.TrimEnd('/'),
                ["api"] = config.ApiType,
                ["models"] = modelsArray,
            }.ToJsonString(JsonHelper.Compact);

            return await openClawConfigService.RunOpenClawConfigSetAsync("models.providers.lmstudio", providerJson);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "同步 LM Studio 模型到 OpenClaw 失败");
            return false;
        }
    }

    private async Task<bool> SyncLlamaCppModelsToOpenClawAsync(OpenClawLlamaCppConfigDto? config)
    {
        if (config == null || !config.Enabled || string.IsNullOrWhiteSpace(config.ModelPath))
        {
            logger.LogWarning("llama.cpp 未配置或未启用，无法同步模型");
            return false;
        }

        var providerJson = OpenClawConfigService.BuildLlamaCppProviderJson(config).ToJsonString(JsonHelper.Compact);
        return await openClawConfigService.RunOpenClawConfigSetAsync("models.providers.llamacpp", providerJson);
    }

    private async Task<bool> SyncOpenVinoModelsToOpenClawAsync(OpenClawOpenVinoConfigDto? config)
    {
        if (config == null || !config.Enabled || string.IsNullOrWhiteSpace(config.ModelPath))
        {
            logger.LogWarning("OpenVINO 未配置或未启用，无法同步模型");
            return false;
        }

        var providerJson = OpenClawConfigService.BuildOpenVinoProviderJson(config).ToJsonString(JsonHelper.Compact);
        return await openClawConfigService.RunOpenClawConfigSetAsync("models.providers.openvino", providerJson);
    }

    #endregion
}
