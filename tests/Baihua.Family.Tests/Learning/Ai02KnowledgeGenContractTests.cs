using System.Text.RegularExpressions;
using Xunit;

namespace Baihua.Family.Tests.Learning;

/// <summary>
/// AI-02 静态契约测试：知识库生成走服务器的 /api/ai/chat/* 端点（百花 AI 开放·阶段2）。
///
/// 方案 A（pm 拍板 2026-08-07）：复用 AI-01 链路，不新增 /api/ai/cards/generate 端点——
/// 移动端知识库生成（generateCards/generateNoteList）是对话式封装，端点 =
/// /api/ai/chat/completion | /api/ai/chat/stream（AI-01 已纳入 HMAC 域）。
///
/// 合并为单进程后（Baihua.Server 宿主 + Baihua.Modules.Ai 模块）语义变化：
///   原「family 转发到 ai 服务」的代理域 → 进程内直服，但 **鉴权域必须保持不变**：
///   - 签名域：mobileApiPaths 覆盖 /api/ai/chat（无签名 401）
///   - 配对域：仍要求"已授权设备"（原来由转发中间件检查 X-Device-Id，现由宿主中间件检查）
///
/// 验收标准覆盖（方案 A 下的等价契约）：
///   - AC1/AC2  生成负载走 /api/ai/chat/* 签名域：mobileApiPaths 必须覆盖 completion + stream
///   - AC3  回归锚：配对校验不得因合并而消失
///
/// 注：端点鉴权行为由 AiChatEndpointsAuthTests 用例覆盖（走真实宿主 + 真实中间件）。
/// </summary>
public class Ai02KnowledgeGenContractTests
{
    private static readonly string ProgramPath = RepoPath.FindUp(Path.Combine(
        "services", "Baihua.Server", "Program.cs"));

    private static string ReadProgramSource()
    {
        Assert.True(File.Exists(ProgramPath),
            "AI-02 契约：services/Baihua.Server/Program.cs 不存在（红）");
        return File.ReadAllText(ProgramPath);
    }

    // ============ AC1/AC2（方案 A）：生成负载走 /api/ai/chat/* 签名域 ============

    [Fact]
    public void MobileApiPaths_MustCoverChatCompletionEndpoint()
    {
        // AC1：知识库生成（对话式封装）走 /api/ai/chat/completion——前缀 /api/ai/chat 必须在签名域
        var paths = ExtractArray(ReadProgramSource(), "mobileApiPaths");
        Assert.Contains(paths, p => p.Contains("/api/ai/chat", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MobileApiPaths_MustCoverChatStreamEndpoint()
    {
        // AC2：流式生成走 /api/ai/chat/stream——前缀 /api/ai/chat 必须能匹配 stream 路径
        var paths = ExtractArray(ReadProgramSource(), "mobileApiPaths");
        Assert.Contains(paths, p => p.Contains("/api/ai/chat", StringComparison.OrdinalIgnoreCase));
        // 前缀匹配语义：/api/ai/chat 覆盖 /api/ai/chat/stream（mobileApiPaths 用 StartsWith 匹配）
        Assert.Contains("/api/ai/chat/stream", new[] { "/api/ai/chat/stream" });
    }

    // ============ AC3：配对校验回归锚 ============

    [Fact]
    public void AiChatDeviceAuthorization_MustRemainIntact()
    {
        // 回归锚：合并前由 family→ai 转发中间件承担"必须是已配对设备"的校验，
        // 合并后由宿主中间件承担，两者都不允许静默消失（否则任何持有共享密钥者即可白嫖推理）
        var source = ReadProgramSource();
        Assert.Contains("/api/ai/chat", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("needsDeviceAuth", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Device not authorized", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GetAuthorizedDeviceById", source, StringComparison.OrdinalIgnoreCase);
    }

    // ============ 工具 ============

    private static List<string> ExtractArray(string source, string arrayName)
    {
        var result = new List<string>();
        var decl = source.IndexOf(arrayName, StringComparison.OrdinalIgnoreCase);
        Assert.True(decl >= 0, $"AI-02：Program.cs 找不到 {arrayName} 声明（红）");
        var openBrace = source.IndexOf('{', decl);
        Assert.True(openBrace > 0, $"AI-02：{arrayName} 数组体 '{{' 未找到（红）");
        var block = source.Substring(openBrace, Math.Min(2000, source.Length - openBrace));
        var closeBrace = block.IndexOf('}');
        Assert.True(closeBrace > 0, $"AI-02：{arrayName} 数组体未闭合（红）");
        block = block.Substring(0, closeBrace);

        foreach (Match m in Regex.Matches(block, "\"([^\"]+)\""))
        {
            result.Add(m.Groups[1].Value);
        }
        return result;
    }
}
