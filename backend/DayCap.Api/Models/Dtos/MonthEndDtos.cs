using DayCap.Api.Models.Entities;

namespace DayCap.Api.Models.Dtos;

/// <summary>月結報表（§12.2 第 3 步）：三到五個重點 + 下期調整的建議。</summary>
public record MonthEndReport(
    int PeriodId,
    DateOnly StartDate,
    DateOnly EndDate,
    bool Closed,
    bool CanClose,
    string? CannotCloseReason,
    // 月結前要先對一次帳（發薪前實際餘額）
    bool NeedsReconciliation,
    // 本期結果＝待分配池餘額（正 = 省下，負 = 超支）
    decimal Result,
    decimal? PreviousResult,
    List<OverspendItem> TopOverspends,
    decimal Unexplained,
    List<SlotSuggestion> Slots,
    List<GoalProgress> Goals,
    MonthEndSummary? Summary,
    List<SubItemView>? SubItemOvers = null,
    // 結果 = 待分配池 + 月額度分類（例如娛樂）沒花完的部分
    decimal PoolBalance = 0,
    List<EnvelopeLeftover>? EnvelopeLeftovers = null);

/// <summary>月額度分類沒花完、月結時算進結餘的錢。</summary>
public record EnvelopeLeftover(int CategoryId, string Name, decimal Amount);

/// <summary>例：「午餐 12 天超預算，共多 1,440」。</summary>
public record OverspendItem(string Label, int Days, decimal Total);

/// <summary>下期調整參考：這個時段回報過的日子平均實際花多少。</summary>
public record SlotSuggestion(int CategoryId, string CategoryName, int SlotId, string SlotName,
    decimal WorkdayAmount, decimal HolidayAmount, int ReportedDays, decimal AvgActual, decimal AvgPlanned);

public record GoalProgress(string Name, decimal Current, decimal Target, decimal Progress, DateOnly TargetDate, DateOnly? EstimatedDate);

public record SlotChange(int CategoryId, int SlotId, decimal WorkdayAmount, decimal HolidayAmount);

/// <summary>
/// 最後一步的決定：結餘帶進下一期待分配池（Pool）或轉進存款（Savings，AccountId 必填）；
/// 缺口怎麼補（Pool / Split / Savings）；下期時段要不要調整。
/// </summary>
public record CloseMonthRequest(ShortfallChoice Decision, int? AccountId, List<SlotChange> SlotChanges);

/// <summary>已完成的月結（快照裡的重點）。</summary>
public record MonthEndSummary(DateTime ClosedAt, decimal Result, ShortfallChoice Decision, decimal CarryAmount, string Message,
    // 結餘轉進存款時，存款帳戶實際記了多少（改過實際轉帳金額就會跟 Result 不同）
    decimal? SavingsMoved = null);

/// <summary>月結結餘實際轉進存款的金額（例如扣了手續費），剩下的留在生活錢裡。</summary>
public record AmendMonthEndSavingsRequest(decimal Amount);
