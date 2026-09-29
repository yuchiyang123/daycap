using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;

namespace DayCap.Api.Services;

public record DayInfo(bool IsHoliday, string? Name);

/// <summary>
/// 一次完整對帳算出的「沒交代的差異」（§12.1）：實際淨額 − 預期淨額。由服務層算好傳進來，
/// 引擎在它的登錄時間點重播：正的進待分配池；負的照超支規則（先池、再攤到之後所有每日類時段）。
/// </summary>
public record ReconDiff(int Id, DateOnly Date, DateTime CreatedAt, decimal Expected, decimal Actual, bool UsePool)
{
    public decimal Diff => Actual - Expected;
}

/// <summary>
/// 純計算：拿一期的計畫（<see cref="PeriodPlan"/>，由設定版本算出）+ 這期所有「有效的事實」，
/// 照登錄時間重播，算出每天每個時段的有效額度、待定區流水與餘額、各分類花費。
/// 沒有 I/O，衍生數字不落地（§2.2）。
///
/// 規則：
/// 1. 沒回報的時段 = 照預算花掉。
/// 2. 回報比預算少 → 差額進待定區。
/// 3. 回報比預算多 → 勾了「先從待定區扣」就先扣待定區（扣到 0），剩下的依比例攤到同分類「之後」
///    每一個還沒回報的時段；後面沒有可攤的額度，才從待定區扣成負數。
///    （§10.1 規格是「平均扣」，做第 10 節時再改，見 DESIGN_NOTES。）
/// 4. 月額度類超過額度的部分從待定區扣（或不勾就讓它維持負數）。
/// 5. 「之後」以 max(回報的日期, 登錄當天的邏輯日) 為界，重播結果不會因為時間經過而改變。
/// 6. 今天改設定調降的時段（§4.1 決定）：在改設定之前還沒回報的，取較低值，差額進待定區。
/// </summary>
public static class BudgetEngine
{
    public static PeriodView Compute(
        BudgetPeriod period,
        PeriodPlan plan,
        DateOnly today,
        Func<DateTime, DateOnly> toLogicalDate,
        IReadOnlyDictionary<DateOnly, DayInfo> days,
        IReadOnlyList<ReconDiff>? reconDiffs = null)
    {
        var categories = plan.Categories.OrderBy(c => c.SortOrder).ToList();
        var categoryById = categories.ToDictionary(c => c.CategoryId);

        var entries = period.Entries.Active();
        var transfers = period.PoolTransfers.Active();
        var incomeAdjustments = period.IncomeAdjustments.Active().OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToList();

        var allocations = plan.Allocations
            .OrderBy(a => a.Date).ThenBy(a => a.SortOrder).ThenBy(a => a.SlotId)
            .ToList();
        var allocByKey = allocations.ToDictionary(a => (a.Date, a.SlotId));
        var effective = allocations.ToDictionary(a => (a.Date, a.SlotId), a => a.Planned);

        var slotEntries = entries
            .Where(e => e.SlotId is not null)
            .GroupBy(e => (e.Date, e.SlotId!.Value))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).First());
        var reportedKeys = slotEntries.Keys.ToHashSet();

        // ---- 期初待定區 ----
        var lines = new List<PoolLine>();
        var unallocated = plan.Income - categories.Sum(c => c.Budget);
        lines.Add(new PoolLine(period.StartDate, unallocated, "Opening", "收入扣掉各分類額度後未分配", null, null));
        foreach (var c in categories.Where(c => c.Mode == BudgetMode.Daily))
        {
            var baseline = allocations.Where(a => a.CategoryId == c.CategoryId).Sum(a => a.BaselinePlanned);
            var gap = c.Budget - baseline;
            if (gap != 0)
            {
                lines.Add(new PoolLine(period.StartDate, gap, "Opening",
                    plan.GapLabels.TryGetValue(c.CategoryId, out var gapLabel) && gap > 0 ? gapLabel
                    : gap > 0 ? $"{c.Name}額度未排進日程的部分" : $"{c.Name}日程排超過額度", null, null));
            }
        }
        foreach (var adj in incomeAdjustments)
        {
            lines.Add(new PoolLine(period.StartDate, adj.Amount, "Income", $"薪資調整：{IncomeLabel(adj)}", null, null));
        }
        foreach (var v in plan.VersionLines)
        {
            lines.Add(new PoolLine(v.Date, v.Amount, "Settings", v.Label, null, null));
        }
        foreach (var c in plan.CarryIns)
        {
            lines.Add(new PoolLine(c.Date, c.Amount, "Carryover", c.Label, null, null));
        }
        var opening = lines.Sum(l => l.Amount);
        var pool = opening;

        var allocatedExtra = categories.ToDictionary(c => c.CategoryId, _ => 0m);
        var envelopeBudget = categories.Where(c => c.Mode == BudgetMode.Envelope).ToDictionary(c => c.CategoryId, c => c.Budget);

        // ---- 依序重播 ----
        var envelopeSpent = categories.Where(c => c.Mode == BudgetMode.Envelope).ToDictionary(c => c.CategoryId, _ => 0m);
        var entryViews = new Dictionary<int, EntryView>();

        var events = entries.Select(e => new Event(e.CreatedAt, 0, e.Id, e, null, null, null))
            .Concat(transfers.Select(t => new Event(t.CreatedAt, 1, t.Id, null, t, null, null)))
            .Concat(plan.Lowerings.Select((l, i) => new Event(l.AsOfUtc, 2, i, null, null, l, null)))
            .Concat((reconDiffs ?? []).Select(r => new Event(r.CreatedAt, 3, r.Id, null, null, null, r)))
            .OrderBy(x => x.At).ThenBy(x => x.Order).ThenBy(x => x.Id)
            .ToList();
        var reconViews = new List<ReconciliationView>();

        foreach (var ev in events)
        {
            if (ev.Recon is { } rd)
            {
                reconViews.Add(ApplyReconciliation(rd, categories, allocations, effective, reportedKeys, toLogicalDate, plan.FloorPercent, ref pool, lines));
                continue;
            }

            if (ev.Lowering is { } low)
            {
                var key = (low.Date, low.SlotId);
                var reportedBefore = slotEntries.TryGetValue(key, out var se) && se.CreatedAt < low.AsOfUtc;
                if (!reportedBefore && effective.TryGetValue(key, out var eff) && low.NewAmount < eff)
                {
                    pool += eff - low.NewAmount;
                    effective[key] = low.NewAmount;
                    lines.Add(new PoolLine(low.Date, eff - low.NewAmount, "Lower", $"今天改設定：{low.SlotName}調降", null, null));
                }
                continue;
            }

            if (ev.Transfer is { } t)
            {
                if (t.CategoryId is { } tc && categoryById.TryGetValue(tc, out var target) && target.Mode != BudgetMode.Fixed)
                {
                    pool -= t.Amount;
                    allocatedExtra[tc] += t.Amount;
                    if (target.Mode == BudgetMode.Envelope)
                    {
                        envelopeBudget[tc] += t.Amount;
                    }
                    else
                    {
                        // 每日類：依比例加到之後每一個還沒回報的時段
                        var cutoff = Max(t.Date, toLogicalDate(t.CreatedAt));
                        var targets = allocations
                            .Where(a => a.CategoryId == tc && a.Date > cutoff && !reportedKeys.Contains((a.Date, a.SlotId)))
                            .ToList();
                        var shares = DistributeAll(t.Amount, targets.Select(a => Math.Max(1m, effective[(a.Date, a.SlotId)])).ToList());
                        for (var i = 0; i < targets.Count; i++) effective[(targets[i].Date, targets[i].SlotId)] += shares[i];
                    }
                    lines.Add(new PoolLine(t.Date, -t.Amount, "Allocate",
                        string.IsNullOrWhiteSpace(t.Note) ? $"分配給{target.Name}" : t.Note, null, t.Id));
                    continue;
                }

                pool += t.Amount;
                lines.Add(new PoolLine(t.Date, t.Amount, "Transfer", string.IsNullOrWhiteSpace(t.Note) ? "手動調整" : t.Note, null, t.Id));
                continue;
            }

            var e = ev.Entry!;
            // 同一時段同時有多筆有效回報時（例如兩台裝置），只有最新那筆生效
            if (e.SlotId is { } sid && slotEntries[(e.Date, sid)].Id != e.Id) continue;
            if (!categoryById.TryGetValue(e.CategoryId, out var cat)) continue;

            var view = cat.Mode switch
            {
                BudgetMode.Envelope => ApplyEnvelope(e, cat, envelopeBudget[cat.CategoryId], envelopeSpent, ref pool, lines),
                _ => ApplyDaily(e, cat, allocations, allocByKey, effective, reportedKeys, toLogicalDate, plan.FloorPercent, ref pool, lines),
            };
            entryViews[e.Id] = view;
        }

        // ---- 組每日檢視 ----
        var dayViews = new List<DayView>();
        for (var d = period.StartDate; d <= period.EndDate; d = d.AddDays(1))
        {
            var info = days.TryGetValue(d, out var di) ? di : new DayInfo(d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday, null);
            var slots = allocations.Where(a => a.Date == d).Select(a =>
            {
                slotEntries.TryGetValue((d, a.SlotId), out var se);
                decimal? actual = se is not null && entryViews.TryGetValue(se.Id, out var sv) ? sv.Actual : null;
                return new SlotView(a.CategoryId, a.SlotId, a.SlotName, a.Planned, effective[(d, a.SlotId)], actual, se?.Id, a.SlotStart);
            }).ToList();

            // 有回報但當天沒有排程的時段（例如假日也去通勤）也要看得到，當成 0 額度的時段。
            foreach (var ((_, slotId), se) in slotEntries.Where(kv => kv.Key.Date == d && !allocByKey.ContainsKey(kv.Key)))
            {
                if (!entryViews.TryGetValue(se.Id, out var sv)) continue;
                slots.Add(new SlotView(se.CategoryId, slotId, sv.SlotName ?? "時段", 0, 0, sv.Actual, se.Id));
            }

            var extras = entries.Where(e => e.Date == d && e.SlotId is null && entryViews.ContainsKey(e.Id))
                .OrderBy(e => e.CreatedAt).Select(e => e.Id).ToList();
            var dayEntryIds = slots.Where(s => s.EntryId is not null).Select(s => s.EntryId!.Value).Concat(extras);
            // 當天的 +/-：只看這天的回報，沒回報 = 照預算 = 0。月額度類不算進「每日」。
            var net = dayEntryIds
                .Select(id => entryViews[id])
                .Where(v => categoryById[v.CategoryId].Mode == BudgetMode.Daily)
                .Sum(v => -v.Diff);

            var status = d < today ? "past" : d == today ? "today" : "future";
            dayViews.Add(new DayView(d, info.IsHoliday, info.Name, status,
                slots.Sum(s => s.BasePlanned), slots.Sum(s => s.Planned), net, slots, extras));
        }

        // ---- 各分類 ----
        var categoryViews = categories.Select(c =>
        {
            var scheduled = allocations.Where(a => a.CategoryId == c.CategoryId).Sum(a => a.Planned);
            decimal spent = 0, plannedRemaining = 0;
            switch (c.Mode)
            {
                case BudgetMode.Fixed:
                    spent = plan.FixedCharges.Where(f => f.CategoryId == c.CategoryId).Sum(f => f.Amount);
                    break;
                case BudgetMode.Envelope:
                    spent = envelopeSpent[c.CategoryId];
                    break;
                default:
                    foreach (var a in allocations.Where(a => a.CategoryId == c.CategoryId))
                    {
                        var key = (a.Date, a.SlotId);
                        if (slotEntries.TryGetValue(key, out var se) && entryViews.TryGetValue(se.Id, out var sv))
                            spent += sv.Actual;
                        else if (a.Date <= today)
                            spent += effective[key];
                        else
                            plannedRemaining += effective[key];
                    }
                    // 沒有排程但有回報的時段 + 額外花費
                    spent += entryViews.Values
                        .Where(v => v.CategoryId == c.CategoryId && (v.SlotId is null || !allocByKey.ContainsKey((v.Date, v.SlotId.Value))))
                        .Sum(v => v.Actual);
                    break;
            }
            return new CategoryView(c.CategoryId, c.Name, c.Group, c.Mode, c.Budget + allocatedExtra[c.CategoryId],
                scheduled, spent, plannedRemaining, spent + plannedRemaining, allocatedExtra[c.CategoryId]);
        }).ToList();

        var adjustmentViews = incomeAdjustments
            .Select(a => new IncomeAdjustmentView(a.Id, a.Kind, a.Days, a.Hours, a.Amount, a.Note, IncomeLabel(a)))
            .ToList();

        return new PeriodView(
            period.Id, period.StartDate, period.EndDate, today, plan.LogicalDayStart,
            plan.WeekdayCount, plan.HolidayCount,
            plan.Income + adjustmentViews.Sum(a => a.Amount),
            plan.Income,
            adjustmentViews,
            period.IncomeConfirmedAt is not null,
            period.SettledAt,
            new PoolView(opening, pool, lines),
            categoryViews,
            dayViews,
            entryViews.Values.OrderByDescending(v => v.CreatedAt).ThenByDescending(v => v.Id).ToList(),
            plan.FixedCharges.OrderBy(f => f.DueDate).ThenBy(f => f.Name)
                .Select(f => new FixedChargeView(f.FixedItemId, f.CategoryId, f.Name, f.Amount, f.DueDate, f.IsSubscription)).ToList(),
            transfers.OrderByDescending(t => t.CreatedAt)
                .Select(t => new PoolTransferView(t.Id, t.Date, t.Amount, t.Note, t.CategoryId)).ToList(),
            reconViews,
            plan.Warnings);
    }

    private sealed record Event(DateTime At, int Order, int Id, Entry? Entry, PoolTransfer? Transfer, TodayLowering? Lowering, ReconDiff? Recon);

    private static ReconciliationView ApplyReconciliation(
        ReconDiff r,
        List<PeriodCategory> categories,
        List<DayAllocation> allocations,
        Dictionary<(DateOnly, int), decimal> effective,
        HashSet<(DateOnly, int)> reportedKeys,
        Func<DateTime, DateOnly> toLogicalDate,
        decimal floorPercent,
        ref decimal pool,
        List<PoolLine> lines)
    {
        var diff = r.Diff;
        decimal fromPool = 0, spread = 0, unabsorbed = 0;
        if (diff > 0)
        {
            pool += diff;
            lines.Add(new PoolLine(r.Date, diff, "Reconcile", "對帳：實際餘額比預期多", null, null));
        }
        else if (diff < 0)
        {
            var over = -diff;
            if (r.UsePool)
            {
                fromPool = Math.Min(over, Math.Max(pool, 0));
                if (fromPool > 0)
                {
                    pool -= fromPool;
                    lines.Add(new PoolLine(r.Date, -fromPool, "Reconcile", "對帳：沒交代的差異", null, null));
                }
            }
            var rest = over - fromPool;
            if (rest > 0)
            {
                var daily = categories.Where(c => c.Mode == BudgetMode.Daily).Select(c => c.CategoryId).ToHashSet();
                var cutoff = Max(r.Date, toLogicalDate(r.CreatedAt));
                var targets = allocations
                    .Where(a => daily.Contains(a.CategoryId) && a.Date > cutoff && !reportedKeys.Contains((a.Date, a.SlotId)))
                    .ToList();
                (spread, _, _) = SpreadOverDays(rest, targets, effective, floorPercent);
                unabsorbed = rest - spread;
                if (unabsorbed > 0)
                {
                    pool -= unabsorbed;
                    lines.Add(new PoolLine(r.Date, -unabsorbed, "Reconcile", "對帳差異超過護欄下限，從待分配池扣", null, null));
                }
            }
        }
        return new ReconciliationView(r.Id, r.Date, r.Expected, r.Actual, diff, fromPool, spread, unabsorbed);
    }

    private static EntryView ApplyEnvelope(Entry e, PeriodCategory cat, decimal budget, Dictionary<int, decimal> envelopeSpent, ref decimal pool, List<PoolLine> lines)
    {
        var actual = Math.Max(0, e.InputAmount);
        var remaining = budget - envelopeSpent[cat.CategoryId];
        envelopeSpent[cat.CategoryId] += actual;
        var over = actual - Math.Max(remaining, 0);
        decimal envelopeOver = 0;
        if (over > 0 && e.UsePool)
        {
            envelopeOver = over;
            pool -= over;
            lines.Add(new PoolLine(e.Date, -over, "EnvelopeOver", $"{cat.Name}超過月額度", e.Id, null));
        }
        return new EntryView(e.Id, e.Date, cat.CategoryId, cat.Name, null, null, e.InputMode, e.InputAmount,
            actual, 0, actual, e.UsePool, 0, 0, 0, 0, envelopeOver, e.Note, e.IsSubscription, e.CreatedAt, 0, 0);
    }

    private static EntryView ApplyDaily(
        Entry e,
        PeriodCategory cat,
        List<DayAllocation> allocations,
        Dictionary<(DateOnly, int), DayAllocation> allocByKey,
        Dictionary<(DateOnly, int), decimal> effective,
        HashSet<(DateOnly, int)> reportedKeys,
        Func<DateTime, DateOnly> toLogicalDate,
        decimal floorPercent,
        ref decimal pool,
        List<PoolLine> lines)
    {
        DayAllocation? alloc = null;
        if (e.SlotId is { } sid) allocByKey.TryGetValue((e.Date, sid), out alloc);
        var planned = alloc is not null ? effective[(e.Date, alloc.SlotId)] : 0;

        var actual = e.SlotId is not null && e.InputMode == EntryInputMode.Overage
            ? planned + e.InputAmount
            : e.InputAmount;
        actual = Math.Max(0, actual);
        var diff = actual - planned;
        var slotName = alloc?.SlotName;

        decimal fromPool = 0, spread = 0, unabsorbed = 0;
        var spreadSlots = 0;
        var spreadDays = 0;
        var label = slotName is null ? $"{cat.Name}額外花費" : $"{cat.Name}・{slotName}";

        if (diff < 0)
        {
            pool += -diff;
            lines.Add(new PoolLine(e.Date, -diff, "Surplus", $"{label}少花", e.Id, null));
        }
        else if (diff > 0)
        {
            if (e.UsePool)
            {
                fromPool = Math.Min(diff, Math.Max(pool, 0));
                if (fromPool > 0)
                {
                    pool -= fromPool;
                    lines.Add(new PoolLine(e.Date, -fromPool, "Cover", $"{label}超支", e.Id, null));
                }
            }

            var rest = diff - fromPool;
            if (rest > 0)
            {
                var cutoff = Max(e.Date, toLogicalDate(e.CreatedAt));
                var targets = allocations
                    .Where(a => a.CategoryId == cat.CategoryId && a.Date > cutoff && !reportedKeys.Contains((a.Date, a.SlotId)))
                    .ToList();
                (spread, spreadSlots, spreadDays) = SpreadOverDays(rest, targets, effective, floorPercent);

                // 攤不完的（後面沒天數，或會把某天扣到護欄下限以下，§10.2）先從待分配池扣；
                // 使用者回報時選了「延到下一期／分兩期／從存款吸收」的話，服務層會另外記事實把它補回來
                unabsorbed = rest - spread;
                if (unabsorbed > 0)
                {
                    pool -= unabsorbed;
                    lines.Add(new PoolLine(e.Date, -unabsorbed, "Unabsorbed", $"{label}超支，超過護欄下限或後面沒天數可攤", e.Id, null));
                }
            }
        }

        return new EntryView(e.Id, e.Date, cat.CategoryId, cat.Name, e.SlotId, slotName, e.InputMode, e.InputAmount,
            actual, planned, diff, e.UsePool, fromPool, spread, spreadSlots, unabsorbed, 0, e.Note, e.IsSubscription, e.CreatedAt,
            spreadDays, spreadDays > 0 ? Math.Round(spread / spreadDays, 0) : 0);
    }

    /// <summary>
    /// 超支攤回（§10.1）：從剩餘天數「平均」扣——每天先分到一樣多（有上限的天數用完就換別天），
    /// 同一天內再依各時段目前的金額比例分。每個時段最多扣到原本排程的 floorPercent%（護欄 §10.2）。
    /// 回傳實際攤掉的金額、動到幾個時段、動到幾天；攤不完的由呼叫端處理。
    /// </summary>
    public static (decimal Spread, int Slots, int Days) SpreadOverDays(
        decimal amount, List<DayAllocation> targets, Dictionary<(DateOnly, int), decimal> effective, decimal floorPercent)
    {
        decimal Capacity(DayAllocation a) =>
            Math.Max(0, effective[(a.Date, a.SlotId)] - Math.Ceiling(a.Planned * floorPercent / 100m));

        var days = targets.Where(a => Capacity(a) > 0).GroupBy(a => a.Date).OrderBy(g => g.Key)
            .Select(g => (Date: g.Key, Slots: g.OrderBy(a => a.SortOrder).ThenBy(a => a.SlotId).ToList()))
            .ToList();
        var dayShares = EqualCapped(Math.Floor(amount), days.Select(d => d.Slots.Sum(Capacity)).ToList());

        decimal spread = 0;
        int slots = 0, touchedDays = 0;
        for (var i = 0; i < days.Count; i++)
        {
            if (dayShares[i] <= 0) continue;
            touchedDays++;
            var caps = days[i].Slots.Select(Capacity).ToList();
            var slotShares = Distribute(dayShares[i], caps);
            for (var j = 0; j < caps.Count; j++)
            {
                if (slotShares[j] <= 0) continue;
                var a = days[i].Slots[j];
                effective[(a.Date, a.SlotId)] -= slotShares[j];
                spread += slotShares[j];
                slots++;
            }
        }
        return (spread, slots, touchedDays);
    }

    /// <summary>把 amount（整數元）平均分給各天，每天不超過自己的上限；除不盡的零頭從前面的天數開始一天 1 元。</summary>
    public static List<decimal> EqualCapped(decimal amount, IReadOnlyList<decimal> caps)
    {
        var result = Enumerable.Repeat(0m, caps.Count).ToList();
        var remaining = Math.Floor(amount);
        var active = Enumerable.Range(0, caps.Count).Where(i => caps[i] >= 1).ToList();
        while (remaining > 0 && active.Count > 0)
        {
            var share = Math.Floor(remaining / active.Count);
            if (share == 0)
            {
                foreach (var i in active)
                {
                    if (remaining <= 0) break;
                    result[i] += 1;
                    remaining -= 1;
                }
                break;
            }
            var next = new List<int>();
            foreach (var i in active)
            {
                var take = Math.Min(share, Math.Floor(caps[i] - result[i]));
                result[i] += take;
                remaining -= take;
                if (caps[i] - result[i] >= 1) next.Add(i);
            }
            active = next;
        }
        return result;
    }

    /// <summary>
    /// 把 amount 依 weights 等比例拆成整數元（最大餘數法）。amount 超過總權重時只拆到總權重為止，
    /// 每一份都不會超過自己的權重（額度不會被攤成負的）。不足 1 元的零頭不拆，留給呼叫端。
    /// </summary>
    public static List<decimal> Distribute(decimal amount, IReadOnlyList<decimal> weights)
    {
        var result = Enumerable.Repeat(0m, weights.Count).ToList();
        var total = weights.Sum();
        if (amount <= 0 || total <= 0) return result;
        var take = Math.Min(Math.Floor(amount), total);
        if (take == total)
        {
            for (var i = 0; i < weights.Count; i++) result[i] = weights[i];
            return result;
        }
        return LargestRemainder(take, weights, total, capAtWeight: true);
    }

    /// <summary>把 amount（整數元）全部依 weights 等比例拆開，不設上限（用在「加」額度）。</summary>
    public static List<decimal> DistributeAll(decimal amount, IReadOnlyList<decimal> weights)
    {
        var total = weights.Sum();
        if (amount <= 0 || total <= 0) return Enumerable.Repeat(0m, weights.Count).ToList();
        return LargestRemainder(Math.Floor(amount), weights, total, capAtWeight: false);
    }

    private static List<decimal> LargestRemainder(decimal take, IReadOnlyList<decimal> weights, decimal total, bool capAtWeight)
    {
        var result = new List<decimal>(weights.Count);
        var remainders = new List<(int Index, decimal Rem)>();
        for (var i = 0; i < weights.Count; i++)
        {
            var raw = take * weights[i] / total;
            var floor = Math.Floor(raw);
            result.Add(floor);
            remainders.Add((i, raw - floor));
        }
        var left = (int)(take - result.Sum());
        foreach (var (index, _) in remainders.OrderByDescending(r => r.Rem).ThenBy(r => r.Index))
        {
            if (left <= 0) break;
            if (capAtWeight && result[index] + 1 > weights[index]) continue;
            result[index] += 1;
            left--;
        }
        return result;
    }

    public static string IncomeLabel(IncomeAdjustment a)
    {
        var qty = a.Days is > 0 ? $" {a.Days:0.##} 天" : a.Hours is > 0 ? $" {a.Hours:0.##} 小時" : "";
        var name = a.Kind switch
        {
            IncomeAdjustmentKind.SickLeave => "病假",
            IncomeAdjustmentKind.PersonalLeave => "事假",
            IncomeAdjustmentKind.MenstrualLeave => "生理假",
            IncomeAdjustmentKind.Overtime => "加班費",
            IncomeAdjustmentKind.Bonus => "獎金",
            IncomeAdjustmentKind.OtherDeduction => "其他扣款",
            _ => "其他加給",
        };
        return string.IsNullOrWhiteSpace(a.Note) ? name + qty : $"{name}{qty}（{a.Note}）";
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
}
