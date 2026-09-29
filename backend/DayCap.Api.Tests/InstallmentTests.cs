using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using DayCap.Api.Models.Settings;
using DayCap.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Tests;

/// <summary>§15 分期：繳款表的不變量、每期變成固定支出、提前還款只動資產。</summary>
public sealed class InstallmentTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DayCapDbContext _db;
    private readonly TestClock _clock = new(new DateOnly(2026, 10, 7));
    private readonly SettingsService _settings;
    private readonly PeriodService _periods;
    private readonly AccountService _accounts;
    private readonly InstallmentService _installments;

    public InstallmentTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _db = new DayCapDbContext(new DbContextOptionsBuilder<DayCapDbContext>().UseSqlite(_conn).Options);
        _db.Database.Migrate();
        var calendar = new Weekends();
        _settings = new SettingsService(_db, _clock, calendar);
        _periods = new PeriodService(_db, _settings, calendar, _clock);
        _accounts = new AccountService(_db, _periods, _settings, _clock);
        _installments = new InstallmentService(_db, _settings, _clock);

        var s = _settings.GetAsync("u").GetAwaiter().GetResult().Settings;
        _settings.SaveAsync("u", new SaveSettingsRequest(s with { Payday = new PaydayRule(1, HolidayShift.None) }, DateOnly.MinValue, "測試設定"))
            .GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private static Installment Detailed(decimal principal, decimal rate, int n, decimal fee = 0) => new()
    {
        Mode = InstallmentMode.Detailed, Principal = principal, AnnualRatePercent = rate, Periods = n, Fee = fee, FirstDueDate = new DateOnly(2026, 10, 10),
    };

    [Theory]
    [InlineData(12_000, 0, 12, 600)]
    [InlineData(30_000, 6, 12, 0)]
    [InlineData(10_000, 15, 7, 350)]
    public void Schedule_pays_back_exactly_the_principal_and_fee_in_whole_dollars(decimal principal, decimal rate, int n, decimal fee)
    {
        var rows = InstallmentSchedule.Build(Detailed(principal, rate, n, fee), []);

        Assert.Equal(n, rows.Count);
        Assert.Equal(principal, rows.Sum(r => r.Principal));
        Assert.Equal(fee, rows.Sum(r => r.Fee));
        Assert.All(rows, r => Assert.Equal(Math.Round(r.Payment), r.Payment));
        Assert.Equal(0, rows[^1].BalanceAfter);
        if (rate == 0) Assert.All(rows, r => Assert.Equal(0, r.Interest)); // 0 利率含手續費
    }

    [Fact]
    public void Prepaying_can_shorten_the_term_or_lower_the_payment()
    {
        var inst = Detailed(12_000, 0, 12);
        InstallmentPrepayment Prepay(PrepayMode mode) => new() { Date = new DateOnly(2027, 1, 1), Amount = 3_000, Mode = mode };

        var shorter = InstallmentSchedule.Build(inst, [Prepay(PrepayMode.ReduceTerm)]);
        var smaller = InstallmentSchedule.Build(inst, [Prepay(PrepayMode.ReduceAmount)]);

        Assert.True(shorter.Count < 12);
        Assert.Equal(12, smaller.Count);
        Assert.True(smaller[^2].Payment < 1_000);
        Assert.Equal(9_000, shorter.Sum(r => r.Principal));
        Assert.Equal(9_000, smaller.Sum(r => r.Principal));
    }

    [Fact]
    public async Task Each_payment_is_a_fixed_charge_that_shrinks_the_after_fixed_base()
    {
        var before = await _periods.GetCurrentAsync("u");
        var housing = before.Categories.First(c => c.Mode == BudgetMode.Fixed);
        var percentBefore = before.Categories.Where(c => c.Mode != BudgetMode.Fixed).Sum(c => c.Budget);

        await _installments.CreateAsync("u", new CreateInstallmentRequest("手機", housing.CategoryId, InstallmentMode.Simple, 12,
            new DateOnly(2026, 10, 10), MonthlyAmount: 1_000));

        var after = await _periods.GetCurrentAsync("u");
        Assert.Contains(after.FixedCharges, f => f.Name.StartsWith("分期：手機") && f.Amount == 1_000);
        Assert.Equal(housing.Budget + 1_000, after.Categories.Single(c => c.CategoryId == housing.CategoryId).Budget);
        // 預設設定用「扣掉固定支出後」的 %：固定支出多了，變動額度一起變少（§7）
        Assert.True(after.Categories.Where(c => c.Mode != BudgetMode.Fixed).Sum(c => c.Budget) < percentBefore);
    }

    [Fact]
    public async Task Prepaying_moves_money_out_of_the_account_but_not_the_pool()
    {
        var accounts = await _accounts.SaveAccountsAsync("u", [new AccountEdit(0, "薪轉", AccountType.Bank, 50_000)]);
        var bank = accounts.Accounts.Single().Id;
        var fixedCat = (await _periods.GetCurrentAsync("u")).Categories.First(c => c.Mode == BudgetMode.Fixed).CategoryId;
        var list = await _installments.CreateAsync("u", new CreateInstallmentRequest("筆電", fixedCat, InstallmentMode.Detailed, 12,
            new DateOnly(2026, 11, 10), Principal: 36_000, AnnualRatePercent: 0));
        var pool = (await _periods.GetCurrentAsync("u")).Pool.Balance;
        var balance = (await _accounts.BalancesAsync("u"))[bank];

        list = await _installments.PrepayAsync("u", list.Single().Id, new PrepayRequest(6_000, PrepayMode.ReduceTerm, bank, null));

        Assert.Equal(30_000, list.Single().PrincipalRemaining);
        Assert.Equal(balance - 6_000, (await _accounts.BalancesAsync("u"))[bank]);
        Assert.Equal(pool, (await _periods.GetCurrentAsync("u")).Pool.Balance);
    }

    [Fact]
    public async Task Payments_must_go_to_a_fixed_category()
    {
        var daily = (await _periods.GetCurrentAsync("u")).Categories.First(c => c.Mode == BudgetMode.Daily).CategoryId;
        await Assert.ThrowsAsync<Common.ValidationException>(() => _installments.CreateAsync("u",
            new CreateInstallmentRequest("手機", daily, InstallmentMode.Simple, 12, new DateOnly(2026, 10, 10), MonthlyAmount: 1_000)));
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
