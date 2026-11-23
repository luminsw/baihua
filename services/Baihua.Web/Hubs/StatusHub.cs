using Microsoft.AspNetCore.SignalR;

namespace Baihua.Web.Hubs;

/// <summary>
/// SignalR Hub 用于实时推送状态更新
/// </summary>
public class StatusHub : Hub
{
    private readonly ILogger<StatusHub> _logger;

    public StatusHub(ILogger<StatusHub> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 客户端连接时
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("SignalR 客户端已连接: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// 客户端断开时
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("SignalR 客户端已断开: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// 状态更新服务 - 用于向所有客户端推送状态变更
/// </summary>
public class StatusUpdateService
{
    private readonly IHubContext<StatusHub> _hubContext;
    private readonly ILogger<StatusUpdateService> _logger;

    public StatusUpdateService(IHubContext<StatusHub> hubContext, ILogger<StatusUpdateService> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <summary>
    /// 通知所有客户端知识库状态已变更
    /// </summary>
    public async Task NotifyVaultStatusChangedAsync()
    {
        _logger.LogInformation("推送知识库状态变更到所有客户端");
        await _hubContext.Clients.All.SendAsync("VaultStatusChanged");
    }

    /// <summary>
    /// 通知所有客户端 AI 状态已变更
    /// </summary>
    public async Task NotifyAIStatusChangedAsync()
    {
        _logger.LogInformation("推送 AI 状态变更到所有客户端");
        await _hubContext.Clients.All.SendAsync("AIStatusChanged");
    }
}
