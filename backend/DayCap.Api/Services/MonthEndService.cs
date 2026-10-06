using System.Text.Json;
using DayCap.Api.Common;
using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Services;

public interface IMonthEndService
{
    Task<MonthEndReport> ReportAsync(string userId, int periodId, CancellationToken ct = default);
    Task<MonthEndReport> CloseAsync(string userId, int periodId, CloseMonthRequest req, CancellationToken ct = default);
}

/// <summary>
/// 月結（§12.2），發薪前做：
/// 1. 上一期沒結，這一期不能正式啟用（PeriodView.PreviousPeriodNeedsClosing）。
/// 2. 先對一次帳（這期最後一週內要有一次完整對帳）。
/// 3. 報表：本期省或超多少（與上期比）、最常超支的時段／分類、沒交代的差異、目標進度與預計達成日。
/// 4. 決定下期是否調整時段金額（存成從下一期第一天起生效的設定版本）；缺口怎麼補。
/// 5. 存成不可修改的快照，這期從此不能再改。
/// 6. 結餘放進下一期的待分配池；缺口依決定結轉、分兩期或從存款吸收。
/// </summary>
public class MonthEndService(
    DayCapDbContext db,
    IPeriodService periods,
    ISettingsService settings,
    IAssetService assets,
    IAppClock clock) : IMonthEndService
{
    /// <summary>發薪日前幾天開始可以月結（和提醒同一天）。</summary>
    public const int OpenDaysBeforePayday = NotificationService.ReminderDaysBeforePayday;

    public async Task<MonthEndReport> ReportAsync(string userId, int periodId, CancellationToken ct = default)
    {
        var period = await periods.LoadAsync(userId, periodId, ct, tracking: false);
        var view = await periods.ComputeAsync(period, ct);
        var today = await settings.LogicalTodayAsync(userId, ct);

        var existing = await db.MonthEnds.AsNoTracking().FirstOrDefaultAsync(m => m.UserId == userId && m.PeriodId == periodId, ct);
        MonthEndSummary? summary = existing is null ? null
            : new MonthEndSummary(existing.ClosedAt, existing.Result, existing.Decision, existing.CarryAmount, Message(existing.Result, existing.Decision));

        var openFrom = period.EndDate.AddDays(1 - OpenDaysBeforePayday);
        // 帳戶只記儲蓄時，對帳不算差額，月結也就不需要先完整對帳
        var savingsOnly = await db.Profiles.AsNoTracking().AnyAsync(p => p.UserId == userId && p.SavingsOnlyAccounts, ct);
        var needsRecon = !savingsOnly && !await HasClosingReconciliationAsync(userId, period, ct);
        string? reason = null;
        if (existing is not null) reason = "這期已經月結。";
        else if (today < openFrom) reason = $"{openFrom:M/d}（發薪日前 {OpenDaysBeforePayday} 天）起才能月結。";
        else if (needsRecon) reason = "先對一次帳：填這期最後一週的實際餘額。";

        var previous = await db.MonthEnds.AsNoTracking()
            .Where(m => m.UserId == userId && m.PeriodId != periodId)
            .Join(db.Periods.AsNoTracking().Where(p => p.EndDate < period.StartDate), m => m.PeriodId, p => p.Id, (m, p) => new { m.Result, p.EndDate })
            .OrderByDescending(x => x.EndDate).Select(x => (decimal?)x.Result).FirstOrDefaultAsync(ct);

        return new MonthEndReport(
            period.Id, period.StartDate, period.EndDate,
            existing is not null, reason is null, reason, needsRecon,
            existing?.Result ?? view.Pool.Balance,
            previous,
            TopOverspends(view),
            view.Reconciliations.Sum(r => r.Diff),
            SlotSuggestions(view),
            await GoalsAsync(userId, view, ct),
            summary,
            // 細項中超最多的（§12.2 第 3 點、§13）
            (view.SubItems ?? []).Where(x => x.Over > 0).OrderByDescending(x => x.Over).Take(5).ToList());
    }

    public async Task<MonthEndReport> CloseAsync(string userId, int periodId, CloseMonthRequest req, CancellationToken ct = default)
    {
        var report = await ReportAsync(userId, periodId, ct);
        if (!report.CanClose) throw new ValidationException(report.CannotCloseReason ?? "現在還不能月結。");

        var period = await periods.LoadAsync(userId, periodId, ct);
        var view = await periods.ComputeAsync(period, ct);
        var result = view.Pool.Balance;
        var next = period.EndDate.AddDays(1);
        var label = $"{period.StartDate:M/d}–{period.EndDate:M/d}";

        var monthEnd = new MonthEnd
        {
            UserId = userId,
            PeriodId = period.Id,
            Result = result,
            Decision = result < 0 ? req.Decision : ShortfallChoice.Pool,
            ClosedAt = clock.UtcNow,
        };
        db.MonthEnds.Add(monthEnd);
        await db.SaveChangesAsync(ct);

        decimal carry = 0;
        if (result > 0)
        {
            // 結餘放進下一期的待分配池（§12.2 第 6 步）
            carry = result;
            AddCarry(userId, period.Id, monthEnd.Id, next, result, $"{label} 結餘");
        }
        else if (result < 0)
        {
            var gap = -result;
            switch (req.Decision)
            {
                case ShortfallChoice.Split:
                    AddCarry(userId, period.Id, monthEnd.Id, next, -Math.Ceiling(gap / 2), $"{label} 缺口（分兩期之一）");
                    AddCarry(userId, period.Id, monthEnd.Id, next.AddMonths(1), -Math.Floor(gap / 2), $"{label} 缺口（分兩期之二）");
                    carry = -gap;
                    break;
                case ShortfallChoice.Savings:
                    var account = await db.CashAccounts.FirstOrDefaultAsync(a => a.Id == req.AccountId && a.UserId == userId && !a.IsArchived, ct)
                                  ?? throw new ValidationException("要選一個存款帳戶來吸收缺口。");
                    if (AccountService.IsLiability(account.Type)) throw new ValidationException("不能用信用卡吸收缺口。");
                    db.AssetAdjustments.Add(new AssetAdjustment
                    {
                        UserId = userId, CashAccountId = account.Id, Date = period.EndDate, Amount = -gap,
                        Note = $"{label} 月結缺口從存款吸收", Source = "monthend", PeriodId = period.Id, CreatedAt = clock.UtcNow,
                    });
                    break;
                default: // Pool / NextPeriod：下一期慢慢還
                    AddCarry(userId, period.Id, monthEnd.Id, next, -gap, $"{label} 缺口");
                    carry = -gap;
                    break;
            }
        }
        monthEnd.CarryAmount = carry;

        if (req.SlotChanges.Count > 0)
        {
            await settings.ApplySlotChangesAsync(userId, req.SlotChanges, next, $"{label} 月結調整", ct);
        }

        // 不可修改的快照：當期結果、報表、做了哪些決定
        monthEnd.Snapshot = JsonSerializer.Serialize(new { report, req.Decision, req.SlotChanges, result, carry, view.Pool.Lines });
        period.ClosedAt = monthEnd.ClosedAt;
        await db.SaveChangesAsync(ct);
        return await ReportAsync(userId, periodId, ct);
    }

    private void AddCarry(string userId, int periodId, int monthEndId, DateOnly target, decimal amount, string label)
    {
        if (amount == 0) return;
        db.PeriodCarryovers.Add(new PeriodCarryover
        {
            UserId = userId, SourcePeriodId = periodId, TargetDate = target, Amount = amount, Label = label,
            MonthEndId = monthEndId, CreatedAt = clock.UtcNow,
        });
    }

    public static string Message(decimal result, ShortfallChoice decision) => result switch
    {
        > 0 => $"上期省了 {result:N0}，已放進待分配池。",
        < 0 => decision switch
        {
            ShortfallChoice.Split => $"上期超支 {-result:N0}，分兩期從待分配池還。",
            ShortfallChoice.Savings => $"上期超支 {-result:N0}，已從存款吸收。",
            _ => $"上期超支 {-result:N0}，這期從待分配池慢慢還。",
        },
        _ => "上期剛好用完。",
    };

    private async Task<bool> HasClosingReconciliationAsync(string userId, BudgetPeriod period, CancellationToken ct)
    {
        var from = period.EndDate.AddDays(-6);
        var recons = await db.Reconciliations.AsNoTracking().Where(r => r.UserId == userId && r.IsFull && r.Date >= from && r.Date <= period.EndDate).ToListAsync(ct);
        var all = await db.Reconciliations.AsNoTracking().Where(r => r.UserId == userId).ToListAsync(ct);
        var active = all.Active().Select(r => r.Id).ToHashSet();
        return recons.Any(r => active.Contains(r.Id));
    }

    /// <summary>最常超支的時段／分類（例：午餐 12 天超預算）。</summary>
    private static List<OverspendItem> TopOverspends(PeriodView view)
    {
        var slotOvers = view.Entries
            .Where(e => e.SlotId is not null && e.Diff > 0)
            .GroupBy(e => $"{e.CategoryName}・{e.SlotName}")
            .Select(g => new OverspendItem(g.Key, g.Select(e => e.Date).Distinct().Count(), g.Sum(e => e.Diff)));
        var envelopeOvers = view.Categories
            .Where(c => c.Mode == BudgetMode.Envelope && c.Spent > c.Budget)
            .Select(c => new OverspendItem($"{c.Name}（月額度）", 0, c.Spent - c.Budget));
        return slotOvers.Concat(envelopeOvers).OrderByDescending(x => x.Days).ThenByDescending(x => x.Total).Take(5).ToList();
    }

    private static List<SlotSuggestion> SlotSuggestions(PeriodView view)
    {
        var names = view.Categories.ToDictionary(c => c.CategoryId, c => c.Name);
        return view.Days.SelectMany(d => d.Slots.Select(s => (d, s)))
            .GroupBy(x => (x.s.CategoryId, x.s.SlotId))
            .Select(g =>
            {
                var reported = g.Where(x => x.s.Actual is not null).ToList();
                var weekday = g.FirstOrDefault(x => !x.d.IsHoliday).s;
                var holiday = g.FirstOrDefault(x => x.d.IsHoliday).s;
                return new SlotSuggestion(g.Key.CategoryId, names.GetValueOrDefault(g.Key.CategoryId, ""), g.Key.SlotId, g.First().s.Name,
                    weekday?.BasePlanned ?? 0, holiday?.BasePlanned ?? 0, reported.Count,
                    reported.Count == 0 ? 0 : Math.Round(reported.Average(x => x.s.Actual!.Value), 0),
                    reported.Count == 0 ? 0 : Math.Round(reported.Average(x => x.s.BasePlanned), 0));
            })
            .ToList();
    }

    /// <summary>目標進度與預計達成日：用每期的儲蓄額度（儲蓄類分類）推估。</summary>
    private async Task<List<GoalProgress>> GoalsAsync(string userId, PeriodView view, CancellationToken ct)
    {
        var a = await assets.GetAsync(userId, false, ct);
        var perPeriod = view.Categories.Where(c => c.Group == CategoryGroup.Savings).Sum(c => c.Budget);
        var today = view.Today;
        return a.Goals.Select(g =>
        {
            DateOnly? eta = null;
            var gap = g.TargetAmount - g.Current;
            if (gap <= 0) eta = today;
            else if (perPeriod > 0) eta = today.AddMonths((int)Math.Ceiling(gap / perPeriod));
            return new GoalProgress(g.Name, g.Current, g.TargetAmount, g.Progress, g.TargetDate, eta);
        }).ToList();
    }
}
