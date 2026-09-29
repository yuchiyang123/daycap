using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using DayCap.Api.Models.Settings;
using DayCap.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Tests;

/// <summary>
/// §5 帳戶模型、§12.1 對帳的機制測試。
/// 規格 §23.4 的黃金情境（對帳 -1,200 後補回報 1,000 等）預期值由使用者親手算，不在這裡。
/// </summary>
public sealed class AccountTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DayCapDbContext _db;
    private readonly TestClock _clock = new(new DateOnly(2026, 10, 7));
    private readonly SettingsService _settings;
    private readonly PeriodService _periods;
    private readonly EntryService _entries;
    private readonly AccountService _accounts;

    public AccountTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _db = new DayCapDbContext(new DbContextOptionsBuilder<DayCapDbContext>().UseSqlite(_conn).Options);
        _db.Database.Migrate();
        _settings = new SettingsService(_db, _clock, new WeekendOnlyCalendar());
        _periods = new PeriodService(_db, _settings, new WeekendOnlyCalendar(), _clock);
        _entries = new EntryService(_db, _periods, _settings, _clock);
        _accounts = new AccountService(_db, _periods, _settings, _clock);

        var s = _settings.GetAsync("u").GetAwaiter().GetResult().Settings;
        _settings.SaveAsync("u", new SaveSettingsRequest(s with { Payday = new PaydayRule(1, HolidayShift.None) }, DateOnly.MinValue, "測試設定"))
            .GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private async Task<(int Bank, int Card)> BankAndCard(decimal bank = 50_000, decimal card = 0)
    {
        var view = await _accounts.SaveAccountsAsync("u", [
            new AccountEdit(0, "薪轉", AccountType.Bank, bank),
            new AccountEdit(0, "信用卡", AccountType.CreditCard, card),
        ]);
        return (view.Accounts.Single(a => a.Type == AccountType.Bank).Id, view.Accounts.Single(a => a.Type == AccountType.CreditCard).Id);
    }

    [Fact]
    public async Task Legacy_stored_balances_become_an_opening_full_reconciliation()
    {
        _db.CashAccounts.Add(new CashAccount { UserId = "u", Name = "玉山", Balance = 85735 });
        await _db.SaveChangesAsync();

        var view = await _accounts.GetAsync("u");

        Assert.Equal(85735, view.Accounts.Single().Balance);
        Assert.Equal(_clock.Current, view.LastFullReconciliation);
    }

    [Fact]
    public async Task Card_spending_raises_the_card_balance_and_paying_the_card_keeps_net_the_same()
    {
        var (bank, card) = await BankAndCard();
        var period = await _periods.GetCurrentAsync("u");
        var fun = period.Categories.Single(c => c.Name == "娛樂");
        await _entries.CreateAsync("u", period.Id, new CreateEntryRequest(_clock.Current, fun.CategoryId, null, EntryInputMode.Actual, 3000, true, "耳機", null, card));

        var afterSwipe = await _accounts.GetAsync("u");
        Assert.Equal(3000, afterSwipe.Accounts.Single(a => a.Id == card).Balance);
        Assert.Equal(3000, afterSwipe.Accounts.Single(a => a.Id == card).CardSpendThisPeriod);

        await _accounts.AddTransferAsync("u", new CreateTransferRequest(_clock.Current, TransferKind.CardPayment, bank, card, 3000, null));
        var afterPay = await _accounts.GetAsync("u");
        Assert.Equal(0, afterPay.Accounts.Single(a => a.Id == card).Balance);
        Assert.Equal(47_000, afterPay.Accounts.Single(a => a.Id == bank).Balance);
        Assert.Equal(afterSwipe.NetLiquid, afterPay.NetLiquid);
    }

    [Fact]
    public async Task Transfers_between_asset_accounts_do_not_change_net()
    {
        var view = await _accounts.SaveAccountsAsync("u", [
            new AccountEdit(0, "薪轉", AccountType.Bank, 10_000),
            new AccountEdit(0, "錢包", AccountType.Cash, 500),
        ]);
        var before = view.NetLiquid;

        await _accounts.AddTransferAsync("u", new CreateTransferRequest(_clock.Current, TransferKind.Transfer,
            view.Accounts[0].Id, view.Accounts[1].Id, 2000, "領錢"));

        var after = await _accounts.GetAsync("u");
        Assert.Equal(before, after.NetLiquid);
        Assert.Equal(2500, after.Accounts.Single(a => a.Name == "錢包").Balance);
    }

    [Fact]
    public async Task First_full_reconciliation_is_only_a_baseline()
    {
        var (bank, card) = await BankAndCard();
        await _periods.GetCurrentAsync("u");

        var result = await _accounts.CreateReconciliationAsync("u", new CreateReconciliationRequest(
            _clock.Current, [new(bank, 40_000), new(card, 0)], true, null));

        // 新增帳戶時的期初餘額只是「部分對帳」，所以這一次是第一個完整對帳
        Assert.False(result.HasBaseline);
        Assert.Equal(0, result.Diff);
    }

    private async Task<(int Bank, int Card, int PeriodId)> WithBaseline()
    {
        var (bank, card) = await BankAndCard();
        var period = await _periods.GetCurrentAsync("u");
        _clock.Current = new DateOnly(2026, 10, 3);
        await _accounts.CreateReconciliationAsync("u", new CreateReconciliationRequest(new DateOnly(2026, 10, 3), [new(bank, 40_000), new(card, 0)], true, "基準"));
        _clock.Current = new DateOnly(2026, 10, 7);
        return (bank, card, period.Id);
    }

    private async Task<decimal> ExpectedToday(int bank, int card) =>
        (await _accounts.PreviewReconciliationAsync("u", new CreateReconciliationRequest(_clock.Current, [new(bank, 0), new(card, 0)], true, null))).Expected;

    private async Task<int> EnvelopeCategory() => (await _periods.GetCurrentAsync("u")).Categories.First(c => c.Mode == BudgetMode.Envelope).CategoryId;

    [Fact]
    public async Task Paying_for_a_friend_counts_only_my_share_and_the_rest_is_a_receivable()
    {
        var (bank, card, periodId) = await WithBaseline();
        var debts = new DebtService(_db, _settings, _clock);
        var before = await ExpectedToday(bank, card);
        var balanceBefore = (await _accounts.BalancesAsync("u"))[bank];

        var view = await _entries.CreateAsync("u", periodId, new CreateEntryRequest(_clock.Current, await EnvelopeCategory(), null,
            EntryInputMode.Actual, 400, true, "晚餐", null, AccountId: bank, Split: new SplitRequest(SplitKind.IPaid, "小明", 800)));

        Assert.Equal(400, view.Entries.Single().Actual); // 預算只扣我的份
        var list = await debts.ListAsync("u");
        Assert.Equal(400, list.ReceivableTotal);
        Assert.Equal(balanceBefore - 800, (await _accounts.BalancesAsync("u"))[bank]); // 銀行實際出去 800
        Assert.Equal(before - 800, await ExpectedToday(bank, card)); // 對帳也知道 800 出去了，不會變成沒交代的差異

        // 還錢：應收清掉、銀行 +400，預算不動
        var pool = (await _periods.GetAsync("u", periodId)).Pool.Balance;
        list = await debts.SettleAsync("u", list.Debts.Single().Id, new SettleDebtRequest(400, bank, null));
        Assert.Equal(0, list.ReceivableTotal);
        Assert.Equal(balanceBefore - 400, (await _accounts.BalancesAsync("u"))[bank]);
        Assert.Equal(pool, (await _periods.GetAsync("u", periodId)).Pool.Balance);
    }

    [Fact]
    public async Task Someone_paying_for_me_is_a_payable_and_my_accounts_do_not_move_until_I_repay()
    {
        var (bank, card, periodId) = await WithBaseline();
        var debts = new DebtService(_db, _settings, _clock);
        var before = await ExpectedToday(bank, card);
        var balanceBefore = (await _accounts.BalancesAsync("u"))[bank];

        await _entries.CreateAsync("u", periodId, new CreateEntryRequest(_clock.Current, await EnvelopeCategory(), null,
            EntryInputMode.Actual, 300, true, "電影", null, AccountId: bank, Split: new SplitRequest(SplitKind.TheyPaid, "小華", null)));

        Assert.Equal(300, (await debts.ListAsync("u")).PayableTotal);
        Assert.Equal(balanceBefore, (await _accounts.BalancesAsync("u"))[bank]); // 錢沒從我的帳戶出去
        Assert.Equal(before, await ExpectedToday(bank, card));

        var list = await debts.ListAsync("u");
        await debts.SettleAsync("u", list.Debts.Single().Id, new SettleDebtRequest(300, bank, null));
        Assert.Equal(balanceBefore - 300, (await _accounts.BalancesAsync("u"))[bank]);
        Assert.Equal(before - 300, await ExpectedToday(bank, card));
    }

    [Fact]
    public async Task Deleting_the_report_voids_its_split()
    {
        var (bank, _, periodId) = await WithBaseline();
        var debts = new DebtService(_db, _settings, _clock);
        var view = await _entries.CreateAsync("u", periodId, new CreateEntryRequest(_clock.Current, await EnvelopeCategory(), null,
            EntryInputMode.Actual, 400, true, null, null, AccountId: bank, Split: new SplitRequest(SplitKind.IPaid, "小明", 800)));

        await _entries.DeleteAsync("u", periodId, view.Entries.Single().Id);

        Assert.Empty((await debts.ListAsync("u")).Debts);
    }

    /// <summary>回歸：刪掉有記付款帳戶的回報後，帳戶餘額要回來（作廢紀錄不帶 AccountId，以前被漏掉）。</summary>
    [Fact]
    public async Task Deleting_a_report_paid_from_an_account_restores_the_balance()
    {
        var (bank, _, periodId) = await WithBaseline();
        var balance = (await _accounts.BalancesAsync("u"))[bank];
        var view = await _entries.CreateAsync("u", periodId, new CreateEntryRequest(_clock.Current, await EnvelopeCategory(), null,
            EntryInputMode.Actual, 500, true, null, null, AccountId: bank));
        Assert.Equal(balance - 500, (await _accounts.BalancesAsync("u"))[bank]);

        await _entries.DeleteAsync("u", periodId, view.Entries.Single().Id);

        Assert.Equal(balance, (await _accounts.BalancesAsync("u"))[bank]);
    }

    [Fact]
    public async Task Balance_higher_than_expected_goes_to_the_pool()
    {
        var (bank, card, periodId) = await WithBaseline();
        var preview = await _accounts.PreviewReconciliationAsync("u", new CreateReconciliationRequest(_clock.Current, [new(bank, 0), new(card, 0)], true, null));
        var expected = preview.Expected; // 系統依基準 + 期間收支推出來的

        var result = await _accounts.CreateReconciliationAsync("u", new CreateReconciliationRequest(
            _clock.Current, [new(bank, expected + 500), new(card, 0)], true, null));

        Assert.True(result.HasBaseline);
        Assert.Equal(500, result.Diff);
        Assert.Equal(result.PoolBefore + 500, result.PoolAfter);
        Assert.Contains((await _periods.GetAsync("u", periodId)).Pool.Lines, l => l.Kind == "Reconcile" && l.Amount == 500);
    }

    [Fact]
    public async Task Balance_lower_than_expected_without_pool_is_spread_over_later_days()
    {
        var (bank, card, periodId) = await WithBaseline();
        var expected = (await _accounts.PreviewReconciliationAsync("u", new CreateReconciliationRequest(_clock.Current, [new(bank, 0), new(card, 0)], false, null))).Expected;

        var result = await _accounts.CreateReconciliationAsync("u", new CreateReconciliationRequest(
            _clock.Current, [new(bank, expected - 300), new(card, 0)], false, null));

        Assert.Equal(-300, result.Diff);
        Assert.Equal(300, result.Spread);
        Assert.Equal(result.PoolBefore, result.PoolAfter);
        var view = await _periods.GetAsync("u", periodId);
        Assert.Equal(300, view.Days.Where(d => d.Date > _clock.Current).Sum(d => d.BasePlanned - d.Planned));
    }

    [Fact]
    public async Task Preview_does_not_save_and_deleting_a_reconciliation_removes_its_effect()
    {
        var (bank, card, periodId) = await WithBaseline();
        var before = await _periods.GetAsync("u", periodId);

        await _accounts.PreviewReconciliationAsync("u", new CreateReconciliationRequest(_clock.Current, [new(bank, 1), new(card, 0)], true, null));
        Assert.Equal(2, await _db.Reconciliations.CountAsync()); // 新帳戶期初（部分）+ 基準，試算沒有存檔

        var created = await _accounts.CreateReconciliationAsync("u", new CreateReconciliationRequest(_clock.Current, [new(bank, 1), new(card, 0)], true, null));
        await _accounts.DeleteReconciliationAsync("u", created.Id!.Value);

        var after = await _periods.GetAsync("u", periodId);
        Assert.Equal(before.Pool.Balance, after.Pool.Balance);
        Assert.Empty(after.Reconciliations);
    }

    private sealed class WeekendOnlyCalendar : ICalendarService
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
