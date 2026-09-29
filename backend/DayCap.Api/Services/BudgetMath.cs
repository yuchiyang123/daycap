using DayCap.Api.Models.Entities;
using DayCap.Api.Models.Settings;
using DayCap.Api.Services.Allocation;

namespace DayCap.Api.Services;

/// <summary>
/// 由一份設定算出一期的整期額度（§7）與餐費每格單價（§8.2）。PlanBuilder 和設定頁的即時合計、
/// 儲存前檢查共用這一份，數字才會一致。
/// </summary>
public static class BudgetMath
{
    /// <summary>
    /// 各分類整期額度：固定支出＝這期的固定項目加總；「用 %」＝基準 × %；沒勾 %＝固定金額。
    /// 基準依 PercentBase：佔月收入，或佔「月收入扣掉固定支出後」（§7）。
    /// % 的零頭用最大餘額法：每個分類先無條件捨去，再把「基準 × % 總和」捨去後剩的整數元，依小數部分大的先補。
    /// </summary>
    public static Dictionary<int, decimal> CategoryBudgets(SettingsDocument doc, IReadOnlyList<PeriodFixedCharge> charges, decimal incomeBase)
    {
        var result = new Dictionary<int, decimal>();
        var fixedTotal = charges.Sum(f => f.Amount);
        var pctBase = doc.Base == PercentBase.AfterFixed ? Math.Max(0, incomeBase - fixedTotal) : incomeBase;

        var percent = doc.Categories.Where(c => c.Mode != BudgetMode.Fixed && c.IsPercent).ToList();
        var raws = percent.Select(c => pctBase * c.Percent / 100m).ToList();
        var floors = raws.Select(Math.Floor).ToList();
        var left = (int)(Math.Floor(raws.Sum()) - floors.Sum());
        foreach (var i in Enumerable.Range(0, percent.Count).OrderByDescending(i => raws[i] - floors[i]).ThenBy(i => i).Take(Math.Max(0, left)))
        {
            floors[i] += 1;
        }
        for (var i = 0; i < percent.Count; i++) result[percent[i].Id] = floors[i];

        foreach (var c in doc.Categories)
        {
            if (c.Mode == BudgetMode.Fixed) result[c.Id] = charges.Where(f => f.CategoryId == c.Id).Sum(f => f.Amount);
            else if (!c.IsPercent) result[c.Id] = Math.Max(0, c.Amount ?? 0);
        }
        return result;
    }

    public static List<PeriodFixedCharge> FixedCharges(SettingsDocument doc, DateOnly start, DateOnly end) =>
        doc.Categories.Where(c => c.Mode == BudgetMode.Fixed).SelectMany(c => FixedChargesFor(c, start, end)).ToList();

    /// <summary>
    /// §8.2 自動分配：把某個每日類分類的額度，用分配器分成每個時段在平日 / 假日的單價。
    /// 格子 key 是 "{時段Id}:w"（平日）與 "{時段Id}:h"（假日）。
    /// 分配器由使用者實作（§8.1）；還沒實作或回傳錯誤時，回傳 null 與原因，呼叫端改用手動金額。
    /// </summary>
    public static (Dictionary<string, decimal>? Units, decimal Leftover, string? Problem) AutoUnits(
        CategoryDoc cat, decimal budget, int weekdays, int holidays)
    {
        if (cat.Auto is not { Enabled: true } auto) return (null, 0, null);
        var cells = MealCells(cat, weekdays, holidays);
        try
        {
            var result = Allocator.Allocate(Math.Max(0, budget), cells, auto.RoundingUnit <= 0 ? 1 : auto.RoundingUnit);
            if (result.Errors.Count > 0) return (null, 0, string.Join("；", result.Errors));
            return (result.UnitAmount.ToDictionary(k => k.Key, k => k.Value), result.Leftover, null);
        }
        catch (NotImplementedException)
        {
            return (null, 0, "分配器尚未實作（§8.1）");
        }
    }

    public static List<AllocationCell> MealCells(CategoryDoc cat, int weekdays, int holidays)
    {
        var multiplier = cat.Auto?.HolidayMultiplier is > 0 ? cat.Auto.HolidayMultiplier : 1m;
        var cells = new List<AllocationCell>();
        foreach (var s in cat.Slots)
        {
            var weight = s.Weight ?? 1m;
            cells.Add(new AllocationCell($"{s.Id}:w", weekdays, weight, s.WorkdayLock, s.WorkdayFloor, 0));
            cells.Add(new AllocationCell($"{s.Id}:h", holidays, s.HolidayWeight ?? weight * multiplier, s.HolidayLock, s.HolidayFloor, 0));
        }
        return cells;
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
