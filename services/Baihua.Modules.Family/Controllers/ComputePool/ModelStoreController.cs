using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using Baihua.AI.Provider;
using Baihua.Contracts.Benchmark;
using Baihua.Contracts.ComputePool;
using Baihua.Core.Services;
using Baihua.Modules.Family.Services;
using Baihua.Modules.Family.Services.ComputePool;
using Baihua.Modules.Family.Services.ServerMessaging;
using Microsoft.AspNetCore.Mvc;
using Baihua.Core;

namespace Baihua.Modules.Family.Controllers.ComputePool;

/// <summary>
/// 算力池对端服务端点（X-Server-Token 自校验，公开路径）：
/// - POST /mg/benchmark/run：对端发起本机单模型快速测速（实测 token/s）
/// - GET  /mg/model-store/list：本机已下载模型清单（可被对端拉取）
/// - GET  /mg/model-store/download/{name}：以 tar 流式下发模型目录
/// - POST /mg/model-store/deploy：对端从来源服务器拉取模型并启动运行时（跨机布署）
/// </summary>
[ApiController]
public class ModelStoreController : ControllerBase
{
    private readonly AiSettingsService _aiSettings;
    private readonly AiClientService _aiClient;
    private readonly Microsoft.Extensions.Options.IOptions<LocalAiOptions> _localAiOptions;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ModelStoreController> _logger;
    private readonly BenchmarkRepository _benchmarkRepository;
    private readonly ServerMessageService _messageService;
    private readonly ILocalRuntimeManager _runtime;

    public ModelStoreController(
        AiSettingsService aiSettings,
        AiClientService aiClient,
        Microsoft.Extensions.Options.IOptions<LocalAiOptions> localAiOptions,
        IConfiguration configuration,
        ILogger<ModelStoreController> logger,
        BenchmarkRepository benchmarkRepository,
        ServerMessageService messageService,
        ILocalRuntimeManager runtime)
    {
        _aiSettings = aiSettings;
        _aiClient = aiClient;
        _localAiOptions = localAiOptions;
        _configuration = configuration;
        _logger = logger;
        _benchmarkRepository = benchmarkRepository;
        _messageService = messageService;
        _runtime = runtime;
    }

    private string ModelRoot => _localAiOptions.Value.GetModelRoot();

    private bool AuthorizePeer()
    {
        var localToken = _configuration["BAIHUA_SERVER_MSG_TOKEN"] ?? "";
        if (string.IsNullOrEmpty(localToken)) return true;
        var token = Request.Headers["X-Server-Token"].FirstOrDefault();
        return string.Equals(token, localToken, StringComparison.Ordinal);
    }

    /// <summary>对端发起本机单模型快速测速（短提示 + max_tokens=64，几秒出结果）。</summary>
    [HttpPost("/mg/benchmark/run")]
    public async Task<ActionResult<BenchmarkRunResultDto>> RunBenchmark([FromBody] PeerBenchmarkRequest request, CancellationToken ct)
    {
        if (!AuthorizePeer()) return Unauthorized(new { error = "口令校验失败" });
        if (string.IsNullOrWhiteSpace(request.ModelName))
            return BadRequest(new BenchmarkRunResultDto { Success = false, Error = "缺少 modelName" });

        try
        {
            var modelName = request.ModelName.Trim();
            var provider = _aiSettings.GetAiProviders()
                .FirstOrDefault(p => p.Models.Any(m => m.Name.Equals(modelName, StringComparison.OrdinalIgnoreCase)));
            if (provider == null)
                return BadRequest(new BenchmarkRunResultDto { Success = false, Error = $"本机无模型 {modelName}" });

            // 快速测速：短提示 + 小 max_tokens，避免长作文把超时窗口耗尽
            var messages = new List<Microsoft.Extensions.AI.ChatMessage>
            {
                new(Microsoft.Extensions.AI.ChatRole.System, "请尽量简短回答。"),
                new(Microsoft.Extensions.AI.ChatRole.User, "用三句话介绍你自己。")
            };
            var options = AiClientService.BuildChatOptions(maxOutputTokens: 64, temperature: 0.3f);

            // 用无缓存的 client 测速（分布式缓存会命中同 prompt 的旧响应，测不出真实速度）
            var rawClient = _aiClient.CreateChatClient(provider, modelName);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await rawClient.GetResponseAsync(messages, options, ct);
            sw.Stop();

            var text = response.Text ?? "";
            var actualTokens = response.Usage?.OutputTokenCount is long ot && ot > 0 ? (int)ot : (int?)(text.Length * 0.7);
            var elapsedSec = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            var tps = Math.Round(actualTokens.Value / elapsedSec, 1);

            // 持久化到 Benchmark 排行榜（category="quick"，不影响模型评测页的 tcm/coding 分类），
            // 使本机 /mg/capabilities 的 GetBenchmarkTps 能持续广播该模型的实测 TPS，
            // 否则算力池总览只有内存里的一次性回写，60 秒刷新后又被清空。
            await _benchmarkRepository.SaveSessionAsync(new BenchmarkSession
            {
                ModelName = modelName,
                Category = "quick",
                ProviderId = provider.Id,
                ModelId = modelName,
                TestedAt = DateTime.UtcNow,
                Results = new List<BenchmarkPromptResult>
                {
                    new()
                    {
                        PromptId = "quick",
                        PromptTitle = "算力池快速测速",
                        LatencyMs = (long)Math.Round(sw.Elapsed.TotalMilliseconds),
                        OutputChars = text.Length,
                        TokensPerSecond = tps,
                        ResponseText = text.Length > 200 ? text[..200] : text,
                        QualityScore = 0
                    }
                }
            });

            return Ok(new BenchmarkRunResultDto
            {
                Success = true,
                ModelName = modelName,
                TokensPerSecond = tps,
                LatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                TestedAt = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "对端测速失败 {Model}", request.ModelName);
            return Ok(new BenchmarkRunResultDto { Success = false, Error = ex.Message, ModelName = request.ModelName.Trim() });
        }
    }

    /// <summary>本机已下载模型清单（供对端拉取，模型商店）。</summary>
    [HttpGet("/mg/model-store/list")]
    public ActionResult<List<ModelStoreEntryDto>> ListStore()
    {
        if (!AuthorizePeer()) return Unauthorized(new { error = "口令校验失败" });
        try
        {
            var root = ModelRoot;
            if (!Directory.Exists(root))
                return Ok(new List<ModelStoreEntryDto>());

            var entries = Directory.GetDirectories(root)
                .Where(d => !d.EndsWith(".downloading", StringComparison.OrdinalIgnoreCase))
                .Select(d =>
                {
                    var files = Directory.GetFiles(d, "*", SearchOption.AllDirectories);
                    return new ModelStoreEntryDto
                    {
                        Name = Path.GetFileName(d),
                        SizeBytes = files.Sum(f => new FileInfo(f).Length),
                        FileCount = files.Length
                    };
                })
                .Where(e => e.SizeBytes > 0)
                .OrderBy(e => e.Name)
                .ToList();
            return Ok(entries);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "列出模型商店失败");
            return Ok(new List<ModelStoreEntryDto>());
        }
    }

    /// <summary>以 tar 流式下发模型目录（模型文件多为已压缩权重，不二次 gzip）。</summary>
    [HttpGet("/mg/model-store/download/{modelName}")]
    public async Task DownloadModel(string modelName, CancellationToken ct)
    {
        if (!AuthorizePeer())
        {
            Response.StatusCode = 401;
            return;
        }

        var root = ModelRoot;
        var dir = Path.Combine(root, modelName);
        if (string.IsNullOrWhiteSpace(modelName) || !Directory.Exists(dir))
        {
            Response.StatusCode = 404;
            await Response.WriteAsJsonAsync(new { error = "模型不存在" }, ct);
            return;
        }

        Response.ContentType = "application/x-tar";
        Response.Headers["Content-Disposition"] = $"attachment; filename=\"{modelName}.tar\"";
        await using var tar = new TarWriter(Response.Body, leaveOpen: false);
        await WriteDirectoryToTarAsync(tar, dir, modelName, ct);
    }

    private static async Task WriteDirectoryToTarAsync(TarWriter tar, string dir, string entryPrefix, CancellationToken ct)
    {
        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(dir, file).Replace(Path.DirectorySeparatorChar, '/');
            var entry = new PaxTarEntry(TarEntryType.RegularFile, $"{entryPrefix}/{relative}")
            {
                DataStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
            };
            await tar.WriteEntryAsync(entry, ct);
            await entry.DataStream.DisposeAsync();
        }
    }

    /// <summary>
    /// 跨机布署：从来源服务器的 /mg/model-store/download 拉取模型 tar，解压到本机模型根，
    /// 再启动运行时（OpenVINO 推理进程）常驻服务。GPU 失败自动回退 CPU。
    /// </summary>
    [HttpPost("/mg/model-store/deploy")]
    public async Task<ActionResult<DeployModelResultDto>> DeployModel([FromBody] PeerDeployRequest request, CancellationToken ct)
    {
        if (!AuthorizePeer()) return Unauthorized(new DeployModelResultDto { Success = false, Error = "口令校验失败", ModelName = request.ModelName });
        if (string.IsNullOrWhiteSpace(request.ModelName) || request.ModelName.Contains('/') || request.ModelName.Contains('\\') || request.ModelName == "..")
            return BadRequest(new DeployModelResultDto { Success = false, Error = "非法的模型名", ModelName = request.ModelName });

        // SSRF 防护：来源必须是局域网/回环地址，且路径必须是模型商店下载端点
        if (!IsAllowedSourceUrl(request.SourceUrl, request.ModelName))
            return BadRequest(new DeployModelResultDto { Success = false, Error = "来源地址不合法（仅允许局域网模型商店）", ModelName = request.ModelName });

        try
        {
            // 来源服务器互联口令（与拉取模型一致：对端登记的口令，缺省用本机口令）
            string? sourceToken = null;
            if (!string.IsNullOrWhiteSpace(request.SourceServerId))
            {
                var sourcePeer = (await _messageService.ListPeersAsync(ct))
                    .FirstOrDefault(p => p.ServerId == request.SourceServerId);
                sourceToken = sourcePeer?.Token;
            }
            if (string.IsNullOrWhiteSpace(sourceToken))
                sourceToken = _messageService.LocalToken;

            var root = ModelRoot;
            Directory.CreateDirectory(root);
            var target = Path.Combine(root, request.ModelName);
            if (Directory.Exists(target))
                return Ok(new DeployModelResultDto { Success = false, Error = $"本机已存在 {request.ModelName}（先删除或选用现有模型）", ModelName = request.ModelName });

            _logger.LogInformation("[ModelStore] 布署 {Model}：从 {Url} 拉取", request.ModelName, request.SourceUrl);
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
            var (ok, error) = await ModelTarTransfer.DownloadAndExtractAsync(
                client, request.SourceUrl, sourceToken, target, request.ModelName, ct);
            if (!ok)
                return Ok(new DeployModelResultDto { Success = false, Error = $"拉取模型失败：{error}", ModelName = request.ModelName });

            // 启动运行时：请求的设备优先，失败自动回退 CPU（如对端无 GPU）
            var device = string.IsNullOrWhiteSpace(request.Device) ? "GPU" : request.Device.Trim();
            var run = await _runtime.StartAsync(target, device, ct);
            if (!run.Success && !device.Equals("CPU", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("[ModelStore] {Model} 用 {Device} 启动失败（{Msg}），回退 CPU", request.ModelName, device, run.Error);
                device = "CPU";
                run = await _runtime.StartAsync(target, device, ct);
            }

            var result = new DeployModelResultDto
            {
                Success = run.Success,
                Error = run.Success ? null : run.Error,
                ModelName = request.ModelName,
                Device = device,
                Port = run.Port,
                Endpoint = run.Endpoint,
                DeployedAt = DateTime.UtcNow
            };
            _logger.LogInformation("[ModelStore] 布署 {Model} 完成：{Device} @ :{Port}", request.ModelName, device, run.Port);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "跨机布署失败 {Model}", request.ModelName);
            return Ok(new DeployModelResultDto { Success = false, Error = ex.Message, ModelName = request.ModelName });
        }
    }

    /// <summary>来源 URL 白名单：仅 http(s) + 回环/私网 IP + 模型商店下载路径，防 SSRF。</summary>
    private static bool IsAllowedSourceUrl(string url, string modelName)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != "http" && uri.Scheme != "https") return false;
        if (!uri.AbsolutePath.StartsWith("/mg/model-store/download/", StringComparison.OrdinalIgnoreCase)) return false;
        // 路径末尾的模型名必须与请求一致
        var name = uri.AbsolutePath.Split('/').Last();
        if (!string.Equals(Uri.UnescapeDataString(name), modelName, StringComparison.OrdinalIgnoreCase)) return false;
        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        // 只允许字面 IP（拒绝域名，避免 DNS rebinding）
        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168));
    }
}
