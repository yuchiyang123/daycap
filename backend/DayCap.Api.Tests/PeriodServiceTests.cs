using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using DayCap.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Tests;

public sealed class PeriodServiceTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DayCapDbContext _db;
    private readonly FakeClock _clock = new(new DateOnly(2026, 10, 7));
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
        _settings = new SettingsService(_db);
        _periods = new PeriodService(_db, _settings, _calendar, _clock);
        _entries = new EntryService(_db, _periods, _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task Current_period_uses_holiday_amounts_on_national_holidays()
    {
        _calendar.Holidays.Add(new DateOnly(2026, 10, 9));  // 補假（星期五）
        _calendar.Holidays.Add(new DateOnly(2026, 10, 10)); // 國慶日（星期六）

        var view = await _periods.GetCurrentAsync("u");

        Assert.Equal(new DateOnly(2026, 10, 1), view.StartDate);
        Assert.Equal(new DateOnly(2026, 10, 31), view.EndDate);
        var holiday = view.Days.Single(d => d.Date == new DateOnly(2026, 10, 9));
        var workday = view.Days.Single(d => d.Date == new DateOnly(2026, 10, 8));
        Assert.True(holiday.IsHoliday);
        // 預設設定：上班日 70+120+150 + 通勤 60；假日 90+180+200，通勤 0 所以不排
        Assert.Equal(400, workday.BasePlanned);
        Assert.Equal(470, holiday.BasePlanned);
    }

    [Fact]
    public async Task Fixed_categories_budget_is_the_sum_of_their_charges_and_charges_get_due_dates()
    {
        var view = await _periods.GetCurrentAsync("u");

        var housing = view.Categories.Single(c => c.Name == "居住");
        Assert.Equal(13200, housing.Budget);
        Assert.Equal(13200, housing.Spent);
        Assert.Contains(view.FixedCharges, f => f.Name == "房租" && f.DueDate == new DateOnly(2026, 10, 5));
    }

    [Fact]
    public async Task Rebuild_changes_only_today_and_later_and_keeps_reports()
    {
        var view = await _periods.GetCurrentAsync("u");
        var food = view.Categories.Single(c => c.Name == "餐費");
        var breakfast = view.Days[0].Slots.First(s => s.Name == "早餐");
        await _entries.CreateAsync("u", view.Id, new CreateEntryRequest(
            new DateOnly(2026, 10, 7), food.CategoryId, breakfast.SlotId, EntryInputMode.Actual, 50, true, null, null));

        var settings = await _settings.GetAsync("u");
        var foodDto = settings.Categories.Single(c => c.Name == "餐費");
        var newSlots = foodDto.Slots.Select(s => s.Name == "早餐" ? s with { WorkdayAmount = 100 } : s).ToList();
        await _settings.SaveAsync("u", settings with
        {
            Categories = settings.Categories.Select(c => c.Id == foodDto.Id ? c with { Slots = newSlots } : c).ToList()
        });

        var rebuilt = await _periods.RebuildAsync("u", view.Id, null);

        var past = rebuilt.Days.Single(d => d.Date == new DateOnly(2026, 10, 6)).Slots.Single(s => s.SlotId == breakfast.SlotId);
        var today = rebuilt.Days.Single(d => d.Date == new DateOnly(2026, 10, 7)).Slots.Single(s => s.SlotId == breakfast.SlotId);
        Assert.Equal(70, past.BasePlanned);
        Assert.Equal(100, today.BasePlanned);
        Assert.Equal(50, today.Actual); // 回報還在，對上新的排程
    }

    [Fact]
    public async Task Preview_does_not_persist_anything()
    {
        var view = await _periods.GetCurrentAsync("u");
        var food = view.Categories.Single(c => c.Name == "餐費");
        var lunch = view.Days[0].Slots.First(s => s.Name == "午餐");

        var preview = await _entries.PreviewAsync("u", view.Id, new CreateEntryRequest(
            new DateOnly(2026, 10, 7), food.CategoryId, lunch.SlotId, EntryInputMode.Overage, 60, false, null, null));

        Assert.Equal(60, preview.Entry.Diff);
        Assert.Equal(60, preview.Entry.Spread);
        Assert.Equal(0, await _db.Entries.CountAsync());
    }

    [Fact]
    public async Task Subscription_reported_now_becomes_a_fixed_item_from_next_period()
    {
        var view = await _periods.GetCurrentAsync("u");
        var fun = view.Categories.Single(c => c.Name == "娛樂");
        var bills = view.Categories.Single(c => c.Name == "固定帳單");

        var after = await _entries.CreateAsync("u", view.Id, new CreateEntryRequest(
            new DateOnly(2026, 10, 7), fun.CategoryId, null, EntryInputMode.Actual, 290, true, "Spotify",
            new SubscriptionRequest("Spotify", bills.CategoryId, BillingCycle.Monthly, null)));

        var item = await _db.FixedItems.SingleAsync(f => f.Name == "Spotify");
        Assert.Equal(new DateOnly(2026, 11, 1), item.ActiveFrom);
        Assert.Equal(7, item.DueDay);

        // 這一期重建也不會重複扣
        var rebuilt = await _periods.RebuildAsync("u", view.Id, null);
        Assert.DoesNotContain(rebuilt.FixedCharges, f => f.Name == "Spotify");
        Assert.Equal(290, after.Categories.Single(c => c.CategoryId == fun.CategoryId).Spent);
    }

    [Fact]
    public async Task Fixed_categories_reject_reports()
    {
        var view = await _periods.GetCurrentAsync("u");
        var housing = view.Categories.Single(c => c.Name == "居住");

        await Assert.ThrowsAsync<Common.ValidationException>(() => _entries.CreateAsync("u", view.Id, new CreateEntryRequest(
            new DateOnly(2026, 10, 7), housing.CategoryId, null, EntryInputMode.Actual, 100, true, null, null)));
    }

    [Theory]
    [InlineData("2026-10-07", 1, "2026-10-01", "2026-10-31")]
    [InlineData("2026-10-04", 5, "2026-09-05", "2026-10-04")]
    [InlineData("2026-10-05", 5, "2026-10-05", "2026-11-04")]
    [InlineData("2027-01-20", 25, "2026-12-25", "2027-01-24")]
    public void Cycle_range_follows_the_start_day(string date, int startDay, string expStart, string expEnd)
    {
        var (s, e) = PeriodService.CycleRange(DateOnly.Parse(date), startDay);
        Assert.Equal(DateOnly.Parse(expStart), s);
        Assert.Equal(DateOnly.Parse(expEnd), e);
    }

    private sealed class FakeClock(DateOnly today) : IAppClock
    {
        public DateTime UtcNow => today.ToDateTime(new TimeOnly(4, 0), DateTimeKind.Utc);
        public DateOnly Today => today;
        public DateOnly ToLocalDate(DateTime utc) => DateOnly.FromDateTime(utc.AddHours(8));
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
