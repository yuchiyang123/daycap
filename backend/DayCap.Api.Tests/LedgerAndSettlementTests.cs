using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using DayCap.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Tests;

public sealed class LedgerAndSettlementTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DayCapDbContext _db;
    private readonly Clock _clock = new(new DateOnly(2026, 10, 7));
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
        _settings = new SettingsService(_db);
        _periods = new PeriodService(_db, _settings, new WeekendCalendar(), _clock);
        _entries = new EntryService(_db, _periods, _clock);
        _assets = new AssetService(_db, new NoQuotes(), _clock);
        _notifications = new NotificationService(_db, _periods, _settings, _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private async Task<int> AddAccount(string name, int balance)
    {
        var view = await _assets.SaveAsync("u", new SaveAssetsRequest([new CashAccountDto(0, name, balance)], [], []));
        return view.CashAccounts.Single().Id;
    }

    [Fact]
    public async Task Leave_deductions_reduce_income_and_the_pool()
    {
        var before = await _periods.GetCurrentAsync("u");

        var after = await _entries.AddIncomeAdjustmentAsync("u", before.Id,
            new CreateIncomeAdjustmentRequest(IncomeAdjustmentKind.PersonalLeave, 1, null, 1833, null));

        Assert.Equal(55000 - 1833, after.Income);
        Assert.Equal(55000, after.BaseIncome);
        Assert.Equal(before.Pool.Balance - 1833, after.Pool.Balance);
        Assert.Contains(after.Pool.Lines, l => l.Kind == "Income" && l.Label.Contains("事假 1 天"));
    }

    [Fact]
    public async Task Allocating_the_pool_to_an_envelope_raises_its_budget()
    {
        var view = await _periods.GetCurrentAsync("u");
        var fun = view.Categories.Single(c => c.Name == "娛樂");

        var after = await _entries.AllocateAsync("u", view.Id, new AllocateRequest("single", fun.CategoryId, 1000));

        Assert.Equal(view.Pool.Balance - 1000, after.Pool.Balance);
        var funAfter = after.Categories.Single(c => c.CategoryId == fun.CategoryId);
        Assert.Equal(fun.Budget + 1000, funAfter.Budget);
        Assert.Equal(1000, funAfter.Allocated);
    }

    [Fact]
    public async Task Allocating_to_a_daily_category_adds_to_later_days_only()
    {
        var view = await _periods.GetCurrentAsync("u");
        var food = view.Categories.Single(c => c.Name == "餐費");

        var after = await _entries.AllocateAsync("u", view.Id, new AllocateRequest("single", food.CategoryId, 2400));

        var foodDays = after.Days.Select(d => new
        {
            d.Date,
            Base = d.Slots.Where(s => s.CategoryId == food.CategoryId).Sum(s => s.BasePlanned),
            Now = d.Slots.Where(s => s.CategoryId == food.CategoryId).Sum(s => s.Planned),
        }).ToList();
        Assert.All(foodDays.Where(d => d.Date <= _clock.Today), d => Assert.Equal(d.Base, d.Now));
        Assert.Equal(2400, foodDays.Sum(d => d.Now - d.Base));
    }

    [Fact]
    public async Task Proportional_allocation_uses_the_whole_amount_and_cannot_exceed_the_pool()
    {
        var view = await _periods.GetCurrentAsync("u");

        var after = await _entries.AllocateAsync("u", view.Id, new AllocateRequest("proportional", null, view.Pool.Balance));

        Assert.Equal(0, after.Pool.Balance);
        Assert.Equal(view.Pool.Balance, after.Categories.Sum(c => c.Allocated));
        await Assert.ThrowsAsync<Common.ValidationException>(() =>
            _entries.AllocateAsync("u", view.Id, new AllocateRequest("proportional", null, 1)));
    }

    [Fact]
    public async Task Overspent_period_is_deducted_from_the_settlement_account_once()
    {
        var accountId = await AddAccount("薪轉", 100_000);
        var s = await _settings.GetAsync("u");
        await _settings.SaveAsync("u", s with { SettlementAccountId = accountId });

        var october = await _periods.GetCurrentAsync("u");
        var fun = october.Categories.Single(c => c.Name == "娛樂");
        // 買 PSP：比娛樂額度 + 整個待定區還多 2,000
        var psp = fun.Budget + october.Pool.Balance + 2000;
        await _entries.CreateAsync("u", october.Id, new CreateEntryRequest(
            _clock.Today, fun.CategoryId, null, EntryInputMode.Actual, psp, true, "PSP", null));

        _clock.Current = new DateOnly(2026, 11, 1); // 發薪日
        await _periods.GetCurrentAsync("u");
        await _periods.GetCurrentAsync("u"); // 再打開一次也不會重複扣

        Assert.Equal(98_000, (await _db.CashAccounts.SingleAsync()).Balance);
        var adj = await _db.AssetAdjustments.SingleAsync();
        Assert.Equal(-2000, adj.Amount);
        Assert.Equal("settlement", adj.Source);

        var notes = await _notifications.GetAsync("u");
        var settlement = notes.Items.Single(n => n.Kind == "settlement");
        Assert.True(settlement.ShowPopup);
        Assert.Contains(settlement.Lines, l => l.Contains("已從「薪轉」扣除"));
    }

    [Fact]
    public async Task Surplus_stays_put_unless_the_user_opted_in()
    {
        var accountId = await AddAccount("薪轉", 100_000);
        var s = await _settings.GetAsync("u");
        await _settings.SaveAsync("u", s with { SettlementAccountId = accountId, SurplusToAccount = false });
        var october = await _periods.GetCurrentAsync("u");

        _clock.Current = new DateOnly(2026, 11, 1);
        await _periods.GetCurrentAsync("u");

        Assert.Equal(100_000, (await _db.CashAccounts.SingleAsync()).Balance);
        Assert.Equal(0, (await _db.Periods.SingleAsync(p => p.Id == october.Id)).SettlementAmount);
    }

    [Fact]
    public async Task Payday_reminder_pops_up_only_on_its_day_and_clears_when_read()
    {
        _clock.Current = new DateOnly(2026, 10, 26); // 11/1 發薪，前 5 天 = 10/27
        Assert.Empty((await _notifications.GetAsync("u")).Items);

        _clock.Current = new DateOnly(2026, 10, 27);
        var onDay = await _notifications.GetAsync("u");
        var reminder = onDay.Items.Single();
        Assert.Equal("reminder", reminder.Kind);
        Assert.True(reminder.ShowPopup);
        Assert.Equal(1, onDay.Unread);

        await _notifications.MarkPopupShownAsync("u", reminder.Id);
        var afterClose = await _notifications.GetAsync("u");
        Assert.False(afterClose.Items.Single().ShowPopup);
        Assert.Equal(1, afterClose.Unread); // 關掉彈窗後紅點還在

        await _notifications.MarkAllReadAsync("u");
        Assert.Equal(0, (await _notifications.GetAsync("u")).Unread);

        _clock.Current = new DateOnly(2026, 10, 28);
        Assert.Single((await _notifications.GetAsync("u")).Items); // 不會每天再產生一筆
    }

    [Fact]
    public void Reminder_explains_the_overspend_and_how_much_to_cut_per_day()
    {
        var view = new PeriodView(1, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), new DateOnly(2026, 10, 27),
            55000, 55000, [], true, null, null,
            new PoolView(0, -1000, []),
            [new CategoryView(1, "餐費", CategoryGroup.Food, BudgetMode.Daily, 12000, 11000, 10000, 3000, 13000, 0)],
            [], [], [], []);

        var (title, lines, tone) = NotificationService.Reminder(view, "薪轉", new DateOnly(2026, 10, 27));

        Assert.Equal("warn", tone);
        Assert.Contains("超支", title);
        Assert.Contains(lines, l => l.Contains("從「薪轉」扣除 1,000"));
        Assert.Contains(lines, l => l.Contains("每天少花約 200")); // 1,000 ÷ 5 天
        Assert.Contains(lines, l => l.StartsWith("餐費：預計 13,000"));
    }

    [Fact]
    public async Task Manual_asset_adjustment_changes_the_balance_and_deleting_restores_it()
    {
        var accountId = await AddAccount("錢包", 5000);

        var adj = await _assets.AddAdjustmentAsync("u", new CreateAssetAdjustmentRequest(accountId, _clock.Today, -1200, "修手機"));
        Assert.Equal(3800, (await _db.CashAccounts.SingleAsync()).Balance);
        Assert.Equal("錢包", adj.AccountName);

        await _assets.DeleteAdjustmentAsync("u", adj.Id);
        Assert.Equal(5000, (await _db.CashAccounts.SingleAsync()).Balance);
    }

    private sealed class Clock(DateOnly today) : IAppClock
    {
        public DateOnly Current { get; set; } = today;
        public DateTime UtcNow => Current.ToDateTime(new TimeOnly(4, 0), DateTimeKind.Utc);
        public DateOnly Today => Current;
        public DateOnly ToLocalDate(DateTime utc) => DateOnly.FromDateTime(utc.AddHours(8));
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
