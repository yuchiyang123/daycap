using DayCap.Api.Common;
using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Services;

public interface IPeriodService
{
    Task<PeriodView> GetCurrentAsync(string userId, CancellationToken ct = default);
    Task<PeriodView> GetAsync(string userId, int periodId, CancellationToken ct = default);
    Task<List<PeriodSummaryDto>> ListAsync(string userId, CancellationToken ct = default);
    Task<PeriodView> RebuildAsync(string userId, int periodId, DateOnly? fromDate, CancellationToken ct = default);
    Task<BudgetPeriod> LoadAsync(string userId, int periodId, CancellationToken ct = default, bool tracking = true);
    Task<PeriodView> ComputeAsync(BudgetPeriod period, CancellationToken ct = default);
}

public class PeriodService(
    DayCapDbContext db,
    ISettingsService settings,
    ICalendarService calendar,
    IAppClock clock) : IPeriodService
{
    public async Task<PeriodView> GetCurrentAsync(string userId, CancellationToken ct = default)
    {
        var today = clock.Today;
        var existing = await db.Periods
            .Where(p => p.UserId == userId && p.StartDate <= today && p.EndDate >= today)
            .Select(p => (int?)p.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is { } id) return await GetAsync(userId, id, ct);

        var profile = await settings.EnsureProfileAsync(userId, ct);
        var (start, end) = CycleRange(today, profile.CycleStartDay);

        // 改過週期起始日時，新週期不能跟舊的重疊：從上一期結束的隔天開始。
        var lastEnd = await db.Periods.Where(p => p.UserId == userId && p.EndDate >= start && p.StartDate <= today)
            .MaxAsync(p => (DateOnly?)p.EndDate, ct);
        if (lastEnd is { } le && le >= start) start = le.AddDays(1);

        var period = new BudgetPeriod { UserId = userId, StartDate = start, EndDate = end, CreatedAt = clock.UtcNow };
        db.Periods.Add(period);
        await BuildAsync(period, profile, start, ct);
        await db.SaveChangesAsync(ct);
        return await ComputeAsync(period, ct);
    }

    public async Task<PeriodView> GetAsync(string userId, int periodId, CancellationToken ct = default) =>
        await ComputeAsync(await LoadAsync(userId, periodId, ct), ct);

    public Task<List<PeriodSummaryDto>> ListAsync(string userId, CancellationToken ct = default) =>
        db.Periods.Where(p => p.UserId == userId)
            .OrderByDescending(p => p.StartDate)
            .Select(p => new PeriodSummaryDto(p.Id, p.StartDate, p.EndDate, p.Income))
            .ToListAsync(ct);

    /// <summary>
    /// 設定改了之後套用到這一期：收入、分類額度、固定支出整個重算；
    /// 每日排程只重建 fromDate（預設今天）之後的日子，過去的日子維持原本的額度。
    /// 回報紀錄完全不動，重播時自動對上新的排程。
    /// </summary>
    public async Task<PeriodView> RebuildAsync(string userId, int periodId, DateOnly? fromDate, CancellationToken ct = default)
    {
        var period = await LoadAsync(userId, periodId, ct);
        var profile = await settings.EnsureProfileAsync(userId, ct);
        var from = fromDate ?? clock.Today;
        if (from < period.StartDate) from = period.StartDate;

        db.PeriodCategories.RemoveRange(period.Categories);
        db.PeriodFixedCharges.RemoveRange(period.FixedCharges);
        db.DayAllocations.RemoveRange(period.Allocations.Where(a => a.Date >= from));
        period.Categories.Clear();
        period.FixedCharges.Clear();
        period.Allocations.RemoveAll(a => a.Date >= from);

        await BuildAsync(period, profile, from, ct);
        period.RebuiltAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return await ComputeAsync(period, ct);
    }

    public async Task<BudgetPeriod> LoadAsync(string userId, int periodId, CancellationToken ct = default, bool tracking = true) =>
        await (tracking ? db.Periods : db.Periods.AsNoTracking())
            .Include(p => p.Categories)
            .Include(p => p.Allocations)
            .Include(p => p.FixedCharges)
            .Include(p => p.Entries)
            .Include(p => p.PoolTransfers)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == periodId && p.UserId == userId, ct)
        ?? throw new NotFoundException("找不到這個週期。");

    public async Task<PeriodView> ComputeAsync(BudgetPeriod period, CancellationToken ct = default)
    {
        var days = await calendar.GetDaysAsync(period.UserId, period.StartDate, period.EndDate, ct);
        return BudgetEngine.Compute(period, clock.Today, clock.ToLocalDate, days);
    }

    private async Task BuildAsync(BudgetPeriod period, UserProfile profile, DateOnly allocationsFrom, CancellationToken ct)
    {
        var categories = await settings.GetActiveCategoriesAsync(period.UserId, ct);
        var days = await calendar.GetDaysAsync(period.UserId, period.StartDate, period.EndDate, ct);
        period.Income = profile.MonthlyIncome;

        foreach (var c in categories)
        {
            var charges = c.Mode == BudgetMode.Fixed ? FixedChargesFor(c, period).ToList() : [];
            period.FixedCharges.AddRange(charges);
            period.Categories.Add(new PeriodCategory
            {
                CategoryId = c.Id,
                Name = c.Name,
                Group = c.Group,
                Mode = c.Mode,
                SortOrder = c.SortOrder,
                Budget = c.Mode == BudgetMode.Fixed
                    ? charges.Sum(f => f.Amount)
                    : (int)Math.Round(profile.MonthlyIncome * c.Percent / 100m, MidpointRounding.AwayFromZero),
            });

            if (c.Mode != BudgetMode.Daily) continue;
            for (var d = allocationsFrom; d <= period.EndDate; d = d.AddDays(1))
            {
                var holiday = days[d].IsHoliday;
                foreach (var s in c.Slots)
                {
                    var amount = holiday ? s.HolidayAmount : s.WorkdayAmount;
                    if (amount <= 0) continue;
                    period.Allocations.Add(new DayAllocation
                    {
                        Date = d,
                        CategoryId = c.Id,
                        SlotId = s.Id,
                        SlotName = s.Name,
                        SortOrder = c.SortOrder * 100 + s.SortOrder,
                        IsHoliday = holiday,
                        Planned = amount,
                    });
                }
            }
        }
    }

    private static IEnumerable<PeriodFixedCharge> FixedChargesFor(Category c, BudgetPeriod period)
    {
        foreach (var item in c.FixedItems.Where(f => f.IsActive && (f.ActiveFrom is null || f.ActiveFrom <= period.StartDate)))
        {
            DateOnly? due;
            if (item.Cycle == BillingCycle.Yearly)
            {
                due = FindDueDate(period, item.DueDay ?? 1, item.BillingMonth ?? 1);
                if (due is null) continue; // 這期不是年繳的月份
            }
            else
            {
                due = item.DueDay is { } dd ? FindDueDate(period, dd, null) : null;
            }

            yield return new PeriodFixedCharge
            {
                CategoryId = c.Id,
                FixedItemId = item.Id,
                Name = item.Name,
                Amount = item.Amount,
                DueDate = due,
                IsSubscription = item.IsSubscription,
            };
        }
    }

    /// <summary>在週期內找扣款日；日期超過當月天數時落在月底（例如 31 號在二月 = 28/29 號）。</summary>
    private static DateOnly? FindDueDate(BudgetPeriod period, int dueDay, int? month)
    {
        for (var d = period.StartDate; d <= period.EndDate; d = d.AddDays(1))
        {
            if (month is { } m && d.Month != m) continue;
            var target = Math.Min(dueDay, DateTime.DaysInMonth(d.Year, d.Month));
            if (d.Day == target) return d;
        }
        return null;
    }

    public static (DateOnly Start, DateOnly End) CycleRange(DateOnly date, int startDay)
    {
        startDay = Math.Clamp(startDay, 1, 28);
        var start = date.Day >= startDay
            ? new DateOnly(date.Year, date.Month, startDay)
            : new DateOnly(date.Year, date.Month, startDay).AddMonths(-1);
        return (start, start.AddMonths(1).AddDays(-1));
    }
}
