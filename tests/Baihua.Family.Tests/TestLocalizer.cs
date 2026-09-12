using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Baihua.Core.Localization;

namespace Baihua.Family.Tests;

/// <summary>
/// 测试用本地化器：直接读取 Baihua.Core 内嵌的中性资源（中文单语言，已无 zh-CN 附属资源）。
/// 不再手工维护字符串字典——测试断言的就是产品真实文案，避免字典与 resx 漂移。
/// </summary>
public static class TestLocalizer
{
    private static readonly Lazy<IStringLocalizer<SharedResources>> _instance =
        new Lazy<IStringLocalizer<SharedResources>>(CreateLocalizer);

    public static IStringLocalizer<SharedResources> Instance => _instance.Value;

    public static IStringLocalizer<SharedResources> Create() => Instance;

    private static IStringLocalizer<SharedResources> CreateLocalizer()
    {
        var factory = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()),
            NullLoggerFactory.Instance);
        return new StringLocalizer<SharedResources>(factory);
    }
}
