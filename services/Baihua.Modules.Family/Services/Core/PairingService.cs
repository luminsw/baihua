using Baihua.Core.Models;
using Baihua.Core.Services;
using System.Security.Cryptography;

namespace Baihua.Modules.Family.Services
{
    /// <summary>
    /// 配对服务 - 简化版，只负责生成二维码
    /// </summary>
    public class PairingService : IDisposable
    {
        private readonly ILogger<PairingService> _logger;
        private readonly ServerAddressService _serverAddressService;

        public PairingService(ILogger<PairingService> logger, ServerAddressService serverAddressService)
        {
            _logger = logger;
            _serverAddressService = serverAddressService;
        }

        /// <summary>
        /// 生成二维码内容（统一单地址模型）
        /// 包含 sharedSecret，移动端扫码后立即可签名请求，无需等 registerViaHttp 异步完成
        /// </summary>
        public (string url, string hostName, string qrCodeData) GenerateQRCodeContent(
            string url, string hostName, string? deviceId = null)
        {
            var serverId = deviceId ?? $"server-{Guid.NewGuid():N}";
            var qrCodeData = System.Text.Json.JsonSerializer.Serialize(new
            {
                serverId = serverId,
                baseUrl = url,
                hostName = hostName,
                deviceId = deviceId,
                sharedSecret = _serverAddressService.GetSharedSecret()
            });
            
            return (url, hostName, qrCodeData);
        }

        public void Dispose()
        {
        }
    }
}
