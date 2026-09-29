using DayCap.Api.Common;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DayCap.Api.Controllers;

/// <summary>信任與安全（§21）：匯出、刪除帳號、修改紀錄、登入裝置。</summary>
[ApiController]
[Authorize]
[Route("api")]
public class TrustController(IUserDataService data, ISessionService sessions, IAppClock clock) : ControllerBase
{
    private string? Device => HttpContext.Items[SessionService.DeviceCookie] as string
                              ?? (Request.Cookies.TryGetValue(SessionService.DeviceCookie, out var d) ? d : null);

    [HttpGet("me/export")]
    public async Task<IActionResult> Export(CancellationToken ct) =>
        File(await data.ExportJsonAsync(User.GetUserId(), ct), "application/json", $"daycap-{clock.Today:yyyyMMdd}.json");

    [HttpGet("me/export/entries.csv")]
    public async Task<IActionResult> ExportCsv(CancellationToken ct) =>
        File(await data.ExportEntriesCsvAsync(User.GetUserId(), ct), "text/csv; charset=utf-8", $"daycap-entries-{clock.Today:yyyyMMdd}.csv");

    [HttpPost("me/delete")]
    public async Task<IActionResult> Delete(DeleteAccountRequest req, CancellationToken ct)
    {
        await data.DeleteAllAsync(User.GetUserId(), req.Confirm, ct);
        return NoContent();
    }

    [HttpGet("me/history")]
    public Task<List<HistoryItem>> History([FromQuery] int days = 30, CancellationToken ct = default) =>
        data.HistoryAsync(User.GetUserId(), days, ct);

    [HttpGet("sessions")]
    public Task<List<SessionView>> Sessions(CancellationToken ct) => sessions.ListAsync(User.GetUserId(), Device, ct);

    [HttpPost("sessions/{id:int}/revoke")]
    public Task<List<SessionView>> Revoke(int id, CancellationToken ct) => sessions.RevokeAsync(User.GetUserId(), id, Device, ct);

    /// <summary>登出其他所有裝置（留下現在這個）。</summary>
    [HttpPost("sessions/revoke-others")]
    public Task<List<SessionView>> RevokeOthers(CancellationToken ct) => sessions.RevokeOthersAsync(User.GetUserId(), Device, ct);
}
