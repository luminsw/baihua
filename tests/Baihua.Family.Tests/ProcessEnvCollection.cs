using Xunit;

// 测试大量使用进程级环境变量（BAIHUA_HOME / BAIHUA_ENCRYPTION_KEY / BAIHUA_DATA_DIR）和
// BaihuaPaths 静态缓存；xUnit 按类并行时，一个类改 env、另一个类正好读到，表现为随机失败的
// "偶发"用例（ApiKeyProtectionServiceTests、DeviceBackupServiceTests 都踩过）。
// 这里统一关掉并行：整套 1000+ 用例串行约十几秒，换来确定性。
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Baihua.Family.Tests;

/// <summary>
/// 会修改进程级环境变量（BAIHUA_HOME / BAIHUA_ENCRYPTION_KEY 等）的测试归到这个集合，
/// 文档化"这些用例依赖全局状态"；真正的并行已由程序集级
/// <c>CollectionBehavior(DisableTestParallelization = true)</c> 关闭。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class ProcessEnvCollection
{
    public const string Name = "ProcessEnv";
}
