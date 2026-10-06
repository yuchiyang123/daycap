using DayCap.Api.Common;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DayCap.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/assets")]
public class AssetsController(IAssetService assets, IAutoMoneyService auto) : ControllerBase
{
    /// <summary>打開資產頁時先補做到期的自動轉帳 / 定期定額，看到的就是最新的。</summary>
    [HttpGet]
    public async Task<AssetsView> Get([FromQuery] bool refresh, CancellationToken ct)
    {
        await auto.RunAsync(User.GetUserId(), ct);
        return await assets.GetAsync(User.GetUserId(), refresh, ct);
    }

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
