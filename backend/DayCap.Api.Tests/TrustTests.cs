using System.Text;
using DayCap.Api.Data;
using DayCap.Api.Models.Dtos;
using DayCap.Api.Models.Entities;
using DayCap.Api.Models.Settings;
using DayCap.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Tests;

/// <summary>§21 信任與安全：匯出、刪除帳號、修改紀錄、登入裝置撤銷。</summary>
public sealed class TrustTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DayCapDbContext _db;
    private readonly TestClock _clock = new(new DateOnly(2026, 10, 7));
    private readonly SettingsService _settings;
    private readonly PeriodService _periods;
    private readonly EntryService _entries;
    private readonly UserDataService _data;
    private readonly SessionService _sessions;

    public TrustTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _db = new DayCapDbContext(new DbContextOptionsBuilder<DayCapDbContext>().UseSqlite(_conn).Options);
        _db.Database.Migrate();
        var calendar = new Weekends();
        _settings = new SettingsService(_db, _clock, calendar);
        _periods = new PeriodService(_db, _settings, calendar, _clock);
        _entries = new EntryService(_db, _periods, _settings, _clock);
        _data = new UserDataService(_db, _clock);
        _sessions = new SessionService(_db, _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private async Task<int> SomeData(string user)
    {
        var s = (await _settings.GetAsync(user)).Settings;
        await _settings.SaveAsync(user, new SaveSettingsRequest(s with { Payday = new PaydayRule(1, HolidayShift.None) }, DateOnly.MinValue, "測試設定"));
        var view = await _periods.GetCurrentAsync(user);
        var cat = view.Categories.First(c => c.Mode == BudgetMode.Envelope).CategoryId;
        var after = await _entries.CreateAsync(user, view.Id, new CreateEntryRequest(_clock.Current, cat, null, EntryInputMode.Actual, 300, true, "=HYPERLINK(\"x\")", null));
        return after.Entries.Single().Id;
    }

    [Fact]
    public async Task Deleting_the_account_removes_every_row_of_that_user_only()
    {
        await SomeData("u");
        await SomeData("other");

        await Assert.ThrowsAsync<Common.ValidationException>(() => _data.DeleteAllAsync("u", "delete"));
        await _data.DeleteAllAsync("u", UserDataService.DeletePhrase);

        Assert.False(await _db.Profiles.AnyAsync(p => p.UserId == "u"));
        Assert.False(await _db.SettingsVersions.AnyAsync(v => v.UserId == "u"));
        Assert.False(await _db.Periods.AnyAsync(p => p.UserId == "u"));
        var otherPeriods = await _db.Periods.Where(p => p.UserId == "other").Select(p => p.Id).ToListAsync();
        Assert.Equal(await _db.Entries.CountAsync(), await _db.Entries.CountAsync(e => otherPeriods.Contains(e.PeriodId)));
        Assert.True(await _db.Profiles.AnyAsync(p => p.UserId == "other"));
    }

    [Fact]
    public async Task Exports_include_the_facts_and_csv_is_safe_to_open()
    {
        await SomeData("u");

        var json = Encoding.UTF8.GetString(await _data.ExportJsonAsync("u"));
        Assert.Contains("\"entries\"", json);
        Assert.Contains("\"settingsVersions\"", json);

        var csv = Encoding.UTF8.GetString(await _data.ExportEntriesCsvAsync("u"));
        Assert.Contains("日期,分類", csv);
        Assert.Contains("'=HYPERLINK", csv); // 公式開頭加單引號，Excel 不會執行
    }

    [Fact]
    public async Task History_shows_what_was_added_changed_and_deleted()
    {
        var id = await SomeData("u");
        var period = (await _periods.GetCurrentAsync("u")).Id;
        await _entries.DeleteAsync("u", period, id);

        var history = await _data.HistoryAsync("u", 30);

        Assert.Contains(history, h => h.Kind == "回報" && h.Action == "新增" && h.Id == id);
        Assert.Contains(history, h => h.Kind == "回報" && h.Action == "刪除" && h.ReplacesId == id);
        Assert.Contains(history, h => h.Kind == "設定");
    }

    [Fact]
    public async Task Revoked_sessions_and_tokens_issued_before_log_out_everywhere_are_rejected()
    {
        await SomeData("u");
        var issued = _clock.UtcNow;
        Assert.True(await _sessions.CheckAsync("u", "phone-token", issued, "iPhone Safari/1"));
        Assert.True(await _sessions.CheckAsync("u", "laptop-token", issued, "Windows Chrome/1"));

        var list = await _sessions.ListAsync("u", "laptop-token");
        await _sessions.RevokeAsync("u", list.Single(s => !s.Current).Id, "laptop-token");
        Assert.False(await _sessions.CheckAsync("u", "phone-token", issued, null));

        // 登出其他所有裝置：還沒打過這個站的舊 token 也不行，按按鈕的這個還可以
        await _sessions.RevokeOthersAsync("u", "laptop-token");
        Assert.False(await _sessions.CheckAsync("u", "tablet-token", issued, null));
        Assert.True(await _sessions.CheckAsync("u", "laptop-token", issued, null));
        Assert.True(await _sessions.CheckAsync("u", "new-login", _clock.UtcNow.AddMinutes(1), null));
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
