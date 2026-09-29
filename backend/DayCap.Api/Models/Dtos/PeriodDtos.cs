using DayCap.Api.Models.Entities;

namespace DayCap.Api.Models.Dtos;

public record PeriodSummaryDto(int Id, DateOnly StartDate, DateOnly EndDate);

public record PeriodView(
    int Id,
    DateOnly StartDate,
    DateOnly EndDate,
    // 邏輯日（§3.1）
    DateOnly Today,
    string LogicalDayStart,
    // 本期平日／假日天數（§3.5）
    int WeekdayCount,
    int HolidayCount,
    // 實領 = 期間第一天有效的月收入 + 本期薪資調整
    decimal Income,
    decimal BaseIncome,
    List<IncomeAdjustmentView> IncomeAdjustments,
    bool IncomeConfirmed,
    DateTime? SettledAt,
    PoolView Pool,
    List<CategoryView> Categories,
    List<DayView> Days,
    List<EntryView> Entries,
    List<FixedChargeView> FixedCharges,
    List<PoolTransferView> Transfers,
    List<ReconciliationView> Reconciliations,
    // 給使用者看的提醒（例如自動分配沒執行）
    List<string> Warnings,
    // 月結（§12.2）：這期是否已月結；上一期還沒月結時帶它的 Id（這期還不能正式啟用）
    bool Closed = false,
    int? PreviousPeriodNeedsClosing = null);

/// <summary>對帳結果：Diff = 實際 − 預期；負的依超支規則處理（FromPool / Spread / Unabsorbed）。</summary>
public record ReconciliationView(int Id, DateOnly Date, decimal Expected, decimal Actual, decimal Diff, decimal FromPool, decimal Spread, decimal Unabsorbed);

public record PoolView(decimal Opening, decimal Balance, List<PoolLine> Lines);

/// <summary>
/// Kind：Opening（期初）、Income（薪資調整）、Settings（期中設定變更）、Surplus（少花存入）、
/// Cover（超支從待定區扣）、Unabsorbed（後續天數不夠攤）、EnvelopeOver（月額度類超支）、
/// Transfer（手動）、Allocate（分配給分類）、Lower（今天改設定調降的時段）。
/// </summary>
public record PoolLine(DateOnly Date, decimal Amount, string Kind, string Label, int? EntryId, int? TransferId);

public record CategoryView(
    int CategoryId,
    string Name,
    CategoryGroup Group,
    BudgetMode Mode,
    decimal Budget,
    decimal Scheduled,
    decimal Spent,
    decimal PlannedRemaining,
    decimal Projected,
    // 從待定區分配進來、加在額度上的金額（Budget 已包含）
    decimal Allocated);

public record DayView(
    DateOnly Date,
    bool IsHoliday,
    string? HolidayName,
    string Status,
    decimal BasePlanned,
    decimal Planned,
    decimal Net,
    List<SlotView> Slots,
    List<int> ExtraEntryIds);

public record SlotView(
    int CategoryId,
    int SlotId,
    string Name,
    decimal BasePlanned,
    decimal Planned,
    decimal? Actual,
    int? EntryId,
    string? Start = null);

public record EntryView(
    int Id,
    DateOnly Date,
    int CategoryId,
    string CategoryName,
    int? SlotId,
    string? SlotName,
    EntryInputMode InputMode,
    decimal InputAmount,
    decimal Actual,
    decimal PlannedAtEntry,
    decimal Diff,
    bool UsePool,
    decimal FromPool,
    decimal Spread,
    int SpreadSlots,
    decimal Unabsorbed,
    decimal EnvelopeOver,
    string? Note,
    bool IsSubscription,
    DateTime CreatedAt,
    // 攤到幾天、平均每天少多少（§10.1 預覽「接下來 16 天每天少 75」）
    int SpreadDays,
    decimal SpreadPerDay,
    // 從罐子付的部分（§11.2），不算進預算
    decimal JarCovered = 0,
    int? JarId = null);

public record FixedChargeView(int? FixedItemId, int CategoryId, string Name, decimal Amount, DateOnly? DueDate, bool IsSubscription);

public record PoolTransferView(int Id, DateOnly Date, decimal Amount, string Note, int? CategoryId, int? JarId = null);

public record IncomeAdjustmentView(int Id, IncomeAdjustmentKind Kind, decimal? Days, decimal? Hours, decimal Amount, string? Note, string Label);

public record CreateEntryRequest(
    DateOnly Date,
    int CategoryId,
    int? SlotId,
    EntryInputMode InputMode,
    decimal Amount,
    bool UsePool,
    string? Note,
    SubscriptionRequest? Subscription,
    // 用哪個帳戶付的（選填，§5）：信用卡 = 欠款增加
    int? AccountId = null,
    // 超支超過護欄下限、後面攤不完的部分怎麼處理（§10.2）；null = 待分配池
    ShortfallChoice? Guardrail = null,
    // Guardrail = Savings 時從哪個帳戶吸收
    int? GuardrailAccountId = null,
    // 從罐子付（§11.2）：額外花費才可以
    int? JarId = null);

/// <summary>回報時順便把它登記成訂閱：下個週期起變成固定支出。</summary>
public record SubscriptionRequest(string Name, int TargetCategoryId, BillingCycle Cycle, int? DueDay);

public record EntryPreview(EntryView Entry, decimal PoolBefore, decimal PoolAfter, decimal CategoryRemainingBefore, decimal CategoryRemainingAfter);

public record CreatePoolTransferRequest(DateOnly Date, decimal Amount, string Note);

/// <summary>Amount 一律填正數，是加是扣由 Kind 決定。</summary>
public record CreateIncomeAdjustmentRequest(IncomeAdjustmentKind Kind, decimal? Days, decimal? Hours, decimal Amount, string? Note);

/// <summary>分配待定區：Mode = single（全部給 CategoryId）或 proportional（依額度比例分給所有變動分類）。</summary>
public record AllocateRequest(string Mode, int? CategoryId, decimal Amount);

/// <summary>手動覆蓋本期結束後的實際入帳日（＝下一期第一天，§3.4）。</summary>
public record NextPaydayRequest(DateOnly Date);

public record SettlementPayload(string Label, decimal Balance, decimal Applied, string? AccountName);

/// <summary>還沒到開始日期時，/api/periods/current 回 409 並帶這個內容。</summary>
public record NotStartedDto(DateOnly StartDate, DateOnly FirstPeriodStart, DateOnly FirstPeriodEnd, int DaysUntilStart);
