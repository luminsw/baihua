using Baihua.Core;
using Baihua.Modules.Family.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Baihua.Contracts.Ai;
using Baihua.Modules.Family.Models;

namespace Baihua.Modules.Family.Controllers
{
    public partial class AIController
    {
        [HttpPost("generate-missing-note")]
        public async Task<ActionResult<GenerateMissingNoteResponse>> GenerateMissingNote([FromBody] GenerateMissingNoteRequest request)
            => await HandleGenerateMissingNoteAsync(request);
    }
}
