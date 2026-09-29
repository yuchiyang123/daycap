using DayCap.Api.Common;
using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Services;

public interface IAssetService
{
    Task<AssetsView> GetAsync(string userId, bool refreshQuotes, CancellationToken ct = default);
    Task<AssetsView> SaveAsync(string userId, SaveAssetsRequest req, CancellationToken ct = default);
    Task<List<AssetAdjustmentView>> ListAdjustmentsAsync(string userId, DateOnly? from, DateOnly? to, CancellationToken ct = default);
    Task<AssetAdjustmentView> AddAdjustmentAsync(string userId, CreateAssetAdjustmentRequest req, CancellationToken ct = default);
    Task DeleteAdjustmentAsync(string userId, int adjustmentId, CancellationToken ct = default);
}

public class AssetService(DayCapDbContext db, IQuoteService quotes, IAccountService accounts, IAppClock clock) : IAssetService
{
    public async Task<AssetsView> GetAsync(string userId, bool refreshQuotes, CancellationToken ct = default)
    {
        // 帳戶餘額是推算的（最近一次對帳 + 之後已知的移動）；現金總額＝資產帳戶 − 信用卡欠款
        var accountsView = await accounts.GetAsync(userId, ct);
        var holdings = await db.Holdings.Where(h => h.UserId == userId).OrderBy(h => h.SortOrder).ToListAsync(ct);
        var goals = await db.Goals.Where(g => g.UserId == userId).OrderBy(g => g.SortOrder).ToListAsync(ct);

        var quoteMap = await quotes.GetQuotesAsync(
            holdings.Where(h => h.ManualPrice is null).Select(h => h.Symbol).ToList(), refreshQuotes, ct);

        var holdingViews = holdings.Select(h =>
        {
            quoteMap.TryGetValue(h.Symbol.ToUpperInvariant(), out var q);
            var price = h.ManualPrice ?? q?.Price;
            var source = h.ManualPrice is not null ? "manual" : q is not null ? "market" : "none";
            var value = price is { } p ? Math.Round(p * h.Shares, 0, MidpointRounding.AwayFromZero) : 0m;
            var cost = Math.Round(h.AvgCost * h.Shares, 0, MidpointRounding.AwayFromZero);
            var name = string.IsNullOrWhiteSpace(h.Name) ? q?.Name ?? h.Symbol : h.Name;
            return new HoldingView(h.Id, h.Symbol, name, h.Shares, h.AvgCost, h.ManualPrice, price, source,
                h.ManualPrice is null ? q?.TradeDate : null, value, cost, value - cost);
        }).ToList();

        var cashTotal = accountsView.NetLiquid;
        var investTotal = holdingViews.Sum(h => h.MarketValue);
        await RecordSnapshotAsync(userId, cashTotal, investTotal, ct);

        var today = clock.Today;
        var goalViews = goals.Select(g =>
        {
            var current = g.Scope switch
            {
                GoalScope.Cash => cashTotal,
                GoalScope.Investments => investTotal,
                _ => cashTotal + investTotal,
            };
            var monthsLeft = Math.Max(0, (g.TargetDate.Year - today.Year) * 12 + g.TargetDate.Month - today.Month);
            var gap = Math.Max(0, g.TargetAmount - current);
            var monthly = gap == 0 ? 0 : Math.Ceiling(gap / Math.Max(1, monthsLeft));
            var progress = g.TargetAmount <= 0 ? 1m : Math.Min(1m, Math.Round(current / (decimal)g.TargetAmount, 4));
            return new GoalView(g.Id, g.Name, g.TargetAmount, g.TargetDate, g.Scope, current, progress, monthsLeft, monthly);
        }).ToList();

        var history = await db.AssetSnapshots.Where(s => s.UserId == userId)
            .OrderBy(s => s.Date)
            .Select(s => new AssetSnapshotDto(s.Date, s.Cash, s.Investments))
            .ToListAsync(ct);

        var fetchedAt = quoteMap.Count == 0 ? (DateTime?)null : quoteMap.Values.Min(q => q.FetchedAt);
        return new AssetsView(cashTotal, investTotal, holdingViews.Sum(h => h.Cost),
            accountsView.Accounts,
            holdingViews, goalViews, history, fetchedAt);
    }

    public async Task<AssetsView> SaveAsync(string userId, SaveAssetsRequest req, CancellationToken ct = default)
    {
        Validate(req);

        // 帳戶：只改名稱、類型；新帳戶的期初餘額記成一筆對帳（AccountService）
        await accounts.SaveAccountsAsync(userId, req.CashAccounts, ct);

        var holdings = await db.Holdings.Where(h => h.UserId == userId).ToListAsync(ct);
        Sync(holdings, req.Holdings, d => d.Id, (e, d, i) =>
        {
            e.Symbol = d.Symbol.Trim().ToUpperInvariant();
            e.Name = d.Name.Trim();
            e.Shares = d.Shares;
            e.AvgCost = d.AvgCost;
            e.ManualPrice = d.ManualPrice;
            e.SortOrder = i;
        }, () => new Holding { UserId = userId }, db.Holdings);

        var goals = await db.Goals.Where(g => g.UserId == userId).ToListAsync(ct);
        Sync(goals, req.Goals, d => d.Id, (e, d, i) =>
        {
            e.Name = d.Name.Trim();
            e.TargetAmount = d.TargetAmount;
            e.TargetDate = d.TargetDate;
            e.Scope = d.Scope;
            e.SortOrder = i;
        }, () => new Goal { UserId = userId }, db.Goals);

        await db.SaveChangesAsync(ct);
        return await GetAsync(userId, false, ct);
    }

    public async Task<List<AssetAdjustmentView>> ListAdjustmentsAsync(string userId, DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var all = await db.AssetAdjustments.Where(a => a.UserId == userId).ToListAsync(ct);
        var rows = all.Active()
            .Where(a => (from is null || a.Date >= from) && (to is null || a.Date <= to))
            .OrderByDescending(a => a.Date).ThenByDescending(a => a.Id)
            .Take(500)
            .ToList();
        var names = await db.CashAccounts.Where(c => c.UserId == userId).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return rows.Select(a => ToView(a, names)).ToList();
    }

    public async Task<AssetAdjustmentView> AddAdjustmentAsync(string userId, CreateAssetAdjustmentRequest req, CancellationToken ct = default)
    {
        if (req.Amount == 0 || Math.Abs(req.Amount) > 100_000_000) throw new ValidationException("金額不正確。");
        var account = await db.CashAccounts.FirstOrDefaultAsync(c => c.Id == req.CashAccountId && c.UserId == userId && !c.IsArchived, ct)
                      ?? throw new ValidationException("找不到這個帳戶，先到資產頁新增一個。");
        if (AccountService.IsLiability(account.Type)) throw new ValidationException("信用卡請用「繳卡費」或在回報時選信用卡付款。");
        var note = (req.Note ?? "").Trim();
        var adj = new AssetAdjustment
        {
            UserId = userId,
            CashAccountId = account.Id,
            Date = req.Date,
            Amount = req.Amount,
            Note = note[..Math.Min(note.Length, 120)],
            Source = "manual",
            CreatedAt = clock.UtcNow,
        };
        db.AssetAdjustments.Add(adj);
        await db.SaveChangesAsync(ct);
        return ToView(adj, new Dictionary<int, string> { [account.Id] = account.Name });
    }

    /// <summary>刪除＝新增一筆作廢紀錄；帳戶餘額是推算的，自然就還原。</summary>
    public async Task DeleteAdjustmentAsync(string userId, int adjustmentId, CancellationToken ct = default)
    {
        var all = await db.AssetAdjustments.Where(a => a.UserId == userId).ToListAsync(ct);
        var adj = all.Active().FirstOrDefault(a => a.Id == adjustmentId)
                  ?? throw new NotFoundException("找不到這筆紀錄。");
        // 只新增（§2.2）：刪除是新增一筆作廢紀錄
        db.AssetAdjustments.Add(new AssetAdjustment
        {
            UserId = userId,
            CashAccountId = adj.CashAccountId,
            Date = adj.Date,
            Note = "刪除",
            Source = adj.Source,
            IsVoid = true,
            ReplacesId = adj.Id,
            CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    private static AssetAdjustmentView ToView(AssetAdjustment a, IReadOnlyDictionary<int, string> names) =>
        new(a.Id, a.CashAccountId, names.TryGetValue(a.CashAccountId, out var n) ? n : "（已刪除的帳戶）",
            a.Date, a.Amount, a.Note, a.Source, a.PeriodId, a.CreatedAt);

    private async Task RecordSnapshotAsync(string userId, decimal cash, decimal investments, CancellationToken ct)
    {
        var today = clock.Today;
        var snap = await db.AssetSnapshots.FirstOrDefaultAsync(s => s.UserId == userId && s.Date == today, ct);
        if (snap is null)
        {
            if (cash == 0 && investments == 0) return; // 還沒填任何資產，不要記一堆 0
            db.AssetSnapshots.Add(new AssetSnapshot { UserId = userId, Date = today, Cash = cash, Investments = investments });
        }
        else if (snap.Cash == cash && snap.Investments == investments)
        {
            return;
        }
        else
        {
            snap.Cash = cash;
            snap.Investments = investments;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>用 id 對應：有就更新、沒有就新增、清單裡沒出現的就刪除。</summary>
    private static void Sync<TEntity, TDto>(
        List<TEntity> existing,
        List<TDto> incoming,
        Func<TDto, int> idOf,
        Action<TEntity, TDto, int> apply,
        Func<TEntity> create,
        DbSet<TEntity> set) where TEntity : class
    {
        var idProp = typeof(TEntity).GetProperty("Id")!;
        var byId = existing.ToDictionary(e => (int)idProp.GetValue(e)!);
        var keep = new HashSet<int>();
        for (var i = 0; i < incoming.Count; i++)
        {
            var d = incoming[i];
            if (idOf(d) != 0 && byId.TryGetValue(idOf(d), out var entity))
            {
                keep.Add(idOf(d));
            }
            else
            {
                entity = create();
                set.Add(entity);
            }
            apply(entity, d, i);
        }
        foreach (var (id, entity) in byId)
        {
            if (!keep.Contains(id)) set.Remove(entity);
        }
    }

    private static void Validate(SaveAssetsRequest req)
    {
        if (req.CashAccounts.Count > 30 || req.Holdings.Count > 60 || req.Goals.Count > 20)
            throw new ValidationException("項目太多了。");
        foreach (var c in req.CashAccounts)
        {
            if (string.IsNullOrWhiteSpace(c.Name) || c.Name.Trim().Length > 40) throw new ValidationException("帳戶名稱不能空白，最多 40 字。");
            if (c.OpeningBalance is { } ob && Math.Abs(ob) > 2_000_000_000) throw new ValidationException($"「{c.Name}」金額超出範圍。");
        }
        foreach (var h in req.Holdings)
        {
            if (string.IsNullOrWhiteSpace(h.Symbol) || h.Symbol.Trim().Length > 16) throw new ValidationException("股票代號不能空白。");
            if (h.Shares < 0 || h.AvgCost < 0 || h.ManualPrice < 0) throw new ValidationException($"「{h.Symbol}」數字不能是負的。");
            if (h.Name.Trim().Length > 40) throw new ValidationException($"「{h.Symbol}」名稱太長。");
        }
        foreach (var g in req.Goals)
        {
            if (string.IsNullOrWhiteSpace(g.Name) || g.Name.Trim().Length > 40) throw new ValidationException("目標名稱不能空白。");
            if (g.TargetAmount <= 0) throw new ValidationException($"「{g.Name}」目標金額要大於 0。");
        }
    }
}
