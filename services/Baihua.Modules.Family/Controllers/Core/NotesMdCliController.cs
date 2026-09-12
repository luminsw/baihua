using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Baihua.Contracts.Vaults;
using Baihua.Core.Localization;
using Baihua.Modules.Family.Services;
using Baihua.Contracts.Core;

namespace Baihua.Modules.Family.Controllers
{
    [ApiController]
    [Route("api/notesmd-cli")]
    public class NotesMdCliController : ControllerBase
    {
        private readonly NotesMdCliService _notesMdCliService;
        private readonly IStringLocalizer<SharedResources> _loc;

        public NotesMdCliController(NotesMdCliService notesMdCliService, IStringLocalizer<SharedResources> loc)
        {
            _notesMdCliService = notesMdCliService;
            _loc = loc;
        }

        /// <summary>
        /// 获取 notesmd-cli 状态及已注册的 vault 路径列表。
        /// </summary>
        [HttpGet("status")]
        public IActionResult GetStatus()
        {
            var available = _notesMdCliService.IsAvailable();
            if (!available)
            {
                return Ok(new { available = false });
            }

            var registeredPaths = _notesMdCliService.GetRegisteredVaultPaths();
            return Ok(new { available = true, registeredPaths });
        }

        /// <summary>
        /// 添加单个 vault 到 notesmd-cli。
        /// </summary>
        [HttpPost("add-vault")]
        public IActionResult AddVault([FromBody] Baihua.Contracts.Vaults.AddVaultRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Path))
            {
                return BadRequest(new { success = false, error = _loc["NotesMdCli_PathEmpty"] });
            }

            var path = request.Path.Trim();
            if (!Directory.Exists(path))
            {
                return BadRequest(new { success = false, error = _loc["NotesMdCli_DirNotFound"] });
            }

            var success = _notesMdCliService.AddVault(path);
            if (success)
            {
                return Ok(new { success = true, path });
            }

            return StatusCode(500, new { success = false, error = _loc["NotesMdCli_AddFailed"] });
        }

        /// <summary>
        /// 批量添加 vaults 到 notesmd-cli。
        /// </summary>
        [HttpPost("batch-add")]
        public IActionResult BatchAdd([FromBody] NotesMdBatchAddRequest request)
        {
            if (request?.Paths == null || request.Paths.Count == 0)
            {
                return BadRequest(new { success = false, error = _loc["NotesMdCli_PathsEmpty"] });
            }

            var validPaths = request.Paths
                .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p.Trim()))
                .Select(p => p.Trim())
                .ToList();

            if (validPaths.Count == 0)
            {
                return BadRequest(new { success = false, error = _loc["NotesMdCli_NoValidPaths"] });
            }

            var (succeeded, failed) = _notesMdCliService.BatchAddVaults(validPaths);
            return Ok(new
            {
                success = failed.Count == 0,
                succeededCount = succeeded.Count,
                failedCount = failed.Count,
                succeeded,
                failed
            });
        }
    }
}
