using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Core;
using Microsoft.AspNetCore.Mvc;
using Baihua.Modules.Family.Services;
using Baihua.Contracts.Core;

namespace Baihua.Modules.Family.Controllers
{
    [ApiController]
    [Route("api/server-address")]
    public class ServerAddressController : ControllerBase
    {
        private readonly ServerAddressService _serverAddressService;
        private readonly ILogger<ServerAddressController> _logger;

        public ServerAddressController(
            ServerAddressService serverAddressService,
            ILogger<ServerAddressController> logger)
        {
            _serverAddressService = serverAddressService;
            _logger = logger;
        }

        [HttpGet]
        public IActionResult GetSettings()
        {
            var settings = _serverAddressService.GetSettings();
            var (url, hostName) = _serverAddressService.GetQrCodeAddresses();

            return Ok(new ServerAddressResponse
            {
                Domain = settings.Domain,
                Url = settings.Url,
                ActualUrl = url,
                HostName = hostName,
                DisplayName = settings.DisplayName
            });
        }

        [HttpPost]
        public async Task<IActionResult> UpdateSettings([FromBody] UpdateServerAddressRequest request)
        {
            try
            {
                var settings = await _serverAddressService.UpdateSettings(request.Domain ?? "", request.DisplayName ?? "");

                var (url, hostName) = _serverAddressService.GetQrCodeAddresses();

                return Ok(new ServerAddressResponse
                {
                    Domain = settings.Domain,
                    Url = settings.Url,
                    ActualUrl = url,
                    HostName = hostName,
                    DisplayName = settings.DisplayName
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新服务器地址配置失败");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }
    }
}
