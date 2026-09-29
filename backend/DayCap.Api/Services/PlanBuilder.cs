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

    /// <summary>每日類「額度 − 排程」差額那一行的說明（自動分配時是分配器零頭，§8.2「零頭來源」）。</summary>
    public Dictionary<int, string> GapLabels { get; init; } = [];

    /// <summary>給使用者看的提醒，例如「分配器尚未實作，先用手動金額」。</summary>
    public List<string> Warnings { get; init; } = [];

    /// <summary>超支護欄（§10.2）：每個時段最多被攤到原本的幾 %。</summary>
    public decimal FloorPercent { get; init; } = 50m;

    /// <summary>從前面的期間結轉過來的金額（月結、延後的超支），由 PeriodService 填入。</summary>
    public List<PlanLine> CarryIns { get; } = [];
}

/// <summary>
/// 由設定版本算出一期的計畫：
/// - 收入、各分類整期額度、固定支出：用期間第一天有效的版本（期中改了下期才生效）。
/// - 每個時段每一天的金額：用那一天有效的版本；和「第一天的版本一路用到底」的差額另外列成 VersionLines。
/// - 每日類開了自動分配（§8.2）：每段版本用「整期額度 − 前面已經排掉的」對剩下的平日 / 假日天數重算單價。
/// </summary>
public static class PlanBuilder
{
    public static PeriodPlan Build(DateOnly start, DateOnly end, SettingsTimeline timeline, IReadOnlyDictionary<DateOnly, DayInfo> days,
        decimal? previousActualIncome = null)
    {
        var startDoc = timeline.For(start).Doc;
        bool IsHoliday(DateOnly d) => days.TryGetValue(d, out var i) ? i.IsHoliday : d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var warnings = new List<string>();

        // ---- 收入、分類與整期額度（第一天的版本）----
        var income = startDoc.Kind == IncomeKind.Variable && previousActualIncome is { } prev ? prev : startDoc.MonthlyIncome;
        if (startDoc.Kind == IncomeKind.Variable)
        {
            warnings.Add(previousActualIncome is null
                ? "非固定收入：還沒有上期實際收入，先用設定的月收入。"
                : "非固定收入：這期以上期實際收入當基準。");
        }
        var fixedCharges = BudgetMath.FixedCharges(startDoc, start, end);
        var budgets = BudgetMath.CategoryBudgets(startDoc, fixedCharges, income);
        var categories = startDoc.Categories.Select((c, i) => new PeriodCategory
        {
            CategoryId = c.Id, Name = c.Name, Group = c.Group, Mode = c.Mode, SortOrder = i, Budget = budgets[c.Id],
        }).ToList();

        // 期中才新增的分類：這期沒有額度（0），它的排程從待定區出
        var laterIndex = categories.Count;
        foreach (var (_, doc) in timeline.Versions.Where(v => v.Version.EffectiveFrom > start && v.Version.EffectiveFrom <= end))
        {
            foreach (var c in doc.Categories.Where(c => c.Mode != BudgetMode.Fixed && categories.All(x => x.CategoryId != c.Id)))
            {
                categories.Add(new PeriodCategory { CategoryId = c.Id, Name = c.Name, Group = c.Group, Mode = c.Mode, SortOrder = laterIndex++, Budget = 0 });
            }
        }

        var allDays = Enumerable.Range(0, end.DayNumber - start.DayNumber + 1).Select(i => start.AddDays(i)).ToList();
        int Weekdays(DateOnly from) => allDays.Count(d => d >= from && !IsHoliday(d));
        int Holidays(DateOnly from) => allDays.Count(d => d >= from && IsHoliday(d));

        // ---- 第一天的版本一路用到底（基準），自動分配用整期天數 ----
        var gapLabels = new Dictionary<int, string>();
        var baselineUnits = new Dictionary<int, Dictionary<string, decimal>?>();
        foreach (var c in startDoc.Categories.Where(c => c.Mode == BudgetMode.Daily))
        {
            var (units, _, problem) = BudgetMath.AutoUnits(c, budgets[c.Id], Weekdays(start), Holidays(start));
            baselineUnits[c.Id] = units;
            if (units is not null) gapLabels[c.Id] = $"{c.Name}自動分配的零頭";
            if (problem is not null) warnings.Add($"「{c.Name}」自動分配沒有執行：{problem}，先用手動金額。");
        }

        decimal Amount(CategoryDoc c, SlotDoc s, bool holiday, Dictionary<string, decimal>? units) =>
            units is not null && units.TryGetValue($"{s.Id}:{(holiday ? 'h' : 'w')}", out var u) ? u : holiday ? s.HolidayAmount : s.WorkdayAmount;

        // ---- 每天每個時段 ----
        var allocations = new List<DayAllocation>();
        var segmentUnits = new Dictionary<(int VersionId, int CategoryId), Dictionary<string, decimal>?>();
        foreach (var d in allDays)
        {
            var holiday = IsHoliday(d);
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
                var docCat = doc.Categories.FirstOrDefault(c => c.Id == cat.Id && c.Mode == BudgetMode.Daily);
                var docSlot = docCat?.Slots.FirstOrDefault(s => s.Id == slotId);
                var startCat = startDoc.Categories.FirstOrDefault(c => c.Id == cat.Id && c.Mode == BudgetMode.Daily);
                var startSlot = startCat?.Slots.FirstOrDefault(s => s.Id == slotId);

                decimal planned = 0;
                if (docCat is not null && docSlot is not null)
                {
                    Dictionary<string, decimal>? units = null;
                    if (docCat.Auto is { Enabled: true } && budgets.ContainsKey(docCat.Id))
                    {
                        if (version.EffectiveFrom <= start || ReferenceEquals(doc, startDoc))
                        {
                            units = baselineUnits.GetValueOrDefault(docCat.Id);
                        }
                        else if (!segmentUnits.TryGetValue((version.Id, docCat.Id), out units))
                        {
                            // 期中改自動分配設定（§4.1）：整期額度 − 這段之前已經排掉的，對剩下的天數重算
                            var used = allocations.Where(a => a.CategoryId == docCat.Id && a.Date < d).Sum(a => a.Planned);
                            (units, _, var problem) = BudgetMath.AutoUnits(docCat, budgets[docCat.Id] - used, Weekdays(d), Holidays(d));
                            if (problem is not null) warnings.Add($"「{docCat.Name}」{d:M/d} 起的自動分配沒有執行：{problem}，先用手動金額。");
                            segmentUnits[(version.Id, docCat.Id)] = units;
                        }
                    }
                    planned = Amount(docCat, docSlot, holiday, units);
                }
                var baseline = startCat is not null && startSlot is not null
                    ? Amount(startCat, startSlot, holiday, baselineUnits.GetValueOrDefault(startCat.Id))
                    : 0;
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
        foreach (var d in allDays)
        {
            foreach (var (version, doc) in timeline.CreatedOnButLater(d))
            {
                foreach (var c in doc.Categories.Where(c => c.Mode == BudgetMode.Daily))
                {
                    foreach (var s in c.Slots)
                    {
                        if (!byKey.TryGetValue((d, s.Id), out var a)) continue;
                        var lower = a.IsHoliday ? s.HolidayAmount : s.WorkdayAmount;
                        if (lower < a.Planned) lowerings.Add(new TodayLowering(d, c.Id, s.Id, s.Name, lower, version.CreatedAt));
                    }
                }
            }
        }

        return new PeriodPlan
        {
            Income = income,
            LogicalDayStart = startDoc.LogicalDayStart,
            Categories = categories,
            Allocations = allocations,
            FixedCharges = fixedCharges,
            VersionLines = versionLines,
            Lowerings = lowerings,
            WeekdayCount = allDays.Count(d => !IsHoliday(d)),
            HolidayCount = allDays.Count(IsHoliday),
            GapLabels = gapLabels,
            Warnings = warnings.Distinct().ToList(),
            FloorPercent = startDoc.FloorPercent,
        };
    }
}
