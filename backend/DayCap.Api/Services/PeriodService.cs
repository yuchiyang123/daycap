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
    /// <param name="extraRecon">試算用：假裝多了這一次對帳（不存檔）。</param>
    Task<PeriodView> ComputeAsync(BudgetPeriod period, CancellationToken ct = default, Reconciliation? extraRecon = null);
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
        // 年繳罐子每期提撥（§11.2）：只有真正建立這一期的請求會做
        await JarMath.AddAnnualContributionsAsync(db, period, clock.UtcNow, ct);
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

    public Task<PeriodView> ComputeAsync(BudgetPeriod period, CancellationToken ct = default, Reconciliation? extraRecon = null) =>
        ComputeInternalAsync(period, extraRecon, [], 0, ct);

    /// <summary>
    /// 重播一期。這期裡面如果有完整對帳（§12.1），算出每一次的「沒交代的差異」：
    /// 預期淨額 = 上一次完整對帳的淨額 + 兩次之間已知的收支（薪水、每日時段、額外花費、固定支出、資產加減）；
    /// 帳戶互轉、繳卡費不影響淨額，所以不用算。差額在對帳的登錄時間點重播（進池或照超支規則）。
    /// 後來補回報的事實會讓預期值跟著變，差額自動縮小。差額會影響之後的額度，所以迭代到穩定為止。
    /// </summary>
    private async Task<PeriodView> ComputeInternalAsync(BudgetPeriod period, Reconciliation? extraRecon,
        Dictionary<int, PeriodView> cache, int depth, CancellationToken ct)
    {
        var timeline = await settings.GetTimelineAsync(period.UserId, ct);
        var days = await calendar.GetDaysAsync(period.UserId, period.StartDate, period.EndDate, ct);
        decimal? previousIncome = null;
        if (timeline.For(period.StartDate).Doc.Kind == IncomeKind.Variable && depth < 2)
        {
            // 非固定收入：% 基準用上期實際收入（含薪資調整）
            var prev = await db.Periods.AsNoTracking().Where(p => p.UserId == period.UserId && p.EndDate < period.StartDate)
                .OrderByDescending(p => p.EndDate).Select(p => (int?)p.Id).FirstOrDefaultAsync(ct);
            if (prev is { } pid)
            {
                if (!cache.TryGetValue(pid, out var pv))
                {
                    pv = await ComputeInternalAsync(await LoadAsync(period.UserId, pid, ct, tracking: false), null, cache, depth + 1, ct);
                    cache[pid] = pv;
                }
                previousIncome = pv.Income;
            }
        }
        var plan = PlanBuilder.Build(period.StartDate, period.EndDate, timeline, days, previousIncome);
        // 從前面期間結轉過來的（月結、延後的超支，§10.2、§12.2）
        var carryovers = (await db.PeriodCarryovers.AsNoTracking()
                .Where(c => c.UserId == period.UserId && c.TargetDate >= period.StartDate && c.TargetDate <= period.EndDate)
                .ToListAsync(ct)).Active();
        foreach (var c in carryovers.OrderBy(c => c.CreatedAt))
        {
            plan.CarryIns.Add(new PlanLine(period.StartDate, c.Amount, c.Label));
        }
        var dayStart = timeline.For(clock.Today).Doc.DayStart;
        var today = clock.LogicalToday(dayStart);
        // 類別細項（§13）用這期「目前」有效的設定；只是彙總回報上的標記，不影響重播
        var subItemDoc = timeline.For(today < period.StartDate ? period.StartDate : today > period.EndDate ? period.EndDate : today).Doc;
        PeriodView Run(IReadOnlyList<ReconDiff> diffs) =>
            SubItems.Decorate(BudgetEngine.Compute(period, plan, today, utc => clock.LogicalDate(utc, dayStart), days, diffs), subItemDoc);

        var recons = (await db.Reconciliations.AsNoTracking().Include(r => r.Lines).Where(r => r.UserId == period.UserId).ToListAsync(ct)).Active();
        if (extraRecon is not null) recons.Add(extraRecon);
        var full = recons.Where(r => r.IsFull).OrderBy(r => r.Date).ThenBy(r => r.CreatedAt).ToList();
        var inPeriod = full.Where(r => r.Date >= period.StartDate && r.Date <= period.EndDate).ToList();

        var view = await DecorateAsync(Run([]), period, ct);
        if (inPeriod.Count == 0 || depth > 2) return view;

        var types = await db.CashAccounts.AsNoTracking().Where(a => a.UserId == period.UserId).ToDictionaryAsync(a => a.Id, a => a.Type, ct);
        var adjustments = (await db.AssetAdjustments.AsNoTracking().Where(a => a.UserId == period.UserId).ToListAsync(ct)).Active();

        List<ReconDiff> diffs = [];
        for (var iteration = 0; iteration < 5; iteration++)
        {
            var next = new List<ReconDiff>();
            foreach (var r in inPeriod)
            {
                var prev = full.LastOrDefault(x => x.Id != r.Id && (x.Date < r.Date || (x.Date == r.Date && x.CreatedAt < r.CreatedAt)));
                if (prev is null || prev.Date < period.StartDate.AddDays(-100)) continue; // 沒有基準：這次只當基準
                var flows = await FlowsAsync(period, view, prev.Date, r.Date, cache, depth, ct)
                            + adjustments.Where(a => a.Date > prev.Date && a.Date <= r.Date).Sum(a => a.Amount);
                next.Add(new ReconDiff(r.Id, r.Date, r.CreatedAt,
                    AccountService.Net(prev, types) + flows, AccountService.Net(r, types), r.UsePool));
            }
            var same = next.Count == diffs.Count && next.Zip(diffs).All(p => p.First.Id == p.Second.Id && p.First.Expected == p.Second.Expected);
            diffs = next;
            view = await DecorateAsync(Run(diffs), period, ct);
            if (same) break;
        }
        return view;
    }

    /// <summary>月結狀態：這期是否已月結、上一期是否還沒月結（§12.2「上一期沒結，這一期不能正式啟用」）。</summary>
    private async Task<PeriodView> DecorateAsync(PeriodView view, BudgetPeriod period, CancellationToken ct)
    {
        var profile = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == period.UserId, ct);
        var minStart = profile?.StartDate ?? DateOnly.MinValue;
        var previous = await db.Periods.AsNoTracking()
            .Where(p => p.UserId == period.UserId && p.EndDate < period.StartDate && p.StartDate >= minStart)
            .OrderByDescending(p => p.EndDate)
            .Select(p => new { p.Id, p.ClosedAt })
            .FirstOrDefaultAsync(ct);
        return view with
        {
            Closed = period.ClosedAt is not null,
            PreviousPeriodNeedsClosing = previous is { ClosedAt: null } ? previous.Id : null,
        };
    }

    /// <summary>(from, to] 之間已知的收支（收入為正、支出為負）。跨到其他期間的日子，用那一期的重播結果。</summary>
    private async Task<decimal> FlowsAsync(BudgetPeriod current, PeriodView currentView, DateOnly from, DateOnly to,
        Dictionary<int, PeriodView> cache, int depth, CancellationToken ct)
    {
        decimal total = 0;
        var d = from.AddDays(1);
        while (d <= to)
        {
            PeriodView? view;
            if (d >= current.StartDate && d <= current.EndDate)
            {
                view = currentView;
            }
            else
            {
                var other = await db.Periods.AsNoTracking()
                    .Where(p => p.UserId == current.UserId && p.StartDate <= d && p.EndDate >= d)
                    .Select(p => (int?)p.Id).FirstOrDefaultAsync(ct);
                if (other is not { } oid)
                {
                    d = d.AddDays(1); // 不屬於任何期間的日子（很久沒用）：沒有已知收支
                    continue;
                }
                if (!cache.TryGetValue(oid, out view))
                {
                    view = await ComputeInternalAsync(await LoadAsync(current.UserId, oid, ct, tracking: false), null, cache, depth + 1, ct);
                    cache[oid] = view;
                }
            }

            var last = view.EndDate < to ? view.EndDate : to;
            for (; d <= last; d = d.AddDays(1)) total += DayFlow(view, d);
        }
        return total;
    }

    /// <summary>某一天已知的收支：發薪那天收入 −（時段實際或照預算）− 額外花費 − 當天到期的固定支出。</summary>
    public static decimal DayFlow(PeriodView view, DateOnly d)
    {
        var day = view.Days.FirstOrDefault(x => x.Date == d);
        if (day is null) return 0;
        var entries = view.Entries.ToDictionary(e => e.Id);
        var outflow = day.Slots.Sum(s => s.Actual ?? s.Planned)
                      + day.ExtraEntryIds.Where(entries.ContainsKey).Sum(id => entries[id].Actual + entries[id].JarCovered) // 罐子付的錢也真的從帳戶出去了
                      + view.FixedCharges.Where(f => (f.DueDate ?? view.StartDate) == d).Sum(f => f.Amount);
        var inflow = d == view.StartDate ? view.Income : 0;
        return inflow - outflow;
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
                    // 內容見 NotificationService：提醒完成月結（§12.2）
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
