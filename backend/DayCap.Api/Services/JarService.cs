using DayCap.Api.Common;
using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Services;

public interface IJarService
{
    Task<List<JarView>> ListAsync(string userId, CancellationToken ct = default);
    Task<List<JarView>> CreateAsync(string userId, SaveJarRequest req, CancellationToken ct = default);
    Task<List<JarView>> UpdateAsync(string userId, int jarId, SaveJarRequest req, CancellationToken ct = default);
    /// <summary>Amount &gt; 0 = 從待分配池存進罐子；&lt; 0 = 從罐子拿回池子。</summary>
    Task<List<JarView>> MoveAsync(string userId, int jarId, decimal amount, CancellationToken ct = default);
    /// <summary>關閉：剩下的錢回待分配池。</summary>
    Task<List<JarView>> CloseAsync(string userId, int jarId, CancellationToken ct = default);
}

/// <summary>
/// 罐子（§11.2）。錢的進出一律記成本期的待分配池調整（帶 JarId），所以池子和罐子永遠對得起來；
/// 從罐子付的花費記在回報上（JarCovered）。
/// </summary>
public class JarService(DayCapDbContext db, IPeriodService periods, ISettingsService settings, IAppClock clock) : IJarService
{
    public async Task<List<JarView>> ListAsync(string userId, CancellationToken ct = default)
    {
        var jars = await db.Jars.AsNoTracking().Where(j => j.UserId == userId).ToListAsync(ct);
        var balances = await JarMath.BalancesAsync(db, userId, ct);
        var today = await settings.LogicalTodayAsync(userId, ct);
        return jars
            .OrderBy(j => j.ClosedAt is null ? 0 : 1)
            .ThenBy(j => j.DueDate ?? DateOnly.MaxValue) // 快到期的在前（§11.2 分配順序 1）
            .ThenBy(j => j.SortOrder).ThenBy(j => j.Id)
            .Select(j => ToView(j, balances.GetValueOrDefault(j.Id), today))
            .ToList();
    }

    public async Task<List<JarView>> CreateAsync(string userId, SaveJarRequest req, CancellationToken ct = default)
    {
        Validate(req);
        await ValidateAutoTotalAsync(userId, null, req.AutoSurplusPercent, ct);
        var current = await periods.GetCurrentAsync(userId, ct);
        if (current.Closed) throw new ValidationException("這期已經月結了。");

        // 年繳固定支出轉成預付提撥：那個固定支出從明天起停用，改由罐子付（先改設定，失敗就什麼都不建）
        if (req.Kind == JarKind.Annual && req.FixedItemId is { } itemId)
            await DeactivateFixedItemAsync(userId, itemId, ct);

        var jar = new Jar
        {
            UserId = userId,
            Kind = req.Kind,
            Name = req.Name.Trim(),
            TargetAmount = Math.Round(req.TargetAmount, 0),
            DueDate = req.DueDate,
            MonthlyAmount = req.Kind == JarKind.Annual ? Monthly(req) : null,
            AutoSurplusPercent = req.AutoSurplusPercent is > 0 ? req.AutoSurplusPercent : null,
            FixedItemId = req.Kind == JarKind.Annual ? req.FixedItemId : null,
            CreatedAt = clock.UtcNow,
        };
        db.Jars.Add(jar);
        await db.SaveChangesAsync(ct);

        var period = await periods.LoadAsync(userId, current.Id, ct);
        switch (jar.Kind)
        {
            case JarKind.Reservation:
                // 登記當下就從「確定可用」扣預留額；池子不夠的攤到之後每天（§11.2）
                period.PoolTransfers.Add(new PoolTransfer
                {
                    Date = current.Today, Amount = -jar.TargetAmount, JarId = jar.Id, SpreadShortfall = true,
                    Note = $"預約：{jar.Name}", CreatedAt = clock.UtcNow,
                });
                break;
            case JarKind.Annual:
                period.PoolTransfers.Add(new PoolTransfer
                {
                    Date = current.Today, Amount = -Math.Min(jar.MonthlyAmount!.Value, jar.TargetAmount), JarId = jar.Id,
                    Note = $"年繳提撥：{jar.Name}", CreatedAt = clock.UtcNow,
                });
                break;
        }
        await db.SaveChangesAsync(ct);
        return await ListAsync(userId, ct);
    }

    public async Task<List<JarView>> UpdateAsync(string userId, int jarId, SaveJarRequest req, CancellationToken ct = default)
    {
        Validate(req);
        var jar = await FindAsync(userId, jarId, ct);
        if (req.Kind != jar.Kind) throw new ValidationException("罐子的種類不能改；要換種類請關掉再開新的。");
        await ValidateAutoTotalAsync(userId, jarId, req.AutoSurplusPercent, ct);
        jar.Name = req.Name.Trim();
        jar.TargetAmount = Math.Round(req.TargetAmount, 0);
        jar.DueDate = req.DueDate;
        if (jar.Kind == JarKind.Annual) jar.MonthlyAmount = Monthly(req);
        jar.AutoSurplusPercent = req.AutoSurplusPercent is > 0 ? req.AutoSurplusPercent : null;
        await db.SaveChangesAsync(ct);
        return await ListAsync(userId, ct);
    }

    public async Task<List<JarView>> MoveAsync(string userId, int jarId, decimal amount, CancellationToken ct = default)
    {
        amount = Math.Round(amount, 0);
        if (amount == 0) throw new ValidationException("金額不能是 0。");
        var jar = await FindAsync(userId, jarId, ct);
        if (jar.ClosedAt is not null) throw new ValidationException("這個罐子已經關閉了。");
        var balance = (await JarMath.BalancesAsync(db, userId, ct)).GetValueOrDefault(jar.Id);
        if (amount < 0 && -amount > balance) throw new ValidationException($"罐子裡只有 {balance:N0}。");

        var current = await periods.GetCurrentAsync(userId, ct);
        if (current.Closed) throw new ValidationException("這期已經月結了。");
        var period = await periods.LoadAsync(userId, current.Id, ct);
        period.PoolTransfers.Add(new PoolTransfer
        {
            Date = current.Today, Amount = -amount, JarId = jar.Id,
            Note = amount > 0 ? $"存進{jar.Name}" : $"從{jar.Name}拿回", CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return await ListAsync(userId, ct);
    }

    public async Task<List<JarView>> CloseAsync(string userId, int jarId, CancellationToken ct = default)
    {
        var jar = await FindAsync(userId, jarId, ct);
        if (jar.ClosedAt is not null) return await ListAsync(userId, ct);
        var balance = (await JarMath.BalancesAsync(db, userId, ct)).GetValueOrDefault(jar.Id);
        if (balance != 0)
        {
            var current = await periods.GetCurrentAsync(userId, ct);
            if (current.Closed) throw new ValidationException("這期已經月結了。");
            var period = await periods.LoadAsync(userId, current.Id, ct);
            period.PoolTransfers.Add(new PoolTransfer
            {
                Date = current.Today, Amount = balance, JarId = jar.Id, Note = $"關閉{jar.Name}，剩下的回池子", CreatedAt = clock.UtcNow,
            });
        }
        jar.ClosedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return await ListAsync(userId, ct);
    }

    // ---------------- 內部 ----------------

    private async Task<Jar> FindAsync(string userId, int jarId, CancellationToken ct) =>
        await db.Jars.FirstOrDefaultAsync(j => j.Id == jarId && j.UserId == userId, ct) ?? throw new NotFoundException("找不到這個罐子。");

    private static void Validate(SaveJarRequest req)
    {
        if (!Enum.IsDefined(req.Kind)) throw new ValidationException("罐子種類不正確。");
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Trim().Length > 40) throw new ValidationException("名稱要 1 到 40 個字。");
        if (req.TargetAmount is <= 0 or > 100_000_000) throw new ValidationException("目標金額要大於 0。");
        if (req.Kind is JarKind.Reservation or JarKind.Annual && req.DueDate is null) throw new ValidationException("要填到期日。");
        if (req.AutoSurplusPercent is < 0 or > 100) throw new ValidationException("自動存入比例要在 0 到 100 之間。");
        if (req.MonthlyAmount is < 0) throw new ValidationException("每期提撥不能是負的。");
    }

    /// <summary>各罐子的自動存入比例加起來不能超過 100%。</summary>
    private async Task ValidateAutoTotalAsync(string userId, int? exceptJarId, decimal? percent, CancellationToken ct)
    {
        if (percent is not > 0) return;
        var others = await db.Jars.Where(j => j.UserId == userId && j.ClosedAt == null && j.Id != exceptJarId && j.AutoSurplusPercent != null)
            .SumAsync(j => j.AutoSurplusPercent!.Value, ct);
        if (others + percent > 100) throw new ValidationException($"各罐子的自動存入比例加起來不能超過 100%（其他罐子已經 {others:0.#}%）。");
    }

    private static decimal Monthly(SaveJarRequest req) =>
        req.MonthlyAmount is > 0 ? Math.Round(req.MonthlyAmount.Value, 0) : Math.Ceiling(req.TargetAmount / 12m);

    private async Task DeactivateFixedItemAsync(string userId, int itemId, CancellationToken ct)
    {
        var dto = (await settings.GetAsync(userId, ct)).Settings;
        var found = false;
        var cats = dto.Categories.Select(c => c with
        {
            FixedItems = c.FixedItems.Select(f =>
            {
                if (f.Id != itemId) return f;
                found = true;
                return f with { IsActive = false };
            }).ToList(),
        }).ToList();
        if (!found) throw new ValidationException("找不到那個固定支出。");
        await settings.SaveAsync(userId, new SaveSettingsRequest(dto with { Categories = cats }, null, null), ct);
    }

    private static JarView ToView(Jar j, decimal balance, DateOnly today) => new(
        j.Id, j.Kind, j.Name, j.TargetAmount, balance, Math.Max(0, j.TargetAmount - balance), j.DueDate,
        j.DueDate is { } d ? d.DayNumber - today.DayNumber : null,
        j.MonthlyAmount, j.AutoSurplusPercent, j.FixedItemId, j.ClosedAt is not null);
}

/// <summary>罐子餘額與每期自動提撥（給期間服務、回報服務共用，不經過 JarService 以免循環相依）。</summary>
public static class JarMath
{
    /// <summary>餘額 ＝ −Σ（帶 JarId 的有效待分配池調整）− Σ（有效回報的 JarCovered）。</summary>
    public static async Task<Dictionary<int, decimal>> BalancesAsync(DayCapDbContext db, string userId, CancellationToken ct)
    {
        var periodIds = db.Periods.Where(p => p.UserId == userId).Select(p => p.Id);
        // 作廢紀錄不帶 JarId，所以要連 ReplacesId 有值的一起讀才判斷得出哪些還有效
        var transfers = (await db.PoolTransfers.AsNoTracking()
            .Where(t => periodIds.Contains(t.PeriodId) && (t.JarId != null || t.ReplacesId != null))
            .ToListAsync(ct)).Active().Where(t => t.JarId is not null);
        var entries = (await db.Entries.AsNoTracking()
            .Where(e => periodIds.Contains(e.PeriodId) && (e.JarId != null || e.ReplacesId != null))
            .ToListAsync(ct)).Active().Where(e => e.JarId is not null);

        var result = new Dictionary<int, decimal>();
        foreach (var t in transfers) result[t.JarId!.Value] = result.GetValueOrDefault(t.JarId.Value) - t.Amount;
        foreach (var e in entries) result[e.JarId!.Value] = result.GetValueOrDefault(e.JarId.Value) - e.JarCovered;
        return result;
    }

    /// <summary>新的一期開始時：每個開著的年繳罐子提撥一期（到目標為止，§11.2）。</summary>
    public static async Task AddAnnualContributionsAsync(DayCapDbContext db, BudgetPeriod period, DateTime now, CancellationToken ct)
    {
        var jars = await db.Jars.Where(j => j.UserId == period.UserId && j.Kind == JarKind.Annual && j.ClosedAt == null).ToListAsync(ct);
        if (jars.Count == 0) return;
        var balances = await BalancesAsync(db, period.UserId, ct);
        foreach (var j in jars)
        {
            var amount = Math.Min(j.MonthlyAmount ?? Math.Ceiling(j.TargetAmount / 12m), j.TargetAmount - balances.GetValueOrDefault(j.Id));
            if (amount <= 0) continue;
            db.PoolTransfers.Add(new PoolTransfer
            {
                PeriodId = period.Id, Date = period.StartDate, Amount = -amount, JarId = j.Id, Note = $"年繳提撥：{j.Name}", CreatedAt = now,
            });
        }
        await db.SaveChangesAsync(ct);
    }
}
