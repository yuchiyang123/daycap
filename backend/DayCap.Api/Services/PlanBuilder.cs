using DayCap.Api.Models.Entities;
using DayCap.Api.Models.Settings;

namespace DayCap.Api.Services;

/// <summary>期中設定變更造成的排程增減，記在待定區（日期＝那個版本的生效日）。</summary>
public sealed record PlanLine(DateOnly Date, decimal Amount, string Label);

/// <summary>
/// 今天建立、明天生效的版本把某個時段調低了：今天這個時段如果在 AsOfUtc 之前還沒回報，
/// 就取較低值（§4.1 決定），差額進待定區。
/// </summary>
public sealed record TodayLowering(DateOnly Date, int CategoryId, int SlotId, string SlotName, decimal NewAmount, DateTime AsOfUtc);

/// <summary>一期的計畫：全部由設定版本 + 行事曆算出，不存資料庫（§2.2）。</summary>
public sealed class PeriodPlan
{
    public decimal Income { get; init; }
    public string LogicalDayStart { get; init; } = SettingsService.DefaultDayStart;
    public List<PeriodCategory> Categories { get; init; } = [];
    public List<DayAllocation> Allocations { get; init; } = [];
    public List<PeriodFixedCharge> FixedCharges { get; init; } = [];
    public List<PlanLine> VersionLines { get; init; } = [];
    public List<TodayLowering> Lowerings { get; init; } = [];
    public int WeekdayCount { get; init; }
    public int HolidayCount { get; init; }
}

/// <summary>
/// 由設定版本算出一期的計畫：
/// - 收入、各分類整期額度、固定支出：用期間第一天有效的版本（期中改了下期才生效）。
/// - 每個時段每一天的金額：用那一天有效的版本；和「第一天的版本一路用到底」的差額另外列成 VersionLines。
/// </summary>
public static class PlanBuilder
{
    public static PeriodPlan Build(DateOnly start, DateOnly end, SettingsTimeline timeline, IReadOnlyDictionary<DateOnly, DayInfo> days)
    {
        var startDoc = timeline.For(start).Doc;
        bool IsHoliday(DateOnly d) => days.TryGetValue(d, out var i) ? i.IsHoliday : d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

        // ---- 分類與整期額度（第一天的版本）----
        var categories = new List<PeriodCategory>();
        var fixedCharges = new List<PeriodFixedCharge>();
        for (var i = 0; i < startDoc.Categories.Count; i++)
        {
            var c = startDoc.Categories[i];
            var charges = c.Mode == BudgetMode.Fixed ? FixedChargesFor(c, start, end).ToList() : [];
            fixedCharges.AddRange(charges);
            categories.Add(new PeriodCategory
            {
                CategoryId = c.Id,
                Name = c.Name,
                Group = c.Group,
                Mode = c.Mode,
                SortOrder = i,
                Budget = c.Mode == BudgetMode.Fixed
                    ? charges.Sum(f => f.Amount)
                    : Math.Round(startDoc.MonthlyIncome * c.Percent / 100m, 0, MidpointRounding.AwayFromZero),
            });
        }

        // 期中才新增的分類：這期沒有額度（0），它的排程從待定區出
        var laterIndex = categories.Count;
        foreach (var (_, doc) in timeline.Versions.Where(v => v.Version.EffectiveFrom > start && v.Version.EffectiveFrom <= end))
        {
            foreach (var c in doc.Categories.Where(c => c.Mode != BudgetMode.Fixed && categories.All(x => x.CategoryId != c.Id)))
            {
                categories.Add(new PeriodCategory { CategoryId = c.Id, Name = c.Name, Group = c.Group, Mode = c.Mode, SortOrder = laterIndex++, Budget = 0 });
            }
        }

        // ---- 每天每個時段 ----
        var allocations = new List<DayAllocation>();
        var weekdays = 0;
        var holidays = 0;
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            var holiday = IsHoliday(d);
            if (holiday) holidays++; else weekdays++;
            var (version, doc) = timeline.For(d);

            var slots = new Dictionary<int, (CategoryDoc Cat, SlotDoc Slot, int Order)>();
            void Collect(SettingsDocument source)
            {
                for (var ci = 0; ci < source.Categories.Count; ci++)
                {
                    var c = source.Categories[ci];
                    if (c.Mode != BudgetMode.Daily) continue;
                    for (var si = 0; si < c.Slots.Count; si++) slots.TryAdd(c.Slots[si].Id, (c, c.Slots[si], ci * 100 + si));
                }
            }
            Collect(doc);
            Collect(startDoc); // 第一天有、之後被刪掉的時段也要列出來（金額 0），差額才算得對

            foreach (var (slotId, (cat, slot, order)) in slots)
            {
                var planned = AmountOf(doc, slotId, holiday);
                var baseline = AmountOf(startDoc, slotId, holiday);
                if (planned <= 0 && baseline <= 0) continue;
                allocations.Add(new DayAllocation
                {
                    Date = d,
                    CategoryId = cat.Id,
                    SlotId = slotId,
                    SlotName = slot.Name,
                    SortOrder = order,
                    IsHoliday = holiday,
                    Planned = planned,
                    BaselinePlanned = baseline,
                    VersionEffectiveFrom = version.EffectiveFrom,
                });
            }
        }

        var versionLines = allocations
            .Where(a => a.Planned != a.BaselinePlanned)
            .GroupBy(a => a.VersionEffectiveFrom)
            .Select(g =>
            {
                var delta = g.Sum(a => a.BaselinePlanned - a.Planned);
                var date = g.Key < start ? start : g.Key;
                return new PlanLine(date, delta, delta < 0
                    ? $"設定變更（{date:M/d} 起）：之後排程增加"
                    : $"設定變更（{date:M/d} 起）：之後排程減少");
            })
            .Where(l => l.Amount != 0)
            .OrderBy(l => l.Date)
            .ToList();

        // ---- 今天改設定、今天還沒回報的時段取較低值 ----
        var byKey = allocations.ToDictionary(a => (a.Date, a.SlotId));
        var lowerings = new List<TodayLowering>();
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            foreach (var (version, doc) in timeline.CreatedOnButLater(d))
            {
                foreach (var c in doc.Categories.Where(c => c.Mode == BudgetMode.Daily))
                {
                    foreach (var s in c.Slots)
                    {
                        if (!byKey.TryGetValue((d, s.Id), out var a)) continue;
                        var lower = byKey[(d, s.Id)].IsHoliday ? s.HolidayAmount : s.WorkdayAmount;
                        if (lower < a.Planned) lowerings.Add(new TodayLowering(d, c.Id, s.Id, s.Name, lower, version.CreatedAt));
                    }
                }
            }
        }

        return new PeriodPlan
        {
            Income = startDoc.MonthlyIncome,
            LogicalDayStart = startDoc.LogicalDayStart,
            Categories = categories,
            Allocations = allocations,
            FixedCharges = fixedCharges,
            VersionLines = versionLines,
            Lowerings = lowerings,
            WeekdayCount = weekdays,
            HolidayCount = holidays,
        };
    }

    private static decimal AmountOf(SettingsDocument doc, int slotId, bool holiday)
    {
        foreach (var c in doc.Categories)
        {
            if (c.Mode != BudgetMode.Daily) continue;
            foreach (var s in c.Slots)
            {
                if (s.Id == slotId) return holiday ? s.HolidayAmount : s.WorkdayAmount;
            }
        }
        return 0;
    }

    private static IEnumerable<PeriodFixedCharge> FixedChargesFor(CategoryDoc c, DateOnly start, DateOnly end)
    {
        foreach (var item in c.FixedItems.Where(f => f.IsActive && (f.ActiveFrom is null || f.ActiveFrom <= start)))
        {
            DateOnly? due;
            if (item.Cycle != BillingCycle.Monthly)
            {
                var first = item.BillingMonth ?? 1;
                int[] months = item.Cycle == BillingCycle.Quarterly
                    ? [first, (first + 2) % 12 + 1, (first + 5) % 12 + 1, (first + 8) % 12 + 1]
                    : [first];
                due = FindDueDate(start, end, item.DueDay ?? 1, months);
                if (due is null) continue; // 這期不是年繳 / 季繳的月份
            }
            else
            {
                due = item.DueDay is { } dd ? FindDueDate(start, end, dd, null) : null;
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
    private static DateOnly? FindDueDate(DateOnly start, DateOnly end, int dueDay, int[]? months)
    {
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (months is not null && !months.Contains(d.Month)) continue;
            var target = Math.Min(dueDay, DateTime.DaysInMonth(d.Year, d.Month));
            if (d.Day == target) return d;
        }
        return null;
    }
}
