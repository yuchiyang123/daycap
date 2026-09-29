using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using DayCap.Api.Models.Settings;
using DayCap.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Tests;

/// <summary>§11.2 罐子：預約、年繳預留、儲蓄目標與自動規則的機制測試。</summary>
public sealed class JarTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DayCapDbContext _db;
    private readonly TestClock _clock = new(new DateOnly(2026, 10, 7));
    private readonly SettingsService _settings;
    private readonly PeriodService _periods;
    private readonly EntryService _entries;
    private readonly JarService _jars;

    public JarTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _db = new DayCapDbContext(new DbContextOptionsBuilder<DayCapDbContext>().UseSqlite(_conn).Options);
        _db.Database.Migrate();
        var calendar = new Weekends();
        _settings = new SettingsService(_db, _clock, calendar);
        _periods = new PeriodService(_db, _settings, calendar, _clock);
        _entries = new EntryService(_db, _periods, _settings, _clock);
        _jars = new JarService(_db, _periods, _settings, _clock);

        var s = _settings.GetAsync("u").GetAwaiter().GetResult().Settings;
        _settings.SaveAsync("u", new SaveSettingsRequest(s with { Payday = new PaydayRule(1, HolidayShift.None) }, DateOnly.MinValue, "測試設定"))
            .GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private async Task<PeriodView> Current() => await _periods.GetCurrentAsync("u");

    private async Task<int> EnvelopeCategory() => (await Current()).Categories.First(c => c.Mode == BudgetMode.Envelope).CategoryId;

    private static CreateEntryRequest Extra(DateOnly date, int categoryId, decimal amount, int? jarId = null) =>
        new(date, categoryId, null, EntryInputMode.Actual, amount, true, null, null, JarId: jarId);

    [Fact]
    public async Task Reservation_takes_its_amount_out_of_the_pool_right_away()
    {
        var before = (await Current()).Pool.Balance;
        Assert.True(before > 3_600, "測試前提：池子夠付");

        var jars = await _jars.CreateAsync("u", new SaveJarRequest(JarKind.Reservation, "紅包", 3_600, new DateOnly(2026, 10, 20)));

        Assert.Equal(3_600, jars.Single().Balance);
        Assert.Equal(before - 3_600, (await Current()).Pool.Balance);
    }

    [Fact]
    public async Task Reservation_bigger_than_the_pool_spreads_the_rest_over_the_coming_days()
    {
        var view = await Current();
        var big = view.Pool.Balance + 2_000;
        var futureBefore = view.Days.Where(d => d.Date > _clock.Current).Sum(d => d.Planned);

        await _jars.CreateAsync("u", new SaveJarRequest(JarKind.Reservation, "機票", big, new DateOnly(2026, 10, 25)));

        var after = await Current();
        var futureAfter = after.Days.Where(d => d.Date > _clock.Current).Sum(d => d.Planned);
        Assert.Equal(2_000, futureBefore - futureAfter + (0 - Math.Min(0, after.Pool.Balance)));
        Assert.Equal(big, (await _jars.ListAsync("u")).Single().Balance);
    }

    [Fact]
    public async Task Paying_less_than_reserved_returns_the_rest_and_deleting_the_payment_undoes_it()
    {
        await _jars.CreateAsync("u", new SaveJarRequest(JarKind.Reservation, "紅包", 3_600, new DateOnly(2026, 10, 20)));
        var jar = (await _jars.ListAsync("u")).Single();
        var poolBefore = (await Current()).Pool.Balance;
        var cat = await EnvelopeCategory();

        var after = await _entries.CreateAsync("u", (await Current()).Id, Extra(_clock.Current, cat, 3_000, jar.Id));

        var entry = after.Entries.Single(e => e.JarId == jar.Id);
        Assert.Equal(3_000, entry.JarCovered);
        Assert.Equal(0, entry.Actual); // 全部由罐子付，不算進這期預算
        Assert.Equal(poolBefore + 600, after.Pool.Balance); // 多預留的 600 回池子
        var closed = (await _jars.ListAsync("u")).Single();
        Assert.True(closed.Closed);
        Assert.Equal(0, closed.Balance);

        var undone = await _entries.DeleteAsync("u", after.Id, entry.Id);
        Assert.Equal(poolBefore, undone.Pool.Balance);
        var reopened = (await _jars.ListAsync("u")).Single();
        Assert.False(reopened.Closed);
        Assert.Equal(3_600, reopened.Balance);
    }

    [Fact]
    public async Task Paying_more_than_reserved_counts_only_the_excess_against_the_budget()
    {
        await _jars.CreateAsync("u", new SaveJarRequest(JarKind.Reservation, "紅包", 3_600, new DateOnly(2026, 10, 20)));
        var jar = (await _jars.ListAsync("u")).Single();
        var cat = await EnvelopeCategory();

        var after = await _entries.CreateAsync("u", (await Current()).Id, Extra(_clock.Current, cat, 4_000, jar.Id));

        var entry = after.Entries.Single(e => e.JarId == jar.Id);
        Assert.Equal(3_600, entry.JarCovered);
        Assert.Equal(400, entry.Actual);
    }

    [Fact]
    public async Task Annual_reserve_contributes_each_period_until_the_target()
    {
        await _jars.CreateAsync("u", new SaveJarRequest(JarKind.Annual, "汽車保險", 1_200, new DateOnly(2027, 3, 15), MonthlyAmount: 500));
        Assert.Equal(500, (await _jars.ListAsync("u")).Single().Balance);

        _clock.Current = new DateOnly(2026, 11, 3);
        await Current(); // 11 月那期建立時提撥
        Assert.Equal(1_000, (await _jars.ListAsync("u")).Single().Balance);

        _clock.Current = new DateOnly(2026, 12, 3);
        await Current();
        Assert.Equal(1_200, (await _jars.ListAsync("u")).Single().Balance); // 最後一期只補到目標
    }

    [Fact]
    public async Task Auto_rule_saves_part_of_a_slot_surplus_and_undoes_with_the_report()
    {
        await _jars.CreateAsync("u", new SaveJarRequest(JarKind.Goal, "旅遊", 30_000, null, AutoSurplusPercent: 50));
        var view = await Current();
        var lunch = view.Days.Single(d => d.Date == _clock.Current).Slots.First(s => s.Planned >= 100);

        var after = await _entries.CreateAsync("u", view.Id, new CreateEntryRequest(
            _clock.Current, lunch.CategoryId, lunch.SlotId, EntryInputMode.Actual, lunch.Planned - 100, true, null, null));

        Assert.Equal(50, (await _jars.ListAsync("u")).Single().Balance);
        var entryId = after.Entries.Single(e => e.SlotId == lunch.SlotId).Id;
        await _entries.DeleteAsync("u", view.Id, entryId);
        Assert.Equal(0, (await _jars.ListAsync("u")).Single().Balance);
    }

    [Fact]
    public async Task Auto_percentages_cannot_exceed_100()
    {
        await _jars.CreateAsync("u", new SaveJarRequest(JarKind.Goal, "旅遊", 30_000, null, AutoSurplusPercent: 60));
        await Assert.ThrowsAsync<Common.ValidationException>(() =>
            _jars.CreateAsync("u", new SaveJarRequest(JarKind.Goal, "預備金", 30_000, null, AutoSurplusPercent: 50)));
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
