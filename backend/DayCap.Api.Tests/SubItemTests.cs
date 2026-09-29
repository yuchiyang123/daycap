using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using DayCap.Api.Models.Settings;
using DayCap.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Tests;

/// <summary>§13 類別細項：彙總與上限提醒，不改變預算怎麼扣。</summary>
public sealed class SubItemTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DayCapDbContext _db;
    private readonly TestClock _clock = new(new DateOnly(2026, 10, 7));
    private readonly SettingsService _settings;
    private readonly PeriodService _periods;
    private readonly EntryService _entries;

    public SubItemTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _db = new DayCapDbContext(new DbContextOptionsBuilder<DayCapDbContext>().UseSqlite(_conn).Options);
        _db.Database.Migrate();
        var calendar = new Weekends();
        _settings = new SettingsService(_db, _clock, calendar);
        _periods = new PeriodService(_db, _settings, calendar, _clock);
        _entries = new EntryService(_db, _periods, _settings, _clock);

        var s = _settings.GetAsync("u").GetAwaiter().GetResult().Settings;
        var cats = s.Categories.Select(c => c.Name == "娛樂" ? c with { SubItems = [new SubItemDoc("遊戲", 500), new SubItemDoc("電影", null)] } : c).ToList();
        _settings.SaveAsync("u", new SaveSettingsRequest(s with { Payday = new PaydayRule(1, HolidayShift.None), Categories = cats }, DateOnly.MinValue, "測試設定"))
            .GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task Sub_items_sum_up_and_flag_the_cap_without_changing_the_budget()
    {
        var view = await _periods.GetCurrentAsync("u");
        var fun = view.Categories.Single(c => c.Name == "娛樂");
        CreateEntryRequest Extra(decimal amount, string? sub) =>
            new(_clock.Current, fun.CategoryId, null, EntryInputMode.Actual, amount, true, null, null, SubItem: sub);

        await _entries.CreateAsync("u", view.Id, Extra(400, "遊戲"));
        await _entries.CreateAsync("u", view.Id, Extra(300, "遊戲"));
        await _entries.CreateAsync("u", view.Id, Extra(350, "電影"));
        var after = await _entries.CreateAsync("u", view.Id, Extra(100, "桌遊")); // 設定裡沒有的細項

        var game = after.SubItems!.Single(x => x.Name == "遊戲");
        Assert.Equal(700, game.Spent);
        Assert.Equal(200, game.Over);
        Assert.Equal(0, after.SubItems!.Single(x => x.Name == "電影").Over);
        Assert.Contains(after.SubItems!, x => x.Name == "桌遊" && x.Cap == null && x.Spent == 100);

        // 預算照分類算：花費 1,150 全部算進娛樂，不因為細項超過上限多扣
        Assert.Equal(1_150, after.Categories.Single(c => c.Name == "娛樂").Spent);
    }

    [Fact]
    public async Task Duplicate_sub_item_names_are_rejected()
    {
        var s = (await _settings.GetAsync("u")).Settings;
        var cats = s.Categories.Select(c => c.Name == "娛樂" ? c with { SubItems = [new SubItemDoc("遊戲", 500), new SubItemDoc("遊戲", 100)] } : c).ToList();
        await Assert.ThrowsAsync<Common.ValidationException>(() => _settings.SaveAsync("u", new SaveSettingsRequest(s with { Categories = cats }, null, null)));
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
