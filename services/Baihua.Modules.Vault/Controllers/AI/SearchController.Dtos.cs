using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace Baihua.Modules.Vault.Controllers;

    public class ReindexRequest
    {
        public string VaultId { get; set; } = "";
    }
