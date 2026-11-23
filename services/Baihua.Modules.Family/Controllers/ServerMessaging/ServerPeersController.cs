using Baihua.Contracts.ServerMessaging;
using Baihua.Modules.Family.Services.ServerMessaging;
using Microsoft.AspNetCore.Mvc;

namespace Baihua.Modules.Family.Controllers.ServerMessaging;

/// <summary>
/// 百花服务器互联——对端管理（WebUI 管理端点，受 Admin 网络策略保护）。
/// </summary>
[ApiController]
[Route("api/server-peers")]
public class ServerPeersController : ControllerBase
{
    private readonly ServerMessageService _service;

    public ServerPeersController(ServerMessageService service) => _service = service;

    [HttpGet]
    public async Task<ActionResult<List<ServerPeerDto>>> List(CancellationToken ct)
    {
        var peers = await _service.ListPeersAsync(ct);
        return Ok(peers.Select(ServerMessageService.MapPeer).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<ServerPeerDto>> Add([FromBody] ServerPeerSaveRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Name) || string.IsNullOrWhiteSpace(request.BaseUrl))
            return BadRequest(new { error = "名称和服务器地址不能为空" });
        if (!Uri.TryCreate(request.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out _))
            return BadRequest(new { error = "服务器地址格式不正确" });

        var peer = await _service.AddPeerAsync(request, ct);
        return Ok(ServerMessageService.MapPeer(peer));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var ok = await _service.DeletePeerAsync(id, ct);
        return ok ? Ok(new { success = true }) : NotFound(new { error = "对端不存在" });
    }
}
