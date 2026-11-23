using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;
using Baihua.Data;
using Baihua.Data.Entities;

namespace Baihua.Modules.Family.Controllers;

[ApiController]
[Route("api/master/{masterId}/[controller]")]
public class VaultFocusController : ControllerBase
{
    private readonly IDbContextFactory<FamilyDbContext> _dbFactory;
    private readonly ILogger<VaultFocusController> _logger;
    private readonly IStringLocalizer<SharedResources> _loc;

    public VaultFocusController(IDbContextFactory<FamilyDbContext> dbFactory, ILogger<VaultFocusController> logger, IStringLocalizer<SharedResources> loc)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        _loc = loc;
    }

    [HttpGet]
    public async Task<ActionResult> GetFocusedVaults(string masterId)
    {
        if (string.IsNullOrWhiteSpace(masterId))
            return BadRequest(new { Success = false, Message = _loc["VaultFocus_MasterIdEmpty"] });

        await using var db = await _dbFactory.CreateDbContextAsync();
        var states = await db.VaultFocusStates
            .Where(v => v.MasterId == masterId && v.State == "focused")
            .OrderByDescending(v => v.UpdatedAt)
            .Select(v => new { v.VaultId, v.State, v.StageName, v.UpdatedAt })
            .ToListAsync();

        return Ok(new { Success = true, Vaults = states });
    }

    [HttpPost("focus")]
    public async Task<ActionResult> FocusVault(string masterId, [FromBody] FocusVaultRequest request)
    {
        if (string.IsNullOrWhiteSpace(masterId))
            return BadRequest(new { Success = false, Message = _loc["VaultFocus_MasterIdEmpty"] });
        if (string.IsNullOrWhiteSpace(request.VaultId))
            return BadRequest(new { Success = false, Message = _loc["VaultFocus_VaultIdEmpty"] });

        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.VaultFocusStates
            .FirstOrDefaultAsync(v => v.MasterId == masterId && v.VaultId == request.VaultId);

        if (existing != null)
        {
            existing.State = "focused";
            existing.StageName = request.StageName;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            db.VaultFocusStates.Add(new VaultFocusState
            {
                MasterId = masterId,
                VaultId = request.VaultId,
                State = "focused",
                StageName = request.StageName
            });
        }

        await db.SaveChangesAsync();
        return Ok(new { Success = true });
    }

    [HttpPost("archive")]
    public async Task<ActionResult> ArchiveVault(string masterId, [FromBody] FocusVaultRequest request)
    {
        if (string.IsNullOrWhiteSpace(masterId))
            return BadRequest(new { Success = false, Message = _loc["VaultFocus_MasterIdEmpty"] });
        if (string.IsNullOrWhiteSpace(request.VaultId))
            return BadRequest(new { Success = false, Message = _loc["VaultFocus_VaultIdEmpty"] });

        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.VaultFocusStates
            .FirstOrDefaultAsync(v => v.MasterId == masterId && v.VaultId == request.VaultId);

        if (existing != null)
        {
            existing.State = "archived";
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            db.VaultFocusStates.Add(new VaultFocusState
            {
                MasterId = masterId,
                VaultId = request.VaultId,
                State = "archived",
                StageName = request.StageName
            });
        }

        await db.SaveChangesAsync();
        return Ok(new { Success = true });
    }

    [HttpGet("all")]
    public async Task<ActionResult> GetAllVaultStates(string masterId)
    {
        if (string.IsNullOrWhiteSpace(masterId))
            return BadRequest(new { Success = false, Message = _loc["VaultFocus_MasterIdEmpty"] });

        await using var db = await _dbFactory.CreateDbContextAsync();
        var states = await db.VaultFocusStates
            .Where(v => v.MasterId == masterId)
            .OrderBy(v => v.State)
            .ThenByDescending(v => v.UpdatedAt)
            .Select(v => new { v.VaultId, v.State, v.StageName, v.UpdatedAt })
            .ToListAsync();

        return Ok(new { Success = true, Vaults = states });
    }
}

public class FocusVaultRequest
{
    public string VaultId { get; set; } = "";
    public string? StageName { get; set; }
}
