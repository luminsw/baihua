using Microsoft.AspNetCore.SignalR.Client;

namespace Baihua.Web.Services;

/// <summary>
/// 全局状态服务 - 单一状态源
/// 通过 SignalR 实时接收状态变更推送，也订阅本地服务事件，并支持定期轮询作为备用
/// </summary>
public class GlobalStateService : IDisposable
{
    private readonly AIStatusService _aiStatusService;
    private readonly VaultStatusService _vaultStatusService;
    private readonly ILogger<GlobalStateService> _logger;

    // 当前状态
    private AIStatusSummary? _aiStatus;
    private VaultStatusSummary? _vaultStatus;

    // 状态变更事件
    public event EventHandler? StateChanged;

    // SignalR
    private HubConnection? _hubConnection;
    private bool _isConnecting = false;
    private readonly object _connectLock = new object();

    // 刷新锁：使用 SemaphoreSlim 排队等待，确保不丢失刷新请求
    private readonly SemaphoreSlim _refreshSemaphore = new SemaphoreSlim(1, 1);


    public GlobalStateService(
        AIStatusService aiStatusService,
        VaultStatusService vaultStatusService,
        ILogger<GlobalStateService> logger)
    {
        _aiStatusService = aiStatusService;
        _vaultStatusService = vaultStatusService;
        _logger = logger;

        // 订阅本地服务事件：即使 SignalR 不可用，同一页面内的配置变更也能触发刷新
        _aiStatusService.StateChanged += OnLocalStateChanged;
        _vaultStatusService.StateChanged += OnLocalStateChanged;
    }

    /// <summary>
    /// 获取AI状态（同步，立即返回当前状态）
    /// </summary>
    public AIStatusSummary? GetAIStatus() => _aiStatus;

    /// <summary>
    /// 初始化：加载状态、启动 SignalR 和定期轮询（非阻塞）
    /// </summary>
    public void Initialize(string hubUrl)
    {
        _ = InitializeAsync(hubUrl);
    }

    private async Task InitializeAsync(string hubUrl)
    {
        // 先刷新一次状态，确保徽章有初始值
        await RefreshAsync();
        // 再建立 SignalR 连接，连接后推送的变更会触发后续刷新
        await EnsureSignalRAsync(hubUrl);

    }

    /// <summary>
    /// 本地服务事件处理器（AIStatusService / VaultStatusService 状态变更时触发）
    /// </summary>
    private void OnLocalStateChanged(object? sender, EventArgs e)
    {
        _logger.LogInformation("[GlobalStateService] 收到本地状态变更事件，触发刷新");
        _ = RefreshAsync();
    }

    /// <summary>
    /// 刷新所有状态（手动刷新）
    /// 使用 SemaphoreSlim 排队等待，确保不丢失刷新请求
    /// </summary>
    public async Task RefreshAsync()
    {
        await _refreshSemaphore.WaitAsync();
        try
        {
            _logger.LogInformation("[GlobalStateService] 开始刷新状态");

            var aiStatus = await _aiStatusService.GetStatusSummaryAsync();
            var vaultStatus = await _vaultStatusService.GetStatusSummaryAsync();

            _aiStatus = aiStatus;
            _vaultStatus = vaultStatus;

            _logger.LogInformation("[GlobalStateService] 状态刷新完成: AI={IsConfigured}, Vault={VaultConfigured}",
                _aiStatus?.IsConfigured, _vaultStatus?.IsConfigured);

            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GlobalStateService] 刷新状态失败");
        }
        finally
        {
            _refreshSemaphore.Release();
        }
    }

    private async Task EnsureSignalRAsync(string hubUrl)
    {
        lock (_connectLock)
        {
            if (_isConnecting || _hubConnection?.State == HubConnectionState.Connected)
                return;
            _isConnecting = true;
        }

        try
        {
            _hubConnection = new HubConnectionBuilder()
                .WithUrl(hubUrl)
                .WithAutomaticReconnect()
                .Build();

            _hubConnection.On("AIStatusChanged", async () =>
            {
                _logger.LogInformation("[GlobalStateService] 收到 SignalR AIStatusChanged 推送");
                await RefreshAsync();
            });

            _hubConnection.On("VaultStatusChanged", async () =>
            {
                _logger.LogInformation("[GlobalStateService] 收到 SignalR VaultStatusChanged 推送");
                await RefreshAsync();
            });

            await _hubConnection.StartAsync();
            _logger.LogInformation("[GlobalStateService] SignalR 已连接: {HubUrl}", hubUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GlobalStateService] SignalR 连接失败，将依赖本地事件和定期轮询");
        }
        finally
        {
            lock (_connectLock)
            {
                _isConnecting = false;
            }
        }
    }

    public void Dispose()
    {
        _aiStatusService.StateChanged -= OnLocalStateChanged;
        _vaultStatusService.StateChanged -= OnLocalStateChanged;
        try
        {
            _hubConnection?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch { /* ignore dispose errors */ }
    }
}
