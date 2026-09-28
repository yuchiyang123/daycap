namespace DayCap.Api.Services;

/// <summary>
/// 所有「今天是哪天」都以台北時間為準（伺服器容器是 UTC）。抽成介面讓測試可以固定時間。
/// </summary>
public interface IAppClock
{
    DateTime UtcNow { get; }
    DateOnly Today { get; }
    DateOnly ToLocalDate(DateTime utc);
}

public class TaipeiClock : IAppClock
{
    private static readonly TimeZoneInfo Zone = ResolveZone();

    public DateTime UtcNow => DateTime.UtcNow;
    public DateOnly Today => ToLocalDate(DateTime.UtcNow);

    public DateOnly ToLocalDate(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone));

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
