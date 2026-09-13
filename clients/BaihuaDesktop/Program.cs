using System.Diagnostics;
using Photino.NET;

namespace BaihuaDesktop;

/// <summary>
/// 百花桌面 App：用 Photino 原生窗口包住 WebUI，后台拉起 server + webui。
/// 双击 baihua-desktop → 窗口打开 → 看到 WebUI；关窗自动停 server + webui。
/// </summary>
internal static class Program
{
    private static readonly string BaseDir = Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
    private static readonly string ServerDir = Path.Combine(BaseDir, "server");
    private static readonly string WebuiDir = Path.Combine(BaseDir, "webui");
    private static readonly string ServerExe = Path.Combine(ServerDir, "bh-server");
    private static readonly string WebuiExe = Path.Combine(WebuiDir, "bh-webui");

    private const int ServerPort = 8788;
    private const int WebuiPort = 5177;

    private static Process? _serverProcess;
    private static Process? _webuiProcess;

    private static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (!File.Exists(ServerExe))
        {
            Console.Error.WriteLine($"找不到后端: {ServerExe}");
            Console.Error.WriteLine("请确保 server/ 目录与桌面 App 同级（用 publish-baihua.sh 打包）");
            Environment.Exit(1);
        }

        var sqlitePath = Environment.GetEnvironmentVariable("SQLITE_PATH")
                         ?? Path.Combine(BaseDir, "baihua.db");

        Console.WriteLine($"[baihua] SQLite: {sqlitePath}");

        StartServer(sqlitePath);
        WaitForHealth($"http://127.0.0.1:{ServerPort}/health", "后端");
        StartWebui();
        WaitForHealth($"http://127.0.0.1:{WebuiPort}/", "WebUI", acceptRedirect: true);

        var webuiUrl = $"http://127.0.0.1:{WebuiPort}/";
        Console.WriteLine($"[baihua] 打开窗口: {webuiUrl}");

        var window = new PhotinoWindow()
            .SetTitle("百花")
            .Load(webuiUrl)
            .SetSize(1280, 860)
            .SetMinSize(960, 640)
            .Center();

        window.WindowClosingHandler = (_, _) =>
        {
            Console.WriteLine("[baihua] 窗口关闭，停止服务...");
            StopAll();
            return false;
        };

        window.WaitForClose();

        StopAll();
    }

    private static void StartServer(string sqlitePath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ServerExe,
            Arguments = $"--urls http://0.0.0.0:{ServerPort}",
            UseShellExecute = false,
            WorkingDirectory = ServerDir,
        };
        psi.EnvironmentVariables["BAIHUA_DB_PROVIDER"] = "sqlite";
        psi.EnvironmentVariables["SQLITE_PATH"] = sqlitePath;
        psi.EnvironmentVariables[$"Baihua__PublicBaseUrl"] = $"http://127.0.0.1:{ServerPort}";

        _serverProcess = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动后端");
        Console.WriteLine($"[baihua] 后端已启动 (PID {_serverProcess.Id})");
    }

    private static void StartWebui()
    {
        var psi = new ProcessStartInfo
        {
            FileName = WebuiExe,
            Arguments = $"--urls http://0.0.0.0:{WebuiPort}",
            UseShellExecute = false,
            WorkingDirectory = WebuiDir,
        };
        psi.EnvironmentVariables["BaihuaServer__BaseUrl"] = $"http://127.0.0.1:{ServerPort}/";

        _webuiProcess = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 WebUI");
        Console.WriteLine($"[baihua] WebUI 已启动 (PID {_webuiProcess.Id})");
    }

    private static void WaitForHealth(string url, string name, bool acceptRedirect = false)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        for (var i = 0; i < 30; i++)
        {
            try
            {
                var resp = http.GetAsync(url).Result;
                if (resp.IsSuccessStatusCode || (acceptRedirect && resp.StatusCode == System.Net.HttpStatusCode.Redirect))
                {
                    Console.WriteLine($"[baihua] {name} 就绪");
                    return;
                }
            }
            catch { }
            Thread.Sleep(1000);
        }
        throw new TimeoutException($"{name} 30 秒内未就绪");
    }

    private static void StopAll()
    {
        StopProcess(_webuiProcess, "WebUI");
        StopProcess(_serverProcess, "后端");
    }

    private static void StopProcess(Process? p, string name)
    {
        if (p is null || p.HasExited) return;
        try
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
            Console.WriteLine($"[baihua] {name} 已停止");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[baihua] 停止 {name} 失败: {ex.Message}");
        }
    }
}