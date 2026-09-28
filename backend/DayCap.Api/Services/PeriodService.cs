using System.Text.Json;
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
    Task<int> DeleteBeforeStartAsync(string userId, CancellationToken ct = default);
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
        var profile = await settings.EnsureProfileAsync(userId, ct);

        // 還沒到開始日期：什麼都不算，告訴前端哪天開始、第一期是哪段。
        if (profile.StartDate is { } startDate && startDate > today)
        {
            var (fs, fe) = FirstPeriodRange(startDate, profile.CycleStartDay);
            throw new NotStartedException(new NotStartedDto(startDate, fs, fe, startDate.DayNumber - today.DayNumber));
        }

        // 開始日期之前留下的週期（試用時產生的）一律忽略。
        var minStart = profile.StartDate ?? DateOnly.MinValue;

        // 發薪日後第一次打開：把已經結束、還沒結算的週期結算掉（超支扣資產 / 結餘存入）。
        await SettleEndedPeriodsAsync(userId, profile, minStart, today, ct);
        var existing = await db.Periods
            .Where(p => p.UserId == userId && p.StartDate >= minStart && p.StartDate <= today && p.EndDate >= today)
            .Select(p => (int?)p.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is { } id) return await GetAsync(userId, id, ct);

        var (start, end) = CycleRange(today, profile.CycleStartDay);
        if (start < minStart) start = minStart; // 第一期從開始日期算起，可能不滿一個月

        // 改過週期起始日時，新週期不能跟舊的重疊：從上一期結束的隔天開始。
        var lastEnd = await db.Periods.Where(p => p.UserId == userId && p.StartDate >= minStart && p.EndDate >= start && p.StartDate <= today)
            .MaxAsync(p => (DateOnly?)p.EndDate, ct);
        if (lastEnd is { } le && le >= start) start = le.AddDays(1);

        var period = new BudgetPeriod { UserId = userId, StartDate = start, EndDate = end, CreatedAt = clock.UtcNow };
        db.Periods.Add(period);
        await BuildAsync(period, profile, start, ct);
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

    /// <summary>
    /// 期末結算：週期結束後，待定區餘額就是這期「收入 − 實際花掉的」。
    /// 負的（超支）→ 從結算帳戶扣掉；正的（結餘）→ 設定了 SurplusToAccount 才存進去，否則不動。
    /// 沒設定結算帳戶就只記錄、只通知。每期只結算一次。
    /// </summary>
    private async Task SettleEndedPeriodsAsync(string userId, UserProfile profile, DateOnly minStart, DateOnly today, CancellationToken ct)
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
            var account = profile.SettlementAccountId is { } aid
                ? await db.CashAccounts.FirstOrDefaultAsync(c => c.Id == aid && c.UserId == userId, ct)
                : null;

            var applied = 0;
            var label = $"{period.StartDate:M/d}–{period.EndDate:M/d}";
            if (account is not null && (balance < 0 || (balance > 0 && profile.SurplusToAccount)))
            {
                applied = balance;
                account.Balance += balance;
                account.UpdatedAt = clock.UtcNow;
                db.AssetAdjustments.Add(new AssetAdjustment
                {
                    UserId = userId,
                    CashAccountId = account.Id,
                    Date = period.EndDate.AddDays(1),
                    Amount = balance,
                    Note = balance < 0 ? $"{label} 超支結算" : $"{label} 結餘存入",
                    Source = "settlement",
                    PeriodId = period.Id,
                    CreatedAt = clock.UtcNow,
                });
            }

            period.SettledAt = clock.UtcNow;
            period.SettlementAmount = applied;
            db.Notifications.Add(new Notification
            {
                UserId = userId,
                Key = $"settlement:{period.Id}",
                Kind = "settlement",
                PeriodId = period.Id,
                PopupOn = today,
                Payload = JsonSerializer.Serialize(new SettlementPayload(label, balance, applied, account?.Name)),
                CreatedAt = clock.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>刪掉開始日期之前的週期（試用資料），連同回報一起。</summary>
    public async Task<int> DeleteBeforeStartAsync(string userId, CancellationToken ct = default)
    {
        var profile = await settings.EnsureProfileAsync(userId, ct);
        if (profile.StartDate is not { } start) return 0;
        return await db.Periods.Where(p => p.UserId == userId && p.StartDate < start).ExecuteDeleteAsync(ct);
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
            .Include(p => p.IncomeAdjustments)
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
            if (item.Cycle != BillingCycle.Monthly)
            {
                var first = item.BillingMonth ?? 1;
                int[] months = item.Cycle == BillingCycle.Quarterly
                    ? [first, (first + 2) % 12 + 1, (first + 5) % 12 + 1, (first + 8) % 12 + 1]
                    : [first];
                due = FindDueDate(period, item.DueDay ?? 1, months);
                if (due is null) continue; // 這期不是年繳 / 季繳的月份
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
    private static DateOnly? FindDueDate(BudgetPeriod period, int dueDay, int[]? months)
    {
        for (var d = period.StartDate; d <= period.EndDate; d = d.AddDays(1))
        {
            if (months is not null && !months.Contains(d.Month)) continue;
            var target = Math.Min(dueDay, DateTime.DaysInMonth(d.Year, d.Month));
            if (d.Day == target) return d;
        }
        return null;
    }

    /// <summary>第一期：從開始日期到下一個週期起始日的前一天。</summary>
    public static (DateOnly Start, DateOnly End) FirstPeriodRange(DateOnly startDate, int cycleStartDay)
    {
        var (_, end) = CycleRange(startDate, cycleStartDay);
        return (startDate, end);
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
