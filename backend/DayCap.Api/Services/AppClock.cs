namespace DayCap.Api.Services;

/// <summary>
/// 時間都以台北時間為準（伺服器容器是 UTC），「哪一天」一律用邏輯日（§3.1）：
/// 把時間往前推「邏輯日起點」（預設 04:00）再取日期，所以週六 01:00 算週五。
/// 抽成介面讓測試可以固定時間。
/// </summary>
public interface IAppClock
{
    DateTime UtcNow { get; }

    /// <summary>台北的日曆日期（資產快照、股價這類和預算無關的地方用）。</summary>
    DateOnly Today { get; }

    DateOnly ToLocalDate(DateTime utc);

    /// <summary>某個 UTC 時刻屬於哪個邏輯日。</summary>
    DateOnly LogicalDate(DateTime utc, TimeSpan dayStart);

    DateOnly LogicalToday(TimeSpan dayStart);
}

public class TaipeiClock : IAppClock
{
    private static readonly TimeZoneInfo Zone = ResolveZone();

    public DateTime UtcNow => DateTime.UtcNow;
    public DateOnly Today => ToLocalDate(DateTime.UtcNow);

    public DateOnly ToLocalDate(DateTime utc) => DateOnly.FromDateTime(ToLocal(utc));

    public DateOnly LogicalDate(DateTime utc, TimeSpan dayStart) => DateOnly.FromDateTime(ToLocal(utc) - dayStart);

    public DateOnly LogicalToday(TimeSpan dayStart) => LogicalDate(DateTime.UtcNow, dayStart);

    public static DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    private static TimeZoneInfo ResolveZone()
    {
        foreach (var id in new[] { "Asia/Taipei", "Taipei Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
        }
        // 容器裡沒裝 tzdata 時的退路：台灣沒有日光節約，固定 +8 就是對的。
        return TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
    }
}
