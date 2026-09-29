using DayCap.Api.Common;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DayCap.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/settings")]
public class SettingsController(ISettingsService settings) : ControllerBase
{
    [HttpGet]
    public Task<SettingsView> Get(CancellationToken ct) => settings.GetAsync(User.GetUserId(), ct);

    /// <summary>新增一筆設定版本：一般從明天起生效；帶 CorrectionFrom 是更正過去（必須附原因）。</summary>
    [HttpPut]
    public Task<SettingsView> Save(SaveSettingsRequest req, CancellationToken ct) => settings.SaveAsync(User.GetUserId(), req, ct);

    /// <summary>設定頁即時合計（不存檔）。</summary>
    [HttpPost("estimate")]
    public Task<SettingsEstimate> Estimate(SettingsDto draft, CancellationToken ct) => settings.EstimateAsync(User.GetUserId(), draft, ct);

    /// <summary>
    /// §8.2 分配器預覽：把格子交給 Allocator.Allocate。分配器由使用者實作（§8.1），
    /// 還沒實作時回 501，前端顯示「分配器尚未實作」。
    /// </summary>
    [HttpPost("allocate-preview")]
    public IActionResult AllocatePreview(AllocatePreviewRequest req)
    {
        if (req.Cells.Count > 100) return BadRequest(new { title = "格子太多了。" });
        try
        {
            return Ok(Services.Allocation.Allocator.Allocate(req.Budget, req.Cells, req.RoundingUnit <= 0 ? 1 : req.RoundingUnit, req.ReleasedShareReceiverKey));
        }
        catch (NotImplementedException)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new { title = "Not Implemented", detail = "分配器尚未實作（§8.1 由你實作 Allocator.Allocate）。" });
        }
    }
}
