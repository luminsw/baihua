using Baihua.Core;
using Baihua.Modules.Family.Services;
using System.Text.Json;
using Baihua.AI.Provider;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Baihua.Modules.Family.Models;
using Baihua.Contracts.Scene;
using Baihua.Contracts.Tasks;
using Baihua.Contracts.Vaults;

namespace Baihua.Modules.Family.Controllers
{
    public partial class TasksController : ControllerBase
    {
        [HttpPost("vault-generation")]
        public async Task<ActionResult<VaultGenerationResponse>> CreateVaultGenerationTask([FromBody] VaultGenerationRequest request)
        {
            // 数字助理：采集知识库生成（强兴趣信号）
            _activityService.Record("vault_gen", request.Industry + " " + request.Keyword);
            return await HandleCreateVaultGenerationTaskAsync(request);
        }
    }
}
