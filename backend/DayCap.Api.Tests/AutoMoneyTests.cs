using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using DayCap.Api.Models.Settings;
using DayCap.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DayCap.Api.Tests;

/// <summary>固定支出的自動轉帳與定期定額。</summary>
public sealed class AutoMoneyTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DayCapDbContext _db;
    private readonly TestClock _clock = new(new DateOnly(2026, 10, 7));
    private readonly SettingsService _settings;
    private readonly PeriodService _periods;
    private readonly AccountService _accounts;
    private readonly FakeQuotes _quotes = new();
    private readonly AutoMoneyService _auto;
    private int _bank, _savings, _holding;

    public AutoMoneyTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _db = new DayCapDbContext(new DbContextOptionsBuilder<DayCapDbContext>().UseSqlite(_conn).Options);
        _db.Database.Migrate();
        var calendar = new Weekends();
        _settings = new SettingsService(_db, _clock, calendar);
        _periods = new PeriodService(_db, _settings, calendar, _clock);
        _accounts = new AccountService(_db, _periods, _settings, _clock);
        _auto = new AutoMoneyService(_db, _settings, _quotes, _clock, NullLogger<AutoMoneyService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    /// <summary>帳戶、持股，並把「緊急預備金」設成轉到本金戶、「0050 定期定額」設成定期定額（從 10/1 起生效）。</summary>
    private async Task Setup(bool transfer = true, bool dca = true)
    {
        // 帳戶在 10/1 就建好了（期初對帳日期 10/1），之後的自動轉帳 / 買進才會算進餘額
        _clock.Current = new DateOnly(2026, 10, 1);
        var view = await _accounts.SaveAccountsAsync("u", [
            new AccountEdit(0, "薪轉", AccountType.Bank, 50_000),
            new AccountEdit(0, "本金", AccountType.Bank, 0),
        ]);
        _bank = view.Accounts.Single(a => a.Name == "薪轉").Id;
        _savings = view.Accounts.Single(a => a.Name == "本金").Id;
        var h = new Holding { UserId = "u", Symbol = "0050", Name = "元大台灣50", Shares = 100, AvgCost = 100 };
        _db.Holdings.Add(h);
        await _db.SaveChangesAsync();
        _holding = h.Id;

        var s = (await _settings.GetAsync("u")).Settings;
        var cats = s.Categories.Select(c => c with
        {
            FixedItems = c.FixedItems.Select(f => f.Name switch
            {
                "緊急預備金" when transfer => f with { DueDay = 6, FromAccountId = _bank, ToAccountId = _savings },
                "0050 定期定額" when dca => f with { Amount = 9_000, DueDay = 6, FromAccountId = _bank, HoldingId = _holding },
                _ => f,
            }).ToList(),
        }).ToList();
        await _settings.SaveAsync("u", new SaveSettingsRequest(s with { Payday = new PaydayRule(1, HolidayShift.None), Categories = cats },
            new DateOnly(2026, 10, 1), "測試設定"));
        _clock.Current = new DateOnly(2026, 10, 7);
        await _periods.GetCurrentAsync("u");
    }

    [Fact]
    public async Task Savings_item_moves_money_to_the_savings_account_once_on_its_due_day()
    {
        await Setup(dca: false);

        Assert.Equal(1, await _auto.RunAsync("u"));
        Assert.Equal(0, await _auto.RunAsync("u")); // 同一期只做一次

        var t = await _db.AccountTransfers.SingleAsync();
        Assert.Equal(new DateOnly(2026, 10, 6), t.Date);
        Assert.Equal(5_000, t.Amount);
        var balances = await _accounts.BalancesAsync("u");
        Assert.Equal(45_000, balances[_bank]);
        Assert.Equal(5_000, balances[_savings]);
    }

    [Fact]
    public async Task Auto_transfer_is_not_an_unexplained_difference_when_reconciling()
    {
        await Setup(dca: false);
        _clock.Current = new DateOnly(2026, 10, 3);
        await _accounts.CreateReconciliationAsync("u", new CreateReconciliationRequest(_clock.Current, [new(_bank, 50_000), new(_savings, 0)], true, "基準"));
        _clock.Current = new DateOnly(2026, 10, 7);
        async Task<decimal> Expected() => (await _accounts.PreviewReconciliationAsync("u",
            new CreateReconciliationRequest(_clock.Current, [new(_bank, 0), new(_savings, 0)], true, null))).Expected;

        var before = await Expected();  // 預算把 5,000 當固定支出扣掉了
        await _auto.RunAsync("u");
        var after = await Expected();   // 但錢只是搬到本金戶，淨額沒變

        Assert.Equal(before + 5_000, after);
    }

    [Fact]
    public async Task Dca_buys_whole_shares_at_that_days_close_and_updates_cost()
    {
        await Setup(transfer: false);
        _quotes.Close = (new DateOnly(2026, 10, 6), 116.5m);

        Assert.Equal(1, await _auto.RunAsync("u"));
        Assert.Equal(0, await _auto.RunAsync("u"));

        var p = await _db.HoldingPurchases.SingleAsync();
        Assert.Equal(77, p.Shares);                 // floor(9000 / 116.5)
        Assert.Equal(8_971, p.Spent);               // 77 × 116.5 = 8970.5 → 8971
        var h = await _db.Holdings.SingleAsync();
        Assert.Equal(177, h.Shares);
        Assert.Equal(Math.Round((100m * 100 + 77 * 116.5m) / 177, 4), h.AvgCost);
        Assert.Equal(50_000 - 8_971, (await _accounts.BalancesAsync("u"))[_bank]);
    }

    [Fact]
    public async Task Dca_waits_until_the_close_price_exists()
    {
        await Setup(transfer: false);
        _quotes.Close = null; // 還沒收盤 / 資料源還沒更新

        Assert.Equal(0, await _auto.RunAsync("u"));
        Assert.Empty(await _db.HoldingPurchases.ToListAsync());

        _quotes.Close = (new DateOnly(2026, 10, 6), 116.5m);
        Assert.Equal(1, await _auto.RunAsync("u"));
    }

    [Fact]
    public async Task Item_cannot_both_transfer_and_buy()
    {
        await Setup(transfer: false, dca: false);
        var s = (await _settings.GetAsync("u")).Settings;
        var cats = s.Categories.Select(c => c with
        {
            FixedItems = c.FixedItems.Select(f => f.Name == "緊急預備金" ? f with { FromAccountId = _bank, ToAccountId = _savings, HoldingId = _holding } : f).ToList(),
        }).ToList();
        await Assert.ThrowsAsync<Common.ValidationException>(() => _settings.SaveAsync("u", new SaveSettingsRequest(s with { Categories = cats }, null, null)));
    }

    /// <summary>使用者實際的用法：只登記玉山，薪轉戶不登記；預備金從外部存進玉山，0050 錢先進玉山再扣。</summary>
    private async Task SetupSavingsOnly()
    {
        _clock.Current = new DateOnly(2026, 10, 1);
        var view = await _accounts.SaveAccountsAsync("u", [new AccountEdit(0, "玉山", AccountType.Bank, 60_000)]);
        _savings = view.Accounts.Single().Id;
        var h = new Holding { UserId = "u", Symbol = "0050", Name = "元大台灣50", Shares = 100, AvgCost = 100 };
        _db.Holdings.Add(h);
        await _db.SaveChangesAsync();
        _holding = h.Id;
        var s = (await _settings.GetAsync("u")).Settings;
        var cats = s.Categories.Select(c => c with
        {
            FixedItems = c.FixedItems.Select(f => f.Name switch
            {
                "緊急預備金" => f with { DueDay = 6, FromAccountId = null, ToAccountId = _savings },
                "0050 定期定額" => f with { Amount = 9_000, DueDay = 6, FromAccountId = _savings, HoldingId = _holding, FundedExternally = true },
                _ => f,
            }).ToList(),
        }).ToList();
        await _settings.SaveAsync("u", new SaveSettingsRequest(s with { Payday = new PaydayRule(1, HolidayShift.None), Categories = cats },
            new DateOnly(2026, 10, 1), "測試設定"));
        _clock.Current = new DateOnly(2026, 10, 7);
        await _periods.GetCurrentAsync("u");
    }

    [Fact]
    public async Task Money_from_an_unregistered_account_is_deposited_and_dca_keeps_only_the_change()
    {
        await SetupSavingsOnly();
        _quotes.Close = (new DateOnly(2026, 10, 6), 116.5m);

        Assert.Equal(2, await _auto.RunAsync("u"));
        Assert.Equal(0, await _auto.RunAsync("u"));

        // 玉山：60,000 ＋ 預備金 5,000 ＋ 0050 先存 9,000 − 買 77 股 8,971 ＝ 65,029
        Assert.Equal(65_029, (await _accounts.BalancesAsync("u"))[_savings]);
        Assert.Equal(177, (await _db.Holdings.SingleAsync()).Shares);
    }

    [Fact]
    public async Task Savings_only_accounts_never_turn_a_reconciliation_into_a_pool_difference()
    {
        await SetupSavingsOnly();
        var profile = await _db.Profiles.SingleAsync();
        profile.SavingsOnlyAccounts = true;
        await _db.SaveChangesAsync();
        _clock.Current = new DateOnly(2026, 10, 3);
        await _accounts.CreateReconciliationAsync("u", new CreateReconciliationRequest(_clock.Current, [new(_savings, 60_000)], true, "基準"));
        _clock.Current = new DateOnly(2026, 10, 7);
        var pool = (await _periods.GetCurrentAsync("u")).Pool.Balance;

        // 薪水、花費都不經過玉山：以前這裡會算出一大筆差額，從待分配池扣掉
        var r = await _accounts.CreateReconciliationAsync("u", new CreateReconciliationRequest(_clock.Current, [new(_savings, 74_000)], true, null));

        Assert.Equal(0, r.Diff);
        Assert.Equal(pool, (await _periods.GetCurrentAsync("u")).Pool.Balance);
        Assert.Equal(74_000, (await _accounts.BalancesAsync("u"))[_savings]); // 餘額照樣更新
    }

    /// <summary>回歸：先買進、之後才勾「錢先從外部存進」，那一期的存入要補上（正式站 10/6 發生過）。</summary>
    [Fact]
    public async Task Ticking_funded_externally_after_buying_backfills_the_deposit()
    {
        await SetupSavingsOnly();
        _quotes.Close = (new DateOnly(2026, 10, 6), 116.5m);
        var s = (await _settings.GetAsync("u")).Settings;
        var off = s.Categories.Select(c => c with { FixedItems = c.FixedItems.Select(f => f.HoldingId is not null ? f with { FundedExternally = null } : f).ToList() }).ToList();
        await _settings.SaveAsync("u", new SaveSettingsRequest(s with { Categories = off }, new DateOnly(2026, 10, 1), "先不勾"));
        await _auto.RunAsync("u");
        var before = (await _accounts.BalancesAsync("u"))[_savings];

        var on = off.Select(c => c with { FixedItems = c.FixedItems.Select(f => f.HoldingId is not null ? f with { FundedExternally = true } : f).ToList() }).ToList();
        await _settings.SaveAsync("u", new SaveSettingsRequest(s with { Categories = on }, new DateOnly(2026, 10, 1), "補勾"));
        Assert.Equal(1, await _auto.RunAsync("u"));
        Assert.Equal(0, await _auto.RunAsync("u"));

        Assert.Equal(before + 9_000, (await _accounts.BalancesAsync("u"))[_savings]);
        Assert.Equal(1, await _db.HoldingPurchases.CountAsync()); // 不會再買一次
    }

    private sealed class FakeQuotes : IQuoteService
    {
        public (DateOnly, decimal)? Close { get; set; }

        public Task<Dictionary<string, PriceQuote>> GetQuotesAsync(IReadOnlyCollection<string> symbols, bool force, CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<string, PriceQuote>());

        public Task<(DateOnly TradeDate, decimal Close)?> GetCloseOnOrAfterAsync(string symbol, DateOnly date, CancellationToken ct = default) =>
            Task.FromResult<(DateOnly TradeDate, decimal Close)?>(Close is { } c && c.Item1 >= date ? c : null);
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
