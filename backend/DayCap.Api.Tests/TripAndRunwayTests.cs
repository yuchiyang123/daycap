using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using DayCap.Api.Models.Settings;
using DayCap.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Tests;

/// <summary>§17 旅遊與外幣、§18 收入中斷（存款還能撐幾天）。</summary>
public sealed class TripAndRunwayTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DayCapDbContext _db;
    private readonly TestClock _clock = new(new DateOnly(2026, 10, 7));
    private readonly SettingsService _settings;
    private readonly PeriodService _periods;
    private readonly EntryService _entries;
    private readonly AccountService _accounts;
    private readonly JarService _jars;
    private readonly TripService _trips;
    private readonly RunwayService _runway;

    public TripAndRunwayTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _db = new DayCapDbContext(new DbContextOptionsBuilder<DayCapDbContext>().UseSqlite(_conn).Options);
        _db.Database.Migrate();
        var calendar = new Weekends();
        _settings = new SettingsService(_db, _clock, calendar);
        _periods = new PeriodService(_db, _settings, calendar, _clock);
        _entries = new EntryService(_db, _periods, _settings, _clock);
        _accounts = new AccountService(_db, _periods, _settings, _clock);
        _jars = new JarService(_db, _periods, _settings, _clock);
        _trips = new TripService(_db, _periods, _settings, _jars, _clock);
        _runway = new RunwayService(_db, _periods, _accounts, _settings);

        var s = _settings.GetAsync("u").GetAwaiter().GetResult().Settings;
        _settings.SaveAsync("u", new SaveSettingsRequest(s with { Payday = new PaydayRule(1, HolidayShift.None) }, DateOnly.MinValue, "測試設定"))
            .GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private static CreateTripRequest Tokyo(DateOnly start, DateOnly end, decimal budget = 20_000) => new("東京", start, end, budget, "JPY", 0.21m);

    [Fact]
    public async Task A_trip_pauses_daily_slots_and_hands_their_budget_to_the_pool()
    {
        var before = await _periods.GetCurrentAsync("u");
        var tripDays = before.Days.Where(d => d.Date >= new DateOnly(2026, 10, 12) && d.Date <= new DateOnly(2026, 10, 15)).ToList();
        var freed = tripDays.Sum(d => d.Planned);
        Assert.True(freed > 0 && before.Pool.Balance > 2_000, "測試前提");

        await _trips.CreateAsync("u", Tokyo(new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 15), 2_000));

        var after = await _periods.GetCurrentAsync("u");
        Assert.All(after.Days.Where(d => d.Date >= new DateOnly(2026, 10, 12) && d.Date <= new DateOnly(2026, 10, 15)), d =>
        {
            Assert.Equal("東京", d.Trip);
            Assert.Equal(0, d.Planned);
        });
        Assert.Equal(before.Days.Single(d => d.Date == new DateOnly(2026, 10, 16)).Planned, after.Days.Single(d => d.Date == new DateOnly(2026, 10, 16)).Planned);
        Assert.Contains(after.Pool.Lines, l => l.Amount == freed && l.Label.Contains("東京"));
        Assert.Equal(before.Pool.Balance + freed - 2_000, after.Pool.Balance); // 暫停的額度回池子，旅遊預算從池子預留
    }

    [Fact]
    public async Task Ending_a_trip_early_brings_the_slots_back_from_today()
    {
        await _trips.CreateAsync("u", Tokyo(new DateOnly(2026, 10, 8), new DateOnly(2026, 10, 15)));
        _clock.Current = new DateOnly(2026, 10, 10);

        var list = await _trips.EndAsync("u", (await _trips.ListAsync("u")).Single().Id);

        Assert.Equal(new DateOnly(2026, 10, 9), list.Single().EndDate);
        var view = await _periods.GetCurrentAsync("u");
        Assert.Null(view.Days.Single(d => d.Date == new DateOnly(2026, 10, 10)).Trip);
        Assert.True(view.Days.Single(d => d.Date == new DateOnly(2026, 10, 10)).Planned > 0);
        Assert.True(list.Single().JarClosed); // 旅遊預算剩下的回池子
    }

    [Fact]
    public async Task Trips_cannot_start_in_the_past_or_overlap()
    {
        await Assert.ThrowsAsync<Common.ValidationException>(() => _trips.CreateAsync("u", Tokyo(new DateOnly(2026, 10, 6), new DateOnly(2026, 10, 9))));
        await _trips.CreateAsync("u", Tokyo(new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 15)));
        await Assert.ThrowsAsync<Common.ValidationException>(() => _trips.CreateAsync("u", Tokyo(new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 18))));
    }

    [Fact]
    public async Task Foreign_currency_is_converted_with_the_rate_given_at_the_time()
    {
        var view = await _periods.GetCurrentAsync("u");
        var cat = view.Categories.First(c => c.Mode == BudgetMode.Envelope).CategoryId;

        var after = await _entries.CreateAsync("u", view.Id, new CreateEntryRequest(_clock.Current, cat, null, EntryInputMode.Actual, 0, true, "拉麵", null,
            Currency: "jpy", ForeignAmount: 1_000, FxRate: 0.21m, TimeZoneId: "Asia/Tokyo"));

        var e = after.Entries.Single();
        Assert.Equal(210, e.Actual);
        Assert.Equal("JPY", e.Currency);
        Assert.Equal(1_000, e.ForeignAmount);
        Assert.Equal("Asia/Tokyo", (await _db.Entries.SingleAsync()).TimeZoneId);
    }

    [Fact]
    public async Task Runway_divides_usable_money_by_recent_actual_daily_spending()
    {
        await _accounts.SaveAccountsAsync("u", [new AccountEdit(0, "薪轉", AccountType.Bank, 30_000), new AccountEdit(0, "卡", AccountType.CreditCard, 2_000)]);
        await _periods.GetCurrentAsync("u");
        _clock.Current = new DateOnly(2026, 10, 20);

        var r = await _runway.GetAsync("u");

        Assert.Equal(28_000, r.UsableAssets); // 信用卡欠款要扣掉
        Assert.Equal(19, r.BasisDays);        // 10/1–10/19（這期之前沒有資料）
        Assert.True(r.AvgDailySpend > 0);
        Assert.Equal((int)Math.Floor(28_000m / r.AvgDailySpend), r.RunwayDays);
    }

    private sealed class Weekends : ICalendarService
    {
        public Task<Dictionary<DateOnly, DayInfo>> GetDaysAsync(string userId, DateOnly from, DateOnly to, CancellationToken ct = default)
        {
            var result = new Dictionary<DateOnly, DayInfo>();
            for (var d = from; d <= to; d = d.AddDays(1))
                result[d] = new DayInfo(d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday, null);
            return Task.FromResult(result);
        }

        public Task SetOverrideAsync(string userId, DateOnly date, bool? isHoliday, CancellationToken ct = default) => Task.CompletedTask;
    }
}
