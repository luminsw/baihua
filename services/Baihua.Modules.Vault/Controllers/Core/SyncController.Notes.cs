using Baihua.Core;
using Microsoft.AspNetCore.Mvc;
using Baihua.Core.Services;

namespace Baihua.Modules.Vault.Controllers
{
    public partial class SyncController : ControllerBase
    {
        /// <summary>
        /// 更新笔记（带冲突检测）
        /// </summary>
        [HttpPut("notes")]
        public ActionResult<ConflictResolution> UpdateNote([FromQuery] string vaultId, [FromBody] NoteUpdateRequest request)
        {
            var vaultPath = ResolveVaultPath(vaultId);
            if (string.IsNullOrEmpty(vaultPath))
                return BadRequest(new { error = _loc["Vault_Required"] });

            if (!System.IO.Directory.Exists(vaultPath))
            {
                return BadRequest(new ConflictResolution
                {
                    Path = request.Path,
                    Status = "error",
                    Message = _loc["Vault_PathInvalid"]
                });
            }

            var result = UpdateNoteInternal(vaultPath, request);
            return Ok(result);
        }

        /// <summary>
        /// 批量更新笔记（带冲突检测）
        /// </summary>
        [HttpPut("notes/batch")]
        public ActionResult<List<ConflictResolution>> BatchUpdateNotes([FromQuery] string vaultId, [FromBody] List<NoteUpdateRequest> requests)
        {
            var vaultPath = ResolveVaultPath(vaultId);
            if (string.IsNullOrEmpty(vaultPath))
                return BadRequest(new { error = _loc["Vault_Required"] });

            var results = new List<ConflictResolution>();

            foreach (var request in requests)
            {
                var result = UpdateNoteInternal(vaultPath, request);
                results.Add(result);
            }

            return Ok(results);
        }
    }
}
