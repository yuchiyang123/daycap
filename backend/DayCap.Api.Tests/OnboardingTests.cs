using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using DayCap.Api.Services;
using DayCap.Api.Services.Allocation;
using DayCap.Api.Services.Onboarding;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Tests;

/// <summary>§20 範本與引導的機制測試。區塊金額怎麼重分配是分配器（使用者實作）的事，這裡不驗算。</summary>
public sealed class OnboardingTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DayCapDbContext _db;
    private readonly TestClock _clock = new(new DateOnly(2026, 10, 7));
    private readonly SettingsService _settings;
    private readonly PeriodService _periods;
    private readonly OnboardingService _onboarding;

    public OnboardingTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _db = new DayCapDbContext(new DbContextOptionsBuilder<DayCapDbContext>().UseSqlite(_conn).Options);
        _db.Database.Migrate();
        var calendar = new Weekends();
        _settings = new SettingsService(_db, _clock, calendar);
        _periods = new PeriodService(_db, _settings, calendar, _clock);
        _onboarding = new OnboardingService(_db, _settings, _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private static bool AllocatorImplemented()
    {
        try
        {
            Allocator.Allocate(100, [new AllocationCell("a", 1, 1, null, null, 0)], 1);
            return true;
        }
        catch (NotImplementedException)
        {
            return false;
        }
    }

    private static ApplyTemplateRequest Request(string code = "rent-out-tpe", params BlockChoice[] blocks) =>
        new(code, 40_000, 5, [.. blocks]);

    [Fact]
    public void Eight_templates_each_adding_up_to_100_percent()
    {
        Assert.Equal(8, Templates.All.Select(t => t.Code).Distinct().Count());
        Assert.All(Templates.All, t => Assert.Equal(100m, t.Blocks.Sum(b => b.Percent)));
        // 住家裡的範本沒有 0% 的房租（§20.4）
        Assert.All(Templates.All.Where(t => t.Code.StartsWith("home")), t => Assert.DoesNotContain(t.Blocks, b => b.Key == Template.Rent));
        Assert.Equal("home-cook-other", Templates.CodeFor(rents: false, eatsOut: false, taipei: false));
    }

    [Fact]
    public async Task New_users_need_onboarding_until_they_apply_or_skip()
    {
        Assert.True((await _onboarding.GetAsync("u")).Needed);
        await _onboarding.SkipAsync("u");
        Assert.False((await _onboarding.GetAsync("u")).Needed);
    }

    [Fact]
    public async Task Unchecked_block_is_zero_and_a_filled_amount_is_kept()
    {
        var p = await _onboarding.PreviewAsync("u", Request(blocks:
        [
            new(Template.Insurance, false, null),
            new(Template.Rent, true, 12_000),
        ]));

        Assert.Empty(p.Errors);
        Assert.Equal(0, p.Blocks.Single(b => b.Key == Template.Insurance).Amount);
        var rent = p.Blocks.Single(b => b.Key == Template.Rent);
        Assert.True(rent.Locked);
        Assert.Equal(12_000, rent.Amount);
        Assert.Equal(40_000, p.Blocks.Sum(b => b.Amount) + p.Unallocated);
        // 試算時每個新分類要分得開（曾經因為 Id 都是 0，餐費拿到的是別的分類的金額）
        // % 存到小數兩位，換算回金額最多差 1 元
        Assert.InRange(p.Meals!.Budget - p.Blocks.Single(b => b.Key == Template.Food).Amount, -1, 1);

        if (!AllocatorImplemented())
        {
            Assert.False(p.AllocatorReady);
            Assert.NotEmpty(p.Warnings);
            Assert.Null(p.Meals!.Slots); // 每餐金額要等分配器
        }
    }

    [Fact]
    public async Task Fixed_amounts_over_income_are_rejected()
    {
        var p = await _onboarding.PreviewAsync("u", Request(blocks: [new(Template.Rent, true, 41_000)]));
        Assert.NotEmpty(p.Errors);
        await Assert.ThrowsAsync<Common.ValidationException>(() => _onboarding.ApplyAsync("u", Request(blocks: [new(Template.Rent, true, 41_000)])));
    }

    [Fact]
    public async Task Applying_copies_the_template_into_settings_and_restarts_empty_periods()
    {
        await _periods.GetCurrentAsync("u"); // 預設設定先建了一期（發薪日 5 號）

        await _onboarding.ApplyAsync("u", new ApplyTemplateRequest("rent-out-tpe", 40_000, 10, [new(Template.Rent, true, 12_000)]));

        var settings = (await _settings.GetAsync("u")).Settings;
        Assert.Equal(40_000, settings.MonthlyIncome);
        Assert.Equal(10, settings.Payday.Day);
        Assert.Contains(settings.Categories.SelectMany(c => c.FixedItems), f => f.Name == "房租" && f.Amount == 12_000);
        Assert.All(settings.Categories.SelectMany(c => c.Slots), s => Assert.True(s.Id > 0));

        var profile = await _db.Profiles.SingleAsync();
        Assert.NotNull(profile.OnboardedAt);
        Assert.Equal("rent-out-tpe", profile.TemplateCode);
        Assert.Equal(1, profile.TemplateVersion);
        Assert.NotNull(profile.TemplateSnapshot);

        var period = await _periods.GetCurrentAsync("u");
        Assert.Equal(new DateOnly(2026, 9, 10), period.StartDate); // 期間跟著新的發薪日（9/10 週四）重切
        Assert.Equal(40_000, period.Income);
    }

    [Fact]
    public async Task Tips_are_remembered_once()
    {
        await _onboarding.MarkTipSeenAsync("u", "surplus");
        var seen = await _onboarding.MarkTipSeenAsync("u", "surplus");
        Assert.Equal(["surplus"], seen);
        await Assert.ThrowsAsync<Common.ValidationException>(() => _onboarding.MarkTipSeenAsync("u", "bad key!"));
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
