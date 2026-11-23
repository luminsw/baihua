using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.SignalR;

namespace Baihua.Core.Hubs
{
    /// <summary>
    /// 任务进度推送 Hub
    /// </summary>
    public class TaskProgressHub : Hub
    {
        private readonly ILogger<TaskProgressHub> _logger;

        public TaskProgressHub(ILogger<TaskProgressHub> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 客户端连接时
        /// </summary>
        public override async Task OnConnectedAsync()
        {
            _logger.LogInformation("客户端连接：{ConnectionId}", Context.ConnectionId);
            await base.OnConnectedAsync();
        }

        /// <summary>
        /// 客户端断开时
        /// </summary>
        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            _logger.LogInformation("客户端断开：{ConnectionId}", Context.ConnectionId);
            await base.OnDisconnectedAsync(exception);
        }
    }
}
