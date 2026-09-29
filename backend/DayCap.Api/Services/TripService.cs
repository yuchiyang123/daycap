using DayCap.Api.Common;
using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Services;

public interface ITripService
{
    Task<List<TripView>> ListAsync(string userId, CancellationToken ct = default);
    Task<List<TripView>> CreateAsync(string userId, CreateTripRequest req, CancellationToken ct = default);
    /// <summary>結束旅遊：結束日改成昨天（還沒開始的直接取消），旅遊預算剩下的回池子。</summary>
    Task<List<TripView>> EndAsync(string userId, int tripId, CancellationToken ct = default);
}

/// <summary>旅遊（§17）：暫時接管日常時段，回國後恢復。</summary>
public class TripService(DayCapDbContext db, IPeriodService periods, ISettingsService settings, IJarService jars, IAppClock clock) : ITripService
{
    public async Task<List<TripView>> ListAsync(string userId, CancellationToken ct = default)
    {
        var today = await settings.LogicalTodayAsync(userId, ct);
        var trips = await TripMath.LoadAsync(db, userId, ct);
        var jarViews = (await jars.ListAsync(userId, ct)).ToDictionary(j => j.Id);
        return trips.OrderByDescending(t => t.StartDate)
            .Select(t =>
            {
                jarViews.TryGetValue(t.JarId, out var jar);
                var status = today < t.StartDate ? "upcoming" : today > t.EndDate ? "ended" : "active";
                return new TripView(t.Id, t.Name, t.StartDate, t.EndDate, t.Budget, t.Currency, t.FxRate, t.JarId,
                    jar?.Balance ?? 0, jar?.Closed ?? true, status);
            }).ToList();
    }

    public async Task<List<TripView>> CreateAsync(string userId, CreateTripRequest req, CancellationToken ct = default)
    {
        var name = req.Name?.Trim() ?? "";
        if (name.Length is 0 or > 40) throw new ValidationException("名稱要 1 到 40 個字。");
        var today = await settings.LogicalTodayAsync(userId, ct);
        if (req.StartDate < today) throw new ValidationException("旅遊要從今天或之後開始（過去的日子照原本的額度算）。");
        if (req.EndDate < req.StartDate || req.EndDate.DayNumber - req.StartDate.DayNumber > 90) throw new ValidationException("結束日要在開始日之後，最長 90 天。");
        if (req.Budget is <= 0 or > 100_000_000) throw new ValidationException("旅遊預算要大於 0。");
        var currency = req.Currency?.Trim().ToUpperInvariant();
        if (currency is { Length: > 0 } && (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))) throw new ValidationException("幣別要是三個英文字母，例如 JPY。");
        if (req.FxRate is <= 0 or > 100_000) throw new ValidationException("匯率要大於 0。");
        var existing = await TripMath.LoadAsync(db, userId, ct);
        if (existing.Any(t => t.StartDate <= req.EndDate && t.EndDate >= req.StartDate)) throw new ValidationException("跟另一趟旅遊的日子重疊了。");

        // 旅遊期間已經有時段回報：先刪掉那些回報，或把開始日往後
        var periodIds = db.Periods.Where(p => p.UserId == userId).Select(p => p.Id);
        var reported = (await db.Entries.AsNoTracking()
                .Where(e => periodIds.Contains(e.PeriodId) && e.Date >= req.StartDate && e.Date <= req.EndDate && (e.SlotId != null || e.ReplacesId != null))
                .ToListAsync(ct)).Active()
            .Any(e => e.SlotId is not null);
        if (reported) throw new ValidationException("旅遊期間有已經回報的時段，請先刪掉那些回報或把開始日往後。");

        // 旅遊預算＝一個預約罐子：登記當下從池子扣（不夠攤到之後每天），旅途中的花費從這裡付
        var jarList = await jars.CreateAsync(userId, new SaveJarRequest(JarKind.Reservation, $"旅遊：{name}", Math.Round(req.Budget, 0), req.EndDate), ct);
        var jarId = jarList.Where(j => j.Name == $"旅遊：{name}" && !j.Closed).Max(j => j.Id);

        db.Trips.Add(new Trip
        {
            UserId = userId, Name = name, StartDate = req.StartDate, EndDate = req.EndDate, Budget = Math.Round(req.Budget, 0),
            Currency = currency is { Length: > 0 } ? currency : null, FxRate = req.FxRate, JarId = jarId,
            CreatedOn = today, CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return await ListAsync(userId, ct);
    }

    public async Task<List<TripView>> EndAsync(string userId, int tripId, CancellationToken ct = default)
    {
        var trip = (await TripMath.LoadAsync(db, userId, ct)).FirstOrDefault(t => t.Id == tripId) ?? throw new NotFoundException("找不到這趟旅遊。");
        var today = await settings.LogicalTodayAsync(userId, ct);
        if (trip.EndDate >= today)
        {
            // 還沒開始：整趟取消；進行中：結束日改成昨天，今天起恢復日常時段
            var cancel = trip.StartDate >= today;
            db.Trips.Add(new Trip
            {
                UserId = userId, Name = trip.Name, StartDate = trip.StartDate, EndDate = cancel ? trip.EndDate : today.AddDays(-1),
                Budget = trip.Budget, Currency = trip.Currency, FxRate = trip.FxRate, JarId = trip.JarId, CreatedOn = trip.CreatedOn,
                CreatedAt = clock.UtcNow, ReplacesId = trip.Id, IsVoid = cancel,
            });
            await db.SaveChangesAsync(ct);
        }
        var jar = (await jars.ListAsync(userId, ct)).FirstOrDefault(j => j.Id == trip.JarId);
        if (jar is { Closed: false }) await jars.CloseAsync(userId, trip.JarId, ct);
        return await ListAsync(userId, ct);
    }
}

/// <summary>旅遊怎麼套到期間計畫上（期間服務用）。</summary>
public static class TripMath
{
    public static async Task<List<Trip>> LoadAsync(DayCapDbContext db, string userId, CancellationToken ct) =>
        (await db.Trips.AsNoTracking().Where(t => t.UserId == userId).ToListAsync(ct)).Active();

    /// <summary>
    /// 旅遊的日子：每日時段的額度歸 0（基準不動），省下來的額度在「建立旅遊那天」一次回到待分配池。
    /// 建立之前就過去的日子不回溯（CreatedOn 之前的不動）。
    /// </summary>
    public static void Apply(PeriodPlan plan, IEnumerable<Trip> trips, DateOnly periodStart, DateOnly periodEnd)
    {
        foreach (var trip in trips)
        {
            var from = Max(trip.StartDate, trip.CreatedOn);
            var freed = 0m;
            foreach (var a in plan.Allocations.Where(a => a.Date >= from && a.Date <= trip.EndDate && a.Planned > 0))
            {
                freed += a.Planned;
                a.Planned = 0;
            }
            if (freed == 0) continue;
            var date = trip.CreatedOn < periodStart ? periodStart : trip.CreatedOn > periodEnd ? periodEnd : trip.CreatedOn;
            plan.VersionLines.Add(new PlanLine(date, freed, $"旅遊「{trip.Name}」期間暫停的每日額度"));
        }
    }

    public static PeriodView Decorate(PeriodView view, IEnumerable<Trip> trips)
    {
        var list = trips.ToList();
        if (list.Count == 0) return view;
        return view with
        {
            Days = view.Days.Select(d => list.FirstOrDefault(t => d.Date >= t.StartDate && d.Date <= t.EndDate) is { } t ? d with { Trip = t.Name } : d).ToList(),
        };
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
}
