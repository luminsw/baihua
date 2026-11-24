using System.Net.Http.Headers;
using System.Text.Json;
using Baihua.Contracts.Ai;
using Baihua.Core.Models;
using Baihua.Core.Services;
using Microsoft.Extensions.Caching.Memory;

namespace Baihua.Modules.Ai.Services;

public sealed class AiProviderModelHealthService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly IAiConfigService _aiConfig;
    private readonly ILogger<AiProviderModelHealthService> _logger;

    public AiProviderModelHealthService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IAiConfigService aiConfig,
        ILogger<AiProviderModelHealthService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _aiConfig = aiConfig;
        _logger = logger;
    }

    public async Task<AiProviderDiagnosticsDto> DiagnoseAsync(
        AiProviderConfig provider,
        CancellationToken cancellationToken = default)
    {
        var configuredModels = provider.GetModelOptions()
            .Select(model => model.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (provider.Tier != AiModelTier.Tier2_Local)
        {
            return new AiProviderDiagnosticsDto
            {
                Id = provider.Id,
                Name = provider.Name,
                Tier = provider.Tier,
                Models = configuredModels.Select(name => new AiModelDiagnosticsDto
                {
                    Id = name,
                    IsConfigured = true
                }).ToList()
            };
        }

        var probe = await ProbeAsync(provider, cancellationToken);
        var modelNames = configuredModels
            .Concat(probe.ModelIds)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return new AiProviderDiagnosticsDto
        {
            Id = provider.Id,
            Name = provider.Name,
            Tier = provider.Tier,
            IsReachable = probe.IsReachable,
            ErrorCode = probe.ErrorCode,
            Models = modelNames.Select(name =>
            {
                var configured = configuredModels.Contains(name, StringComparer.OrdinalIgnoreCase);
                bool? available = probe.ErrorCode == null
                    ? probe.ModelIds.Contains(name, StringComparer.OrdinalIgnoreCase)
                    : null;
                return new AiModelDiagnosticsDto
                {
                    Id = name,
                    IsConfigured = configured,
                    IsAvailable = available
                };
            }).ToList()
        };
    }

    public async Task<IReadOnlySet<string>> GetAvailableModelIdsAsync(
        AiProviderConfig provider,
        CancellationToken cancellationToken = default)
    {
        if (provider.Tier != AiModelTier.Tier2_Local)
        {
            return provider.GetModelOptions()
                .Select(model => model.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var probe = await ProbeAsync(provider, cancellationToken);
        if (probe.ErrorCode != null)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return probe.ModelIds;
    }

    public async Task<bool> IsModelAvailableAsync(
        AiProviderConfig provider,
        string modelName,
        CancellationToken cancellationToken = default)
    {
        if (provider.Tier != AiModelTier.Tier2_Local)
            return true;

        var probe = await ProbeAsync(provider, cancellationToken);
        return probe.ErrorCode == null
            && probe.ModelIds.Contains(modelName, StringComparer.OrdinalIgnoreCase);
    }

    internal static Uri BuildModelsUri(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            throw new ArgumentException("Provider base URL must be an absolute URI.", nameof(baseUrl));

        var path = baseUri.AbsolutePath.TrimEnd('/');
        var lastSlash = path.LastIndexOf('/');
        var lastSegment = path[(lastSlash + 1)..];
        if (lastSegment.Length > 1
            && lastSegment[0] == 'v'
            && int.TryParse(lastSegment.AsSpan(1), out _))
        {
            path = path[..lastSlash];
        }

        var builder = new UriBuilder(baseUri)
        {
            Path = $"{path}/v1/models",
            Query = "",
            Fragment = ""
        };
        return builder.Uri;
    }

    private async Task<ProbeResult> ProbeAsync(AiProviderConfig provider, CancellationToken cancellationToken)
    {
        var cacheKey = $"ai-model-health:{provider.Id}:{provider.AiBaseUrl}";
        if (_cache.TryGetValue(cacheKey, out ProbeResult? cached) && cached != null)
            return cached;

        ProbeResult result;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildModelsUri(provider.AiBaseUrl));
            var hasApiKey = _aiConfig.GetApiKeySummaries()
                .Any(summary => summary.ProviderId == provider.Id && summary.HasApiKey);
            var apiKey = hasApiKey ? _aiConfig.GetApiKey(provider.Id) : "";
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var client = _httpClientFactory.CreateClient("AiProviderModelHealth");
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                result = new ProbeResult(true, $"http_{(int)response.StatusCode}", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
            else
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (!document.RootElement.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array)
                {
                    result = new ProbeResult(true, "invalid_model_list", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                }
                else
                {
                    var modelIds = data.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.Object
                            && item.TryGetProperty("id", out var id)
                            && id.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetProperty("id").GetString())
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Select(id => id!)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    result = new ProbeResult(true, null, modelIds);
                }
            }
        }
        catch (ArgumentException)
        {
            result = new ProbeResult(false, "invalid_base_url", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
        catch (HttpRequestException)
        {
            result = new ProbeResult(false, "connection_failed", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = new ProbeResult(false, "timeout", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            result = new ProbeResult(true, "invalid_model_list", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        _cache.Set(cacheKey, result, CacheDuration);
        if (result.ErrorCode != null)
            _logger.LogDebug("AI 本地模型探测失败，Provider={ProviderId}, Error={ErrorCode}", provider.Id, result.ErrorCode);
        return result;
    }

    private sealed record ProbeResult(bool IsReachable, string? ErrorCode, HashSet<string> ModelIds);
}
