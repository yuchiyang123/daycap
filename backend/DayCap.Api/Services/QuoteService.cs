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
