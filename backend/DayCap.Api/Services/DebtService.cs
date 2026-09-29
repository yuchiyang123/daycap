using DayCap.Api.Common;
using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Services;

public interface IDebtService
{
    Task<DebtsView> ListAsync(string userId, CancellationToken ct = default);
    Task<DebtsView> CreateAsync(string userId, CreateDebtRequest req, CancellationToken ct = default);
    Task<DebtsView> SettleAsync(string userId, int debtId, SettleDebtRequest req, CancellationToken ct = default);
    Task<DebtsView> DeleteAsync(string userId, int debtId, CancellationToken ct = default);
}

/// <summary>
/// 共用支出（§14）：代墊的錢不是花費，是應收（資產）；別人先幫我付是應付（負債）。
/// 收回 / 還款只動帳戶，預算不動。對帳時這些錢的進出算在「已知收支」裡，不會被當成沒交代的差異。
/// </summary>
public class DebtService(DayCapDbContext db, ISettingsService settings, IAppClock clock) : IDebtService
{
    public async Task<DebtsView> ListAsync(string userId, CancellationToken ct = default)
    {
        var (debts, settlements) = await DebtMath.LoadAsync(db, userId, ct);
        var settled = settlements.GroupBy(s => s.DebtId).ToDictionary(g => g.Key, g => g.Sum(s => s.Amount));
        var views = debts
            .Select(d => new DebtView(d.Id, d.Kind, d.Counterparty, d.Amount, settled.GetValueOrDefault(d.Id),
                d.Amount - settled.GetValueOrDefault(d.Id), d.Date, d.Note, d.AccountId, d.SourceEntryId))
            .OrderBy(d => d.Outstanding == 0 ? 1 : 0).ThenByDescending(d => d.Date).ThenByDescending(d => d.Id)
            .ToList();
        var open = views.Where(v => v.Outstanding != 0).ToList();
        var people = open.GroupBy(v => v.Counterparty)
            .Select(g =>
            {
                var r = g.Where(v => v.Kind == DebtKind.Receivable).Sum(v => v.Outstanding);
                var p = g.Where(v => v.Kind == DebtKind.Payable).Sum(v => v.Outstanding);
                return new CounterpartyBalance(g.Key, r, p, r - p);
            })
            .OrderByDescending(x => Math.Abs(x.Net)).ToList();
        return new DebtsView(views,
            open.Where(v => v.Kind == DebtKind.Receivable).Sum(v => v.Outstanding),
            open.Where(v => v.Kind == DebtKind.Payable).Sum(v => v.Outstanding),
            people);
    }

    public async Task<DebtsView> CreateAsync(string userId, CreateDebtRequest req, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(req.Kind)) throw new ValidationException("種類不正確。");
        var name = CleanName(req.Counterparty);
        var amount = Math.Round(req.Amount, 0);
        if (amount is <= 0 or > 100_000_000) throw new ValidationException("金額要大於 0。");
        await CheckAccountAsync(userId, req.AccountId, ct);
        db.Debts.Add(new Debt
        {
            UserId = userId, Kind = req.Kind, Counterparty = name, Amount = amount,
            Date = req.Date ?? await settings.LogicalTodayAsync(userId, ct),
            AccountId = req.AccountId, Note = Trim(req.Note, 120), CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return await ListAsync(userId, ct);
    }

    public async Task<DebtsView> SettleAsync(string userId, int debtId, SettleDebtRequest req, CancellationToken ct = default)
    {
        var view = await ListAsync(userId, ct);
        var debt = view.Debts.FirstOrDefault(d => d.Id == debtId) ?? throw new NotFoundException("找不到這筆應收應付。");
        var amount = Math.Round(req.Amount, 0);
        if (amount <= 0) throw new ValidationException("金額要大於 0。");
        if (amount > debt.Outstanding) throw new ValidationException($"只剩 {debt.Outstanding:N0} 沒結清。");
        await CheckAccountAsync(userId, req.AccountId, ct);
        db.DebtSettlements.Add(new DebtSettlement
        {
            UserId = userId, DebtId = debtId, Amount = amount, AccountId = req.AccountId,
            Date = req.Date ?? await settings.LogicalTodayAsync(userId, ct), CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return await ListAsync(userId, ct);
    }

    public async Task<DebtsView> DeleteAsync(string userId, int debtId, CancellationToken ct = default)
    {
        var (debts, _) = await DebtMath.LoadAsync(db, userId, ct);
        var debt = debts.FirstOrDefault(d => d.Id == debtId) ?? throw new NotFoundException("找不到這筆應收應付。");
        if (debt.SourceEntryId is not null) throw new ValidationException("這筆是回報時分帳出來的，要刪那筆回報。");
        await DebtMath.VoidAsync(db, userId, debt, clock.UtcNow, ct);
        await db.SaveChangesAsync(ct);
        return await ListAsync(userId, ct);
    }

    private async Task CheckAccountAsync(string userId, int? accountId, CancellationToken ct)
    {
        if (accountId is { } id && !await db.CashAccounts.AnyAsync(a => a.Id == id && a.UserId == userId && !a.IsArchived, ct))
            throw new ValidationException("找不到這個帳戶。");
    }

    public static string CleanName(string? name)
    {
        var n = name?.Trim() ?? "";
        if (n.Length is 0 or > 40) throw new ValidationException("對象名字要 1 到 40 個字。");
        return n;
    }

    private static string? Trim(string? s, int max) => s?.Trim() is { Length: > 0 } t ? t[..Math.Min(t.Length, max)] : null;
}

/// <summary>應收應付的讀取、作廢、以及對帳 / 帳戶餘額要用的錢流（給其他服務共用）。</summary>
public static class DebtMath
{
    public static async Task<(List<Debt> Debts, List<DebtSettlement> Settlements)> LoadAsync(DayCapDbContext db, string userId, CancellationToken ct)
    {
        var debts = (await db.Debts.AsNoTracking().Where(d => d.UserId == userId).ToListAsync(ct)).Active();
        var ids = debts.Select(d => d.Id).ToHashSet();
        var settlements = (await db.DebtSettlements.AsNoTracking().Where(s => s.UserId == userId).ToListAsync(ct)).Active()
            .Where(s => ids.Contains(s.DebtId)).ToList();
        return (debts, settlements);
    }

    /// <summary>作廢一筆應收應付和它的收回 / 還款紀錄（只新增作廢紀錄）。</summary>
    public static async Task VoidAsync(DayCapDbContext db, string userId, Debt debt, DateTime now, CancellationToken ct)
    {
        db.Debts.Add(new Debt
        {
            UserId = userId, Kind = debt.Kind, Counterparty = debt.Counterparty, Date = debt.Date, Note = "刪除",
            SourceEntryId = debt.SourceEntryId, IsVoid = true, ReplacesId = debt.Id, CreatedAt = now,
        });
        foreach (var s in (await db.DebtSettlements.AsNoTracking().Where(s => s.UserId == userId && s.DebtId == debt.Id).ToListAsync(ct)).Active())
        {
            db.DebtSettlements.Add(new DebtSettlement
            {
                UserId = userId, DebtId = debt.Id, Date = s.Date, IsVoid = true, ReplacesId = s.Id, CreatedAt = now,
            });
        }
    }

    /// <summary>
    /// 某天應收應付造成的淨資產流動（對帳的「已知收支」）：
    /// 應收成立 −（多付的錢出去了）、收回 +；應付成立 +（預算已經算了我的份，但錢沒從我的帳戶出去）、還款 −。
    /// </summary>
    public static decimal NetFlow(IEnumerable<Debt> debts, IEnumerable<DebtSettlement> settlements, Func<DateOnly, bool> inRange)
    {
        var kinds = debts.ToDictionary(d => d.Id, d => d.Kind);
        var created = debts.Where(d => inRange(d.Date)).Sum(d => d.Kind == DebtKind.Receivable ? -d.Amount : d.Amount);
        var settled = settlements.Where(s => inRange(s.Date) && kinds.ContainsKey(s.DebtId))
            .Sum(s => kinds[s.DebtId] == DebtKind.Receivable ? s.Amount : -s.Amount);
        return created + settled;
    }
}
