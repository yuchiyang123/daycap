using DayCap.Api.Common;
using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using DayCap.Api.Models.Settings;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Services;

public interface IInstallmentService
{
    Task<List<InstallmentView>> ListAsync(string userId, CancellationToken ct = default);
    Task<List<InstallmentView>> CreateAsync(string userId, CreateInstallmentRequest req, CancellationToken ct = default);
    Task<List<InstallmentView>> PrepayAsync(string userId, int id, PrepayRequest req, CancellationToken ct = default);
    Task<List<InstallmentView>> DeleteAsync(string userId, int id, CancellationToken ct = default);
    /// <summary>試算：不存檔，回傳繳款表。</summary>
    InstallmentView Preview(CreateInstallmentRequest req);
}

/// <summary>分期（§15）：每期繳款＝固定支出；提前還款從資產付，負債減少，不算花費、不扣預算。</summary>
public class InstallmentService(DayCapDbContext db, ISettingsService settings, IAppClock clock) : IInstallmentService
{
    public async Task<List<InstallmentView>> ListAsync(string userId, CancellationToken ct = default)
    {
        var today = await settings.LogicalTodayAsync(userId, ct);
        var items = await InstallmentMath.LoadAsync(db, userId, ct);
        var prepayments = await InstallmentMath.PrepaymentsAsync(db, userId, ct);
        return items
            .Select(i => ToView(i, prepayments.Where(p => p.InstallmentId == i.Id).ToList(), today))
            .OrderBy(v => v.Remaining == 0 ? 1 : 0).ThenBy(v => v.NextDueDate ?? DateOnly.MaxValue)
            .ToList();
    }

    public InstallmentView Preview(CreateInstallmentRequest req)
    {
        var inst = Build(req, "preview");
        return ToView(inst, [], clock.Today);
    }

    public async Task<List<InstallmentView>> CreateAsync(string userId, CreateInstallmentRequest req, CancellationToken ct = default)
    {
        var inst = Build(req, userId);
        var doc = (await settings.GetTimelineAsync(userId, ct)).Latest.Doc;
        if (!doc.Categories.Any(c => c.Id == req.CategoryId && c.Mode == BudgetMode.Fixed))
            throw new ValidationException("每期繳款要放在一個固定支出類別。");
        db.Installments.Add(inst);
        await db.SaveChangesAsync(ct);
        return await ListAsync(userId, ct);
    }

    public async Task<List<InstallmentView>> PrepayAsync(string userId, int id, PrepayRequest req, CancellationToken ct = default)
    {
        var view = (await ListAsync(userId, ct)).FirstOrDefault(v => v.Id == id) ?? throw new NotFoundException("找不到這筆分期。");
        var amount = Math.Round(req.Amount, 0);
        if (amount <= 0) throw new ValidationException("金額要大於 0。");
        if (amount > view.PrincipalRemaining) throw new ValidationException($"剩下的本金只有 {view.PrincipalRemaining:N0}。");
        if (!Enum.IsDefined(req.Mode)) throw new ValidationException("提前還款方式不正確。");
        if (req.AccountId is { } acc && !await db.CashAccounts.AnyAsync(a => a.Id == acc && a.UserId == userId && !a.IsArchived, ct))
            throw new ValidationException("找不到這個帳戶。");
        db.InstallmentPrepayments.Add(new InstallmentPrepayment
        {
            UserId = userId, InstallmentId = id, Amount = amount, AccountId = req.AccountId, Mode = req.Mode,
            Date = req.Date ?? await settings.LogicalTodayAsync(userId, ct), CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return await ListAsync(userId, ct);
    }

    public async Task<List<InstallmentView>> DeleteAsync(string userId, int id, CancellationToken ct = default)
    {
        var inst = (await InstallmentMath.LoadAsync(db, userId, ct)).FirstOrDefault(i => i.Id == id) ?? throw new NotFoundException("找不到這筆分期。");
        db.Installments.Add(new Installment
        {
            UserId = userId, Name = inst.Name, CategoryId = inst.CategoryId, FirstDueDate = inst.FirstDueDate, Periods = inst.Periods,
            Note = "刪除", IsVoid = true, ReplacesId = inst.Id, CreatedAt = clock.UtcNow,
        });
        foreach (var p in (await InstallmentMath.PrepaymentsAsync(db, userId, ct)).Where(p => p.InstallmentId == id))
        {
            db.InstallmentPrepayments.Add(new InstallmentPrepayment
            {
                UserId = userId, InstallmentId = id, Date = p.Date, IsVoid = true, ReplacesId = p.Id, CreatedAt = clock.UtcNow,
            });
        }
        await db.SaveChangesAsync(ct);
        return await ListAsync(userId, ct);
    }

    private Installment Build(CreateInstallmentRequest req, string userId)
    {
        var name = req.Name?.Trim() ?? "";
        if (name.Length is 0 or > 40) throw new ValidationException("名稱要 1 到 40 個字。");
        if (!Enum.IsDefined(req.Mode)) throw new ValidationException("輸入方式不正確。");
        if (req.Periods is < 1 or > 360) throw new ValidationException("期數要在 1 到 360 之間。");
        if (req.Mode == InstallmentMode.Simple)
        {
            if (req.MonthlyAmount is not > 0 || req.MonthlyAmount > 10_000_000) throw new ValidationException("每期金額要大於 0。");
        }
        else
        {
            if (req.Principal is not > 0 || req.Principal > 100_000_000) throw new ValidationException("本金要大於 0。");
            if (req.AnnualRatePercent is < 0 or > 100) throw new ValidationException("年利率要在 0 到 100% 之間。");
            if (req.Fee is < 0) throw new ValidationException("手續費不能是負的。");
        }
        return new Installment
        {
            UserId = userId,
            Name = name,
            CategoryId = req.CategoryId,
            Mode = req.Mode,
            MonthlyAmount = req.Mode == InstallmentMode.Simple ? Math.Round(req.MonthlyAmount!.Value, 0) : null,
            Periods = req.Periods,
            Principal = req.Mode == InstallmentMode.Detailed ? Math.Round(req.Principal!.Value, 0) : null,
            AnnualRatePercent = req.Mode == InstallmentMode.Detailed ? req.AnnualRatePercent ?? 0 : null,
            Fee = req.Mode == InstallmentMode.Detailed ? Math.Round(req.Fee ?? 0, 0) : null,
            FirstDueDate = req.FirstDueDate,
            Note = req.Note?.Trim() is { Length: > 0 } n ? n[..Math.Min(n.Length, 120)] : null,
            CreatedAt = clock.UtcNow,
        };
    }

    private static InstallmentView ToView(Installment i, List<InstallmentPrepayment> prepayments, DateOnly today)
    {
        var rows = InstallmentSchedule.Build(i, prepayments);
        var paid = rows.Where(r => r.DueDate <= today).ToList();
        var future = rows.Where(r => r.DueDate > today).ToList();
        var principalPaid = paid.Sum(r => r.Principal) + prepayments.Where(p => p.Date <= today).Sum(p => p.Amount);
        var principalTotal = i.Mode == InstallmentMode.Simple ? (i.MonthlyAmount ?? 0) * i.Periods : i.Principal ?? 0;
        return new InstallmentView(
            i.Id, i.Name, i.CategoryId, i.Mode, i.MonthlyAmount, i.Periods, i.Principal, i.AnnualRatePercent, i.Fee, i.FirstDueDate, i.Note,
            rows.Count, paid.Count, future.Sum(r => r.Payment), Math.Max(0, principalTotal - principalPaid),
            future.FirstOrDefault()?.DueDate, future.FirstOrDefault()?.Payment,
            rows.Sum(r => r.Interest + r.Fee),
            rows.Select(r => new InstallmentRowView(r.Index, r.DueDate, r.Payment, r.Principal, r.Interest, r.Fee, r.BalanceAfter)).ToList(),
            prepayments.Select(p => new PrepaymentView(p.Id, p.Date, p.Amount, p.Mode, p.AccountId)).ToList());
    }
}

/// <summary>分期的讀取與每期固定支出（期間服務、帳戶服務共用）。</summary>
public static class InstallmentMath
{
    public static async Task<List<Installment>> LoadAsync(DayCapDbContext db, string userId, CancellationToken ct) =>
        (await db.Installments.AsNoTracking().Where(i => i.UserId == userId).ToListAsync(ct)).Active();

    public static async Task<List<InstallmentPrepayment>> PrepaymentsAsync(DayCapDbContext db, string userId, CancellationToken ct)
    {
        var ids = (await LoadAsync(db, userId, ct)).Select(i => i.Id).ToHashSet();
        return (await db.InstallmentPrepayments.AsNoTracking().Where(p => p.UserId == userId).ToListAsync(ct)).Active()
            .Where(p => ids.Contains(p.InstallmentId)).ToList();
    }

    /// <summary>這段期間內到期的分期繳款，當成固定支出（§15）。</summary>
    public static async Task<List<PeriodFixedCharge>> ChargesAsync(DayCapDbContext db, string userId, DateOnly start, DateOnly end, CancellationToken ct)
    {
        var items = await LoadAsync(db, userId, ct);
        if (items.Count == 0) return [];
        var prepayments = await PrepaymentsAsync(db, userId, ct);
        var result = new List<PeriodFixedCharge>();
        foreach (var i in items)
        {
            var rows = InstallmentSchedule.Build(i, prepayments.Where(p => p.InstallmentId == i.Id));
            foreach (var r in rows.Where(r => r.DueDate >= start && r.DueDate <= end))
            {
                result.Add(new PeriodFixedCharge
                {
                    CategoryId = i.CategoryId, Name = $"分期：{i.Name}（{r.Index}/{rows.Count}）", Amount = r.Payment, DueDate = r.DueDate,
                });
            }
        }
        return result;
    }
}
