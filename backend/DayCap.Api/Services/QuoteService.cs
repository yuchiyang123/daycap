using System.Globalization;
using System.Text.Json;
using DayCap.Api.Data;
using DayCap.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace DayCap.Api.Services;

public interface IQuoteService
{
    /// <summary>確保這些代號的收盤價夠新（超過 6 小時就重抓），回傳目前快取。</summary>
    Task<Dictionary<string, PriceQuote>> GetQuotesAsync(IReadOnlyCollection<string> symbols, bool force, CancellationToken ct = default);

    /// <summary>這一天（休市就往後找第一個交易日）的收盤價；還沒有資料（例如還沒收盤）回 null。</summary>
    Task<(DateOnly TradeDate, decimal Close)?> GetCloseOnOrAfterAsync(string symbol, DateOnly date, CancellationToken ct = default);
}

/// <summary>
/// 台股收盤價：上市抓證交所 OpenAPI（STOCK_DAY_ALL），上櫃抓櫃買中心 OpenAPI。
/// 兩支都是「全市場一次給」的 JSON，所以一次抓完，只存使用者有持有的代號。
/// 任何一邊掛掉都只記 log，繼續用舊的快取價格。
/// </summary>
public class QuoteService(
    DayCapDbContext db,
    IHttpClientFactory httpFactory,
    IAppClock clock,
    ILogger<QuoteService> logger) : IQuoteService
{
    public const string HttpClientName = "quotes";
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);
    private static readonly SemaphoreSlim FetchLock = new(1, 1);

    public async Task<Dictionary<string, PriceQuote>> GetQuotesAsync(IReadOnlyCollection<string> symbols, bool force, CancellationToken ct = default)
    {
        var wanted = symbols.Select(s => s.Trim().ToUpperInvariant()).Where(s => s.Length > 0).Distinct().ToList();
        if (wanted.Count == 0) return [];

        var cached = await db.PriceQuotes.Where(q => wanted.Contains(q.Symbol)).ToDictionaryAsync(q => q.Symbol, ct);
        var stale = force || wanted.Any(s => !cached.TryGetValue(s, out var q) || clock.UtcNow - q.FetchedAt > MaxAge);
        if (!stale) return cached;

        await FetchLock.WaitAsync(ct);
        try
        {
            var fetched = new Dictionary<string, (string Name, decimal Price, DateOnly Date)>();
            await FetchTwseAsync(fetched, ct);
            await FetchTpexAsync(fetched, ct);

            foreach (var symbol in wanted)
            {
                if (!fetched.TryGetValue(symbol, out var f)) continue;
                if (!cached.TryGetValue(symbol, out var q))
                {
                    q = new PriceQuote { Symbol = symbol };
                    db.PriceQuotes.Add(q);
                    cached[symbol] = q;
                }
                q.Name = f.Name;
                q.Price = f.Price;
                q.TradeDate = f.Date;
                q.FetchedAt = clock.UtcNow;
            }
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            FetchLock.Release();
        }
        return cached;
    }

    public async Task<(DateOnly TradeDate, decimal Close)?> GetCloseOnOrAfterAsync(string symbol, DateOnly date, CancellationToken ct = default)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        // 先試證交所，再試櫃買中心；這個月找不到（月底休市）就看下個月
        foreach (var month in new[] { date, new DateOnly(date.Year, date.Month, 1).AddMonths(1) })
        {
            var rows = await HistoryRowsAsync(
                $"https://www.twse.com.tw/rwd/zh/afterTrading/STOCK_DAY?date={month:yyyyMMdd}&stockNo={Uri.EscapeDataString(symbol)}&response=json", false, ct);
            if (rows.Count == 0)
                rows = await HistoryRowsAsync(
                    $"https://www.tpex.org.tw/www/zh-tw/afterTrading/tradingStock?code={Uri.EscapeDataString(symbol)}&date={month:yyyy'/'MM'/'dd}&response=json", true, ct);
            var hit = rows.Where(r => r.Date >= date).OrderBy(r => r.Date).FirstOrDefault();
            if (hit.Close > 0) return (hit.Date, hit.Close);
        }
        return null;
    }

    /// <summary>每日成交資訊：第 1 欄民國日期（115/10/06）、第 7 欄收盤價。</summary>
    private async Task<List<(DateOnly Date, decimal Close)>> HistoryRowsAsync(string url, bool tpex, CancellationToken ct)
    {
        var result = new List<(DateOnly, decimal)>();
        try
        {
            var client = httpFactory.CreateClient(HttpClientName);
            using var res = await client.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return result;
            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            JsonElement data;
            if (tpex)
            {
                if (!doc.RootElement.TryGetProperty("tables", out var tables) || tables.GetArrayLength() == 0) return result;
                if (!tables[0].TryGetProperty("data", out data)) return result;
            }
            else if (!doc.RootElement.TryGetProperty("data", out data)) return result;
            foreach (var row in data.EnumerateArray())
            {
                if (row.GetArrayLength() < 7) continue;
                var parts = (row[0].GetString() ?? "").Split('/');
                if (parts.Length != 3 || !int.TryParse(parts[0], out var y) || !int.TryParse(parts[1], out var m) || !int.TryParse(parts[2], out var d)) continue;
                if (!decimal.TryParse((row[6].GetString() ?? "").Replace(",", ""), System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out var close) || close <= 0) continue;
                result.Add((new DateOnly(y + 1911, m, d), close));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Price history {Url} failed", url);
        }
        return result;
    }

    private async Task FetchTwseAsync(Dictionary<string, (string, decimal, DateOnly)> into, CancellationToken ct)
    {
        foreach (var row in await GetRowsAsync("https://openapi.twse.com.tw/v1/exchangeReport/STOCK_DAY_ALL", ct))
        {
            if (Str(row, "Code") is { } code && Dec(row, "ClosingPrice") is { } price)
            {
                into[code.ToUpperInvariant()] = (Str(row, "Name") ?? code, price, RocDate(Str(row, "Date")) ?? clock.Today);
            }
        }
    }

    private async Task FetchTpexAsync(Dictionary<string, (string, decimal, DateOnly)> into, CancellationToken ct)
    {
        foreach (var row in await GetRowsAsync("https://www.tpex.org.tw/openapi/v1/tpex_mainboard_daily_close_quotes", ct))
        {
            if (Str(row, "SecuritiesCompanyCode") is { } code && Dec(row, "Close") is { } price && !into.ContainsKey(code.ToUpperInvariant()))
            {
                into[code.ToUpperInvariant()] = (Str(row, "CompanyName") ?? code, price, RocDate(Str(row, "Date")) ?? clock.Today);
            }
        }
    }

    private async Task<List<JsonElement>> GetRowsAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = httpFactory.CreateClient(HttpClientName);
            using var res = await client.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode)
            {
                logger.LogWarning("Quote source {Url} returned {Status}", url, (int)res.StatusCode);
                return [];
            }
            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList()
                : [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Quote source {Url} failed", url);
            return [];
        }
    }

    private static string? Str(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;

    private static decimal? Dec(JsonElement row, string name) =>
        Str(row, name) is { } s && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : null;

    /// <summary>證交所日期是民國年 1150924 這種格式。</summary>
    private static DateOnly? RocDate(string? s)
    {
        if (s is null || s.Length < 7 || !int.TryParse(s[..^4], out var rocYear)) return null;
        return DateOnly.TryParseExact($"{rocYear + 1911}{s[^4..]}", "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }
}
