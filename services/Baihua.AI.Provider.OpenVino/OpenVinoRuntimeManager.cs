using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using Baihua.Contracts.LocalModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Baihua.AI.Provider.OpenVino;

using Baihua.AI.Provider;
using Baihua.Core;

/// <summary>
/// OpenVINO LLM 运行管理器：把已下载的模型目录启动为 openvino_llm_server.py 子进程
/// （Intel Arc 核显 GPU 推理），支持停止/状态探测。
/// </summary>
public class OpenVinoRuntimeManager : ILocalRuntimeManager
{
    private readonly ILogger<OpenVinoRuntimeManager> _logger;
    private readonly LocalAiOptions _options;
    private readonly OmsOptions _omsOptions;
    private readonly ConcurrentDictionary<int, RunningInstance> _running = new();
    private static readonly object PythonLock = new();
    private static string? _pythonCache;

    // OVMS 注册模型 id 探测缓存（避免每次目录刷新都请求 /v1/models）
    private HashSet<string>? _omsIds;
    private DateTime _omsProbeAt;

    private sealed record RunningInstance(int Port, int ProcessId, string ModelPath, DateTime StartedAt);

    public OpenVinoRuntimeManager(
        ILogger<OpenVinoRuntimeManager> logger,
        IOptions<LocalAiOptions> options,
        IOptions<OmsOptions> omsOptions)
    {
        _logger = logger;
        _options = options.Value;
        _omsOptions = omsOptions.Value;
    }

    /// <summary>模型根目录（可通过 LocalAI:DownloadDirectory 配置）</summary>
    public string ModelRoot => _options.GetModelRoot();

    /// <summary>扫描已下载模型目录</summary>
    public List<OpenVinoInstalledModelDto> GetInstalledModels()
    {
        var result = new List<OpenVinoInstalledModelDto>();
        try
        {
            if (!Directory.Exists(ModelRoot)) return result;
            foreach (var dir in Directory.GetDirectories(ModelRoot))
            {
                // 单文件 LLM（openvino_model.bin）或多文件 VL 模型（openvino_language_model.bin 等）都算已下载
                var bin = Path.Combine(dir, "openvino_model.bin");
                var vlBin = Path.Combine(dir, "openvino_language_model.bin");
                if (!File.Exists(bin) && !File.Exists(vlBin)) continue;
                var size = DirectorySize(dir);
                var name = Path.GetFileName(dir);
                var running = _running.Values.FirstOrDefault(r => Path.GetFullPath(r.ModelPath) == Path.GetFullPath(dir));
                result.Add(new OpenVinoInstalledModelDto
                {
                    Name = name,
                    Path = dir,
                    SizeBytes = size,
                    HasOpenVinoBin = true,
                    IsRunning = running != null,
                    Port = running?.Port,
                    LastModified = Directory.GetLastWriteTime(dir)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "扫描模型目录失败");
        }

        // 尽力合并 OpenVINO 服务 pod（k8s 部署：模型托管在 bh-openvino，不在本机模型根）
        MergeRemoteServed(result);

        // 本地 OVMS 常驻托管：注册的模型视为运行中（懒加载，首次推理自动编译加载）
        MergeOmsRegistered(result);

        return result.OrderBy(m => m.Name).ToList();
    }

    /// <summary>
    /// 合并 OVMS（OpenVINO Model Server）常驻注册的模型状态：本机已下载且
    /// 在 OVMS config.json 注册（/v1/models 可见）的模型标记为"运行中"，
    /// 端口取 OVMS 端口（默认 8000）。注册但本地目录不存在的不在此列（目录页
    /// 只展示已下载条目；目录缺失的 OVMS 模型不判定为已安装）。
    /// </summary>
    private void MergeOmsRegistered(List<OpenVinoInstalledModelDto> result)
    {
        var omsIds = GetOmsRegisteredIds();
        if (omsIds.Count == 0) return;
        var port = OmsPort;
        foreach (var m in result)
        {
            var omsId = OmsIdForDirName(m.Name);
            if (omsId == null || !omsIds.Contains(omsId)) continue;
            m.IsOmsHosted = true;
            // 已有自起子进程时保留子进程端口（OVMS 状态作为兜底，不覆盖）
            if (!m.IsRunning)
            {
                m.IsRunning = true;
                m.Port = port;
            }
        }
    }

    /// <summary>本地模型目录名 → OVMS 注册模型 id</summary>
    private static string? OmsIdForDirName(string dirName) => dirName switch
    {
        "Qwen3-4B-int4-ov" => "qwen3-4b",
        "Qwen3.5-4B-int4-ov" => "qwen3.5-4b",
        "Qwen3-Embedding-0.6B-int8-ov" => "qwen3-embedding-0.6b",
        "Qwen2.5-VL-7B-Instruct-int4-ov" => "qwen2.5-vl-7b",
        "Qwen2.5-VL-3B-Instruct-int4-ov" => "qwen2.5-vl-3b",
        "Qwen2.5-7B-Instruct-int4-ov" => "qwen2.5",
        "Qwen2.5-14B-Instruct-INT4-OV" => "qwen2.5-14b",
        "Qwen2.5-Coder-7B-Instruct-int4-ov" => "qwen2.5-coder-7b",
        "Qwen3.5-9B-int8-ov" => "qwen3.5-9b",
        "BianCang-Qwen2.5-7B-Instruct" => "biancang",
        "bge-small-zh-v1.5" => "bge-small-zh",
        _ => null
    };

    /// <summary>OVMS REST 基地址（去掉尾部斜杠）</summary>
    private string OmsBaseUrl => _omsOptions.BaseUrl.TrimEnd('/');

    /// <summary>OVMS 监听端口（从 BaseUrl 解析，默认 8000）</summary>
    private int OmsPort
    {
        get
        {
            try { return new Uri(OmsBaseUrl).Port; }
            catch { return 8000; }
        }
    }

    /// <summary>探测 OVMS /v1/models 注册的模型 id 集合（10s 缓存，失败返回空）</summary>
    private HashSet<string> GetOmsRegisteredIds()
    {
        if (_omsIds != null && DateTime.UtcNow - _omsProbeAt < TimeSpan.FromSeconds(10))
            return _omsIds;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var data = client.GetFromJsonAsync<System.Text.Json.JsonElement>(OmsBaseUrl + "/v1/models")
                .GetAwaiter().GetResult();
            if (data.TryGetProperty("data", out var list) && list.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var e in list.EnumerateArray())
                {
                    if (e.ValueKind == System.Text.Json.JsonValueKind.Object
                        && e.TryGetProperty("id", out var id)
                        && id.ValueKind == System.Text.Json.JsonValueKind.String)
                        ids.Add(id.GetString()!);
                }
            }
            _omsIds = ids;
            _omsProbeAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            // OVMS 不可达 → 保持空集合（本地扫描结果原样返回）
            _logger.LogDebug(ex, "探测 OVMS 模型列表失败（{Url}）", OmsBaseUrl);
        }
        return _omsIds ?? ids;
    }

    /// <summary>
    /// 合并 OpenVINO 服务（pod/远端）正在托管的模型，使其在"已下载模型"里可见。
    /// k8s 部署下视觉模型在 bh-openvino 的 /models 目录（LLM :8000 / Vision :8801），
    /// 本机 Family 的模型根里没有——通过 OPENVINO_LLM_URL/OPENVINO_HOST_URL 的 /health 探测。
    /// </summary>
    private void MergeRemoteServed(List<OpenVinoInstalledModelDto> result)
    {
        var podUrl = Environment.GetEnvironmentVariable("OPENVINO_LLM_URL")
            ?? Environment.GetEnvironmentVariable("OPENVINO_HOST_URL");
        if (string.IsNullOrWhiteSpace(podUrl)) return;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var health = client.GetFromJsonAsync<RemoteHealthDto>(podUrl.TrimEnd('/') + "/health")
                .GetAwaiter().GetResult();
            if (health == null || string.IsNullOrWhiteSpace(health.ModelPath)) return;

            var name = Path.GetFileName(health.ModelPath.TrimEnd('/').TrimEnd('\\'));
            if (string.IsNullOrWhiteSpace(name)) name = health.Model;
            if (string.IsNullOrWhiteSpace(name)) return;
            if (result.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;

            result.Add(new OpenVinoInstalledModelDto
            {
                Name = name,
                Path = health.ModelPath,
                SizeBytes = 0,
                HasOpenVinoBin = true,
                IsRunning = true,
                Port = health.Port ?? 8000,
                LastModified = DateTime.Now,
                IsOmsHosted = true
            });
        }
        catch
        {
            // 远端服务不可达（native 未启动 / pod 未就绪）→ 忽略，保持本地扫描结果
        }
    }

    private sealed class RemoteHealthDto
    {
        public string? Model { get; set; }
        public string? ModelPath { get; set; }
        public int? Port { get; set; }
    }

    /// <summary>启动模型（GPU 推理），等待就绪后返回端口</summary>
    public async Task<OpenVinoRunResult> StartAsync(string modelPath, string device, CancellationToken ct = default)
    {
        modelPath = modelPath.Trim().Trim('"');
        var isVl = File.Exists(Path.Combine(modelPath, "openvino_language_model.bin"));
        if (!File.Exists(Path.Combine(modelPath, "openvino_model.bin")) && !isVl)
            return new OpenVinoRunResult { Success = false, Error = $"目录中未找到 OpenVINO 模型文件: {modelPath}" };

        // 已运行则直接返回
        var existing = _running.Values.FirstOrDefault(r => Path.GetFullPath(r.ModelPath) == Path.GetFullPath(modelPath));
        if (existing != null)
            return new OpenVinoRunResult { Success = true, Port = existing.Port, ProcessId = existing.ProcessId, Endpoint = $"http://127.0.0.1:{existing.Port}/v1" };

        var python = await FindPythonWithOpenVinoAsync(ct);
        if (python == null)
            return new OpenVinoRunResult { Success = false, Error = "未找到可用 Python（需要 pip install openvino-genai）" };

        var script = ResolveScriptPath();
        if (script == null)
            return new OpenVinoRunResult { Success = false, Error = "未找到 openvino_llm_server.py" };

        var port = await FindFreePortAsync(8000, 8030, ct);
        if (port == 0)
            return new OpenVinoRunResult { Success = false, Error = "8000-8029 端口均被占用" };

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = python,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(modelPath);
            psi.ArgumentList.Add("--device");
            psi.ArgumentList.Add(device);
            psi.ArgumentList.Add("--port");
            psi.ArgumentList.Add(port.ToString());
            psi.ArgumentList.Add("--max-context-size");
            psi.ArgumentList.Add("4096");
            // 避免继承被污染的 PYTHONHOME/PYTHONPATH 导致解释器初始化崩溃
            StripPythonEnv(psi);
            var proc = Process.Start(psi);
            if (proc == null)
                return new OpenVinoRunResult { Success = false, Error = "进程启动失败" };

            // 等待 /v1/models 就绪（模型加载可能需要 1-3 分钟）
            var deadline = DateTime.UtcNow.AddMinutes(3);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (proc.HasExited)
                    return new OpenVinoRunResult { Success = false, Error = $"进程提前退出（exit={proc.ExitCode}），模型可能无效或设备不支持" };
                try
                {
                    var resp = await http.GetAsync($"http://127.0.0.1:{port}/v1/models", ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        _running[port] = new RunningInstance(port, proc.Id, Path.GetFullPath(modelPath), DateTime.Now);
                        _logger.LogInformation("OpenVINO 模型已启动: {Model} @ {Port}", modelPath, port);
                        return new OpenVinoRunResult
                        {
                            Success = true,
                            Port = port,
                            ProcessId = proc.Id,
                            Endpoint = $"http://127.0.0.1:{port}/v1"
                        };
                    }
                }
                catch { /* 未就绪，继续等 */ }
                await Task.Delay(2000, ct);
            }
            try { proc.Kill(entireProcessTree: true); } catch { }
            return new OpenVinoRunResult { Success = false, Error = "模型加载超时（3 分钟），已停止进程" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动 OpenVINO 模型失败: {Model}", modelPath);
            return new OpenVinoRunResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>停止模型（按端口，仅管理本管理器启动的实例）</summary>
    public async Task<bool> StopAsync(int port, CancellationToken ct = default)
    {
        if (!_running.TryRemove(port, out var inst))
            return false;
        try
        {
            var p = Process.GetProcessById(inst.ProcessId);
            p.Kill(entireProcessTree: true);
        }
        catch { }
        await Task.CompletedTask;
        return true;
    }

    /// <summary>当前运行中的模型列表（含本管理器启动的）</summary>
    public List<OpenVinoInstalledModelDto> GetRunning()
    {
        var result = new List<OpenVinoInstalledModelDto>();
        foreach (var r in _running.Values)
        {
            result.Add(new OpenVinoInstalledModelDto
            {
                Name = Path.GetFileName(r.ModelPath.TrimEnd(Path.DirectorySeparatorChar)),
                Path = r.ModelPath,
                IsRunning = true,
                Port = r.Port,
            });
        }
        return result;
    }

    #region 工具

    private static long DirectorySize(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        }
        catch { return 0; }
    }

    private static async Task<int> FindFreePortAsync(int start, int end, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var port = start; port <= end; port++)
        {
            try
            {
                var resp = await http.GetAsync($"http://127.0.0.1:{port}/v1/models", ct);
                _ = resp; // 有响应说明被占用
            }
            catch
            {
                return port; // 连接失败 = 空闲
            }
        }
        return 0;
    }

    private string? ResolveScriptPath()
    {
        var published = Path.Combine(AppContext.BaseDirectory, "LocalVision", "openvino_llm_server.py");
        if (File.Exists(published)) return published;
        // 开发环境：源码目录
        var dev = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "services", "Baihua.AI.Provider", "LocalVision", "openvino_llm_server.py");
        if (File.Exists(dev)) return Path.GetFullPath(dev);
        return null;
    }

    private async Task<string?> FindPythonWithOpenVinoAsync(CancellationToken ct)
    {
        if (_pythonCache != null) return _pythonCache;
        lock (PythonLock)
        {
            if (_pythonCache != null) return _pythonCache;

            foreach (var candidate in PythonCandidates())
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = candidate,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add("import openvino_genai");
                    StripPythonEnv(psi);
                    using var proc = Process.Start(psi);
                    if (proc == null) continue;
                    _ = proc.StandardError.ReadToEndAsync();
                    proc.WaitForExit(15000);
                    if (proc.HasExited && proc.ExitCode == 0)
                    {
                        _pythonCache = candidate;
                        return _pythonCache;
                    }
                }
                catch { }
            }
        }
        return null;
    }

    /// <summary>
    /// 候选 Python 列表（按优先级）：
    /// 1) 显式配置 LocalAI:PythonExe；
    /// 2) PATH 命令 python / py / python3 / py3；
    /// 3) Windows 常见标准 Python 安装目录（LocalAppData\Programs\Python\Python3xx\python.exe）；
    /// 4) Linux 常见路径。
    /// 探测条件：能 import openvino_genai。
    /// </summary>
    private IEnumerable<string> PythonCandidates()
    {
        if (!string.IsNullOrWhiteSpace(_options.PythonExe))
            yield return _options.PythonExe;

        foreach (var c in new[] { "python", "py", "python3", "py3" })
            yield return c;

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var baseDir = Path.Combine(localAppData, "Programs", "Python");
            // 从高到低尝试 Python313/312/311/310，均探测 openvino_genai
            foreach (var majorMinor in new[] { "313", "312", "311", "310" })
            {
                var exe = Path.Combine(baseDir, $"Python{majorMinor}", "python.exe");
                if (File.Exists(exe)) yield return exe;
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return "/usr/bin/python3";
            if (File.Exists("/usr/local/bin/python3")) yield return "/usr/local/bin/python3";
        }
    }

    /// <summary>
    /// 清理可能被污染的 PYTHONHOME / PYTHONPATH。当环境里存在指向其它发行版
    /// （如 OpenVINO Model Server 自带 Python）的 PYTHONHOME 时，任何解释器都会在
    /// 初始化阶段因找不到标准库 encodings 而崩溃，导致 import openvino_genai 必败。
    /// </summary>
    private static void StripPythonEnv(ProcessStartInfo psi)
    {
        psi.Environment.Remove("PYTHONHOME");
        psi.Environment.Remove("PYTHONPATH");
    }

    #endregion
}
