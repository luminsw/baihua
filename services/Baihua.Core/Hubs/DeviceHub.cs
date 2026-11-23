using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.SignalR;

namespace Baihua.Core.Hubs
{
    /// <summary>
    /// 设备配对Hub - 简化版
    /// </summary>
    public class DeviceHub : Hub
    {
        private readonly ILogger<DeviceHub> _logger;

        public DeviceHub(ILogger<DeviceHub> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 客户端连接时
        /// </summary>
        public override async Task OnConnectedAsync()
        {
            _logger.LogInformation("客户端连接: {ConnectionId}", Context.ConnectionId);
            await base.OnConnectedAsync();
        }

        /// <summary>
        /// 客户端断开时
        /// </summary>
        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            _logger.LogInformation("客户端断开: {ConnectionId}", Context.ConnectionId);
            await base.OnDisconnectedAsync(exception);
        }


    }
}
