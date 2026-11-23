namespace Baihua.Contracts.Platform;

public class PlatformInfoResponse
{
    public string OsName { get; set; } = "";
    public bool IsWindows { get; set; }
    public bool IsLinux { get; set; }
    public bool IsMacOS { get; set; }
    public string UserName { get; set; } = "";
    public string HomeDir { get; set; } = "";
    public int AiRequestTimeoutMinutes { get; set; } = 5;
}
