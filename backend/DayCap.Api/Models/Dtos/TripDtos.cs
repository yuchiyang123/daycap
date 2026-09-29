namespace DayCap.Api.Models.Dtos;

public record CreateTripRequest(string Name, DateOnly StartDate, DateOnly EndDate, decimal Budget, string? Currency, decimal? FxRate);

/// <summary>旅遊（§17）。Status：upcoming / active / ended。Remaining＝旅遊預算罐子裡還剩多少。</summary>
public record TripView(int Id, string Name, DateOnly StartDate, DateOnly EndDate, decimal Budget, string? Currency, decimal? FxRate,
    int JarId, decimal Remaining, bool JarClosed, string Status);

/// <summary>
/// 收入中斷（§18）：照目前花法，可動用的錢還能撐幾天。
/// UsableAssets＝銀行、現金、電子支付合計 − 信用卡欠款（不含股票）；AvgDailySpend＝最近 Days 天實際花費的平均（沒回報的時段照預算算）。
/// </summary>
public record RunwayView(decimal UsableAssets, decimal AvgDailySpend, int BasisDays, DateOnly From, DateOnly To, int? RunwayDays);
