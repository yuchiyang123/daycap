using DayCap.Api.Common;
using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using DayCap.Api.Models.Settings;

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
        var view = await periods.ComputeAsync(period, ct);
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

        period.Entries.Add(entry);
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
        var target = period.Entries.Active().FirstOrDefault(e => e.Id == entryId) ?? throw new NotFoundException("找不到這筆回報。");
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

    public async Task<PeriodView> AddTransferAsync(string userId, int periodId, CreatePoolTransferRequest req, CancellationToken ct = default)
    {
        var period = await periods.LoadAsync(userId, periodId, ct);
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
