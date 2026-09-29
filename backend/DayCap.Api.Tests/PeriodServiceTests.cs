using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using DayCap.Api.Models.Settings;
using DayCap.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Tests;

public sealed class PeriodServiceTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DayCapDbContext _db;
    private readonly TestClock _clock = new(new DateOnly(2026, 10, 7));
    private readonly FakeCalendar _calendar = new();
    private readonly SettingsService _settings;
    private readonly PeriodService _periods;
    private readonly EntryService _entries;

    public PeriodServiceTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _db = new DayCapDbContext(new DbContextOptionsBuilder<DayCapDbContext>().UseSqlite(_conn).Options);
        _db.Database.Migrate();
        _settings = new SettingsService(_db, _clock);
        _periods = new PeriodService(_db, _settings, _calendar, _clock);
        _entries = new EntryService(_db, _periods, _settings, _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    /// <summary>測試用：以「更正」從最早開始生效的方式改設定，讓這期立刻套用。</summary>
    private async Task Configure(Func<SettingsDto, SettingsDto> change)
    {
        var s = (await _settings.GetAsync("u")).Settings;
        await _settings.SaveAsync("u", new SaveSettingsRequest(change(s), DateOnly.MinValue, "測試設定"));
    }

    private Task MonthlyPeriods() => Configure(s => s with { Payday = new PaydayRule(1, HolidayShift.None) });

    private static SettingsDto WithSlot(SettingsDto s, string slotName, Func<SlotDto, SlotDto> change) => s with
    {
        Categories = s.Categories.Select(c => c with { Slots = c.Slots.Select(x => x.Name == slotName ? change(x) : x).ToList() }).ToList(),
    };

    [Fact]
    public async Task Holiday_amounts_apply_on_national_holidays_and_counts_are_reported()
    {
        await MonthlyPeriods();
        _calendar.Holidays.Add(new DateOnly(2026, 10, 9));
        _calendar.Holidays.Add(new DateOnly(2026, 10, 10));

        var view = await _periods.GetCurrentAsync("u");

        Assert.Equal(new DateOnly(2026, 10, 1), view.StartDate);
        Assert.Equal(new DateOnly(2026, 10, 31), view.EndDate);
        Assert.Equal(400, view.Days.Single(d => d.Date == new DateOnly(2026, 10, 8)).BasePlanned);
        Assert.Equal(470, view.Days.Single(d => d.Date == new DateOnly(2026, 10, 9)).BasePlanned);
        Assert.Equal(31, view.WeekdayCount + view.HolidayCount);
        Assert.Equal(view.Days.Count(d => d.IsHoliday), view.HolidayCount);
    }

    [Fact]
    public async Task A_normal_save_takes_effect_tomorrow_and_leaves_earlier_days_alone()
    {
        await MonthlyPeriods();
        var before = await _periods.GetCurrentAsync("u");
        var s = (await _settings.GetAsync("u")).Settings;

        await _settings.SaveAsync("u", new SaveSettingsRequest(WithSlot(s, "午餐", x => x with { WorkdayAmount = 150 }), null, null));

        var after = await _periods.GetCurrentAsync("u");
        decimal Lunch(DateOnly d) => after.Days.Single(x => x.Date == d).Slots.Single(x => x.Name == "午餐").BasePlanned;
        Assert.Equal(120, Lunch(new DateOnly(2026, 10, 6)));
        Assert.Equal(120, Lunch(new DateOnly(2026, 10, 7))); // 今天調升不生效
        Assert.Equal(150, Lunch(new DateOnly(2026, 10, 8)));
        Assert.Contains(after.Pool.Lines, l => l.Kind == "Settings" && l.Amount < 0);
        Assert.Equal(before.Categories.Single(c => c.Name == "餐費").Budget, after.Categories.Single(c => c.Name == "餐費").Budget);
    }

    [Fact]
    public async Task Lowering_today_uses_the_lower_amount_for_slots_not_yet_reported()
    {
        await MonthlyPeriods();
        var s = (await _settings.GetAsync("u")).Settings;

        await _settings.SaveAsync("u", new SaveSettingsRequest(WithSlot(s, "午餐", x => x with { WorkdayAmount = 100 }), null, null));

        var view = await _periods.GetCurrentAsync("u");
        var lunchToday = view.Days.Single(d => d.Date == _clock.Current).Slots.Single(x => x.Name == "午餐");
        Assert.Equal(120, lunchToday.BasePlanned);
        Assert.Equal(100, lunchToday.Planned);
        Assert.Contains(view.Pool.Lines, l => l.Kind == "Lower" && l.Amount == 20);
    }

    [Fact]
    public async Task Correction_to_the_past_requires_a_reason()
    {
        var s = (await _settings.GetAsync("u")).Settings;

        await Assert.ThrowsAsync<Common.ValidationException>(() =>
            _settings.SaveAsync("u", new SaveSettingsRequest(s, new DateOnly(2026, 10, 1), " ")));

        var ok = await _settings.SaveAsync("u", new SaveSettingsRequest(s with { MonthlyIncome = 50000 }, new DateOnly(2026, 10, 1), "薪資單發現記錯"));
        Assert.Contains(ok.Versions, v => v.IsCorrection && v.EffectiveFrom == new DateOnly(2026, 10, 1));
    }

    [Fact]
    public async Task Slots_must_start_at_the_logical_day_start_and_not_overlap()
    {
        var s = (await _settings.GetAsync("u")).Settings;

        await Assert.ThrowsAsync<Common.ValidationException>(() =>
            _settings.SaveAsync("u", new SaveSettingsRequest(WithSlot(s, "早餐", x => x with { Start = "06:00" }), null, null)));
        await Assert.ThrowsAsync<Common.ValidationException>(() =>
            _settings.SaveAsync("u", new SaveSettingsRequest(WithSlot(s, "晚餐", x => x with { Start = "09:00" }), null, null)));
    }

    [Fact]
    public async Task Legacy_overwrite_settings_are_imported_with_the_same_ids()
    {
        _db.Profiles.Add(new UserProfile { UserId = "old", MonthlyIncome = 40000, CycleStartDay = 10 });
        var cat = new Category { UserId = "old", Name = "吃飯", Group = CategoryGroup.Food, Mode = BudgetMode.Daily, Percent = 30 };
        cat.Slots.Add(new DailySlot { Name = "午餐", WorkdayAmount = 100, HolidayAmount = 120 });
        _db.Categories.Add(cat);
        await _db.SaveChangesAsync();

        var view = await _settings.GetAsync("old");

        Assert.Equal(40000, view.Settings.MonthlyIncome);
        Assert.Equal(10, view.Settings.Payday.Day);
        var imported = view.Settings.Categories.Single();
        Assert.Equal(cat.Id, imported.Id);
        Assert.Equal(cat.Slots[0].Id, imported.Slots.Single().Id);
        Assert.Equal("04:00", imported.Slots.Single().Start);
        Assert.Equal("由舊設定匯入", view.Versions.Single().Note);
    }

    [Fact]
    public async Task Saving_imported_settings_keeps_ids_even_when_category_and_slot_ids_coincide()
    {
        // 舊資料的分類和時段各自從 1 開始編號：分類 1 和時段 1 同時存在
        _db.Profiles.Add(new UserProfile { UserId = "old", MonthlyIncome = 40000, CycleStartDay = 1 });
        var cat = new Category { UserId = "old", Name = "吃飯", Group = CategoryGroup.Food, Mode = BudgetMode.Daily, Percent = 30 };
        cat.Slots.Add(new DailySlot { Name = "午餐", WorkdayAmount = 100, HolidayAmount = 120 });
        _db.Categories.Add(cat);
        await _db.SaveChangesAsync();
        Assert.Equal(cat.Id, cat.Slots[0].Id);

        var s = (await _settings.GetAsync("old")).Settings;
        var saved = await _settings.SaveAsync("old", new SaveSettingsRequest(s with { MonthlyIncome = 41000 }, null, null));

        var c = saved.Settings.Categories.Single();
        Assert.Equal(cat.Id, c.Id);
        Assert.Equal(cat.Slots[0].Id, c.Slots.Single().Id);
    }

    [Fact]
    public async Task Fixed_categories_budget_is_the_sum_of_their_charges_and_charges_get_due_dates()
    {
        await MonthlyPeriods();
        var view = await _periods.GetCurrentAsync("u");

        var housing = view.Categories.Single(c => c.Name == "居住");
        Assert.Equal(13200, housing.Budget);
        Assert.Contains(view.FixedCharges, f => f.Name == "房租" && f.DueDate == new DateOnly(2026, 10, 5));
    }

    [Fact]
    public async Task Next_payday_override_moves_the_end_of_the_current_period()
    {
        await MonthlyPeriods();
        var view = await _periods.GetCurrentAsync("u");

        var after = await _periods.SetNextPaydayAsync("u", view.Id, new DateOnly(2026, 10, 30));

        Assert.Equal(new DateOnly(2026, 10, 29), after.EndDate);
        await Assert.ThrowsAsync<Common.ValidationException>(() => _periods.SetNextPaydayAsync("u", view.Id, _clock.Current));
    }

    [Fact]
    public async Task Deleting_a_report_adds_a_void_record_instead_of_removing_rows()
    {
        await MonthlyPeriods();
        var view = await _periods.GetCurrentAsync("u");
        var food = view.Categories.Single(c => c.Name == "餐費");
        var lunch = view.Days.Single(d => d.Date == _clock.Current).Slots.Single(s => s.Name == "午餐");
        var created = await _entries.CreateAsync("u", view.Id, new CreateEntryRequest(_clock.Current, food.CategoryId, lunch.SlotId, EntryInputMode.Actual, 200, true, null, null));
        var entryId = created.Entries.Single().Id;

        var after = await _entries.DeleteAsync("u", view.Id, entryId);

        Assert.Empty(after.Entries);
        Assert.Equal(2, await _db.Entries.CountAsync());
        Assert.True(await _db.Entries.AnyAsync(e => e.IsVoid && e.ReplacesId == entryId));
    }

    [Fact]
    public async Task Preview_does_not_persist_anything()
    {
        await MonthlyPeriods();
        var view = await _periods.GetCurrentAsync("u");
        var food = view.Categories.Single(c => c.Name == "餐費");
        var lunch = view.Days[0].Slots.First(s => s.Name == "午餐");

        var preview = await _entries.PreviewAsync("u", view.Id, new CreateEntryRequest(
            _clock.Current, food.CategoryId, lunch.SlotId, EntryInputMode.Overage, 60, false, null, null));

        Assert.Equal(60, preview.Entry.Diff);
        Assert.Equal(0, await _db.Entries.CountAsync());
    }

    [Fact]
    public async Task Subscription_reported_now_becomes_a_fixed_item_from_next_period()
    {
        await MonthlyPeriods();
        var view = await _periods.GetCurrentAsync("u");
        var fun = view.Categories.Single(c => c.Name == "娛樂");
        var bills = view.Categories.Single(c => c.Name == "固定帳單");

        var after = await _entries.CreateAsync("u", view.Id, new CreateEntryRequest(
            _clock.Current, fun.CategoryId, null, EntryInputMode.Actual, 290, true, "Spotify",
            new SubscriptionRequest("Spotify", bills.CategoryId, BillingCycle.Monthly, null)));

        var latest = (await _settings.GetAsync("u")).Settings;
        var item = latest.Categories.Single(c => c.Id == bills.CategoryId).FixedItems.Single(f => f.Name == "Spotify");
        Assert.Equal(new DateOnly(2026, 11, 1), item.ActiveFrom);
        Assert.DoesNotContain(after.FixedCharges, f => f.Name == "Spotify");
        Assert.Equal(290, after.Categories.Single(c => c.CategoryId == fun.CategoryId).Spent);
    }

    [Fact]
    public async Task Fixed_categories_reject_reports()
    {
        var view = await _periods.GetCurrentAsync("u");
        var housing = view.Categories.Single(c => c.Name == "居住");

        await Assert.ThrowsAsync<Common.ValidationException>(() => _entries.CreateAsync("u", view.Id, new CreateEntryRequest(
            _clock.Current, housing.CategoryId, null, EntryInputMode.Actual, 100, true, null, null)));
    }

    [Fact]
    public async Task Before_the_start_date_nothing_is_calculated()
    {
        await MonthlyPeriods();
        await Configure(s => s with { StartDate = new DateOnly(2026, 10, 15) });

        var ex = await Assert.ThrowsAsync<Common.NotStartedException>(() => _periods.GetCurrentAsync("u"));

        Assert.Equal(new DateOnly(2026, 10, 15), ex.Info.FirstPeriodStart);
        Assert.Equal(new DateOnly(2026, 10, 31), ex.Info.FirstPeriodEnd);
        Assert.Equal(0, await _db.Periods.CountAsync());
    }

    [Fact]
    public async Task First_period_starts_on_the_start_date_and_ignores_trial_periods()
    {
        await MonthlyPeriods();
        var trial = await _periods.GetCurrentAsync("u");
        await Configure(s => s with { StartDate = new DateOnly(2026, 10, 5) });

        var view = await _periods.GetCurrentAsync("u");

        Assert.NotEqual(trial.Id, view.Id);
        Assert.Equal(new DateOnly(2026, 10, 5), view.StartDate);
        Assert.Equal(1, await _periods.DeleteBeforeStartAsync("u"));
    }

    [Fact]
    public async Task Quarterly_items_are_charged_every_third_month_from_the_first_billing_month()
    {
        await MonthlyPeriods();
        await Configure(s => s with
        {
            Categories = s.Categories.Select(c => c.Name != "固定帳單" ? c : c with
            {
                FixedItems = [.. c.FixedItems, new FixedItemDto(0, "保險季繳", 3000, 20, false, BillingCycle.Quarterly, 1, true, null)],
            }).ToList(),
        });

        var october = await _periods.GetCurrentAsync("u");
        Assert.Contains(october.FixedCharges, f => f.Name == "保險季繳" && f.DueDate == new DateOnly(2026, 10, 20));

        _clock.Current = new DateOnly(2026, 11, 3);
        var november = await _periods.GetCurrentAsync("u");
        Assert.DoesNotContain(november.FixedCharges, f => f.Name == "保險季繳");
    }

    [Fact]
    public void Payday_shift_moves_off_non_working_days()
    {
        var off = new HashSet<DateOnly> { new(2026, 1, 3), new(2026, 1, 4), new(2026, 1, 5) };
        bool IsOff(DateOnly d) => off.Contains(d);

        Assert.Equal(new DateOnly(2026, 1, 5), Payday.Scheduled(2026, 1, new PaydayRule(5, HolidayShift.None), IsOff));
        Assert.Equal(new DateOnly(2026, 1, 2), Payday.Scheduled(2026, 1, new PaydayRule(5, HolidayShift.Before), IsOff));
        Assert.Equal(new DateOnly(2026, 1, 6), Payday.Scheduled(2026, 1, new PaydayRule(5, HolidayShift.After), IsOff));
        Assert.Equal(new DateOnly(2026, 2, 28), Payday.Scheduled(2026, 2, new PaydayRule(31, HolidayShift.None), IsOff));
    }

    private sealed class FakeCalendar : ICalendarService
    {
        public HashSet<DateOnly> Holidays { get; } = [];

        public Task<Dictionary<DateOnly, DayInfo>> GetDaysAsync(string userId, DateOnly from, DateOnly to, CancellationToken ct = default)
        {
            var result = new Dictionary<DateOnly, DayInfo>();
            for (var d = from; d <= to; d = d.AddDays(1))
            {
                var weekend = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                result[d] = new DayInfo(weekend || Holidays.Contains(d), Holidays.Contains(d) ? "假日" : null);
            }
            return Task.FromResult(result);
        }

        public Task SetOverrideAsync(string userId, DateOnly date, bool? isHoliday, CancellationToken ct = default) => Task.CompletedTask;
    }
}

/// <summary>固定在某個邏輯日台北中午的時鐘（UTC 04:00）。</summary>
public sealed class TestClock(DateOnly today) : IAppClock
{
    public DateOnly Current { get; set; } = today;
    public DateTime UtcNow => Current.ToDateTime(new TimeOnly(4, 0), DateTimeKind.Utc);
    public DateOnly Today => Current;
    public DateOnly ToLocalDate(DateTime utc) => DateOnly.FromDateTime(utc.AddHours(8));
    public DateOnly LogicalDate(DateTime utc, TimeSpan dayStart) => DateOnly.FromDateTime(utc.AddHours(8) - dayStart);
    public DateOnly LogicalToday(TimeSpan dayStart) => LogicalDate(UtcNow, dayStart);
}
