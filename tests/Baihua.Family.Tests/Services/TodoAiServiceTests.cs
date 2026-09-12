using Baihua.Core.Services;
using Baihua.Modules.Family.Services.Todo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// AI 待办生成服务测试（目标 → 具体待办预览，不落库）。
/// 与 AnkiCardGeneratorTests 一致：不发起真实 AI 请求，覆盖参数校验、无 AI 配置路径与 JSON 提取。
/// </summary>
public class TodoAiServiceTests
{
    private TodoAiService CreateService(AiSettingsService? aiSettings = null)
    {
        var settings = aiSettings ?? CreateEmptyAiSettingsService();
        // 无 AI 配置路径不会触达 AiClientService，传 null 即可
        return new TodoAiService(null!, settings,
            LoggerFactory.Create(b => { }).CreateLogger<TodoAiService>());
    }

    /// <summary>无任何 AI Provider 配置（GetMainAiProvider 返回 null）</summary>
    private static AiSettingsService CreateEmptyAiSettingsService()
    {
        var configuration = new ConfigurationBuilder().Build();
        var logger = LoggerFactory.Create(b => { }).CreateLogger<AiSettingsService>();
        return new AiSettingsService(configuration, TestDoubles.StubAiConfigService.Empty, logger);
    }

    // ============ 参数校验 ============

    [Fact]
    public async Task Generate_EmptyGoal_ReturnsFail()
    {
        var service = CreateService();
        var result = await service.GeneratePreviewAsync("  ");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Null(result.Preview);
    }

    [Fact]
    public async Task Generate_GoalTooLong_ReturnsFail()
    {
        var service = CreateService();
        var result = await service.GeneratePreviewAsync(new string('长', 201));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    // ============ 无 AI 配置 ============

    [Fact]
    public async Task Generate_NoAiProvider_ReturnsFailWithFriendlyMessage()
    {
        var service = CreateService();
        var result = await service.GeneratePreviewAsync("办理护照");

        Assert.False(result.Success);
        Assert.Contains("AI", result.Error ?? "");
    }

    // ============ JSON 提取 ============

    [Fact]
    public void ExtractJsonObject_WithMarkdownBlock_ExtractsObject()
    {
        var input = """
            好的，这是拆解结果：
            ```json
            {"title":"办理护照","items":[{"title":"预约","note":"移民局小程序"}]}
            ```
            希望对你有帮助
            """;

        var result = TodoAiService.ExtractJsonObject(input);

        Assert.Equal("{\"title\":\"办理护照\",\"items\":[{\"title\":\"预约\",\"note\":\"移民局小程序\"}]}", result);
    }

    [Fact]
    public void ExtractJsonObject_WithLeadingText_ExtractsObject()
    {
        var input = "以下是计划：{" +
                    "\"title\":\"目标\",\"items\":[{\"title\":\"事项\",\"note\":\"指引\"}]} 完毕";

        var result = TodoAiService.ExtractJsonObject(input);

        Assert.Equal("{\"title\":\"目标\",\"items\":[{\"title\":\"事项\",\"note\":\"指引\"}]}", result);
    }

    [Fact]
    public void ExtractJsonObject_PlainJson_Unchanged()
    {
        var input = "{\"title\":\"目标\",\"items\":[]}";

        Assert.Equal(input, TodoAiService.ExtractJsonObject(input));
    }

    [Fact]
    public void ExtractJsonObject_Empty_ReturnsEmpty()
    {
        Assert.Equal("", TodoAiService.ExtractJsonObject(""));
        Assert.Equal("   ", TodoAiService.ExtractJsonObject("   "));
    }
}
