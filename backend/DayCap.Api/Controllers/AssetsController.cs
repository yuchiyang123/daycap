using DayCap.Api.Common;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DayCap.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/assets")]
public class AssetsController(IAssetService assets) : ControllerBase
{
    [HttpGet]
    public Task<AssetsView> Get([FromQuery] bool refresh, CancellationToken ct) => assets.GetAsync(User.GetUserId(), refresh, ct);

    [HttpPut]
    public Task<AssetsView> Save(SaveAssetsRequest req, CancellationToken ct) => assets.SaveAsync(User.GetUserId(), req, ct);

    [HttpGet("adjustments")]
    public Task<List<AssetAdjustmentView>> ListAdjustments([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        assets.ListAdjustmentsAsync(User.GetUserId(), from, to, ct);

    [HttpPost("adjustments")]
    public Task<AssetAdjustmentView> AddAdjustment(CreateAssetAdjustmentRequest req, CancellationToken ct) =>
        assets.AddAdjustmentAsync(User.GetUserId(), req, ct);

    [HttpDelete("adjustments/{adjustmentId:int}")]
    public async Task<IActionResult> DeleteAdjustment(int adjustmentId, CancellationToken ct)
    {
        await assets.DeleteAdjustmentAsync(User.GetUserId(), adjustmentId, ct);
        return NoContent();
    }
}
