using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Core;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Baihua.Modules.Family.Services;

namespace Baihua.Modules.Family.Controllers
{
    /// <summary>
    /// 配对控制器 - 简化版，只提供二维码
    /// </summary>
    [ApiController]
    [Route("api/pairing")]
    public partial class PairingController : ControllerBase
    {
        private readonly PairingService _pairingService;
        private readonly ServerAddressService _serverAddressService;
        private readonly ILogger<PairingController> _logger;

        public PairingController(
            PairingService pairingService, 
            ServerAddressService serverAddressService,
            ILogger<PairingController> logger)
        {
            _pairingService = pairingService;
            _serverAddressService = serverAddressService;
            _logger = logger;
        }

        /// <summary>
        /// 生成二维码(WebUI调用)
        /// </summary>
        [HttpGet("qrcode")]
        public IActionResult GetQRCode()
            => HandleGetQRCode();
    }
}
