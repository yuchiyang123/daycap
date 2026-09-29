using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using DayCap.Api.Models.Settings;
using DayCap.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Tests;

public sealed class LedgerAndSettlementTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DayCapDbContext _db;
    private readonly TestClock _clock = new(new DateOnly(2026, 10, 7));
    private readonly SettingsService _settings;
    private readonly PeriodService _periods;
    private readonly EntryService _entries;
    private readonly AssetService _assets;
    private readonly NotificationService _notifications;

    public LedgerAndSettlementTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _db = new DayCapDbContext(new DbContextOptionsBuilder<DayCapDbContext>().UseSqlite(_conn).Options);
        _db.Database.Migrate();
        _settings = new SettingsService(_db, _clock);
        _periods = new PeriodService(_db, _settings, new WeekendCalendar(), _clock);
        _entries = new EntryService(_db, _periods, _settings, _clock);
        _assets = new AssetService(_db, new NoQuotes(), new AccountService(_db, _periods, _settings, _clock), _clock);
        _notifications = new NotificationService(_db, _periods, _settings, _clock);

        // 每月 1 號發薪、不調整，讓期間固定是 10/1–10/31
        var s = _settings.GetAsync("u").GetAwaiter().GetResult().Settings;
        _settings.SaveAsync("u", new SaveSettingsRequest(s with { Payday = new PaydayRule(1, HolidayShift.None) }, DateOnly.MinValue, "測試設定"))
            .GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private async Task<int> AddAccount(string name, decimal balance)
    {
        var view = await _assets.SaveAsync("u", new SaveAssetsRequest([new AccountEdit(0, name, AccountType.Bank, balance)], [], []));
        return view.CashAccounts.Single().Id;
    }

    [Fact]
    public async Task Leave_deductions_reduce_income_and_the_pool()
    {
        var before = await _periods.GetCurrentAsync("u");

        var after = await _entries.AddIncomeAdjustmentAsync("u", before.Id,
            new CreateIncomeAdjustmentRequest(IncomeAdjustmentKind.PersonalLeave, 1, null, 1833, null));

        Assert.Equal(55000 - 1833, after.Income);
        Assert.Equal(before.Pool.Balance - 1833, after.Pool.Balance);
    }

    [Fact]
    public async Task Allocating_the_pool_to_an_envelope_raises_its_budget()
    {
        var view = await _periods.GetCurrentAsync("u");
        var fun = view.Categories.Single(c => c.Name == "娛樂");

        var after = await _entries.AllocateAsync("u", view.Id, new AllocateRequest("single", fun.CategoryId, 1000));

        Assert.Equal(view.Pool.Balance - 1000, after.Pool.Balance);
        Assert.Equal(fun.Budget + 1000, after.Categories.Single(c => c.CategoryId == fun.CategoryId).Budget);
    }

    [Fact]
    public async Task Proportional_allocation_uses_the_whole_amount_and_cannot_exceed_the_pool()
    {
        var view = await _periods.GetCurrentAsync("u");

        var after = await _entries.AllocateAsync("u", view.Id, new AllocateRequest("proportional", null, view.Pool.Balance));

        Assert.Equal(0, after.Pool.Balance);
        await Assert.ThrowsAsync<Common.ValidationException>(() =>
            _entries.AllocateAsync("u", view.Id, new AllocateRequest("proportional", null, 1)));
    }

    [Fact]
    public async Task Overspent_period_is_recorded_but_savings_are_not_touched()
    {
        await AddAccount("薪轉", 100_000);
        var october = await _periods.GetCurrentAsync("u");
        var fun = october.Categories.Single(c => c.Name == "娛樂");
        var psp = fun.Budget + october.Pool.Balance + 2000;
        await _entries.CreateAsync("u", october.Id, new CreateEntryRequest(
            _clock.Current, fun.CategoryId, null, EntryInputMode.Actual, psp, true, "PSP", null));

        _clock.Current = new DateOnly(2026, 11, 1);
        await _periods.GetCurrentAsync("u");

        Assert.Empty(await _db.AssetAdjustments.ToListAsync()); // §2.1：不自動從存款扣
        var settlement = (await _notifications.GetAsync("u")).Items.Single(n => n.Kind == "settlement");
        Assert.Contains(settlement.Lines, l => l.Contains("超支 2,000") && l.Contains("月結"));
    }

    [Fact]
    public async Task Payday_reminder_pops_up_only_on_its_day_and_clears_when_read()
    {
        _clock.Current = new DateOnly(2026, 10, 26); // 11/1 發薪，前 5 天 = 10/27
        Assert.Empty((await _notifications.GetAsync("u")).Items);

        _clock.Current = new DateOnly(2026, 10, 27);
        var onDay = await _notifications.GetAsync("u");
        var reminder = onDay.Items.Single();
        Assert.True(reminder.ShowPopup);

        await _notifications.MarkPopupShownAsync("u", reminder.Id);
        Assert.Equal(1, (await _notifications.GetAsync("u")).Unread);

        await _notifications.MarkAllReadAsync("u");
        Assert.Equal(0, (await _notifications.GetAsync("u")).Unread);
    }

    [Fact]
    public async Task Manual_asset_adjustment_changes_the_balance_and_deleting_adds_a_void_record()
    {
        var accountId = await AddAccount("錢包", 5000);

        var adj = await _assets.AddAdjustmentAsync("u", new CreateAssetAdjustmentRequest(accountId, _clock.Current, -1200, "修手機"));
        Assert.Equal(3800, (await _assets.GetAsync("u", false)).CashTotal);

        await _assets.DeleteAdjustmentAsync("u", adj.Id);
        Assert.Equal(5000, (await _assets.GetAsync("u", false)).CashTotal);
        Assert.Equal(2, await _db.AssetAdjustments.CountAsync());
        Assert.Empty(await _assets.ListAdjustmentsAsync("u", null, null));
    }

    private sealed class WeekendCalendar : ICalendarService
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
