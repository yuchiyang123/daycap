using DayCap.Api.Common;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DayCap.Api.Controllers;

/// <summary>月結（§12.2）。</summary>
[ApiController]
[Authorize]
[Route("api/month-end")]
public class MonthEndController(IMonthEndService monthEnd) : ControllerBase
{
    [HttpGet("{periodId:int}")]
    public Task<MonthEndReport> Report(int periodId, CancellationToken ct) => monthEnd.ReportAsync(User.GetUserId(), periodId, ct);

    [HttpPost("{periodId:int}")]
    public Task<MonthEndReport> Close(int periodId, CloseMonthRequest req, CancellationToken ct) =>
        monthEnd.CloseAsync(User.GetUserId(), periodId, req, ct);
}
