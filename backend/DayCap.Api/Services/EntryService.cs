using DayCap.Api.Common;
using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Services;

public interface IEntryService
{
    Task<PeriodView> CreateAsync(string userId, int periodId, CreateEntryRequest req, CancellationToken ct = default);
    Task<EntryPreview> PreviewAsync(string userId, int periodId, CreateEntryRequest req, CancellationToken ct = default);
    Task<PeriodView> DeleteAsync(string userId, int periodId, int entryId, CancellationToken ct = default);
    Task<PeriodView> AddTransferAsync(string userId, int periodId, CreatePoolTransferRequest req, CancellationToken ct = default);
    Task<PeriodView> DeleteTransferAsync(string userId, int periodId, int transferId, CancellationToken ct = default);
}

public class EntryService(DayCapDbContext db, IPeriodService periods, IAppClock clock) : IEntryService
{
    public async Task<PeriodView> CreateAsync(string userId, int periodId, CreateEntryRequest req, CancellationToken ct = default)
    {
        var period = await periods.LoadAsync(userId, periodId, ct);
        var entry = BuildEntry(period, req);

        // 同一天同一時段只留一筆：重新回報 = 取代舊的。
        if (req.SlotId is { } slotId)
        {
            var old = period.Entries.Where(e => e.Date == req.Date && e.SlotId == slotId).ToList();
            db.Entries.RemoveRange(old);
            period.Entries.RemoveAll(e => old.Contains(e));
        }

        if (req.Subscription is { } sub)
        {
            await AddSubscriptionAsync(userId, period, req, sub, ct);
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

        var entry = BuildEntry(period, req);
        entry.Id = int.MaxValue;
        var replaced = req.SlotId is { } slotId
            ? period.Entries.Where(e => e.Date == req.Date && e.SlotId == slotId).ToList()
            : [];
        period.Entries.RemoveAll(e => replaced.Contains(e));
        period.Entries.Add(entry);
        var after = await periods.ComputeAsync(period, ct);

        var view = after.Entries.First(e => e.Id == int.MaxValue);
        int Remaining(PeriodView v) => v.Categories.Where(c => c.CategoryId == req.CategoryId)
            .Select(c => c.Budget - c.Projected).FirstOrDefault();
        return new EntryPreview(view, before.Pool.Balance, after.Pool.Balance, Remaining(before), Remaining(after));
    }

    public async Task<PeriodView> DeleteAsync(string userId, int periodId, int entryId, CancellationToken ct = default)
    {
        var period = await periods.LoadAsync(userId, periodId, ct);
        var entry = period.Entries.FirstOrDefault(e => e.Id == entryId) ?? throw new NotFoundException("找不到這筆回報。");
        db.Entries.Remove(entry);
        period.Entries.Remove(entry);
        await db.SaveChangesAsync(ct);
        return await periods.ComputeAsync(period, ct);
    }

    public async Task<PeriodView> AddTransferAsync(string userId, int periodId, CreatePoolTransferRequest req, CancellationToken ct = default)
    {
        var period = await periods.LoadAsync(userId, periodId, ct);
        if (req.Amount == 0 || Math.Abs(req.Amount) > 100_000_000) throw new ValidationException("金額不正確。");
        if (req.Date < period.StartDate || req.Date > period.EndDate) throw new ValidationException("日期不在這個週期內。");
        period.PoolTransfers.Add(new PoolTransfer
        {
            Date = req.Date,
            Amount = req.Amount,
            Note = (req.Note ?? "").Trim()[..Math.Min((req.Note ?? "").Trim().Length, 120)],
            CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return await periods.ComputeAsync(period, ct);
    }

    public async Task<PeriodView> DeleteTransferAsync(string userId, int periodId, int transferId, CancellationToken ct = default)
    {
        var period = await periods.LoadAsync(userId, periodId, ct);
        var t = period.PoolTransfers.FirstOrDefault(x => x.Id == transferId) ?? throw new NotFoundException("找不到這筆調整。");
        db.PoolTransfers.Remove(t);
        period.PoolTransfers.Remove(t);
        await db.SaveChangesAsync(ct);
        return await periods.ComputeAsync(period, ct);
    }

    private Entry BuildEntry(BudgetPeriod period, CreateEntryRequest req)
    {
        if (req.Date < period.StartDate || req.Date > period.EndDate) throw new ValidationException("日期不在這個週期內。");
        var cat = period.Categories.FirstOrDefault(c => c.CategoryId == req.CategoryId)
                  ?? throw new ValidationException("這個週期沒有這個分類。");
        if (cat.Mode == BudgetMode.Fixed) throw new ValidationException($"「{cat.Name}」是鎖定的固定支出，不能回報。");
        if (Math.Abs(req.Amount) > 10_000_000) throw new ValidationException("金額超出範圍。");

        var inputMode = req.InputMode;
        if (req.SlotId is null || cat.Mode == BudgetMode.Envelope)
        {
            // 沒有時段就沒有「預算」可比，輸入的一定是實際價格。
            inputMode = EntryInputMode.Actual;
        }
        if (inputMode == EntryInputMode.Actual && req.Amount < 0) throw new ValidationException("實際價格不能是負數。");

        var slotId = cat.Mode == BudgetMode.Daily ? req.SlotId : null;
        var note = req.Note?.Trim();
        if (note is { Length: > 120 }) note = note[..120];

        return new Entry
        {
            Date = req.Date,
            CategoryId = req.CategoryId,
            SlotId = slotId,
            InputMode = inputMode,
            InputAmount = req.Amount,
            UsePool = req.UsePool,
            Note = string.IsNullOrEmpty(note) ? null : note,
            IsSubscription = req.Subscription is not null,
            CreatedAt = clock.UtcNow,
        };
    }

    private async Task AddSubscriptionAsync(string userId, BudgetPeriod period, CreateEntryRequest req, SubscriptionRequest sub, CancellationToken ct)
    {
        if (req.SlotId is not null) throw new ValidationException("訂閱要用「額外花費」回報，不能綁在時段上。");
        if (string.IsNullOrWhiteSpace(sub.Name)) throw new ValidationException("訂閱要有名稱。");
        var target = await db.Categories.FirstOrDefaultAsync(c => c.Id == sub.TargetCategoryId && c.UserId == userId && !c.IsArchived, ct)
                     ?? throw new ValidationException("找不到要放訂閱的固定支出分類。");
        if (target.Mode != BudgetMode.Fixed) throw new ValidationException("訂閱只能放進固定支出（鎖定）的分類。");

        db.FixedItems.Add(new FixedItem
        {
            CategoryId = target.Id,
            Name = sub.Name.Trim()[..Math.Min(sub.Name.Trim().Length, 60)],
            Amount = req.Amount,
            DueDay = sub.DueDay ?? req.Date.Day,
            IsSubscription = true,
            Cycle = sub.Cycle,
            BillingMonth = sub.Cycle == BillingCycle.Yearly ? req.Date.Month : null,
            IsActive = true,
            ActiveFrom = period.EndDate.AddDays(1),
        });
    }
}
