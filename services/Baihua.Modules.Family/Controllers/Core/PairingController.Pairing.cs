using Microsoft.AspNetCore.Mvc;
using Baihua.Modules.Family.Services;
using Baihua.Contracts.Pairing;

namespace Baihua.Modules.Family.Controllers;

public partial class PairingController : ControllerBase
{
    private IActionResult HandleGetQRCode()
    {
        var (url, hostName) = _serverAddressService.GetQrCodeAddresses();
        
        var serverInstanceId = _serverAddressService.GetServerInstanceId();
        var (baseUrl, name, qrCodeData) = _pairingService.GenerateQRCodeContent(
            url, hostName, serverInstanceId);

        return Ok(new ServerQRResponse
        {
            Url = baseUrl,
            HostName = name,
            ServerId = serverInstanceId,
            DeviceId = serverInstanceId,
            QrCodeData = qrCodeData
        });
    }
}
