using System.Text.Json;
using DayCap.Api.Common;
using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using DayCap.Api.Models.Settings;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Services;

public interface IPeriodService
{
    Task<PeriodView> GetCurrentAsync(string userId, CancellationToken ct = default);
    Task<PeriodView> GetAsync(string userId, int periodId, CancellationToken ct = default);
    Task<List<PeriodSummaryDto>> ListAsync(string userId, CancellationToken ct = default);
    Task<BudgetPeriod> LoadAsync(string userId, int periodId, CancellationToken ct = default, bool tracking = true);
    Task<PeriodView> ComputeAsync(BudgetPeriod period, CancellationToken ct = default);
    Task<int> DeleteBeforeStartAsync(string userId, CancellationToken ct = default);
    Task<PeriodView> SetNextPaydayAsync(string userId, int periodId, DateOnly payday, CancellationToken ct = default);
}

/// <summary>
/// 期間（§3.4）：資料庫存明確的起訖日，已開始的期間不因規則變更而改變。
/// 新期間從上一期結束隔天開始；中間有斷層（很久沒打開）時，從「今天以前最近的發薪日」開始。
/// 結束日＝下一個預定發薪日前一天；可以手動覆蓋本期結束後的實際入帳日。
/// </summary>
public class PeriodService(
    DayCapDbContext db,
    ISettingsService settings,
    ICalendarService calendar,
    IAppClock clock) : IPeriodService
{
    public async Task<PeriodView> GetCurrentAsync(string userId, CancellationToken ct = default)
    {
        var profile = await settings.EnsureProfileAsync(userId, ct);
        var timeline = await settings.GetTimelineAsync(userId, ct);
        var today = clock.LogicalToday(timeline.For(clock.Today).Doc.DayStart);

        // 還沒到開始日期：什麼都不算，告訴前端哪天開始、第一期是哪段。
        if (profile.StartDate is { } startDate && startDate > today)
        {
            var end = await NextPaydayAfterAsync(userId, startDate, timeline.For(startDate).Doc.Payday, ct);
            throw new NotStartedException(new NotStartedDto(startDate, startDate, end.AddDays(-1), startDate.DayNumber - today.DayNumber));
        }

        // 開始日期之前留下的週期（試用時產生的）一律忽略。
        var minStart = profile.StartDate ?? DateOnly.MinValue;

        await SettleEndedPeriodsAsync(userId, minStart, today, ct);

        var existing = await db.Periods
            .Where(p => p.UserId == userId && p.StartDate >= minStart && p.StartDate <= today && p.EndDate >= today)
            .Select(p => (int?)p.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is { } id) return await GetAsync(userId, id, ct);

        var rule = timeline.For(today).Doc.Payday;
        var previousEnd = await db.Periods.Where(p => p.UserId == userId && p.StartDate >= minStart && p.EndDate < today)
            .MaxAsync(p => (DateOnly?)p.EndDate, ct);

        DateOnly start;
        if (previousEnd is { } pe && (await NextPaydayAfterAsync(userId, pe.AddDays(1), rule, ct)).AddDays(-1) >= today)
        {
            start = pe.AddDays(1); // 接著上一期
        }
        else
        {
            start = await LatestPaydayOnOrBeforeAsync(userId, today, rule, ct);
            if (previousEnd is { } pe2 && start <= pe2) start = pe2.AddDays(1);
        }
        if (start < minStart) start = minStart; // 第一期從開始日期算起，可能不滿一個月
        var periodEnd = (await NextPaydayAfterAsync(userId, start, rule, ct)).AddDays(-1);
        if (periodEnd < today) periodEnd = today;

        var period = new BudgetPeriod { UserId = userId, StartDate = start, EndDate = periodEnd, CreatedAt = clock.UtcNow };
        db.Periods.Add(period);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // 同時有兩個請求都在建這一期（唯一索引 UserId + StartDate 擋下其中一個）：改讀已經建好的那一期
            db.ChangeTracker.Clear();
            var winner = await db.Periods.Where(p => p.UserId == userId && p.StartDate == start).Select(p => (int?)p.Id).FirstOrDefaultAsync(ct);
            if (winner is null) throw;
            return await GetAsync(userId, winner.Value, ct);
        }
        return await ComputeAsync(period, ct);
    }

    public async Task<PeriodView> GetAsync(string userId, int periodId, CancellationToken ct = default) =>
        await ComputeAsync(await LoadAsync(userId, periodId, ct), ct);

    public Task<List<PeriodSummaryDto>> ListAsync(string userId, CancellationToken ct = default) =>
        db.Periods.Where(p => p.UserId == userId)
            .OrderByDescending(p => p.StartDate)
            .Select(p => new PeriodSummaryDto(p.Id, p.StartDate, p.EndDate))
            .ToListAsync(ct);

    public async Task<BudgetPeriod> LoadAsync(string userId, int periodId, CancellationToken ct = default, bool tracking = true) =>
        await (tracking ? db.Periods : db.Periods.AsNoTracking())
            .Include(p => p.Entries)
            .Include(p => p.PoolTransfers)
            .Include(p => p.IncomeAdjustments)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == periodId && p.UserId == userId, ct)
        ?? throw new NotFoundException("找不到這個週期。");

    public async Task<PeriodView> ComputeAsync(BudgetPeriod period, CancellationToken ct = default)
    {
        var timeline = await settings.GetTimelineAsync(period.UserId, ct);
        var days = await calendar.GetDaysAsync(period.UserId, period.StartDate, period.EndDate, ct);
        var plan = PlanBuilder.Build(period.StartDate, period.EndDate, timeline, days);
        var dayStart = timeline.For(clock.Today).Doc.DayStart;
        return BudgetEngine.Compute(period, plan, clock.LogicalToday(dayStart), utc => clock.LogicalDate(utc, dayStart), days);
    }

    /// <summary>刪掉開始日期之前的週期（試用資料），連同回報一起。</summary>
    public async Task<int> DeleteBeforeStartAsync(string userId, CancellationToken ct = default)
    {
        var profile = await settings.EnsureProfileAsync(userId, ct);
        if (profile.StartDate is not { } start) return 0;
        return await db.Periods.Where(p => p.UserId == userId && p.StartDate < start).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// 手動覆蓋實際入帳日（§3.4）：本期結束日改成入帳日前一天。只能改進行中的期間，
    /// 而且入帳日要在明天以後（今天以前的日子已經照原本的期間算過了）。
    /// </summary>
    public async Task<PeriodView> SetNextPaydayAsync(string userId, int periodId, DateOnly payday, CancellationToken ct = default)
    {
        var period = await LoadAsync(userId, periodId, ct);
        var today = await settings.LogicalTodayAsync(userId, ct);
        if (period.StartDate > today || period.EndDate < today) throw new ValidationException("只能改進行中的這一期。");
        if (payday <= today) throw new ValidationException("入帳日要在明天以後。");
        if (payday.DayNumber - period.StartDate.DayNumber > 62) throw new ValidationException("一期最長兩個月左右，入帳日太遠了。");
        if (await db.Periods.AnyAsync(p => p.UserId == userId && p.StartDate > period.StartDate, ct))
            throw new ValidationException("下一期已經開始，不能再改這一期的結束日。");

        period.EndDate = payday.AddDays(-1);
        await db.SaveChangesAsync(ct);
        return await ComputeAsync(period, ct);
    }

    /// <summary>
    /// 期末：記下這期的結果並發通知。依規格 §2.1（2026-09-29 決定）不再自動從存款扣或存入，
    /// 超支怎麼處理交給 §12.2 月結。
    /// </summary>
    private async Task SettleEndedPeriodsAsync(string userId, DateOnly minStart, DateOnly today, CancellationToken ct)
    {
        var ids = await db.Periods
            .Where(p => p.UserId == userId && p.SettledAt == null && p.EndDate < today && p.StartDate >= minStart)
            .OrderBy(p => p.StartDate)
            .Select(p => p.Id)
            .ToListAsync(ct);

        foreach (var id in ids)
        {
            var period = await LoadAsync(userId, id, ct);
            var balance = (await ComputeAsync(period, ct)).Pool.Balance;
            period.SettledAt = clock.UtcNow;
            period.SettlementAmount = 0;
            var key = $"settlement:{period.Id}";
            if (!await db.Notifications.AnyAsync(n => n.UserId == userId && n.Key == key, ct))
            {
                db.Notifications.Add(new Notification
                {
                    UserId = userId,
                    Key = key,
                    Kind = "settlement",
                    PeriodId = period.Id,
                    PopupOn = today,
                    Payload = JsonSerializer.Serialize(new SettlementPayload($"{period.StartDate:M/d}–{period.EndDate:M/d}", balance, 0, null)),
                    CreatedAt = clock.UtcNow,
                });
            }
            await db.SaveChangesAsync(ct);
        }
    }

    // ---- 發薪日（依行事曆判斷工作日）----

    private async Task<Func<DateOnly, bool>> HolidayLookupAsync(string userId, DateOnly around, CancellationToken ct)
    {
        var days = await calendar.GetDaysAsync(userId, around.AddDays(-80), around.AddDays(80), ct);
        return d => days.TryGetValue(d, out var i) ? i.IsHoliday : d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
    }

    private async Task<DateOnly> NextPaydayAfterAsync(string userId, DateOnly date, PaydayRule rule, CancellationToken ct) =>
        Payday.NextAfter(date, rule, await HolidayLookupAsync(userId, date, ct));

    private async Task<DateOnly> LatestPaydayOnOrBeforeAsync(string userId, DateOnly date, PaydayRule rule, CancellationToken ct) =>
        Payday.LatestOnOrBefore(date, rule, await HolidayLookupAsync(userId, date, ct));
}
