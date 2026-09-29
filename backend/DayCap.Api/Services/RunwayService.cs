using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Services;

public interface IRunwayService
{
    Task<RunwayView> GetAsync(string userId, CancellationToken ct = default);
}

/// <summary>收入中斷（§18）：存款還能撐幾天＝可動用資產 ÷ 最近實際平均每日花費。</summary>
public class RunwayService(DayCapDbContext db, IPeriodService periods, IAccountService accounts, ISettingsService settings) : IRunwayService
{
    public const int BasisDays = 30;

    public async Task<RunwayView> GetAsync(string userId, CancellationToken ct = default)
    {
        var view = await accounts.GetAsync(userId, ct);
        var usable = view.Accounts.Sum(a => AccountService.IsLiability(a.Type) ? -a.Balance : a.Balance);

        var today = await settings.LogicalTodayAsync(userId, ct);
        var to = today.AddDays(-1); // 今天還沒過完，不算
        var from = to.AddDays(1 - BasisDays);
        var list = await db.Periods.AsNoTracking()
            .Where(p => p.UserId == userId && p.EndDate >= from && p.StartDate <= to)
            .OrderBy(p => p.StartDate).Select(p => p.Id).ToListAsync(ct);

        decimal spent = 0;
        var days = 0;
        foreach (var id in list)
        {
            var pv = await periods.GetAsync(userId, id, ct);
            for (var d = pv.StartDate > from ? pv.StartDate : from; d <= pv.EndDate && d <= to; d = d.AddDays(1))
            {
                spent += PeriodService.DayOutflow(pv, d);
                days++;
            }
        }
        var avg = days == 0 ? 0 : Math.Round(spent / days, 0);
        int? runway = avg > 0 ? (int)Math.Floor(Math.Max(0, usable) / avg) : null;
        return new RunwayView(usable, avg, days, from, to, runway);
    }
}
