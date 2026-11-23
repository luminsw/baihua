
using Baihua.Core.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

using Baihua.Core.Modules;
using Baihua.Core.Services;
namespace Baihua.Modules.Vault.Controllers;
    [ApiController]
    [Route("api/[controller]")]
    public partial class SearchController : ControllerBase
    {
        private readonly IVaultQueryService _vaultQuery;
        private readonly VaultSettingsService _vaultSettings;
        private readonly EmbeddingService _embeddingService;
        private readonly VaultNoteIndexer _vaultNoteIndexer;
        private readonly ILogger<SearchController> _logger;
        private readonly IStringLocalizer<SharedResources> _loc;

        public SearchController(
            IVaultQueryService vaultQuery,
            VaultSettingsService vaultSettings,
            EmbeddingService embeddingService,
            VaultNoteIndexer vaultNoteIndexer,
            ILogger<SearchController> logger,
            IStringLocalizer<SharedResources> loc)
        {
            _vaultQuery = vaultQuery;
            _vaultSettings = vaultSettings;
            _embeddingService = embeddingService;
            _vaultNoteIndexer = vaultNoteIndexer;
            _logger = logger;
            _loc = loc;
        }

    }
