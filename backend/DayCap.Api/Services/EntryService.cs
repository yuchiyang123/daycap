using DayCap.Api.Common;
using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using DayCap.Api.Models.Settings;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Services;

public interface IEntryService
{
    Task<PeriodView> CreateAsync(string userId, int periodId, CreateEntryRequest req, CancellationToken ct = default);
    Task<EntryPreview> PreviewAsync(string userId, int periodId, CreateEntryRequest req, CancellationToken ct = default);
    Task<PeriodView> DeleteAsync(string userId, int periodId, int entryId, CancellationToken ct = default);
    Task<PeriodView> AddTransferAsync(string userId, int periodId, CreatePoolTransferRequest req, CancellationToken ct = default);
    Task<PeriodView> DeleteTransferAsync(string userId, int periodId, int transferId, CancellationToken ct = default);
    Task<PeriodView> AddIncomeAdjustmentAsync(string userId, int periodId, CreateIncomeAdjustmentRequest req, CancellationToken ct = default);
    Task<PeriodView> DeleteIncomeAdjustmentAsync(string userId, int periodId, int adjustmentId, CancellationToken ct = default);
    Task<PeriodView> ConfirmIncomeAsync(string userId, int periodId, CancellationToken ct = default);
    Task<PeriodView> AllocateAsync(string userId, int periodId, AllocateRequest req, CancellationToken ct = default);

    /// <summary>§9.3 全天例外：mode = "zero"（今天全部 0）或 "planned"（今天全部照預算確認）。</summary>
    Task<PeriodView> SetDayAsync(string userId, int periodId, DateOnly date, string mode, CancellationToken ct = default);
}

/// <summary>
/// 這期的事實：回報、待定區調整、薪資調整。全部只新增（§2.2）：
/// 修改＝新增一筆 ReplacesId 指向舊的；刪除＝新增一筆作廢紀錄。重播只看有效的那些。
/// </summary>
public class EntryService(DayCapDbContext db, IPeriodService periods, ISettingsService settings, IAppClock clock) : IEntryService
{
    public async Task<PeriodView> CreateAsync(string userId, int periodId, CreateEntryRequest req, CancellationToken ct = default)
    {
        var period = await periods.LoadAsync(userId, periodId, ct);
        EnsureOpen(period);
        var view = await periods.ComputeAsync(period, ct);
        req = await BackdateIntoCurrentAsync(userId, period, view, req, ct);
        var entry = BuildEntry(period, view, req);
        if (req.AccountId is { } accountId)
        {
            if (!db.CashAccounts.Any(a => a.Id == accountId && a.UserId == userId && !a.IsArchived))
                throw new ValidationException("找不到付款帳戶。");
            entry.AccountId = accountId;
        }

        // 同一天同一時段再回報 = 修改：新的一筆取代舊的（舊的留著，可以追溯）
        if (req.SlotId is { } slotId)
        {
            entry.ReplacesId = period.Entries.Active()
                .Where(e => e.Date == req.Date && e.SlotId == slotId)
                .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
                .Select(e => (int?)e.Id).FirstOrDefault();
        }

        if (req.Subscription is { } sub)
        {
            if (req.SlotId is not null) throw new ValidationException("訂閱要用「額外花費」回報，不能綁在時段上。");
            if (string.IsNullOrWhiteSpace(sub.Name)) throw new ValidationException("訂閱要有名稱。");
            var name = sub.Name.Trim();
            await settings.AddFixedItemAsync(userId, sub.TargetCategoryId, new FixedItemDoc(
                0, name[..Math.Min(name.Length, 60)], Math.Round(req.Amount, 0), sub.DueDay ?? req.Date.Day, true,
                sub.Cycle, sub.Cycle == BillingCycle.Monthly ? null : req.Date.Month, true, period.EndDate.AddDays(1)), ct);
        }

        var jar = req.JarId is { } jarId ? await PrepareJarPaymentAsync(userId, entry, jarId, req, ct) : null;

        period.Entries.Add(entry);
        await db.SaveChangesAsync(ct);
        if (jar is not null) await AfterJarPaymentAsync(userId, period, entry, jar, view.Today, ct);

        var after = await periods.ComputeAsync(period, ct);
        if (await AutoSaveSurplusAsync(userId, period, entry, after, ct)) after = await periods.ComputeAsync(period, ct);
        if (req.Guardrail is { } choice && choice != ShortfallChoice.Pool)
        {
            var excess = after.Entries.FirstOrDefault(e => e.Id == entry.Id)?.Unabsorbed ?? 0;
            if (excess > 0)
            {
                await ApplyShortfallAsync(userId, period, entry.Id, excess, choice, req.GuardrailAccountId, after.Today, ct);
                after = await periods.ComputeAsync(period, ct);
            }
        }
        return after;
    }

    /// <summary>從罐子付（§11.2）：罐子餘額能付多少就付多少，超過的部分照一般超支規則。</summary>
    private async Task<Jar> PrepareJarPaymentAsync(string userId, Entry entry, int jarId, CreateEntryRequest req, CancellationToken ct)
    {
        if (req.SlotId is not null || req.InputMode != EntryInputMode.Actual)
            throw new ValidationException("從罐子付要用「額外花費」、輸入實際金額。");
        var jar = await db.Jars.FirstOrDefaultAsync(j => j.Id == jarId && j.UserId == userId && j.ClosedAt == null, ct)
                  ?? throw new ValidationException("找不到這個罐子，或已經關閉。");
        var balance = (await JarMath.BalancesAsync(db, userId, ct)).GetValueOrDefault(jar.Id);
        entry.JarId = jar.Id;
        entry.JarCovered = Math.Max(0, Math.Min(entry.InputAmount, balance));
        return jar;
    }

    /// <summary>
    /// 預約付掉後：預留比實際多的部分回待分配池（結餘規則），罐子關閉；刪掉這筆回報時一起還原。
    /// 年繳付掉後：罐子留著，下次繳費日往後一年，繼續每期提撥。
    /// </summary>
    private async Task AfterJarPaymentAsync(string userId, BudgetPeriod period, Entry entry, Jar jar, DateOnly today, CancellationToken ct)
    {
        if (jar.Kind == JarKind.Reservation)
        {
            var leftover = (await JarMath.BalancesAsync(db, userId, ct)).GetValueOrDefault(jar.Id);
            if (leftover > 0)
            {
                period.PoolTransfers.Add(new PoolTransfer
                {
                    Date = entry.Date, Amount = leftover, JarId = jar.Id, SourceEntryId = entry.Id,
                    Note = $"預約「{jar.Name}」比實際多預留的", CreatedAt = clock.UtcNow,
                });
            }
            jar.ClosedAt = clock.UtcNow;
            jar.ClosedByEntryId = entry.Id;
        }
        else if (jar.Kind == JarKind.Annual && jar.DueDate is { } due && due <= today.AddDays(60))
        {
            jar.DueDate = due.AddYears(1);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>自動規則（§11.2 第 3 點）：時段省下的錢，照各罐子設定的比例自動存進去；刪回報時一起作廢。</summary>
    private async Task<bool> AutoSaveSurplusAsync(string userId, BudgetPeriod period, Entry entry, PeriodView after, CancellationToken ct)
    {
        if (entry.SlotId is null) return false;
        var saved = -(after.Entries.FirstOrDefault(e => e.Id == entry.Id)?.Diff ?? 0);
        if (saved <= 0) return false;
        var jars = await db.Jars.AsNoTracking()
            .Where(j => j.UserId == userId && j.ClosedAt == null && j.AutoSurplusPercent != null && j.AutoSurplusPercent > 0)
            .ToListAsync(ct);
        foreach (var j in jars)
        {
            var amount = Math.Floor(saved * j.AutoSurplusPercent!.Value / 100m);
            if (amount <= 0) continue;
            period.PoolTransfers.Add(new PoolTransfer
            {
                Date = entry.Date, Amount = -amount, JarId = j.Id, SourceEntryId = entry.Id,
                Note = $"自動存進{j.Name}（省下的 {j.AutoSurplusPercent:0.#}%）", CreatedAt = clock.UtcNow,
            });
        }
        if (jars.Count == 0) return false;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// 護欄（§10.2）：攤不完的超支原本從待分配池扣；使用者選了別的處理方式，就記一筆「補回待分配池」，
    /// 再記下真正的去處——延到下一期、分兩期還（下一期與再下一期各一半）、或從存款吸收。
    /// 這些事實都掛在那筆回報上，刪回報時一起作廢。
    /// </summary>
    private async Task ApplyShortfallAsync(string userId, BudgetPeriod period, int entryId, decimal excess, ShortfallChoice choice,
        int? accountId, DateOnly today, CancellationToken ct)
    {
        var date = today < period.StartDate ? period.StartDate : today > period.EndDate ? period.EndDate : today;
        var label = choice switch
        {
            ShortfallChoice.NextPeriod => "超支延到下一期",
            ShortfallChoice.Split => "超支分兩期還",
            _ => "超支從存款吸收",
        };

        if (choice == ShortfallChoice.Savings)
        {
            var account = await db.CashAccounts.FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == userId && !a.IsArchived, ct)
                          ?? throw new ValidationException("要選一個存款帳戶來吸收超支。");
            if (AccountService.IsLiability(account.Type)) throw new ValidationException("不能用信用卡吸收超支。");
            db.AssetAdjustments.Add(new AssetAdjustment
            {
                UserId = userId, CashAccountId = account.Id, Date = date, Amount = -excess, Note = label,
                Source = "manual", PeriodId = period.Id, SourceEntryId = entryId, CreatedAt = clock.UtcNow,
            });
        }
        else
        {
            var next = period.EndDate.AddDays(1);
            var parts = choice == ShortfallChoice.Split
                ? new[] { (next, Math.Ceiling(excess / 2)), (next.AddMonths(1), Math.Floor(excess / 2)) }
                : new[] { (next, excess) };
            foreach (var (target, amount) in parts)
            {
                if (amount <= 0) continue;
                db.PeriodCarryovers.Add(new PeriodCarryover
                {
                    UserId = userId, SourcePeriodId = period.Id, TargetDate = target, Amount = -amount,
                    Label = $"{period.StartDate:M/d} 那期{label}", SourceEntryId = entryId, CreatedAt = clock.UtcNow,
                });
            }
        }

        period.PoolTransfers.Add(new PoolTransfer
        {
            Date = date, Amount = excess, Note = label, SourceEntryId = entryId, CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// §4.2 補登到已月結的期間：快照不改，改記在本期，備註標明原本的日期。
    /// 已過去的時段沒辦法回頭算，所以一律當成這個分類的額外花費。
    /// </summary>
    private async Task<CreateEntryRequest> BackdateIntoCurrentAsync(string userId, BudgetPeriod period, PeriodView view, CreateEntryRequest req, CancellationToken ct)
    {
        if (req.Date >= period.StartDate) return req;
        var closed = await db.Periods.AsNoTracking()
            .AnyAsync(p => p.UserId == userId && p.StartDate <= req.Date && p.EndDate >= req.Date && p.ClosedAt != null, ct);
        if (!closed) return req;
        var today = view.Today < period.StartDate ? period.StartDate : view.Today > period.EndDate ? period.EndDate : view.Today;
        var note = string.IsNullOrWhiteSpace(req.Note) ? $"補登 {req.Date:M/d}" : $"補登 {req.Date:M/d}：{req.Note.Trim()}";
        return req with { Date = today, SlotId = null, InputMode = EntryInputMode.Actual, Note = note };
    }

    private static void EnsureOpen(BudgetPeriod period)
    {
        if (period.ClosedAt is not null)
            throw new ValidationException("這期已經月結，不能再改；要補登請記在本期（會標明原本的日期）。");
    }

    public async Task<PeriodView> SetDayAsync(string userId, int periodId, DateOnly date, string mode, CancellationToken ct = default)
    {
        if (mode is not ("zero" or "planned")) throw new ValidationException("不支援的全天操作。");
        var period = await periods.LoadAsync(userId, periodId, ct);
        EnsureOpen(period);
        var view = await periods.ComputeAsync(period, ct);
        var day = view.Days.FirstOrDefault(d => d.Date == date) ?? throw new ValidationException("日期不在這個週期內。");
        var daily = view.Categories.Where(c => c.Mode == BudgetMode.Daily).Select(c => c.CategoryId).ToHashSet();
        foreach (var slot in day.Slots.Where(s => s.EntryId is null && daily.Contains(s.CategoryId)))
        {
            period.Entries.Add(new Entry
            {
                Date = date,
                CategoryId = slot.CategoryId,
                SlotId = slot.SlotId,
                InputMode = EntryInputMode.Actual,
                InputAmount = mode == "zero" ? 0 : slot.Planned,
                UsePool = true,
                Note = mode == "zero" ? "今天全部 0" : "照預算確認",
                TimeZoneId = "Asia/Taipei",
                CreatedAt = clock.UtcNow,
            });
        }
        await db.SaveChangesAsync(ct);
        return await periods.ComputeAsync(period, ct);
    }

    /// <summary>不寫入，只算「如果這樣回報會怎樣」，給回報視窗即時顯示影響。</summary>
    public async Task<EntryPreview> PreviewAsync(string userId, int periodId, CreateEntryRequest req, CancellationToken ct = default)
    {
        // 不追蹤：下面會直接改 period.Entries 來試算，絕對不能被任何 SaveChanges 寫進去。
        var period = await periods.LoadAsync(userId, periodId, ct, tracking: false);
        var before = await periods.ComputeAsync(period, ct);

        var entry = BuildEntry(period, before, req);
        entry.Id = int.MaxValue;
        if (req.JarId is { } jarId && req.SlotId is null)
        {
            var balance = (await JarMath.BalancesAsync(db, userId, ct)).GetValueOrDefault(jarId);
            entry.JarId = jarId;
            entry.JarCovered = Math.Max(0, Math.Min(entry.InputAmount, balance));
        }
        if (req.SlotId is { } slotId)
        {
            entry.ReplacesId = period.Entries.Active()
                .Where(e => e.Date == req.Date && e.SlotId == slotId)
                .Select(e => (int?)e.Id).FirstOrDefault();
        }
        period.Entries.Add(entry);
        var after = await periods.ComputeAsync(period, ct);

        var view = after.Entries.First(e => e.Id == int.MaxValue);
        decimal Remaining(PeriodView v) => v.Categories.Where(c => c.CategoryId == req.CategoryId)
            .Select(c => c.Budget - c.Projected).FirstOrDefault();
        return new EntryPreview(view, before.Pool.Balance, after.Pool.Balance, Remaining(before), Remaining(after));
    }

    public async Task<PeriodView> DeleteAsync(string userId, int periodId, int entryId, CancellationToken ct = default)
    {
        var period = await periods.LoadAsync(userId, periodId, ct);
        EnsureOpen(period);
        var target = period.Entries.Active().FirstOrDefault(e => e.Id == entryId) ?? throw new NotFoundException("找不到這筆回報。");
        await VoidLinkedAsync(userId, period, entryId, ct);
        period.Entries.Add(new Entry
        {
            Date = target.Date,
            CategoryId = target.CategoryId,
            SlotId = target.SlotId,
            InputMode = target.InputMode,
            Note = "刪除",
            IsVoid = true,
            ReplacesId = target.Id,
            CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return await periods.ComputeAsync(period, ct);
    }

    /// <summary>刪回報時，把護欄選擇產生的「補回待分配池」、結轉、存款吸收一起作廢。</summary>
    private async Task VoidLinkedAsync(string userId, BudgetPeriod period, int entryId, CancellationToken ct)
    {
        // 預約因為這筆回報而關閉的罐子重新打開
        foreach (var jar in await db.Jars.Where(j => j.UserId == userId && j.ClosedByEntryId == entryId).ToListAsync(ct))
        {
            jar.ClosedAt = null;
            jar.ClosedByEntryId = null;
        }
        foreach (var t in period.PoolTransfers.Active().Where(t => t.SourceEntryId == entryId))
        {
            period.PoolTransfers.Add(new PoolTransfer { Date = t.Date, Note = "刪除", IsVoid = true, ReplacesId = t.Id, SourceEntryId = entryId, CreatedAt = clock.UtcNow });
        }
        foreach (var c in (await db.PeriodCarryovers.Where(c => c.UserId == userId && c.SourceEntryId == entryId).ToListAsync(ct)).Active())
        {
            db.PeriodCarryovers.Add(new PeriodCarryover { UserId = userId, SourcePeriodId = c.SourcePeriodId, TargetDate = c.TargetDate, Label = "刪除", IsVoid = true, ReplacesId = c.Id, SourceEntryId = entryId, CreatedAt = clock.UtcNow });
        }
        foreach (var a in (await db.AssetAdjustments.Where(a => a.UserId == userId && a.SourceEntryId == entryId).ToListAsync(ct)).Active())
        {
            db.AssetAdjustments.Add(new AssetAdjustment { UserId = userId, CashAccountId = a.CashAccountId, Date = a.Date, Note = "刪除", Source = a.Source, IsVoid = true, ReplacesId = a.Id, SourceEntryId = entryId, CreatedAt = clock.UtcNow });
        }
    }

    public async Task<PeriodView> AddTransferAsync(string userId, int periodId, CreatePoolTransferRequest req, CancellationToken ct = default)
    {
        var period = await periods.LoadAsync(userId, periodId, ct);
        EnsureOpen(period);
        var amount = Math.Round(req.Amount, 0);
        if (amount == 0 || Math.Abs(amount) > 100_000_000) throw new ValidationException("金額不正確。");
        if (req.Date < period.StartDate || req.Date > period.EndDate) throw new ValidationException("日期不在這個週期內。");
        var note = (req.Note ?? "").Trim();
        period.PoolTransfers.Add(new PoolTransfer
        {
            Date = req.Date,
            Amount = amount,
            Note = note[..Math.Min(note.Length, 120)],
            CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return await periods.ComputeAsync(period, ct);
    }

    public async Task<PeriodView> DeleteTransferAsync(string userId, int periodId, int transferId, CancellationToken ct = default)
    {
        var period = await periods.LoadAsync(userId, periodId, ct);
        EnsureOpen(period);
        var t = period.PoolTransfers.Active().FirstOrDefault(x => x.Id == transferId) ?? throw new NotFoundException("找不到這筆調整。");
        period.PoolTransfers.Add(new PoolTransfer
        {
            Date = t.Date,
            CategoryId = t.CategoryId,
            Note = "刪除",
            IsVoid = true,
            ReplacesId = t.Id,
            CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return await periods.ComputeAsync(period, ct);
    }

    public async Task<PeriodView> AddIncomeAdjustmentAsync(string userId, int periodId, CreateIncomeAdjustmentRequest req, CancellationToken ct = default)
    {
        var period = await periods.LoadAsync(userId, periodId, ct);
        EnsureOpen(period);
        var amount = Math.Round(req.Amount, 0);
        if (amount is <= 0 or > 100_000_000) throw new ValidationException("金額要大於 0。");
        if (req.Days is < 0 or > 31 || req.Hours is < 0 or > 400) throw new ValidationException("天數或時數超出範圍。");
        var deduct = req.Kind is IncomeAdjustmentKind.SickLeave or IncomeAdjustmentKind.PersonalLeave
            or IncomeAdjustmentKind.MenstrualLeave or IncomeAdjustmentKind.OtherDeduction;
        var note = req.Note?.Trim();
        period.IncomeAdjustments.Add(new IncomeAdjustment
        {
            Kind = req.Kind,
            Days = req.Days,
            Hours = req.Hours,
            Amount = deduct ? -amount : amount,
            Note = string.IsNullOrEmpty(note) ? null : note[..Math.Min(note.Length, 120)],
            CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return await periods.ComputeAsync(period, ct);
    }

    public async Task<PeriodView> DeleteIncomeAdjustmentAsync(string userId, int periodId, int adjustmentId, CancellationToken ct = default)
    {
        var period = await periods.LoadAsync(userId, periodId, ct);
        EnsureOpen(period);
        var adj = period.IncomeAdjustments.Active().FirstOrDefault(a => a.Id == adjustmentId) ?? throw new NotFoundException("找不到這筆薪資調整。");
        period.IncomeAdjustments.Add(new IncomeAdjustment
        {
            Kind = adj.Kind,
            Note = "刪除",
            IsVoid = true,
            ReplacesId = adj.Id,
            CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return await periods.ComputeAsync(period, ct);
    }

    public async Task<PeriodView> ConfirmIncomeAsync(string userId, int periodId, CancellationToken ct = default)
    {
        var period = await periods.LoadAsync(userId, periodId, ct);
        period.IncomeConfirmedAt ??= clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return await periods.ComputeAsync(period, ct);
    }

    /// <summary>
    /// 把待定區的錢分出去：全部給一個分類，或依各變動分類的額度比例分給全部。
    /// 每個分類一筆 PoolTransfer（有 CategoryId），刪掉那筆就還原。
    /// </summary>
    public async Task<PeriodView> AllocateAsync(string userId, int periodId, AllocateRequest req, CancellationToken ct = default)
    {
        var period = await periods.LoadAsync(userId, periodId, ct);
        EnsureOpen(period);
        var view = await periods.ComputeAsync(period, ct);
        var amount = Math.Floor(req.Amount);
        if (amount <= 0) throw new ValidationException("金額要大於 0。");
        if (amount > view.Pool.Balance) throw new ValidationException($"待定區只剩 {view.Pool.Balance:N0}。");

        var variable = view.Categories.Where(c => c.Mode != BudgetMode.Fixed).ToList();
        List<(CategoryView Cat, decimal Amount)> plan;
        if (req.Mode == "single")
        {
            var target = variable.FirstOrDefault(c => c.CategoryId == req.CategoryId)
                         ?? throw new ValidationException("要選一個每日或月額度的分類（固定支出不能分配）。");
            plan = [(target, amount)];
        }
        else if (req.Mode == "proportional")
        {
            if (variable.Count == 0) throw new ValidationException("沒有可以分配的分類。");
            var shares = BudgetEngine.DistributeAll(amount, variable.Select(c => Math.Max(1m, c.Budget)).ToList());
            plan = variable.Zip(shares).Where(x => x.Second > 0).Select(x => (x.First, x.Second)).ToList();
        }
        else
        {
            throw new ValidationException("分配方式不正確。");
        }

        var today = view.Today;
        var date = today < period.StartDate ? period.StartDate : today > period.EndDate ? period.EndDate : today;
        foreach (var (cat, share) in plan)
        {
            period.PoolTransfers.Add(new PoolTransfer
            {
                Date = date,
                CategoryId = cat.CategoryId,
                Amount = share,
                Note = $"待定區分配給{cat.Name}",
                CreatedAt = clock.UtcNow,
            });
        }
        await db.SaveChangesAsync(ct);
        return await periods.ComputeAsync(period, ct);
    }

    private Entry BuildEntry(BudgetPeriod period, PeriodView view, CreateEntryRequest req)
    {
        if (req.Date < period.StartDate || req.Date > period.EndDate) throw new ValidationException("日期不在這個週期內。");
        var cat = view.Categories.FirstOrDefault(c => c.CategoryId == req.CategoryId)
                  ?? throw new ValidationException("這個週期沒有這個分類。");
        if (cat.Mode == BudgetMode.Fixed) throw new ValidationException($"「{cat.Name}」是鎖定的固定支出，不能回報。");
        var amount = Math.Round(req.Amount, 0);
        if (Math.Abs(amount) > 10_000_000) throw new ValidationException("金額超出範圍。");

        var inputMode = req.InputMode;
        if (req.SlotId is null || cat.Mode == BudgetMode.Envelope)
        {
            // 沒有時段就沒有「預算」可比，輸入的一定是實際價格。
            inputMode = EntryInputMode.Actual;
        }
        if (inputMode == EntryInputMode.Actual && amount < 0) throw new ValidationException("實際價格不能是負數。");

        var note = req.Note?.Trim();
        if (note is { Length: > 120 }) note = note[..120];

        return new Entry
        {
            Date = req.Date,
            CategoryId = req.CategoryId,
            SlotId = cat.Mode == BudgetMode.Daily ? req.SlotId : null,
            InputMode = inputMode,
            InputAmount = amount,
            UsePool = req.UsePool,
            Note = string.IsNullOrEmpty(note) ? null : note,
            IsSubscription = req.Subscription is not null,
            TimeZoneId = "Asia/Taipei",
            CreatedAt = clock.UtcNow,
        };
    }
}
