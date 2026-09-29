using DayCap.Api.Common;
using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Services;

public interface IAccountService
{
    Task<AccountsView> GetAsync(string userId, CancellationToken ct = default);
    Task<AccountsView> SaveAccountsAsync(string userId, List<AccountEdit> edits, CancellationToken ct = default);
    Task<Dictionary<int, decimal>> BalancesAsync(string userId, CancellationToken ct = default);
    Task EnsureMigratedAsync(string userId, CancellationToken ct = default);

    Task<List<TransferView>> ListTransfersAsync(string userId, DateOnly? from, DateOnly? to, CancellationToken ct = default);
    Task<TransferView> AddTransferAsync(string userId, CreateTransferRequest req, CancellationToken ct = default);
    Task DeleteTransferAsync(string userId, int id, CancellationToken ct = default);

    Task<List<ReconciliationSummary>> ListReconciliationsAsync(string userId, CancellationToken ct = default);
    Task<ReconciliationResult> PreviewReconciliationAsync(string userId, CreateReconciliationRequest req, CancellationToken ct = default);
    Task<ReconciliationResult> CreateReconciliationAsync(string userId, CreateReconciliationRequest req, CancellationToken ct = default);
    Task DeleteReconciliationAsync(string userId, int id, CancellationToken ct = default);
}

/// <summary>
/// 帳戶模型（§5）與對帳（§12.1）。
/// - 帳戶餘額不存：最近一次對帳的實際值 + 之後的轉帳、繳卡費、資產加減、有記付款帳戶的回報。
/// - 互轉、繳卡費不算花費，淨資產不變。
/// - 對帳存「那天的實際餘額」，差額（沒交代的差異）由 PeriodService 在重播時算。
/// </summary>
public class AccountService(DayCapDbContext db, IPeriodService periods, ISettingsService settings, IAppClock clock) : IAccountService
{
    public static bool IsLiability(AccountType t) => t == AccountType.CreditCard;

    public async Task<AccountsView> GetAsync(string userId, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(userId, ct);
        var accounts = await db.CashAccounts.Where(a => a.UserId == userId).OrderBy(a => a.SortOrder).ToListAsync(ct);
        var balances = await BalancesAsync(userId, ct);
        var recons = (await LoadReconsAsync(userId, ct)).Active();
        var today = await settings.LogicalTodayAsync(userId, ct);

        // 本期刷卡（§12.2 下月卡費預估）：本期間有記信用卡付款的回報
        var current = await db.Periods.Where(p => p.UserId == userId && p.StartDate <= today && p.EndDate >= today)
            .Select(p => new { p.Id }).FirstOrDefaultAsync(ct);
        var cardIds = accounts.Where(a => IsLiability(a.Type)).Select(a => a.Id).ToHashSet();
        var cardSpend = current is null
            ? []
            : (await db.Entries.Where(e => e.PeriodId == current.Id && (e.AccountId != null || e.ReplacesId != null)).ToListAsync(ct)).Active()
                .Where(e => e.AccountId != null && cardIds.Contains(e.AccountId!.Value) && e.InputMode == EntryInputMode.Actual)
                .GroupBy(e => e.AccountId!.Value).ToDictionary(g => g.Key, g => g.Sum(e => e.InputAmount));

        var views = accounts.Where(a => !a.IsArchived).Select(a =>
        {
            var last = recons.Where(r => r.Lines.Any(l => l.AccountId == a.Id)).OrderBy(r => r.Date).ThenBy(r => r.CreatedAt).LastOrDefault();
            return new AccountView(a.Id, a.Name, a.Type, balances.GetValueOrDefault(a.Id), last?.Date,
                cardSpend.GetValueOrDefault(a.Id), a.IsArchived);
        }).ToList();

        var net = views.Sum(v => IsLiability(v.Type) ? -v.Balance : v.Balance);
        var lastFull = recons.Where(r => r.IsFull).Select(r => (DateOnly?)r.Date).DefaultIfEmpty(null).Max();
        return new AccountsView(views, net, lastFull);
    }

    public async Task<AccountsView> SaveAccountsAsync(string userId, List<AccountEdit> edits, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(userId, ct);
        pendingOpenings.Clear();
        if (edits.Count > 30) throw new ValidationException("帳戶最多 30 個。");
        var existing = await db.CashAccounts.Where(a => a.UserId == userId).ToListAsync(ct);
        var today = await settings.LogicalTodayAsync(userId, ct);
        var keep = new HashSet<int>();
        var opening = new Reconciliation { UserId = userId, Date = today, IsFull = false, Note = "新帳戶期初餘額", CreatedAt = clock.UtcNow };

        for (var i = 0; i < edits.Count; i++)
        {
            var e = edits[i];
            if (string.IsNullOrWhiteSpace(e.Name) || e.Name.Trim().Length > 40) throw new ValidationException("帳戶名稱不能空白，最多 40 字。");
            if (!Enum.IsDefined(e.Type)) throw new ValidationException("帳戶類型不正確。");
            var account = e.Id > 0 ? existing.FirstOrDefault(a => a.Id == e.Id) : null;
            if (account is null)
            {
                account = new CashAccount { UserId = userId, UpdatedAt = clock.UtcNow };
                db.CashAccounts.Add(account);
                if (e.OpeningBalance is { } ob)
                {
                    if (Math.Abs(ob) > 2_000_000_000) throw new ValidationException($"「{e.Name}」金額超出範圍。");
                    opening.Lines.Add(new ReconciliationLine { Balance = Math.Round(ob, 0) });
                    // 帳戶 Id 要存檔後才有，先記住對應
                    pendingOpenings.Add((account, opening.Lines[^1]));
                }
            }
            else
            {
                keep.Add(account.Id);
            }
            account.Name = e.Name.Trim();
            account.Type = e.Type;
            account.SortOrder = i;
            account.IsArchived = false;
        }
        // 清單裡沒有的帳戶只封存不刪：對帳、轉帳紀錄還會用到它
        foreach (var a in existing.Where(a => !keep.Contains(a.Id))) a.IsArchived = true;

        await db.SaveChangesAsync(ct);
        if (pendingOpenings.Count > 0)
        {
            foreach (var (account, line) in pendingOpenings) line.AccountId = account.Id;
            db.Reconciliations.Add(opening);
            await db.SaveChangesAsync(ct);
        }
        return await GetAsync(userId, ct);
    }

    private readonly List<(CashAccount Account, ReconciliationLine Line)> pendingOpenings = [];

    /// <summary>舊版直接存的帳戶餘額 → 一筆「期初」完整對帳（只做一次）。</summary>
    public async Task EnsureMigratedAsync(string userId, CancellationToken ct = default)
    {
        if (await db.Reconciliations.AnyAsync(r => r.UserId == userId, ct)) return;
        var accounts = await db.CashAccounts.Where(a => a.UserId == userId && !a.IsArchived).ToListAsync(ct);
        if (accounts.Count == 0) return;
        var today = await settings.LogicalTodayAsync(userId, ct);
        db.Reconciliations.Add(new Reconciliation
        {
            UserId = userId,
            Date = today,
            IsFull = true,
            Note = "期初（由舊版帳戶餘額匯入）",
            CreatedAt = clock.UtcNow,
            Lines = accounts.Select(a => new ReconciliationLine { AccountId = a.Id, Balance = a.Balance }).ToList(),
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<Dictionary<int, decimal>> BalancesAsync(string userId, CancellationToken ct = default)
    {
        var accounts = await db.CashAccounts.Where(a => a.UserId == userId).ToDictionaryAsync(a => a.Id, ct);
        var recons = (await LoadReconsAsync(userId, ct)).Active().OrderBy(r => r.Date).ThenBy(r => r.CreatedAt).ToList();
        var transfers = (await db.AccountTransfers.Where(t => t.UserId == userId).ToListAsync(ct)).Active();
        var adjustments = (await db.AssetAdjustments.Where(a => a.UserId == userId).ToListAsync(ct)).Active();
        var periodIds = await db.Periods.Where(p => p.UserId == userId).Select(p => p.Id).ToListAsync(ct);
        // 作廢 / 取代的紀錄不帶 AccountId，要一起讀進來 Active() 才判斷得出哪些已經不算了
        var paidEntries = (await db.Entries.Where(e => periodIds.Contains(e.PeriodId) && (e.AccountId != null || e.ReplacesId != null)).ToListAsync(ct)).Active()
            .Where(e => e.AccountId != null && e.InputMode == EntryInputMode.Actual).ToList();
        var (debts, settlements) = await DebtMath.LoadAsync(db, userId, ct);
        var debtKinds = debts.ToDictionary(d => d.Id, d => d.Kind);
        var prepayments = await InstallmentMath.PrepaymentsAsync(db, userId, ct);

        var result = new Dictionary<int, decimal>();
        foreach (var (id, account) in accounts)
        {
            var last = recons.LastOrDefault(r => r.Lines.Any(l => l.AccountId == id));
            var baseBalance = last?.Lines.Last(l => l.AccountId == id).Balance ?? 0;
            var since = last is null ? (DateOnly.MinValue, DateTime.MinValue) : (last.Date, last.CreatedAt);
            bool After(DateOnly d, DateTime created) => d > since.Item1 || (d == since.Item1 && created > since.Item2);
            var liability = IsLiability(account.Type);

            // 資產帳戶：進 +、出 −；信用卡（欠款）：刷卡 +、還款 −
            decimal delta = 0;
            foreach (var t in transfers.Where(t => After(t.Date, t.CreatedAt)))
            {
                if (t.FromAccountId == id) delta += liability ? t.Amount : -t.Amount;
                if (t.ToAccountId == id) delta += liability ? -t.Amount : t.Amount;
            }
            foreach (var a in adjustments.Where(a => a.CashAccountId == id && After(a.Date, a.CreatedAt)))
            {
                delta += liability ? -a.Amount : a.Amount;
            }
            foreach (var e in paidEntries.Where(e => e.AccountId == id && After(e.Date, e.CreatedAt)))
            {
                delta += liability ? e.InputAmount : -e.InputAmount;
            }
            // 分帳（§14）：代墊多付的錢從帳戶出去；收回應收進帳；還應付出帳
            foreach (var d in debts.Where(d => d.AccountId == id && d.Kind == DebtKind.Receivable && After(d.Date, d.CreatedAt)))
            {
                delta += liability ? d.Amount : -d.Amount;
            }
            foreach (var s in settlements.Where(s => s.AccountId == id && debtKinds.ContainsKey(s.DebtId) && After(s.Date, s.CreatedAt)))
            {
                var incoming = debtKinds[s.DebtId] == DebtKind.Receivable;
                delta += (incoming ? 1 : -1) * (liability ? -s.Amount : s.Amount);
            }
            // 分期提前還款（§15）
            foreach (var p in prepayments.Where(p => p.AccountId == id && After(p.Date, p.CreatedAt)))
            {
                delta += liability ? p.Amount : -p.Amount;
            }
            result[id] = baseBalance + delta;
        }
        return result;
    }

    // ---------------- 轉帳 / 繳卡費 ----------------

    public async Task<List<TransferView>> ListTransfersAsync(string userId, DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var names = await db.CashAccounts.Where(a => a.UserId == userId).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        return (await db.AccountTransfers.Where(t => t.UserId == userId).ToListAsync(ct)).Active()
            .Where(t => (from is null || t.Date >= from) && (to is null || t.Date <= to))
            .OrderByDescending(t => t.Date).ThenByDescending(t => t.Id)
            .Select(t => ToView(t, names))
            .ToList();
    }

    public async Task<TransferView> AddTransferAsync(string userId, CreateTransferRequest req, CancellationToken ct = default)
    {
        var accounts = await db.CashAccounts.Where(a => a.UserId == userId && !a.IsArchived).ToDictionaryAsync(a => a.Id, ct);
        if (!accounts.TryGetValue(req.FromAccountId, out var from) || !accounts.TryGetValue(req.ToAccountId, out var to))
            throw new ValidationException("找不到帳戶。");
        if (from.Id == to.Id) throw new ValidationException("轉出和轉入不能是同一個帳戶。");
        var amount = Math.Round(req.Amount, 0);
        if (amount <= 0 || amount > 100_000_000) throw new ValidationException("金額要大於 0。");
        if (req.Kind == TransferKind.CardPayment && !IsLiability(to.Type)) throw new ValidationException("繳卡費的轉入帳戶要是信用卡。");

        var note = req.Note?.Trim();
        var t = new AccountTransfer
        {
            UserId = userId,
            Date = req.Date,
            Kind = req.Kind,
            FromAccountId = from.Id,
            ToAccountId = to.Id,
            Amount = amount,
            Note = string.IsNullOrEmpty(note) ? null : note[..Math.Min(note.Length, 120)],
            CreatedAt = clock.UtcNow,
        };
        db.AccountTransfers.Add(t);
        await db.SaveChangesAsync(ct);
        return ToView(t, accounts.ToDictionary(a => a.Key, a => a.Value.Name));
    }

    public async Task DeleteTransferAsync(string userId, int id, CancellationToken ct = default)
    {
        var t = (await db.AccountTransfers.Where(x => x.UserId == userId).ToListAsync(ct)).Active().FirstOrDefault(x => x.Id == id)
                ?? throw new NotFoundException("找不到這筆轉帳。");
        db.AccountTransfers.Add(new AccountTransfer
        {
            UserId = userId, Date = t.Date, Kind = t.Kind, FromAccountId = t.FromAccountId, ToAccountId = t.ToAccountId,
            Note = "刪除", IsVoid = true, ReplacesId = t.Id, CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    private static TransferView ToView(AccountTransfer t, IReadOnlyDictionary<int, string> names) =>
        new(t.Id, t.Date, t.Kind, t.FromAccountId, names.GetValueOrDefault(t.FromAccountId, "（已刪除）"),
            t.ToAccountId, names.GetValueOrDefault(t.ToAccountId, "（已刪除）"), t.Amount, t.Note, t.CreatedAt);

    // ---------------- 對帳 ----------------

    public async Task<List<ReconciliationSummary>> ListReconciliationsAsync(string userId, CancellationToken ct = default)
    {
        var types = await db.CashAccounts.Where(a => a.UserId == userId).ToDictionaryAsync(a => a.Id, a => a.Type, ct);
        return (await LoadReconsAsync(userId, ct)).Active()
            .OrderByDescending(r => r.Date).ThenByDescending(r => r.CreatedAt)
            .Select(r => new ReconciliationSummary(r.Id, r.Date, r.IsFull, Net(r, types), r.Note, r.CreatedAt,
                r.Lines.Select(l => new ReconcileLineInput(l.AccountId, l.Balance)).ToList()))
            .ToList();
    }

    public Task<ReconciliationResult> PreviewReconciliationAsync(string userId, CreateReconciliationRequest req, CancellationToken ct = default) =>
        RunAsync(userId, req, persist: false, ct);

    public Task<ReconciliationResult> CreateReconciliationAsync(string userId, CreateReconciliationRequest req, CancellationToken ct = default) =>
        RunAsync(userId, req, persist: true, ct);

    public async Task DeleteReconciliationAsync(string userId, int id, CancellationToken ct = default)
    {
        var r = (await LoadReconsAsync(userId, ct)).Active().FirstOrDefault(x => x.Id == id) ?? throw new NotFoundException("找不到這次對帳。");
        db.Reconciliations.Add(new Reconciliation
        {
            UserId = userId, Date = r.Date, IsFull = r.IsFull, Note = "刪除", IsVoid = true, ReplacesId = r.Id, CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task<ReconciliationResult> RunAsync(string userId, CreateReconciliationRequest req, bool persist, CancellationToken ct)
    {
        await EnsureMigratedAsync(userId, ct);
        var today = await settings.LogicalTodayAsync(userId, ct);
        if (req.Date > today) throw new ValidationException("對帳日期不能是未來。");
        if (await db.Periods.AnyAsync(p => p.UserId == userId && p.StartDate <= req.Date && p.EndDate >= req.Date && p.ClosedAt != null, ct))
            throw new ValidationException("那一期已經月結，不能再加對帳（§4.2）。");
        var accounts = await db.CashAccounts.Where(a => a.UserId == userId && !a.IsArchived).ToDictionaryAsync(a => a.Id, ct);
        if (req.Lines.Count == 0) throw new ValidationException("至少要填一個帳戶的餘額。");
        foreach (var l in req.Lines)
        {
            if (!accounts.ContainsKey(l.AccountId)) throw new ValidationException("找不到帳戶。");
            if (Math.Abs(l.Balance) > 2_000_000_000) throw new ValidationException("金額超出範圍。");
        }
        var note = req.Note?.Trim();
        var recon = new Reconciliation
        {
            UserId = userId,
            Date = req.Date,
            IsFull = accounts.Keys.All(id => req.Lines.Any(l => l.AccountId == id)),
            UsePool = req.UsePool,
            Note = string.IsNullOrEmpty(note) ? null : note[..Math.Min(note.Length, 120)],
            CreatedAt = clock.UtcNow,
            Lines = req.Lines.GroupBy(l => l.AccountId).Select(g => new ReconciliationLine { AccountId = g.Key, Balance = Math.Round(g.Last().Balance, 0) }).ToList(),
        };

        var period = await db.Periods.AsNoTracking()
            .Where(p => p.UserId == userId && p.StartDate <= req.Date && p.EndDate >= req.Date)
            .Select(p => (int?)p.Id).FirstOrDefaultAsync(ct);

        PeriodView? before = null;
        if (period is { } pid) before = await periods.ComputeAsync(await periods.LoadAsync(userId, pid, ct, tracking: false), ct);

        if (persist)
        {
            db.Reconciliations.Add(recon);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            recon.Id = int.MaxValue;
        }

        var types = accounts.ToDictionary(a => a.Key, a => a.Value.Type);
        if (period is not { } periodId || !recon.IsFull)
        {
            var net = Net(recon, types);
            return new ReconciliationResult(persist ? recon.Id : null, recon.Date, false, null, net, net, 0, 0, 0, 0, false,
                before?.Pool.Balance ?? 0, before?.Pool.Balance ?? 0);
        }

        var after = await periods.ComputeAsync(await periods.LoadAsync(userId, periodId, ct, tracking: false), ct, persist ? null : recon);
        var rv = after.Reconciliations.FirstOrDefault(x => x.Id == recon.Id);
        var baseline = (await LoadReconsAsync(userId, ct)).Active()
            .Where(x => x.IsFull && x.Id != recon.Id && (x.Date < recon.Date || (x.Date == recon.Date && x.CreatedAt < recon.CreatedAt)))
            .OrderBy(x => x.Date).ThenBy(x => x.CreatedAt).LastOrDefault();

        if (rv is null)
        {
            var net = Net(recon, types);
            return new ReconciliationResult(persist ? recon.Id : null, recon.Date, false, baseline?.Date, net, net, 0, 0, 0, 0, false,
                before?.Pool.Balance ?? 0, after.Pool.Balance);
        }

        var variableBudget = after.Categories.Where(c => c.Mode != BudgetMode.Fixed).Sum(c => c.Budget);
        return new ReconciliationResult(
            persist ? recon.Id : null, recon.Date, true, baseline?.Date,
            rv.Expected, rv.Actual, rv.Diff, rv.FromPool, rv.Spread, rv.Unabsorbed,
            variableBudget > 0 && Math.Abs(rv.Diff) > variableBudget * 0.3m,
            before?.Pool.Balance ?? 0, after.Pool.Balance);
    }

    private async Task<List<Reconciliation>> LoadReconsAsync(string userId, CancellationToken ct) =>
        await db.Reconciliations.Include(r => r.Lines).Where(r => r.UserId == userId).ToListAsync(ct);

    public static decimal Net(Reconciliation r, IReadOnlyDictionary<int, AccountType> types) =>
        r.Lines.Sum(l => types.TryGetValue(l.AccountId, out var t) && IsLiability(t) ? -l.Balance : l.Balance);
}
