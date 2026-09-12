using Baihua.Contracts.ServerMessaging;
using Baihua.Modules.Family.Services.ServerMessaging;
using Microsoft.AspNetCore.Mvc;

namespace Baihua.Modules.Family.Controllers.ServerMessaging;

/// <summary>
/// 百花服务器互联——消息收发。
/// - /api/server-msg/*：WebUI 管理端点（发送 / 列表）
/// - /mg/server-msg/inbox：对端服务器推送消息的接收端点（X-Server-Token 鉴权）
/// </summary>
[ApiController]
public class ServerMessagesController : ControllerBase
{
    private readonly ServerMessageService _service;
    private readonly ILogger<ServerMessagesController> _logger;

    public ServerMessagesController(ServerMessageService service, ILogger<ServerMessagesController> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <summary>WebUI 发送消息到对端。</summary>
    [HttpPost("/api/server-msg/send")]
    public async Task<ActionResult<ServerMessageSendResult>> Send([FromBody] ServerMessageSendRequest request, CancellationToken ct)
    {
        if (request == null || request.PeerId == Guid.Empty)
            return BadRequest(new { error = "缺少对端" });
        var result = await _service.SendMessageAsync(request.PeerId, request.Content ?? "", ct);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>与某对端的双向消息列表。</summary>
    [HttpGet("/api/server-msg/list")]
    public async Task<ActionResult<List<ServerMessageDto>>> List([FromQuery] Guid peerId, CancellationToken ct)
    {
        if (peerId == Guid.Empty)
            return BadRequest(new { error = "缺少对端" });
        var messages = await _service.ListMessagesAsync(peerId, ct);
        return Ok(messages.Select(ServerMessageService.MapMessage).ToList());
    }

    /// <summary>对端服务器推送消息（X-Server-Token 鉴权）。</summary>
    [HttpPost("/mg/server-msg/inbox")]
    public async Task<IActionResult> Inbox([FromBody] ServerMessageInboxRequest request, CancellationToken ct)
    {
        var token = Request.Headers["X-Server-Token"].FirstOrDefault();
        var (ok, error) = await _service.ReceiveMessageAsync(request ?? new ServerMessageInboxRequest(), token, ct);
        if (!ok)
        {
            _logger.LogWarning("[ServerMsg] inbox rejected: {Error}", error);
            return error == "口令校验失败" ? Unauthorized(new { error }) : BadRequest(new { error });
        }
        _logger.LogInformation("[ServerMsg] inbox received from {From} ({FromServerId}): {Len} chars",
            request?.FromName, request?.FromServerId, request?.Content?.Length ?? 0);
        return Ok(new { success = true });
    }
}
