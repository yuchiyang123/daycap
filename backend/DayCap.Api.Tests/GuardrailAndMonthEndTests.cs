using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using DayCap.Api.Models.Settings;
using DayCap.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Tests;

/// <summary>§10 超支攤提 / 護欄、§9.3 全天例外、§12.2 月結的機制測試（黃金情境的預期值由使用者親手算，不在這裡）。</summary>
public sealed class GuardrailAndMonthEndTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DayCapDbContext _db;
    private readonly TestClock _clock = new(new DateOnly(2026, 10, 7));
    private readonly SettingsService _settings;
    private readonly PeriodService _periods;
    private readonly EntryService _entries;
    private readonly AccountService _accounts;
    private readonly MonthEndService _monthEnd;

    public GuardrailAndMonthEndTests()
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
        var assets = new AssetService(_db, new NoQuotes(), _accounts, _clock);
        _monthEnd = new MonthEndService(_db, _periods, _settings, assets, _clock);

        var s = _settings.GetAsync("u").GetAwaiter().GetResult().Settings;
        _settings.SaveAsync("u", new SaveSettingsRequest(s with { Payday = new PaydayRule(1, HolidayShift.None) }, DateOnly.MinValue, "測試設定"))
            .GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private async Task<(PeriodView View, CategoryView Food, SlotView Lunch)> Setup(DateOnly? day = null)
    {
        var view = await _periods.GetCurrentAsync("u");
        var food = view.Categories.Single(c => c.Name == "餐費");
        var lunch = view.Days.Single(d => d.Date == (day ?? _clock.Current)).Slots.Single(s => s.Name == "午餐");
        return (view, food, lunch);
    }

    [Theory]
    [InlineData(7, new[] { 10, 10, 10 }, new[] { 3, 2, 2 })]
    [InlineData(30, new[] { 5, 20, 20 }, new[] { 5, 13, 12 })]
    [InlineData(50, new[] { 5, 5 }, new[] { 5, 5 })]
    public void Equal_capped_spreads_evenly_and_respects_caps(int amount, int[] caps, int[] expected)
    {
        var result = BudgetEngine.EqualCapped(amount, caps.Select(c => (decimal)c).ToList());
        Assert.Equal(expected.Select(x => (decimal)x), result);
    }

    [Fact]
    public async Task Overspend_is_taken_evenly_per_day_not_by_slot_size()
    {
        var (view, food, lunch) = await Setup();

        var after = await _entries.CreateAsync("u", view.Id, new CreateEntryRequest(
            _clock.Current, food.CategoryId, lunch.SlotId, EntryInputMode.Overage, 240, false, null, null));

        var entry = after.Entries.Single();
        Assert.Equal(240, entry.Spread);
        Assert.Equal(entry.SpreadDays, after.Days.Count(d => d.Date > _clock.Current && d.Planned < d.BasePlanned));
        var perDay = after.Days.Where(d => d.Date > _clock.Current).Select(d => d.BasePlanned - d.Planned).Distinct().ToList();
        Assert.True(perDay.Max() - perDay.Min() <= 1, "每天少的金額應該幾乎一樣（平均扣，§10.1）");
    }

    [Fact]
    public async Task Guardrail_keeps_every_slot_at_or_above_half_of_its_plan()
    {
        var (view, food, lunch) = await Setup(new DateOnly(2026, 10, 28));
        _clock.Current = new DateOnly(2026, 10, 28);

        var after = await _entries.CreateAsync("u", view.Id, new CreateEntryRequest(
            _clock.Current, food.CategoryId, lunch.SlotId, EntryInputMode.Overage, 50_000, false, null, null));

        foreach (var s in after.Days.Where(d => d.Date > _clock.Current).SelectMany(d => d.Slots.Where(x => x.CategoryId == food.CategoryId)))
        {
            Assert.True(s.Planned >= Math.Ceiling(s.BasePlanned / 2), $"{s.Name} 被扣到護欄下限以下");
        }
        Assert.True(after.Entries.Single().Unabsorbed > 0);
    }

    [Fact]
    public async Task Choosing_next_period_moves_the_shortfall_there_and_deleting_the_report_undoes_it()
    {
        var (view, food, lunch) = await Setup(new DateOnly(2026, 10, 31));
        _clock.Current = new DateOnly(2026, 10, 31); // 最後一天：後面沒得攤
        var before = await _periods.GetAsync("u", view.Id);

        var after = await _entries.CreateAsync("u", view.Id, new CreateEntryRequest(
            _clock.Current, food.CategoryId, lunch.SlotId, EntryInputMode.Overage, 500, false, null, null,
            Guardrail: ShortfallChoice.NextPeriod));

        Assert.Equal(before.Pool.Balance, after.Pool.Balance); // 這期的池子不受影響
        var carry = await _db.PeriodCarryovers.SingleAsync();
        Assert.Equal(-500, carry.Amount);
        Assert.Equal(new DateOnly(2026, 11, 1), carry.TargetDate);

        await _entries.DeleteAsync("u", view.Id, after.Entries.Single().Id);
        Assert.Empty((await _db.PeriodCarryovers.ToListAsync()).Active());
    }

    [Fact]
    public async Task All_zero_today_reports_every_unreported_slot_as_nothing_spent()
    {
        var (view, _, _) = await Setup();
        var today = view.Days.Single(d => d.Date == _clock.Current);

        var after = await _entries.SetDayAsync("u", view.Id, _clock.Current, "zero");

        var t = after.Days.Single(d => d.Date == _clock.Current);
        Assert.All(t.Slots.Where(s => after.Categories.Single(c => c.CategoryId == s.CategoryId).Mode == BudgetMode.Daily), s => Assert.Equal(0, s.Actual));
        Assert.Equal(today.Planned, t.Net);
    }

    [Fact]
    public async Task Month_end_needs_a_reconciliation_then_carries_the_result_and_locks_the_period()
    {
        await _accounts.SaveAccountsAsync("u", [new AccountEdit(0, "薪轉", AccountType.Bank, 50_000)]);
        var october = await _periods.GetCurrentAsync("u");
        var bankId = (await _accounts.GetAsync("u")).Accounts.Single().Id;

        _clock.Current = new DateOnly(2026, 10, 20);
        Assert.False((await _monthEnd.ReportAsync("u", october.Id)).CanClose); // 太早

        _clock.Current = new DateOnly(2026, 10, 29);
        var report = await _monthEnd.ReportAsync("u", october.Id);
        Assert.False(report.CanClose);
        Assert.True(report.NeedsReconciliation);

        await _accounts.CreateReconciliationAsync("u", new CreateReconciliationRequest(_clock.Current, [new(bankId, 48_000)], true, "發薪前"));
        var ready = await _monthEnd.ReportAsync("u", october.Id);
        Assert.True(ready.CanClose);

        var closed = await _monthEnd.CloseAsync("u", october.Id, new CloseMonthRequest(ShortfallChoice.Pool, null, []));
        Assert.True(closed.Closed);
        Assert.NotNull(closed.Summary);

        // 這期鎖住了
        var food = october.Categories.Single(c => c.Name == "餐費");
        await Assert.ThrowsAsync<Common.ValidationException>(() => _entries.CreateAsync("u", october.Id,
            new CreateEntryRequest(_clock.Current, food.CategoryId, null, EntryInputMode.Actual, 100, true, null, null)));

        // 下一期：結餘出現在期初，也不再要求月結
        _clock.Current = new DateOnly(2026, 11, 2);
        var november = await _periods.GetCurrentAsync("u");
        Assert.Contains(november.Pool.Lines, l => l.Kind == "Carryover" && l.Amount == closed.Result);
        Assert.Null(november.PreviousPeriodNeedsClosing);

        // 補登到已月結的期間：改記在本期，標明原本的日期（§4.2）
        var back = await _entries.CreateAsync("u", november.Id, new CreateEntryRequest(
            new DateOnly(2026, 10, 15), food.CategoryId, null, EntryInputMode.Actual, 300, true, "聚餐", null));
        var e = back.Entries.Single();
        Assert.Equal(_clock.Current, e.Date);
        Assert.StartsWith("補登 10/15", e.Note);
    }

    [Fact]
    public async Task Next_period_waits_for_the_previous_month_end()
    {
        var october = await _periods.GetCurrentAsync("u");
        _clock.Current = new DateOnly(2026, 11, 2);

        var november = await _periods.GetCurrentAsync("u");

        Assert.Equal(october.Id, november.PreviousPeriodNeedsClosing);
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

    private sealed class NoQuotes : IQuoteService
    {
        public Task<Dictionary<string, PriceQuote>> GetQuotesAsync(IReadOnlyCollection<string> symbols, bool force, CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<string, PriceQuote>());
    }
}
