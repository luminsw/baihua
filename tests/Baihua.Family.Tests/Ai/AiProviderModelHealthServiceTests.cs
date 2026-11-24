using System.Net;
using System.Text;
using Baihua.Contracts.Ai;
using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Modules.Ai.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Baihua.Family.Tests.Ai;

public class AiProviderModelHealthServiceTests
{
    [Fact]
    public async Task DiagnoseAsync_ReportsOnlyModelsActuallyServedByLocalProvider()
    {
        Uri? requestedUri = null;
        using var client = CreateClient(request =>
        {
            requestedUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"id":"qwen2.5-vl-7b"},{"id":"unconfigured-model"}]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var service = CreateService(client);
        var provider = new AiProviderConfig
        {
            Id = "local",
            Name = "Local OVMS",
            AiBaseUrl = "http://ovms:8000/v3",
            Tier = AiModelTier.Tier2_Local,
            Models =
            [
                new AiModelConfig { Name = "qwen2.5-vl-7b" },
                new AiModelConfig { Name = "not-loaded" }
            ]
        };

        var result = await service.DiagnoseAsync(provider);

        Assert.Equal("/v1/models", requestedUri?.AbsolutePath);
        Assert.True(result.IsReachable);
        Assert.Null(result.ErrorCode);
        Assert.Contains(result.Models, model =>
            model.Id == "qwen2.5-vl-7b" && model.IsConfigured && model.IsAvailable == true);
        Assert.Contains(result.Models, model =>
            model.Id == "not-loaded" && model.IsConfigured && model.IsAvailable == false);
        Assert.Contains(result.Models, model =>
            model.Id == "unconfigured-model" && !model.IsConfigured && model.IsAvailable == true);

        var availableIds = await service.GetAvailableModelIdsAsync(provider);
        Assert.Contains("qwen2.5-vl-7b", availableIds);
        Assert.DoesNotContain("not-loaded", availableIds);
    }

    [Fact]
    public async Task DiagnoseAsync_DoesNotProbeCloudProviders()
    {
        using var client = CreateClient(_ => throw new InvalidOperationException("Unexpected probe."));
        var service = CreateService(client);
        var provider = new AiProviderConfig
        {
            Id = "cloud",
            Name = "Cloud",
            AiBaseUrl = "https://api.example.test/v1",
            Tier = AiModelTier.Tier3_Cloud,
            Models = [new AiModelConfig { Name = "cloud-model" }]
        };

        var result = await service.DiagnoseAsync(provider);

        Assert.Null(result.IsReachable);
        Assert.Single(result.Models);
        Assert.Null(result.Models[0].IsAvailable);
    }

    private static AiProviderModelHealthService CreateService(HttpClient client)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(item => item.CreateClient("AiProviderModelHealth")).Returns(client);
        var config = new Mock<IAiConfigService>();
        config.Setup(item => item.GetApiKeySummaries()).Returns([]);
        config.Setup(item => item.GetApiKey(It.IsAny<string>())).Returns("");

        return new AiProviderModelHealthService(
            factory.Object,
            new MemoryCache(new MemoryCacheOptions()),
            config.Object,
            NullLogger<AiProviderModelHealthService>.Instance);
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return new HttpClient(new DelegateHttpMessageHandler(handler));
    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
