using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Text.Json;
using Baihua.Contracts.Ai;
using Baihua.Contracts.ComputePool;
using Baihua.Core;
using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Modules.Family.Services.ServerMessaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Baihua.Modules.Family.Services.ComputePool;

/// <summary>
/// 百花算力池汇聚服务：
/// - 定时拉取每个对端服务器的 /mg/capabilities（X-Server-Token 鉴权）并缓存；
/// - 把对外暴露了 OpenAI 兼容端点的对端自动注册为本机 AI 提供方（peer- 前缀），
///   聊天/拜师/OpenClaw 即可直接选用对端算力；
/// - 向 WebUI 提供算力池总览（/api/compute-pool）。
/// </summary>
public class ComputePoolService : IHostedService, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly ServerMessageService _messageService;
    private readonly IAiConfigService _aiConfig;
    private readonly AiSettingsService _aiSettings;
    private readonly ServerAddressService _serverAddress;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ComputePoolService> _logger;
    private readonly BenchmarkRepository _benchmarkRepository;
    private readonly ComfyDrawService _draw;

    private readonly ConcurrentDictionary<string, ComputeNodeCapabilitiesDto> _peerCapabilities = new();
    /// <summary>对端 /health 可达时间（capabilities 未就绪时仍能判断在线）</summary>
    private readonly ConcurrentDictionary<string, DateTime> _peerReachable = new();
    private Timer? _timer;

    public ComputePoolService(
        ServerMessageService messageService,
        IAiConfigService aiConfig,
        AiSettingsService aiSettings,
        ServerAddressService serverAddress,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ComputePoolService> logger,
        BenchmarkRepository benchmarkRepository,
        ComfyDrawService draw)
    {
        _messageService = messageService;
        _aiConfig = aiConfig;
        _aiSettings = aiSettings;
        _serverAddress = serverAddress;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _benchmarkRepository = benchmarkRepository;
        _draw = draw;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer ??= new Timer(async _ =>
            {
                try { await RefreshAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "算力池刷新失败"); }
            },
            null, TimeSpan.FromSeconds(5), RefreshInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Start()
    {
        _timer ??= new Timer(async _ =>
            {
                try { await RefreshAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "算力池刷新失败"); }
            },
            null, TimeSpan.FromSeconds(5), RefreshInterval);
    }

    /// <summary>拉取所有对端能力并自动注册可用的 AI 提供方。</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var peers = await _messageService.ListPeersAsync(ct);
        var localToken = _messageService.LocalToken;
        var localServerId = GetLocalServerId();
        var localHostUrl = GetLocalHostUrl();
        using var client = _httpClientFactory.CreateClient("ComputePool");
        client.Timeout = TimeSpan.FromSeconds(10);

        // 本轮仍有效的对端身份（ServerId），用于清理缓存里已删除/被取代的旧条目
        var knownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawPeer in peers)
        {
            var peer = rawPeer;
            // 指向本机/自己登记的条目：不拉取、不展示（历史遗留的自我登记，如旧 ServerId + 本机 IP）
            if (IsSelfPeer(peer, localServerId, localHostUrl))
            {
                _peerCapabilities.TryRemove(peer.ServerId, out _);
                _peerReachable.TryRemove(peer.ServerId, out _);
                // ServerId 与本机完全相同 → 确定是本机自我登记（幽灵节点），直接从登记表清理，
                // 避免算力池每次刷新都出现"本机出现两次"
                if (!string.IsNullOrEmpty(peer.ServerId)
                    && string.Equals(peer.ServerId, localServerId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("[ComputePool] 清理本机自我登记 {Name} ({ServerId}) @ {BaseUrl}",
                        peer.Name, peer.ServerId, peer.BaseUrl);
                    await _messageService.DeletePeerAsync(peer.Id, ct);
                }
                continue;
            }
            knownIds.Add(peer.ServerId);

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{peer.BaseUrl.TrimEnd('/')}/mg/capabilities");
                var token = !string.IsNullOrWhiteSpace(peer.Token) ? peer.Token! : localToken;
                if (!string.IsNullOrEmpty(token))
                    req.Headers.TryAddWithoutValidation("X-Server-Token", token);

                using var resp = await client.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    // capabilities 未就绪（对端是旧代码/未配置）→ 用 /health 判定在线，算力状态留空
                    _peerCapabilities.TryRemove(peer.ServerId, out _);
                    await ProbePeerHealthAsync(peer, client, ct);
                    continue;
                }

                var caps = await resp.Content.ReadFromJsonAsync<ComputeNodeCapabilitiesDto>(ct);
                if (caps == null || string.IsNullOrEmpty(caps.ServerId))
                    continue;

                // 该登记实际指向本机（如多实例/历史遗留的自我登记）→ 不缓存、不注册提供方
                if (string.Equals(caps.ServerId, localServerId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("[ComputePool] 对端 {BaseUrl} 实为本机，跳过", peer.BaseUrl);
                    _peerCapabilities.TryRemove(peer.ServerId, out _);
                    _peerReachable.TryRemove(peer.ServerId, out _);
                    continue;
                }

                // 对端改名/重装后 ServerId 变化 → 校正登记身份，让新身份在算力池里正常显示
                if (!string.Equals(caps.ServerId, peer.ServerId, StringComparison.OrdinalIgnoreCase))
                {
                    var oldServerId = peer.ServerId;
                    var reconciled = await _messageService.ReconcilePeerIdentityAsync(
                        peer.Id, caps.ServerId, caps.Name, ct);
                    if (reconciled == null)
                        continue;
                    // 旧身份失效：清缓存 + 移除旧身份自动登记的对端提供方（peer- 前缀）
                    _peerCapabilities.TryRemove(oldServerId, out _);
                    _peerReachable.TryRemove(oldServerId, out _);
                    if (!string.IsNullOrEmpty(oldServerId))
                    {
                        var oldProviderId = $"peer-{oldServerId}";
                        if (_aiConfig.GetProvider(oldProviderId) != null)
                        {
                            _aiConfig.DeleteProvider(oldProviderId);
                            _aiSettings.ClearAiProvidersCache();
                            _logger.LogInformation("[ComputePool] 对端 {Name} 身份变化，清理旧提供方 {ProviderId}",
                                caps.Name, oldProviderId);
                        }
                    }
                    peer = reconciled;
                }

                knownIds.Add(peer.ServerId);
                caps.HostUrl = peer.BaseUrl;
                caps.ModelStore = await FetchPeerModelStoreAsync(peer, token, client, ct);
                _peerCapabilities[peer.ServerId] = caps;
                _logger.LogDebug("[ComputePool] 已缓存对端能力: {Name} ({ServerId}) {ProviderCount} 个提供方",
                    caps.Name, caps.ServerId, caps.Providers.Count);

                await AutoRegisterProviderAsync(peer, caps, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("[ComputePool] 拉取对端能力失败 {BaseUrl}: {Msg}", peer.BaseUrl, ex.Message);
            }
        }

        // 清理已删除/被身份校正取代的对端（能力与在线状态缓存）
        foreach (var key in _peerCapabilities.Keys)
        {
            if (!knownIds.Contains(key))
                _peerCapabilities.TryRemove(key, out _);
        }
        foreach (var key in _peerReachable.Keys)
        {
            if (!knownIds.Contains(key))
                _peerReachable.TryRemove(key, out _);
        }
    }


    /// <summary>对端 /health 探测：可达则记录在线（供 UI 显示"在线但未接入算力池"）。</summary>
    private async Task ProbePeerHealthAsync(ServerPeer peer, HttpClient client, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{peer.BaseUrl.TrimEnd('/')}/health");
            using var resp = await client.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
                _peerReachable[peer.ServerId] = DateTime.UtcNow;
            else
                _peerReachable.TryRemove(peer.ServerId, out _);
        }
        catch
        {
            _peerReachable.TryRemove(peer.ServerId, out _);
        }
    }

    /// <summary>拉取对端模型商店清单（无则空列表）。</summary>
    private async Task<List<ModelStoreEntryDto>> FetchPeerModelStoreAsync(ServerPeer peer, string token, HttpClient client, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{peer.BaseUrl.TrimEnd('/')}/mg/model-store/list");
            if (!string.IsNullOrEmpty(token))
                req.Headers.TryAddWithoutValidation("X-Server-Token", token);
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return new List<ModelStoreEntryDto>();
            return await resp.Content.ReadFromJsonAsync<List<ModelStoreEntryDto>>(ct) ?? new List<ModelStoreEntryDto>();
        }
        catch
        {
            return new List<ModelStoreEntryDto>();
        }
    }

    /// <summary>
    /// 把对端注册为本机 AI 提供方（仅当对端声明了 OpenAiBaseUrl）。
    /// 一个对端一个提供方（peer-{ServerId}），合并其全部模型；ApiKey 用本机互联口令
    /// （对端 OpenAI shim 按 BAIHUA_AI_EXTERNAL_TOKEN 校验）。
    /// </summary>
    private async Task AutoRegisterProviderAsync(ServerPeer peer, ComputeNodeCapabilitiesDto caps, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(caps.OpenAiBaseUrl))
            return;

        var providerId = $"peer-{peer.ServerId}";
        if (providerId.Length > 50) providerId = providerId[..50];

        // 只取对端可共享的本地算力（非 peer-、非云端）：云端模型对端可直连、peer- 是它人模型，
        // 注册进来只会让本机模型列表重复。
        var shareable = caps.Providers.Where(ComputePoolShareFilter.IsShareable).ToList();
        var models = shareable
            .SelectMany(p => p.Models)
            .Select(m => new AiModelConfig { Name = m.Name, IsMain = false })
            .GroupBy(m => m.Name)
            .Select(g => g.First())
            .ToList();
        if (models.Count == 0)
            return;

        // 对端广播的层级（"1"/"2"/"3"）沿用到注册提供方，不再写死本地大模型
        var peerTier = AiModelTier.Tier2_Local;
        if (shareable.Count > 0
            && Enum.TryParse<AiModelTier>(shareable[0].Tier, out var parsedTier)
            && parsedTier != AiModelTier.Unset)
            peerTier = parsedTier;

        // 已存在且模型一致 → 跳过写入（避免每次刷新都动 DB）
        var existing = _aiConfig.GetProvider(providerId);
        if (existing != null)
        {
            var existingNames = existing.Models.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var newNames = models.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var expectedName = $"{caps.Name} · 局域网算力池";
            if (existingNames.SequenceEqual(newNames)
                && string.Equals(existing.AiBaseUrl?.TrimEnd('/'), caps.OpenAiBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.Name, expectedName, StringComparison.Ordinal)
                && existing.Tier == peerTier)
            {
                return;
            }
        }

        var setting = new AiProviderSetting
        {
            ProviderId = providerId,
            ProviderName = $"{caps.Name} · 局域网算力池",
            BaseUrl = caps.OpenAiBaseUrl.TrimEnd('/'),
            ModelsJson = AiConfigService.SerializeModels(models),
            IsMain = false,
            IsEnabled = true,
            Tier = (int)peerTier,
            SortOrder = 100
        };

        var localToken = _messageService.LocalToken;
        // 写收口：Provider 配置归 AI 模块所有，进程内经接口写入（API Key 由 AI 模块加密保存）
        try
        {
            _aiConfig.SaveProvider(setting, string.IsNullOrEmpty(localToken) ? null : localToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ComputePool] 注册对端提供方 {ProviderId} 失败", providerId);
            return;
        }
        _aiSettings.ClearAiProvidersCache();
        _logger.LogInformation("[ComputePool] 已注册对端提供方 {ProviderId} ({Name}): {ModelCount} 个模型, {Url}",
            providerId, caps.Name, models.Count, caps.OpenAiBaseUrl);
    }

    /// <summary>算力池总览（本机 + 对端）。</summary>
    public async Task<ComputePoolViewDto> GetPoolViewAsync(CancellationToken ct = default)
    {
        var localNode = await GetLocalNodeAsync();
        // 本机节点补上 AI 服务（shim 路由）的提供方，与 /mg/capabilities 广播保持一致
        // （同样只补可共享的本地算力，避免 peer- / 云端模型在本机节点重复出现）
        var aiProviders = await GetAiServiceProvidersAsync(ct);
        foreach (var remote in aiProviders)
        {
            if (!ComputePoolShareFilter.IsShareable(remote))
                continue;
            if (localNode.Providers.Any(g => string.Equals(g.Id, remote.Id, StringComparison.OrdinalIgnoreCase)))
                continue;
            localNode.Providers.Add(remote);
        }

        var nodes = new List<ComputePoolNodeDto> { localNode };
        var peers = await _messageService.ListPeersAsync(ct);
        var localServerId = GetLocalServerId();
        var localHostUrl = GetLocalHostUrl();

        foreach (var peer in peers)
        {
            // 指向本机的对端登记（历史遗留的自我登记）不展示，避免本机出现两次
            if (IsSelfPeer(peer, localServerId, localHostUrl))
                continue;

            _peerCapabilities.TryGetValue(peer.ServerId, out var caps);
            var registered = _aiConfig.GetProvider($"peer-{peer.ServerId}") != null;
            nodes.Add(new ComputePoolNodeDto
            {
                ServerId = peer.ServerId,
                PeerId = peer.Id,
                Name = caps?.Name ?? peer.Name,
                HostUrl = peer.BaseUrl,
                OpenAiBaseUrl = caps?.OpenAiBaseUrl ?? "",
                IsLocal = false,
                Online = caps != null
                    || (_peerReachable.TryGetValue(peer.ServerId, out var rt) && rt > DateTime.UtcNow.AddMinutes(-5))
                    || (peer.LastSeenUtc.HasValue && peer.LastSeenUtc.Value > DateTime.UtcNow.AddMinutes(-5)),
                LastSeenUtc = caps?.UpdatedAt ?? peer.LastSeenUtc,
                CpuCores = caps?.CpuCores,
                Providers = caps?.Providers.Where(ComputePoolShareFilter.IsShareable).ToList() ?? new List<ComputeProviderDto>(),
                Draw = caps?.Draw,
                ProviderRegistered = registered,
                ModelStore = caps?.ModelStore ?? new List<ModelStoreEntryDto>()
            });
        }

        return new ComputePoolViewDto { Nodes = nodes, UpdatedAt = DateTime.UtcNow };
    }

    /// <summary>选用某个节点+模型：把对端提供方设为本机主提供方并选主模型（写收口到 AI 服务）。</summary>
    public async Task<(bool ok, string? error)> SelectModelAsync(string serverId, string modelName, CancellationToken ct = default)
    {
        var providerId = $"peer-{serverId}";
        var provider = _aiConfig.GetProvider(providerId);
        if (provider == null)
            return (false, "该节点尚未注册为可选用提供方（对端需配置 BAIHUA_PUBLIC_OPENAI_BASE_URL）");
        var model = provider.Models.FirstOrDefault(m => m.Name == modelName);
        if (model == null)
            return (false, $"对端未提供模型 {modelName}");

        var setting = new AiProviderSetting
        {
            ProviderId = providerId,
            ProviderName = provider.Name,
            BaseUrl = provider.AiBaseUrl ?? "",
            ModelsJson = AiConfigService.SerializeModels(provider.Models.Select(m => new AiModelConfig
            {
                Name = m.Name,

                IsMain = m.Name == modelName
            }).ToList()),
            IsMain = true,
            IsEnabled = true,
            Tier = (int)provider.Tier,
            SortOrder = 0
        };
        try
        {
            _aiConfig.SaveProvider(setting, plainApiKey: null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存选用配置失败");
            return (false, "保存选用配置失败");
        }
        _aiSettings.ClearAiProvidersCache();
        return (true, null);
    }

    /// <summary>
    /// 从算力池页删除对端登记：删除登记 + 清理能力/在线缓存 + 移除该对端自动登记的对端提供方（peer- 前缀）。
    /// </summary>
    public async Task<(bool ok, string? error)> DeletePeerAsync(Guid peerId, CancellationToken ct = default)
    {
        var peer = await _messageService.GetPeerAsync(peerId, ct);
        if (peer == null)
            return (false, "对端登记不存在");

        var serverId = peer.ServerId;
        if (!await _messageService.DeletePeerAsync(peerId, ct))
            return (false, "删除对端登记失败");

        if (!string.IsNullOrEmpty(serverId))
        {
            _peerCapabilities.TryRemove(serverId, out _);
            _peerReachable.TryRemove(serverId, out _);
            var providerId = $"peer-{serverId}";
            if (_aiConfig.GetProvider(providerId) != null)
            {
                _aiConfig.DeleteProvider(providerId);
                _aiSettings.ClearAiProvidersCache();
                _logger.LogInformation("[ComputePool] 删除对端 {Name} 时同步移除提供方 {ProviderId}", peer.Name, providerId);
            }
        }
        _logger.LogInformation("[ComputePool] 已删除对端登记: {Name} ({ServerId})", peer.Name, serverId);
        return (true, null);
    }

    private async Task<ComputePoolNodeDto> GetLocalNodeAsync()
    {
        var settings = _serverAddress.GetSettings();
        var hostUrl = GetLocalHostUrl();
        var openAiBaseUrl = _configuration["BAIHUA_PUBLIC_OPENAI_BASE_URL"];
        if (string.IsNullOrWhiteSpace(openAiBaseUrl))
            openAiBaseUrl = $"{hostUrl}/mg/pool/v1"; // 统一推理网关（全网路由）

        // 绘图能力（用 3s 短超时探测本地 ComfyUI，避免阻塞算力池刷新）
        var drawOnline = await ProbeDrawOnlineAsync();

        return new ComputePoolNodeDto
        {
            ServerId = _serverAddress.GetServerInstanceId(),
            Name = string.IsNullOrWhiteSpace(settings.DisplayName) ? Environment.MachineName : settings.DisplayName,
            HostUrl = hostUrl,
            OpenAiBaseUrl = openAiBaseUrl,
            IsLocal = true,
            Online = true,
            LastSeenUtc = DateTime.UtcNow,
            CpuCores = Environment.ProcessorCount,
            Providers = _aiSettings.GetAiProviders()
                .Where(p => p.Models is { Count: > 0 })
                .Where(ComputePoolShareFilter.IsShareable) // 只展示本地算力：排除 peer- 对端登记与 Tier3 云端
                .Select(p => new ComputeProviderDto
                {
                    Id = p.Id,
                    Name = p.Name,
                    Tier = ((int)p.Tier).ToString(),
                    Models = p.Models.Select(m => new ComputeModelDto
                    {
                        Name = m.Name,
                        IsMain = m.IsMain,
                        TokensPerSecond = GetBenchmarkTps(m.Name)
                    }).ToList()
                })
                .ToList(),
            Draw = new DrawCapabilityDto
            {
                ComfyOnline = drawOnline,
                Image = drawOnline,
                Video = drawOnline,
                ImageCheckpoint = drawOnline ? ComfyWorkflowBuilder.DefaultImageCheckpoint : null,
                VideoCheckpoint = drawOnline ? ComfyWorkflowBuilder.DefaultVideoCheckpoint : null
            },
            ProviderRegistered = true
        };
    }

    /// <summary>短超时探测本机 ComfyUI 是否在线（用于算力池总览的绘图能力标识）。</summary>
    private async Task<bool> ProbeDrawOnlineAsync()
    {
        try
        {
            using var shortCt = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            return await _draw.IsAvailableAsync(shortCt.Token);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>排行榜里该模型最近一次实测 token/s（无记录返回 null）。</summary>
    private double? GetBenchmarkTps(string modelName)
    {
        try
        {
            return _benchmarkRepository.GetLeaderboard()
                .Where(h => string.Equals(h.ModelName, modelName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(h => h.LastTestedAt)
                .Select(h => h.AvgTokensPerSecond)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>跨机测速：对端运行单模型快速 benchmark，结果回写能力缓存并返回。</summary>
    public async Task<BenchmarkRunResultDto?> RunPeerBenchmarkAsync(string serverId, string modelName, CancellationToken ct = default)
    {
        // 本机模型 → 本地测速（经本机 /mg/benchmark/run 同一逻辑，直接调用）
        if (string.Equals(serverId, GetLocalServerId(), StringComparison.OrdinalIgnoreCase))
        {
            return await RunLocalBenchmarkAsync(modelName, ct);
        }

        var peer = (await _messageService.ListPeersAsync(ct)).FirstOrDefault(p => p.ServerId == serverId);
        if (peer == null) return null;

        try
        {
            using var client = _httpClientFactory.CreateClient("ComputePool");
            client.Timeout = TimeSpan.FromMinutes(10);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{peer.BaseUrl.TrimEnd('/')}/mg/benchmark/run");
            var token = !string.IsNullOrWhiteSpace(peer.Token) ? peer.Token! : _messageService.LocalToken;
            if (!string.IsNullOrEmpty(token))
                req.Headers.TryAddWithoutValidation("X-Server-Token", token);
            req.Content = JsonContent.Create(new PeerBenchmarkRequest { ModelName = modelName });

            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var result = await resp.Content.ReadFromJsonAsync<BenchmarkRunResultDto>(ct);
            if (result is { Success: true })
            {
                // 回写能力缓存：对端该模型的 TPS 更新
                if (_peerCapabilities.TryGetValue(peer.ServerId, out var caps))
                {
                    foreach (var p in caps.Providers)
                    foreach (var m in p.Models)
                    {
                        if (string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase))
                            m.TokensPerSecond = result.TokensPerSecond;
                    }
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "跨机测速失败 {Server} {Model}", serverId, modelName);
            return null;
        }
    }

    /// <summary>从对端拉取模型（模型商店）：下载 tar 流并解压到本机模型根目录。</summary>
    public async Task<(bool ok, string? error)> PullPeerModelAsync(string serverId, string modelName, CancellationToken ct = default)
    {
        var peer = (await _messageService.ListPeersAsync(ct)).FirstOrDefault(p => p.ServerId == serverId);
        if (peer == null) return (false, "对端未登记");
        if (string.IsNullOrWhiteSpace(modelName) || modelName.Contains('/') || modelName.Contains('\\') || modelName == "..")
            return (false, "非法的模型名");

        var root = GetLocalModelRoot();
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, modelName);
        if (Directory.Exists(target))
            return (false, $"本机已存在 {modelName}");

        try
        {
            using var client = _httpClientFactory.CreateClient("ComputePool");
            client.Timeout = TimeSpan.FromMinutes(60);
            var url = $"{peer.BaseUrl.TrimEnd('/')}/mg/model-store/download/{Uri.EscapeDataString(modelName)}";
            var token = !string.IsNullOrWhiteSpace(peer.Token) ? peer.Token! : _messageService.LocalToken;
            var (ok, error) = await ModelTarTransfer.DownloadAndExtractAsync(client, url, token, target, modelName, ct);
            if (!ok)
                return (false, error);
            _logger.LogInformation("[ComputePool] 已从 {Server} 拉取模型 {Model}", peer.Name, modelName);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "拉取模型失败 {Server} {Model}", serverId, modelName);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 跨机布署：本机已有模型 → 请求对端从本机 model-store 拉取并启动运行时（对端常驻服务）。
    /// 对端拉取完成后调用 ILocalRuntimeManager.StartAsync 启动推理（GPU 失败自动回退 CPU）。
    /// </summary>
    public async Task<DeployModelResultDto> DeployPeerModelAsync(string serverId, string modelName, string? device, CancellationToken ct = default)
    {
        // 本机布署到本机没有意义
        if (string.Equals(serverId, GetLocalServerId(), StringComparison.OrdinalIgnoreCase))
            return new DeployModelResultDto { Success = false, Error = "目标不能是本机", ModelName = modelName };

        var peer = (await _messageService.ListPeersAsync(ct)).FirstOrDefault(p => p.ServerId == serverId);
        if (peer == null)
            return new DeployModelResultDto { Success = false, Error = "对端未登记", ModelName = modelName };
        if (string.IsNullOrWhiteSpace(modelName) || modelName.Contains('/') || modelName.Contains('\\') || modelName == "..")
            return new DeployModelResultDto { Success = false, Error = "非法的模型名", ModelName = modelName };

        // 本机必须已有该模型（布署 = 把本机模型推给对端跑）
        var localRoot = GetLocalModelRoot();
        if (!Directory.Exists(Path.Combine(localRoot, modelName)))
            return new DeployModelResultDto { Success = false, Error = $"本机模型根中不存在 {modelName}（先在本机下载）", ModelName = modelName };

        // 本机对外下载地址：对端将从这里拉取 tar
        var hostIp = _configuration["BAIHUA_HOST_IP"];
        var localUrl = !string.IsNullOrWhiteSpace(hostIp)
            ? $"http://{hostIp}"
            : _serverAddress.GetLocalPublicBaseUrl();
        if (string.IsNullOrEmpty(localUrl))
            return new DeployModelResultDto { Success = false, Error = "无法确定本机对外地址（BAIHUA_HOST_IP 未配置）", ModelName = modelName };

        try
        {
            using var client = _httpClientFactory.CreateClient("ComputePool");
            client.Timeout = TimeSpan.FromMinutes(60);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{peer.BaseUrl.TrimEnd('/')}/mg/model-store/deploy");
            var token = !string.IsNullOrWhiteSpace(peer.Token) ? peer.Token! : _messageService.LocalToken;
            if (!string.IsNullOrEmpty(token))
                req.Headers.TryAddWithoutValidation("X-Server-Token", token);
            req.Content = JsonContent.Create(new PeerDeployRequest
            {
                SourceServerId = GetLocalServerId(),
                SourceUrl = $"{localUrl}/mg/model-store/download/{Uri.EscapeDataString(modelName)}",
                ModelName = modelName,
                Device = device
            });

            using var resp = await client.SendAsync(req, ct);
            var result = await resp.Content.ReadFromJsonAsync<DeployModelResultDto>(ct);
            if (result == null)
                return new DeployModelResultDto { Success = false, Error = $"对端返回 {(int)resp.StatusCode}", ModelName = modelName };
            if (result.Success)
                _logger.LogInformation("[ComputePool] 已布署 {Model} 到 {Server}（{Device} @ :{Port}）",
                    modelName, peer.Name, result.Device, result.Port);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "跨机布署失败 {Server} {Model}", serverId, modelName);
            return new DeployModelResultDto { Success = false, Error = ex.Message, ModelName = modelName };
        }
    }

    private async Task<BenchmarkRunResultDto?> RunLocalBenchmarkAsync(string modelName, CancellationToken ct)
    {
        // 复用对端端点逻辑：直接构造请求交给同一 controller 不可行，改为通过本机 HTTP 调用
        var hostIp = _configuration["BAIHUA_HOST_IP"];
        var localUrl = !string.IsNullOrWhiteSpace(hostIp)
            ? $"http://{hostIp}"
            : _serverAddress.GetLocalPublicBaseUrl();
        if (string.IsNullOrEmpty(localUrl)) return null;
        try
        {
            using var client = _httpClientFactory.CreateClient("ComputePool");
            client.Timeout = TimeSpan.FromMinutes(10);
            using var resp = await client.PostAsJsonAsync($"{localUrl}/mg/benchmark/run",
                new PeerBenchmarkRequest { ModelName = modelName }, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<BenchmarkRunResultDto>(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "本机测速失败 {Model}", modelName);
            return null;
        }
    }

    private string GetLocalServerId()
    {
        try { return _serverAddress.GetServerInstanceId(); }
        catch { return ""; }
    }

    /// <summary>本机对外 HTTP 入口（算力池展示/自我登记判断用）。</summary>
    private string GetLocalHostUrl()
    {
        var hostIp = _configuration["BAIHUA_HOST_IP"];
        return !string.IsNullOrWhiteSpace(hostIp)
            ? $"http://{hostIp}"
            : _serverAddress.GetLocalPublicBaseUrl();
    }

    /// <summary>
    /// 判断一条对端登记是否指向本机（历史遗留的自我登记）：
    /// ServerId 相同、入口地址同一主机（忽略端口差异）、或缓存能力实为本机。
    /// </summary>
    private bool IsSelfPeer(ServerPeer peer, string localServerId, string localHostUrl)
    {
        if (!string.IsNullOrEmpty(peer.ServerId) && !string.IsNullOrEmpty(localServerId)
            && string.Equals(peer.ServerId, localServerId, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrEmpty(peer.BaseUrl) && !string.IsNullOrEmpty(localHostUrl)
            && SameEndpoint(peer.BaseUrl, localHostUrl))
            return true;

        if (_peerCapabilities.TryGetValue(peer.ServerId, out var caps)
            && !string.IsNullOrEmpty(caps.ServerId) && !string.IsNullOrEmpty(localServerId)
            && string.Equals(caps.ServerId, localServerId, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>比较两个 URL 是否指向同一主机（http 默认 80 / https 默认 443 视为无端口）。</summary>
    private static bool SameEndpoint(string a, string b)
    {
        if (!Uri.TryCreate(a.TrimEnd('/'), UriKind.Absolute, out var ua)
            || !Uri.TryCreate(b.TrimEnd('/'), UriKind.Absolute, out var ub))
            return string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

        if (!string.Equals(ua.Scheme, ub.Scheme, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(ua.Host, ub.Host, StringComparison.OrdinalIgnoreCase))
            return false;
        var pa = ua.IsDefaultPort ? (ua.Scheme == "https" ? 443 : 80) : ua.Port;
        var pb = ub.IsDefaultPort ? (ub.Scheme == "https" ? 443 : 80) : ub.Port;
        return pa == pb;
    }

    private string GetLocalModelRoot()
    {
        // 与 ModelDownloadService 一致：DownloadDirectory 或 $BAIHUA_HOME/models
        var dl = _configuration["LocalAI:DownloadDirectory"];
        if (!string.IsNullOrWhiteSpace(dl)) return dl;
        var home = _configuration["BAIHUA_HOME"];
        return string.IsNullOrWhiteSpace(home) ? Path.Combine(Environment.CurrentDirectory, "models") : Path.Combine(home, "models");
    }


    /// <summary>网关路由：查找拥有该模型的节点（本机 AI 服务优先，其余按实测 TPS 从快到慢）。</summary>
    public async Task<(string baseUrl, string providerId, string name, bool isLocal, double? tps)?> FindBestNodeAsync(string modelName, CancellationToken ct = default)
    {
        var nodes = await FindCandidateNodesAsync(modelName, ct);
        return nodes.Count > 0 ? nodes[0] : null;
    }

    /// <summary>网关 failover：拥有该模型的所有候选节点（本机优先，对端按实测 TPS 降序）。</summary>
    public async Task<List<(string baseUrl, string providerId, string name, bool isLocal, double? tps)>> FindCandidateNodesAsync(string modelName, CancellationToken ct = default)
    {
        var result = new List<(string baseUrl, string providerId, string name, bool isLocal, double? tps)>();

        // 本机 AI 服务（shim）是否提供该模型
        var aiProviders = await GetAiServiceProvidersAsync(ct);
        var localAiProvider = aiProviders.FirstOrDefault(p => p.Models.Any(m => string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase)));
        if (localAiProvider != null)
        {
            var hostIp = _configuration["BAIHUA_HOST_IP"];
            var localHost = !string.IsNullOrWhiteSpace(hostIp) ? $"http://{hostIp}" : _serverAddress.GetLocalPublicBaseUrl();
            var localModel = localAiProvider.Models.First(m => string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase));
            result.Add(($"{localHost}/mg/ai/v1", localAiProvider.Id, "本机", true, localModel.TokensPerSecond));
        }

        // 对端：拥有该模型的节点按 TPS 降序
        var peers = new List<(string baseUrl, string providerId, string name, double? tps)>();
        foreach (var kv in _peerCapabilities)
        {
            var caps = kv.Value;
            if (string.IsNullOrWhiteSpace(caps.OpenAiBaseUrl)) continue;
            var p = caps.Providers.FirstOrDefault(p => p.Models.Any(m => string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase)));
            if (p == null) continue;
            var model = p.Models.First(m => string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase));
            peers.Add((caps.OpenAiBaseUrl, p.Id, caps.Name, model.TokensPerSecond));
        }
        result.AddRange(peers
            .OrderByDescending(c => c.tps.HasValue)
            .ThenByDescending(c => c.tps ?? 0)
            .Select(c => (c.baseUrl, c.providerId, c.name, false, c.tps)));

        return result;
    }

    private async Task<List<ComputeProviderDto>> GetAiServiceProvidersAsync(CancellationToken ct)
    {
        try
        {
            var aiBase = Environment.GetEnvironmentVariable("BAIHUA_AI_URL")
                ?? Environment.GetEnvironmentVariable("TASK_RUNNER_AI_API_URL")
                ?? "http://127.0.0.1:8791";
            using var client = _httpClientFactory.CreateClient("ComputePool");
            client.Timeout = TimeSpan.FromSeconds(8);
            var resp = await client.GetAsync($"{aiBase.TrimEnd('/')}/api/ai/config/providers", ct);
            if (!resp.IsSuccessStatusCode)
                return new List<ComputeProviderDto>();
            var providers = await resp.Content.ReadFromJsonAsync<List<AiProviderConfig>>(ct);
            return providers?
                .Where(p => p.Models is { Count: > 0 })
                .Select(p => new ComputeProviderDto
                {
                    Id = p.Id,
                    Name = p.Name,
                    Tier = ((int)p.Tier).ToString(),
                    Models = p.Models.Select(m => new ComputeModelDto
                    {
                        Name = m.Name,
                        IsMain = m.IsMain,
                        TokensPerSecond = GetBenchmarkTps(m.Name)
                    }).ToList()
                })
                .ToList() ?? new List<ComputeProviderDto>();
        }
        catch
        {
            return new List<ComputeProviderDto>();
        }
    }

    public void Dispose() => _timer?.Dispose();
}
