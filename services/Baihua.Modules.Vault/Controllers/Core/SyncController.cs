using Baihua.Core;
using Microsoft.AspNetCore.Mvc;
using Baihua.Core.Modules;
using Baihua.Core.Services;
using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;

namespace Baihua.Modules.Vault.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public partial class SyncController : ControllerBase
    {
        private readonly VaultSettingsService _vaultSettings;
        private readonly TaskManager _taskManager;
        private readonly IVaultQueryService _vaultQuery;
        private readonly ILogger<SyncController> _logger;
        private readonly IStringLocalizer<SharedResources> _loc;

        public SyncController(
            VaultSettingsService vaultSettings,
            TaskManager taskManager,
            IVaultQueryService vaultQuery,
            ILogger<SyncController> logger,
            IStringLocalizer<SharedResources> loc)
        {
            _vaultSettings = vaultSettings;
            _taskManager = taskManager;
            _vaultQuery = vaultQuery;
            _logger = logger;
            _loc = loc;
        }

        /// <summary>
        /// 获取所有笔记的元数据列表（用于移动端同步）
        /// </summary>
        [HttpGet("notes")]
        public ActionResult<List<NoteMetadata>> GetNotes([FromQuery] string vaultId)
        {
            var vaultPath = ResolveVaultPath(vaultId);
            if (string.IsNullOrEmpty(vaultPath))
                return BadRequest(new { error = _loc["Vault_Required"] });

            var notes = GetNotesInternal(vaultPath, null);
            return Ok(notes);
        }

        /// <summary>
        /// 获取所有任务列表（用于移动端同步）
        /// </summary>
        [HttpGet("tasks")]
        public ActionResult GetTasks()
        {
            var tasks = GetTasksInternal(null);
            return Ok(new { tasks, total = tasks.Count });
        }

        /// <summary>
        /// 完整的同步数据（笔记 + 任务）
        /// </summary>
        [HttpGet]
        public ActionResult<SyncResponse> Sync([FromQuery] string vaultId, [FromQuery] long? since = null)
        {
            var vaultPath = ResolveVaultPath(vaultId);
            if (string.IsNullOrEmpty(vaultPath))
                return BadRequest(new { error = _loc["Vault_Required"] });

            var notes = GetNotesInternal(vaultPath, null);
            var tasks = GetTasksInternal(null);

            return Ok(new SyncResponse
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Notes = notes,
                Tasks = tasks,
                TotalNotes = notes.Count,
                TotalTasks = tasks.Count
            });
        }

        /// <summary>
        /// 获取系统信息（用于移动端配置）
        /// </summary>
        [HttpGet("system")]
        public ActionResult<SystemInfo> GetSystemInfo([FromQuery] string vaultId)
        {
            var vaultPath = ResolveVaultPath(vaultId);
            if (string.IsNullOrEmpty(vaultPath))
                return BadRequest(new { error = _loc["Vault_Required"] });

            return Ok(new SystemInfo
            {
                ServerVersion = "1.0.0",
                VaultPath = vaultPath,
                VaultExists = !string.IsNullOrEmpty(vaultPath) && System.IO.Directory.Exists(vaultPath),
                VaultFileCount = !string.IsNullOrEmpty(vaultPath) && System.IO.Directory.Exists(vaultPath)
                    ? System.IO.Directory.GetFiles(vaultPath, "*.md", System.IO.SearchOption.AllDirectories).Length
                    : 0,
                ServiceUptime = GetUptime(),
                ApiBaseUrl = $"{Request.Scheme}://{Request.Host}",
                SupportedFeatures = new List<string> { "notes", "tasks", "search", "ai", "conflict-resolution" }
            });
        }
    }
}
