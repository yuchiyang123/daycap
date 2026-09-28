using System.Globalization;
using System.Text.Json;
using DayCap.Api.Data;
using DayCap.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Services;

public interface ICalendarService
{
    /// <summary>回傳區間內每一天是不是假日（國定假日 + 週末 + 補班 + 使用者手動覆寫）。</summary>
    Task<Dictionary<DateOnly, DayInfo>> GetDaysAsync(string userId, DateOnly from, DateOnly to, CancellationToken ct = default);

    Task SetOverrideAsync(string userId, DateOnly date, bool? isHoliday, CancellationToken ct = default);
}

/// <summary>
/// 假日資料來源：行政院人事行政總處「政府行政機關辦公日曆表」，用 ruyut/TaiwanCalendar
/// 整理好的 JSON（每年一檔，含週末、補假、補班）。一年抓一次存進 DB。
/// 抓不到（還沒公告、網路掛了）就退回「週六日 = 假日」，而且不寫入快取，下次會再試。
/// </summary>
public class CalendarService(
    DayCapDbContext db,
    IHttpClientFactory httpFactory,
    IConfiguration config,
    ILogger<CalendarService> logger) : ICalendarService
{
    public const string HttpClientName = "calendar";

    public async Task<Dictionary<DateOnly, DayInfo>> GetDaysAsync(string userId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        for (var y = from.Year; y <= to.Year; y++) await EnsureYearAsync(y, ct);

        var cached = await db.CalendarDays.Where(d => d.Date >= from && d.Date <= to).ToDictionaryAsync(d => d.Date, ct);
        var overrides = await db.DayTypeOverrides.Where(o => o.UserId == userId && o.Date >= from && o.Date <= to)
            .ToDictionaryAsync(o => o.Date, ct);

        var result = new Dictionary<DateOnly, DayInfo>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            DayInfo info = cached.TryGetValue(d, out var c)
                ? new DayInfo(c.IsHoliday, string.IsNullOrEmpty(c.Description) ? null : c.Description)
                : new DayInfo(d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday, null);
            if (overrides.TryGetValue(d, out var o))
            {
                info = new DayInfo(o.IsHoliday, o.IsHoliday ? "自訂假日" : "自訂上班日");
            }
            result[d] = info;
        }
        return result;
    }

    public async Task SetOverrideAsync(string userId, DateOnly date, bool? isHoliday, CancellationToken ct = default)
    {
        var existing = await db.DayTypeOverrides.FindAsync([userId, date], ct);
        if (isHoliday is null)
        {
            if (existing is not null) db.DayTypeOverrides.Remove(existing);
        }
        else if (existing is null)
        {
            db.DayTypeOverrides.Add(new DayTypeOverride { UserId = userId, Date = date, IsHoliday = isHoliday.Value });
        }
        else
        {
            existing.IsHoliday = isHoliday.Value;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureYearAsync(int year, CancellationToken ct)
    {
        var start = new DateOnly(year, 1, 1);
        var end = new DateOnly(year, 12, 31);
        if (await db.CalendarDays.AnyAsync(d => d.Date >= start && d.Date <= end, ct)) return;

        var template = config["Calendar:SourceUrl"] ?? "https://cdn.jsdelivr.net/gh/ruyut/TaiwanCalendar/data/{year}.json";
        var url = template.Replace("{year}", year.ToString(CultureInfo.InvariantCulture));
        try
        {
            var client = httpFactory.CreateClient(HttpClientName);
            using var res = await client.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode)
            {
                logger.LogWarning("Calendar {Year} not available ({Status}), falling back to weekends", year, (int)res.StatusCode);
                return;
            }

            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            var rows = await JsonSerializer.DeserializeAsync<List<CalendarRow>>(stream, JsonOptions, ct) ?? [];
            var parsed = rows
                .Select(r => DateOnly.TryParseExact(r.Date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    ? new CalendarDay { Date = d, IsHoliday = r.IsHoliday, Description = r.Description ?? "" }
                    : null)
                .OfType<CalendarDay>()
                .Where(d => d.Date.Year == year)
                .ToList();
            if (parsed.Count < 300)
            {
                logger.LogWarning("Calendar {Year} looks incomplete ({Count} rows), not caching", year, parsed.Count);
                return;
            }

            db.CalendarDays.AddRange(parsed);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Calendar {Year} fetch failed, falling back to weekends", year);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record CalendarRow(string Date, bool IsHoliday, string? Description);
}
