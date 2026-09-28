using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;

namespace DayCap.Api.Services;

public record DayInfo(bool IsHoliday, string? Name);

/// <summary>
/// 純計算：拿一個週期的快照 + 所有回報，照發生順序重播，算出每天每個時段的有效額度、
/// 待定區流水與餘額、各分類花費。沒有 I/O，方便單元測試。
///
/// 規則（照需求）：
/// 1. 沒回報的時段 = 照預算花掉，不用記帳。
/// 2. 回報比預算少 → 差額進待定區。
/// 3. 回報比預算多 → 勾了「先從待定區扣」就先扣待定區（扣到 0 為止），
///    剩下的依比例攤到同分類「之後」每一個還沒回報的時段，讓後面每天的額度遞減。
///    後面沒有可攤的額度，才從待定區扣成負數。
/// 4. 月額度類（Envelope）沒有每日時段，超過額度的部分從待定區扣（或不勾就讓它維持負數）。
/// 5. 「之後」以 max(回報的日期, 按下回報那天) 為界，所以重播結果不會因為時間經過而改變。
/// </summary>
public static class BudgetEngine
{
    public static PeriodView Compute(
        BudgetPeriod period,
        DateOnly today,
        Func<DateTime, DateOnly> toLocalDate,
        IReadOnlyDictionary<DateOnly, DayInfo> days)
    {
        var categories = period.Categories.OrderBy(c => c.SortOrder).ToList();
        var categoryById = categories.ToDictionary(c => c.CategoryId);

        var allocations = period.Allocations
            .OrderBy(a => a.Date).ThenBy(a => a.SortOrder).ThenBy(a => a.SlotId)
            .ToList();
        var allocByKey = allocations.ToDictionary(a => (a.Date, a.SlotId));
        var effective = allocations.ToDictionary(a => (a.Date, a.SlotId), a => a.Planned);

        var slotEntries = period.Entries
            .Where(e => e.SlotId is not null)
            .GroupBy(e => (e.Date, e.SlotId!.Value))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).First());
        var reportedKeys = slotEntries.Keys.ToHashSet();

        // ---- 期初待定區 ----
        var lines = new List<PoolLine>();
        var totalBudget = categories.Sum(c => c.Budget);
        var unallocated = period.Income - totalBudget;
        lines.Add(new PoolLine(period.StartDate, unallocated, "Opening", "收入扣掉各分類額度後未分配", null, null));
        foreach (var c in categories.Where(c => c.Mode == BudgetMode.Daily))
        {
            var scheduled = allocations.Where(a => a.CategoryId == c.CategoryId).Sum(a => a.Planned);
            var gap = c.Budget - scheduled;
            if (gap != 0)
            {
                lines.Add(new PoolLine(period.StartDate, gap, "Opening",
                    gap > 0 ? $"{c.Name}額度未排進日程的部分" : $"{c.Name}日程排超過額度", null, null));
            }
        }
        var opening = lines.Sum(l => l.Amount);
        var pool = opening;

        // ---- 依序重播 ----
        var envelopeSpent = categories.Where(c => c.Mode == BudgetMode.Envelope).ToDictionary(c => c.CategoryId, _ => 0);
        var entryViews = new Dictionary<int, EntryView>();

        var events = period.Entries.Select(e => (At: e.CreatedAt, Id: e.Id, Entry: (Entry?)e, Transfer: (PoolTransfer?)null))
            .Concat(period.PoolTransfers.Select(t => (At: t.CreatedAt, Id: t.Id, Entry: (Entry?)null, Transfer: (PoolTransfer?)t)))
            .OrderBy(x => x.At).ThenBy(x => x.Entry is null ? 1 : 0).ThenBy(x => x.Id)
            .ToList();

        foreach (var ev in events)
        {
            if (ev.Transfer is { } t)
            {
                pool += t.Amount;
                lines.Add(new PoolLine(t.Date, t.Amount, "Transfer", string.IsNullOrWhiteSpace(t.Note) ? "手動調整" : t.Note, null, t.Id));
                continue;
            }

            var e = ev.Entry!;
            // 同一時段被重複回報時，只有最新那筆生效（舊的留在資料庫當紀錄，不參與計算）。
            if (e.SlotId is { } sid && slotEntries[(e.Date, sid)].Id != e.Id) continue;
            if (!categoryById.TryGetValue(e.CategoryId, out var cat)) continue;

            var view = cat.Mode switch
            {
                BudgetMode.Envelope => ApplyEnvelope(e, cat, envelopeSpent, ref pool, lines),
                _ => ApplyDaily(e, cat, allocByKey, effective, reportedKeys, toLocalDate, ref pool, lines),
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
                var actual = se is not null && entryViews.TryGetValue(se.Id, out var sv) ? sv.Actual : (int?)null;
                return new SlotView(a.CategoryId, a.SlotId, a.SlotName, a.Planned, effective[(d, a.SlotId)], actual, se?.Id);
            }).ToList();

            // 有回報但當天沒有排程的時段（例如假日也去通勤）也要看得到，當成 0 額度的時段。
            foreach (var ((date, slotId), se) in slotEntries.Where(kv => kv.Key.Date == d && !allocByKey.ContainsKey(kv.Key)))
            {
                if (!entryViews.TryGetValue(se.Id, out var sv)) continue;
                slots.Add(new SlotView(se.CategoryId, slotId, sv.SlotName ?? "時段", 0, 0, sv.Actual, se.Id));
            }

            var extras = period.Entries.Where(e => e.Date == d && e.SlotId is null && entryViews.ContainsKey(e.Id))
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
            int spent, plannedRemaining;
            switch (c.Mode)
            {
                case BudgetMode.Fixed:
                    spent = period.FixedCharges.Where(f => f.CategoryId == c.CategoryId).Sum(f => f.Amount);
                    plannedRemaining = 0;
                    break;
                case BudgetMode.Envelope:
                    spent = envelopeSpent[c.CategoryId];
                    plannedRemaining = 0;
                    break;
                default:
                    spent = 0;
                    plannedRemaining = 0;
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
            return new CategoryView(c.CategoryId, c.Name, c.Group, c.Mode, c.Budget, scheduled, spent, plannedRemaining, spent + plannedRemaining);
        }).ToList();

        return new PeriodView(
            period.Id, period.StartDate, period.EndDate, today, period.Income,
            new PoolView(opening, pool, lines),
            categoryViews,
            dayViews,
            entryViews.Values.OrderByDescending(v => v.CreatedAt).ThenByDescending(v => v.Id).ToList(),
            period.FixedCharges.OrderBy(f => f.DueDate).ThenBy(f => f.Name)
                .Select(f => new FixedChargeView(f.Id, f.CategoryId, f.Name, f.Amount, f.DueDate, f.IsSubscription)).ToList(),
            period.PoolTransfers.OrderByDescending(t => t.CreatedAt)
                .Select(t => new PoolTransferView(t.Id, t.Date, t.Amount, t.Note)).ToList());
    }

    private static EntryView ApplyEnvelope(Entry e, PeriodCategory cat, Dictionary<int, int> envelopeSpent, ref int pool, List<PoolLine> lines)
    {
        var actual = Math.Max(0, e.InputAmount);
        var remaining = cat.Budget - envelopeSpent[cat.CategoryId];
        envelopeSpent[cat.CategoryId] += actual;
        var over = actual - Math.Max(remaining, 0);
        var envelopeOver = 0;
        if (over > 0 && e.UsePool)
        {
            envelopeOver = over;
            pool -= over;
            lines.Add(new PoolLine(e.Date, -over, "EnvelopeOver", $"{cat.Name}超過月額度", e.Id, null));
        }
        return new EntryView(e.Id, e.Date, cat.CategoryId, cat.Name, null, null, e.InputMode, e.InputAmount,
            actual, 0, actual, e.UsePool, 0, 0, 0, 0, envelopeOver, e.Note, e.IsSubscription, e.CreatedAt);
    }

    private static EntryView ApplyDaily(
        Entry e,
        PeriodCategory cat,
        Dictionary<(DateOnly, int), DayAllocation> allocByKey,
        Dictionary<(DateOnly, int), int> effective,
        HashSet<(DateOnly, int)> reportedKeys,
        Func<DateTime, DateOnly> toLocalDate,
        ref int pool,
        List<PoolLine> lines)
    {
        DayAllocation? alloc = null;
        if (e.SlotId is { } sid) allocByKey.TryGetValue((e.Date, sid), out alloc);
        var planned = alloc is not null ? effective[(e.Date, alloc.SlotId)] : 0;

        int actual = e.SlotId is not null && e.InputMode == EntryInputMode.Overage
            ? planned + e.InputAmount
            : e.InputAmount;
        actual = Math.Max(0, actual);
        var diff = actual - planned;
        var slotName = alloc?.SlotName;

        int fromPool = 0, spread = 0, spreadSlots = 0, unabsorbed = 0;
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
                var cutoff = Max(e.Date, toLocalDate(e.CreatedAt));
                var targets = allocByKey.Values
                    .Where(a => a.CategoryId == cat.CategoryId && a.Date > cutoff
                                && !reportedKeys.Contains((a.Date, a.SlotId))
                                && effective[(a.Date, a.SlotId)] > 0)
                    .OrderBy(a => a.Date).ThenBy(a => a.SortOrder).ThenBy(a => a.SlotId)
                    .ToList();

                var shares = Distribute(rest, targets.Select(a => effective[(a.Date, a.SlotId)]).ToList());
                for (var i = 0; i < targets.Count; i++)
                {
                    if (shares[i] == 0) continue;
                    effective[(targets[i].Date, targets[i].SlotId)] -= shares[i];
                    spread += shares[i];
                    spreadSlots++;
                }

                unabsorbed = rest - spread;
                if (unabsorbed > 0)
                {
                    pool -= unabsorbed;
                    lines.Add(new PoolLine(e.Date, -unabsorbed, "Unabsorbed", $"{label}超支，後續額度不夠攤", e.Id, null));
                }
            }
        }

        return new EntryView(e.Id, e.Date, cat.CategoryId, cat.Name, e.SlotId, slotName, e.InputMode, e.InputAmount,
            actual, planned, diff, e.UsePool, fromPool, spread, spreadSlots, unabsorbed, 0, e.Note, e.IsSubscription, e.CreatedAt);
    }

    /// <summary>
    /// 把 amount 依 weights 等比例拆成整數（最大餘數法）。amount 超過總權重時只拆到總權重為止，
    /// 每一份都不會超過自己的權重（額度不會被攤成負的）。
    /// </summary>
    public static List<int> Distribute(int amount, IReadOnlyList<int> weights)
    {
        var result = new List<int>(new int[weights.Count]);
        long total = weights.Sum(w => (long)w);
        if (amount <= 0 || total <= 0) return result;
        var take = (int)Math.Min(amount, total);
        if (take == total)
        {
            for (var i = 0; i < weights.Count; i++) result[i] = weights[i];
            return result;
        }

        var remainders = new List<(int Index, long Rem)>();
        var assigned = 0;
        for (var i = 0; i < weights.Count; i++)
        {
            var num = (long)take * weights[i];
            result[i] = (int)(num / total);
            assigned += result[i];
            remainders.Add((i, num % total));
        }
        foreach (var (index, _) in remainders.OrderByDescending(r => r.Rem).ThenBy(r => r.Index).Take(take - assigned))
        {
            result[index]++;
        }
        return result;
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
}
