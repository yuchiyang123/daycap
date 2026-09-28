using DayCap.Api.Common;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DayCap.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/calendar")]
public class CalendarController(ICalendarService calendar) : ControllerBase
{
    [HttpGet]
    public async Task<List<CalendarDayDto>> Get([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct)
    {
        if (to < from || to.DayNumber - from.DayNumber > 400) throw new ValidationException("日期區間不正確（最多 400 天）。");
        var days = await calendar.GetDaysAsync(User.GetUserId(), from, to, ct);
        return days.OrderBy(d => d.Key).Select(d => new CalendarDayDto(d.Key, d.Value.IsHoliday, d.Value.Name)).ToList();
    }

    /// <summary>isHoliday = null 代表取消覆寫，回到政府行事曆。</summary>
    [HttpPut("overrides/{date}")]
    public async Task<IActionResult> SetOverride(DateOnly date, DayOverrideRequest req, CancellationToken ct)
    {
        await calendar.SetOverrideAsync(User.GetUserId(), date, req.IsHoliday, ct);
        return NoContent();
    }
}
