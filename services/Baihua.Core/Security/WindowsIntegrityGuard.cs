using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Baihua.Core.Security;

/// <summary>
/// 进程完整性级别（IL）自检：确认服务「以什么完整性级别运行、能否写文件」。
///
/// <para><b>为什么需要</b>：Windows 会按**映像文件（.exe）的完整性标签**决定进程令牌的 IL。
/// 若构建产物落在带 Low 标签的目录里（例如被沙箱工具打过 Low 标签的工作区），
/// `bh-server.exe` 就会以 <b>Low IL</b> 启动：读文件正常，但写 `%TEMP%`、`%LOCALAPPDATA%`、
/// 用户目录一律 `UnauthorizedAccessException: Access to the path ... is denied`。
/// 这个现象曾经被误判成 WMI/计划任务令牌降级、目录 ACL、Defender 受控文件夹访问，
/// 排查了很久（见 docs/PLAN_NATIVE_CELL.md「Low 完整性标签」一节）。</para>
///
/// <para><b>做法</b>：启动最早处读一次自身令牌的完整性级别，并做一次临时目录写入探针；
/// 不正常时直接打 ERROR 并给出可执行的修复命令，而不是等到写笔记/备份/索引快照时才报错。</para>
/// </summary>
public static class WindowsIntegrityGuard
{
    /// <summary>Medium 完整性级别（S-1-16-8192）。低于它：对 Medium 对象只有读权限，写一律被拒。</summary>
    private const int MediumIntegrity = 0x2000;

    private const int TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    /// <summary>启动自检：读自身 IL + 写探针，异常时记录 ERROR（不影响启动）。</summary>
    public static void LogSelfCheck(ILogger logger, string? contentRoot = null)
    {
        var writeProbe = ProbeTempWrite();

        if (!OperatingSystem.IsWindows())
        {
            // Linux/macOS 无完整性级别概念；仅报告写探针结果
            if (!writeProbe.Ok)
            {
                logger.LogError("启动自检：临时目录不可写（{Path}）：{Error}",
                    writeProbe.Path, writeProbe.Error);
            }
            return;
        }

        var level = TryGetIntegrityLevel(out var integrity) ? integrity : (int?)null;
        var levelText = level switch
        {
            null => "未知",
            < 0x1000 => $"Low(0x{level:X})",
            < 0x2000 => $"MediumMinus(0x{level:X})",
            < 0x3000 => $"Medium(0x{level:X})",
            < 0x4000 => $"High(0x{level:X})",
            _ => $"System(0x{level:X})",
        };

        if (level is not null && level < MediumIntegrity)
        {
            var dir = contentRoot ?? AppContext.BaseDirectory;
            logger.LogError(
                "启动自检：服务正以 **{Level}** 完整性级别运行，写文件会被系统拒绝（读取不受影响）。\n" +
                "  常见原因：构建产物目录带了 Low 完整性标签 —— Windows 按 .exe 文件的标签决定进程 IL，\n" +
                "  而沙箱类工具（如 DSH 的 Windows 沙箱）会给工作区打 Low 标签。\n" +
                "  修复（普通终端执行，然后重新构建/启动）：\n" +
                "    icacls \"{ProbeDir}\" /setintegritylevel (OI)(CI)Medium\n" +
                "  校验：icacls \"{ExeDir}\" 不应再出现 \"Low Mandatory Level\"。",
                levelText, dir, AppContext.BaseDirectory);
        }
        else if (level is not null)
        {
            logger.LogInformation("启动自检：进程完整性级别 {Level}，临时目录写入 {WriteResult}",
                levelText, writeProbe.Ok ? "正常" : "失败");
        }

        if (!writeProbe.Ok)
        {
            logger.LogError(
                "启动自检：临时目录不可写（{Path}）：{Error}。知识库写笔记 / 索引快照 / 自动备份会全部失败。",
                writeProbe.Path, writeProbe.Error);
        }
    }

    /// <summary>读自身进程令牌的完整性级别（Windows）；失败返回 false。</summary>
    [SupportedOSPlatform("windows")]
    public static bool TryGetIntegrityLevel(out int integrityLevel)
    {
        integrityLevel = 0;
        var token = IntPtr.Zero;
        var buffer = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token)) return false;
            buffer = Marshal.AllocHGlobal(256);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, 256, out _)) return false;

            var sid = Marshal.ReadIntPtr(buffer);
            if (sid == IntPtr.Zero) return false;

            // SID: Revision(1) SubAuthorityCount(1) IdentifierAuthority(6) SubAuthority[](4 each)
            var subAuthorityCount = Marshal.ReadByte(sid, 1);
            if (subAuthorityCount == 0) return false;
            integrityLevel = Marshal.ReadInt32(sid, 8 + (subAuthorityCount - 1) * 4);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    private static (bool Ok, string Path, string? Error) ProbeTempWrite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"baihua-write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(path, "ok");
            File.Delete(path);
            return (true, path, null);
        }
        catch (Exception ex)
        {
            return (false, path, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, int desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength,
        out int returnLength);
}
